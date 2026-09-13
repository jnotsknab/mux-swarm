using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine.Teams;
using System.Reflection;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Cancellation keys act on the active turn, never depend on a foreground lane or stdin monitor.</summary>
[Collection("ConsoleState")]
public class TurnCancellationKeyTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private sealed class Terminal : ITuiTerminal
    {
        public int Width => 100;
        public int Height => 25;
        public void Write(string text) { }
        public void Flush() { }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeAndCtrlQCancelLeadEvenWithForegroundedChild(bool ctrlQ)
    {
        var fields = new[]{typeof(ConsoleInputPump).GetField("_current",Static)!,typeof(MuxConsole).GetField("_driver",Static)!,
            typeof(MuxConsole).GetField("_tuiActive",Static)!,typeof(MuxConsole).GetField("_interactiveRenderMode",Static)!};
        var saved=fields.ToDictionary(f=>f,f=>f.GetValue(null));
        bool prompt=ConsoleInputPump.PromptActive,modal=ConsoleInputPump.ModalActive,stdio=MuxConsole.StdioMode;
        using var pump=ConsoleInputPump.CreateUnstartedForTest();
        using var lead=new CancellationTokenSource();using var child=new CancellationTokenSource();
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var map=(Dictionary<string,CancellationTokenSource>)typeof(MuxConsole).GetField("_laneCts",Static)!.GetValue(null)!;
        string lane="cancel-test-"+Guid.NewGuid().ToString("N");
        try
        {
            var driver=new TuiDriver(new Terminal(),frameEngine:true);typeof(TuiDriver).GetField("_foregroundAgent",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(driver,lane);map[lane]=child;
            foreach(var f in fields)f.SetValue(null,f.Name switch {"_current"=>pump,"_driver"=>driver,"_tuiActive"=>true,_=>RenderMode.Tui});
            ConsoleInputPump.PromptActive=false;ConsoleInputPump.ModalActive=false;MuxConsole.StdioMode=false;
            using var listener=EscapeKeyListener.Start(lead,stop.Token);
            if(ctrlQ)pump.FeedRawKey(new ConsoleKeyInfo('\u0011',0,false,false,false));
            else pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\u001b',ConsoleKey.Escape,false,false,false)));
            Assert.True(lead.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)),"Turn did not cancel");
            Assert.False(child.IsCancellationRequested); // listener must not use global lane cancellation; propagation is ownership's job
        }
        finally
        {
            stop.Cancel();map.Remove(lane);foreach(var pair in saved)pair.Key.SetValue(null,pair.Value);
            ConsoleInputPump.PromptActive=prompt;ConsoleInputPump.ModalActive=modal;MuxConsole.StdioMode=stdio;
        }
    }
}


public class ExecutionCancellationTests
{
    [Fact]
    public async Task NestedBackgroundKeepsRootLinkAfterIntermediateScopeAndSourceEnd()
    {
        using var root = new CancellationTokenSource();
        CancellationTokenSource grandchild;
        using (ExecutionCancellation.Enter(root.Token))
        {
            using var child = ExecutionCancellation.Link(CancellationToken.None);
            using (ExecutionCancellation.Enter(child.Token))
                grandchild = await Task.Run(() => ExecutionCancellation.Link(CancellationToken.None));
        }
        using (grandchild)
        {
            Assert.False(ExecutionCancellation.Current.CanBeCanceled);
            root.Cancel(); Assert.True(grandchild.IsCancellationRequested);
        }
    }
    [Fact]
    public async Task IndependentOwnersDoNotCrossCancelAndScopeRestores()
    {
        using var a = new CancellationTokenSource(); using var b = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            using var owner = ExecutionCancellation.Enter(b.Token);
            using var link = ExecutionCancellation.Link(CancellationToken.None);
            ready.SetResult(); await Task.Delay(Timeout.Infinite, link.Token);
        });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (ExecutionCancellation.Enter(a.Token))
        {
            using var child = ExecutionCancellation.Link(CancellationToken.None); a.Cancel();
            Assert.True(child.IsCancellationRequested); Assert.False(task.IsCompleted);
        }
        b.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(ExecutionCancellation.Current.CanBeCanceled);
    }
}

[Collection("ConsoleState")]
public class TurnOwnedWorkerTests : IDisposable
{
    private readonly Dictionary<string,(AIAgent Agent,AgentSession Session,Common.AgentDefinition Def)> _specialists=MultiAgentOrchestrator.Specialists;
    private readonly AppConfig _config=App.Config;
    private readonly SwarmConfig? _swarm=App.SwarmConfig;
    private readonly bool _stdio=MuxConsole.StdioMode;
    private readonly TextWriter _output=Console.Out;
    private readonly FieldInfo _swarmPath=typeof(PlatformContext).GetField("_swarmPathOverride",BindingFlags.NonPublic|BindingFlags.Static)!;
    private readonly object? _oldPath;
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"mux-turn-cancel-"+Guid.NewGuid().ToString("N"));
    private readonly List<DetachedJob> _jobs=new();
    private readonly List<CancellationTokenSource> _sources=new();
    private readonly List<Task> _turns=new();
    private readonly StringWriter _sink=new();
    public TurnOwnedWorkerTests()
    {
        Assert.Null(StdinCancelMonitor.Instance); // native TUI failure premise, no synthetic transport monitor
        _oldPath=_swarmPath.GetValue(null);Directory.CreateDirectory(_dir);
        App.Config=new AppConfig();App.SwarmConfig=new SwarmConfig();
        var path=Path.Combine(_dir,"Swarm.json");File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(App.SwarmConfig));_swarmPath.SetValue(null,path);
        MultiAgentOrchestrator.Specialists=new();MuxConsole.StdioMode=true;Console.SetOut(_sink);
    }
    public void Dispose()
    {
        foreach(var c in _sources) {try{c.Cancel();}catch{}}
        // Console.Out must not be restored while any owned turn/job can still write a footer into
        // the next test's captured stdout. A straggler fails THIS test, not a neighbor.
        foreach(var t in _turns) Assert.True(t.Wait(10000),"an owned lead turn outlived its test");
        foreach(var j in _jobs) {DetachedRunner.Cancel(j.Id);if(j.Task is {} jt)Assert.True(jt.Wait(10000),"an owned detached job outlived its test");}
        // A cancelled AIFunction invocation can surface to the caller before its inner batch fully
        // unwinds; those workers still write (e.g. an origin-tagged agent_turn_end footer). Keep OUR
        // sink installed until output has quiesced so a straggler can never pollute the next test.
        var deadline=DateTime.UtcNow.AddSeconds(10);
        int stable=0,last=_sink.GetStringBuilder().Length;
        while(stable<5 && DateTime.UtcNow<deadline)
        {
            Thread.Sleep(100);
            int now=_sink.GetStringBuilder().Length;
            if(now==last)stable++; else {stable=0;last=now;}
        }
        Assert.True(stable>=5,"console output did not quiesce; an owned worker is still writing");
        foreach(var c in _sources) c.Dispose();
        // Remove only jobs created by this fixture, never cancel unrelated registry entries.
        var field=typeof(DetachedRunner).GetField("_jobs",BindingFlags.NonPublic|BindingFlags.Static)!;
        var list=(List<DetachedJob>)field.GetValue(null)!;foreach(var j in _jobs)list.Remove(j);
        MultiAgentOrchestrator.Specialists=_specialists;App.Config=_config;App.SwarmConfig=_swarm;MuxConsole.StdioMode=_stdio;Console.SetOut(_output);
        _swarmPath.SetValue(null,_oldPath);Directory.Delete(_dir,true);
    }
    private CancellationTokenSource Source(){var c=new CancellationTokenSource();_sources.Add(c);return c;}
    private sealed class BlockingClient : IChatClient
    {
        internal TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Received;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,
            [EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            Received=cancellationToken;Started.TrySetResult();
            try {await Task.Delay(Timeout.Infinite,cancellationToken);}
            finally {Stopped.TrySetResult();}
            yield break;
        }
        public object? GetService(Type type,object? key=null)=>null;
        public void Dispose(){}
    }
    private async Task<(AIAgent Agent,AgentSession Session,Common.AgentDefinition Def)> Add(string name,IChatClient client)
    {
        var agent=client.AsAIAgent(new ChatClientAgentOptions{Name=name});
        var tuple=(agent,await agent.CreateSessionAsync(),new Common.AgentDefinition(name,"test","unused",false,t=>t));
        MultiAgentOrchestrator.Specialists[name]=tuple;return tuple;
    }
    private static async Task Cancelled(Task task)=>await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task.WaitAsync(TimeSpan.FromSeconds(2)));

    [Fact]
    public async Task DirectAndParallelWorkersObserveOwnerWithoutStdinMonitor()
    {
        var direct=new BlockingClient();var parallel=new BlockingClient();var a=await Add("Direct",direct);await Add("Parallel",parallel);
        var root=Source();Task d,p;
        using(ExecutionCancellation.Enter(root.Token))
        {
            d=MultiAgentOrchestrator.RunSubAgentAsync(a,"work",1,CancellationToken.None,cleanSession:true);
            p=ParallelSwarmOrchestrator.ExecuteParallelWorker("Parallel","work","Lead",MultiAgentOrchestrator.Specialists,new(),new(),_=>parallel,new(),null,null,1,false,CancellationToken.None,cleanSession:true);
        }
        await Task.WhenAll(direct.Started.Task,parallel.Started.Task).WaitAsync(TimeSpan.FromSeconds(2));root.Cancel();
        await Cancelled(d);await Cancelled(p);Assert.True(direct.Received.IsCancellationRequested);Assert.True(parallel.Received.IsCancellationRequested);
        await Task.WhenAll(direct.Stopped.Task,parallel.Stopped.Task).WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task DetachedJobCancelsWithOwnerButSeparateJobSurvives()
    {
        var owned=new BlockingClient();var independent=new BlockingClient();await Add("Owned",owned);await Add("Independent",independent);
        var owner=Source();var separate=Source();DetachedJob job;
        using(ExecutionCancellation.Enter(owner.Token))job=(await DetachedRunner.LaunchAsync("Owned","work",_=>owned,new(),CancellationToken.None))!;
        var other=(await DetachedRunner.LaunchAsync("Independent","work",_=>independent,new(),separate.Token))!;
        _jobs.Add(job);_jobs.Add(other);
        await Task.WhenAll(owned.Started.Task,independent.Started.Task).WaitAsync(TimeSpan.FromSeconds(2));owner.Cancel();
        await job.Task!.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(DetachedStatus.Cancelled,job.Status);
        Assert.Equal(DetachedStatus.Running,other.Status);Assert.False(independent.Received.IsCancellationRequested);
        separate.Cancel();await other.Task!.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task CancelledOwnerRejectsNewLaunchBeforeRegistryOrModelWork()
    {
        var client=new BlockingClient();await Add("NoLaunch",client);var owner=Source();owner.Cancel();int count=DetachedRunner.Jobs().Count;
        using(ExecutionCancellation.Enter(owner.Token))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>DetachedRunner.LaunchAsync("NoLaunch","work",_=>client,new(),CancellationToken.None));
        Assert.False(client.Started.Task.IsCompleted);Assert.Equal(count,DetachedRunner.Jobs().Count);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task GigaActualToolUsesInvocationTokenForBlockingAndBackground(bool background)
    {
        var client=new BlockingClient();await Add("Member",client);
        App.SwarmConfig=new SwarmConfig {SingleAgent=new AgentConfig{Name="Lead"},Agents=new(){new AgentConfig{Name="Member"}},Teams=new(){new TeamConfig{Name="cancel-team",Members=new(){"Member"}}}};
        var tools=GigaMode.BuildTools(_=>client,new(),CancellationToken.None);
        var run=tools.OfType<AIFunction>().Single(t=>t.Name=="run_team");
        Assert.DoesNotContain("invocationToken",run.JsonSchema.ToString());
        var owner=Source();
        var call=run.InvokeAsync(new AIFunctionArguments { ["name"]="cancel-team",["assignments"]="[{\"agent\":\"Member\",\"task\":\"work\"}]",["background"]=background },owner.Token).AsTask();
        if(background){await call.WaitAsync(TimeSpan.FromSeconds(2));_jobs.Add(DetachedRunner.Jobs().Last(j=>j.Agent=="Member"));}
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));owner.Cancel();
        if(background){var job=_jobs[^1];await job.Task!.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(DetachedStatus.Cancelled,job.Status);}
        else await Cancelled(call);
        await client.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task GigaQueuedMemberNeverStartsAfterCancellation()
    {
        var first=new BlockingClient();var second=new BlockingClient();await Add("First",first);await Add("Second",second);
        App.SwarmConfig=new SwarmConfig{SingleAgent=new AgentConfig{Name="Lead"},Agents=new(){new AgentConfig{Name="First"},new AgentConfig{Name="Second"}},Teams=new(){new TeamConfig{Name="queue-team",Members=new(){"First","Second"}}}};
        int previous=App.MaxDegreeParallelism;App.MaxDegreeParallelism=1;
        var root=Source();
        try
        {
            var run=GigaMode.BuildTools(_=>first,new(),CancellationToken.None).OfType<AIFunction>().Single(t=>t.Name=="run_team");
            var call=run.InvokeAsync(new AIFunctionArguments{["name"]="queue-team",["assignments"]="[{\"agent\":\"First\",\"task\":\"work\"},{\"agent\":\"Second\",\"task\":\"queued\"}]"},root.Token).AsTask();
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));root.Cancel();await Cancelled(call);Assert.False(second.Started.Task.IsCompleted);
        }
        finally {App.MaxDegreeParallelism=previous;}
    }
    private sealed class NestedClient(Func<Task> child) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,
            [EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            await child();yield break;
        }
        public object? GetService(Type type,object? key=null)=>null;
        public void Dispose(){}
    }
    [Fact]
    public async Task NestedRunnerGrandchildObservesTurnWithoutCapturedParentToken()
    {
        var grandchild=new BlockingClient();var leaf=await Add("Grandchild",grandchild);
        var nested=await Add("Nested",new NestedClient(async()=>await MultiAgentOrchestrator.RunSubAgentAsync(leaf,"leaf",1,CancellationToken.None)));
        var root=Source();Task task;
        using(ExecutionCancellation.Enter(root.Token))task=MultiAgentOrchestrator.RunSubAgentAsync(nested,"parent",1,CancellationToken.None);
        await grandchild.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));root.Cancel();await Cancelled(task);
        Assert.True(grandchild.Received.IsCancellationRequested);
    }

    private sealed class InvokeDelegationClient(string name,bool background,Action beforeInvoke,Action<CancellationToken>? capture=null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,
            [EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            beforeInvoke();capture?.Invoke(cancellationToken);
            var tool=options!.Tools!.OfType<AIFunction>().Single(t=>t.Name==name);
            var args=name=="delegate_parallel"
                ? new AIFunctionArguments{["assignments"]=new[]{new ParallelSwarmOrchestrator.ParallelTaskRequest("MainChild","work")},["background"]=background}
                : new AIFunctionArguments{["agentName"]="MainChild",["task"]="work"};
            await tool.InvokeAsync(args,cancellationToken);
            if(background)await Task.Delay(Timeout.Infinite,cancellationToken);
            yield break;
        }
        public object? GetService(Type type,object? key=null)=>null;
        public void Dispose(){}
    }
    [Theory]
    [InlineData("delegate_to_agent_lite",false)]
    [InlineData("delegate_parallel",false)]
    [InlineData("delegate_parallel",true)]
    public async Task MainAgentRealToolAssemblyAndEscapePropagateWithoutMonitor(string tool,bool background)
    {
        var child=new BlockingClient();var childAgent=await Add("MainChild",child);
        var defsField=typeof(MultiAgentOrchestrator).GetField("AgentDefs",BindingFlags.NonPublic|BindingFlags.Static)!;
        var savedDefs=defsField.GetValue(null);
        var oldDef=SingleAgentOrchestrator.AgentDef;
        var oldMcp=App.McpTools;
        var oldInject=AutoInject.Current;
        var oldGiga=App.GigaMode;
        var current=typeof(ConsoleInputPump).GetField("_current",BindingFlags.NonPublic|BindingFlags.Static)!;
        var savedPump=current.GetValue(null);bool savedPrompt=ConsoleInputPump.PromptActive;
        using var pump=ConsoleInputPump.CreateUnstartedForTest();
        var root=Source();
        try
        {
            defsField.SetValue(null,new List<Common.AgentDefinition>());
            App.McpTools=new List<ModelContextProtocol.Client.McpClientTool>();
            AutoInject.Current=AutoInject.Mode.None;App.GigaMode=false;
            SingleAgentOrchestrator.AgentDef=new Common.AgentDefinition("Lead","test","unused",false,_=>Array.Empty<AITool>());
            current.SetValue(null,pump);ConsoleInputPump.PromptActive=false;
            var leadClient=new InvokeDelegationClient(tool,background,()=>MultiAgentOrchestrator.Specialists["MainChild"]=childAgent);
            var task=SingleAgentOrchestrator.ChatAgentAsync(leadClient,root.Token,maxIterations:1,
                systemPromptOverride:"test",chatClientFactory:_=>child,persistSession:false,incomingGoal:"test cancellation",
                allowSubAgents:tool=="delegate_to_agent_lite",allowParallelSubAgents:tool=="delegate_parallel");
            _turns.Add(task);
            try
            {
                await child.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if(background)_jobs.Add(DetachedRunner.Jobs().Last(j=>j.Agent=="MainChild"));
                pump.FeedRawKey(new ConsoleKeyInfo('\u0011',0,false,false,false));
                await task.WaitAsync(TimeSpan.FromSeconds(3));
                await child.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(root.IsCancellationRequested);Assert.True(child.Received.IsCancellationRequested);
                if(background){await _jobs[^1].Task!.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(DetachedStatus.Cancelled,_jobs[^1].Status);}
            }
            finally {root.Cancel();try{await task.WaitAsync(TimeSpan.FromSeconds(2));}catch{}}
        }
        finally
        {
            defsField.SetValue(null,savedDefs);SingleAgentOrchestrator.AgentDef=oldDef;App.McpTools=oldMcp;
            AutoInject.Current=oldInject;App.GigaMode=oldGiga;current.SetValue(null,savedPump);ConsoleInputPump.PromptActive=savedPrompt;
        }
    }
    [Fact]
    public async Task ExactWorkflowDriverIsCancelledWhileUnrelatedDriverLives()
    {
        using var first=StartDisposableProcess();using var second=StartDisposableProcess();var root=Source();
        var run=new MuxSwarm.State.WorkflowRun {Id="cancel-test-"+Guid.NewGuid().ToString("N"),Name="test",Mode="dynamic",Driver=first};
        MuxSwarm.State.WorkflowRunRegistry.Register(run);
        try
        {
            using(ExecutionCancellation.Enter(root.Token))MuxSwarm.State.WorkflowRunRegistry.BindExecutionCancellation(run);
            root.Cancel();await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(second.HasExited);Assert.Equal(MuxSwarm.State.WorkflowRunState.Cancelled,run.State);
        }
        finally
        {
            if(!first.HasExited)first.Kill(true);if(!second.HasExited)second.Kill(true);
            var field=typeof(MuxSwarm.State.WorkflowRunRegistry).GetField("_runs",BindingFlags.NonPublic|BindingFlags.Static)!;
            ((List<MuxSwarm.State.WorkflowRun>)field.GetValue(null)!).Remove(run);
        }
    }
    private static System.Diagnostics.Process StartDisposableProcess()
    {
        var info=OperatingSystem.IsWindows()
            ?new System.Diagnostics.ProcessStartInfo("cmd.exe","/d /c ping -t 127.0.0.1 > nul")
            :new System.Diagnostics.ProcessStartInfo("/bin/sh","-c \"sleep 120\"");
        info.UseShellExecute=false;info.CreateNoWindow=true;
        return System.Diagnostics.Process.Start(info)!;
    }

    private sealed class ReturningAfterCancelClient(Action cancel) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)
        {cancel();return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,"[{\"index\":1,\"subject\":\"must-not-create\",\"description\":\"\",\"dependsOn\":[]}]")));}
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public object? GetService(Type type,object? key=null)=>null;
        public void Dispose(){}
    }
    [Fact]
    public async Task CancelledDecompositionCannotApplyBoardAfterProviderReturnsNormally()
    {
        var root=Source();var board=MuxSwarm.State.TaskBoard.Open("decomp-test",Path.Combine(_dir,"board"));
        using(ExecutionCancellation.Enter(root.Token))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>TaskDecomposer.DecomposeAsync(board,"goal",new[]{"Member"},new ReturningAfterCancelClient(root.Cancel),null,5,CancellationToken.None));
        Assert.Empty(board.Snapshot());
    }
    [Fact]
    public async Task WorkflowReportingDoneCannotEscapeOwnerCancellationWhileDriverLives()
    {
        using var process=StartDisposableProcess();var root=Source();
        var run=new MuxSwarm.State.WorkflowRun{Id="early-done-"+Guid.NewGuid().ToString("N"),Name="early",Mode="dynamic",Driver=process,State=MuxSwarm.State.WorkflowRunState.Done};
        MuxSwarm.State.WorkflowRunRegistry.Register(run);
        try
        {
            using(ExecutionCancellation.Enter(root.Token))MuxSwarm.State.WorkflowRunRegistry.BindExecutionCancellation(run);
            root.Cancel();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MuxSwarm.State.WorkflowRunState.Cancelled,run.State);
        }
        finally
        {
            if(!process.HasExited)process.Kill(true);
            ((List<MuxSwarm.State.WorkflowRun>)typeof(MuxSwarm.State.WorkflowRunRegistry).GetField("_runs",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!).Remove(run);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TeamDispatchAndClaimedAssignmentObserveInvocationAndReleaseClaim(bool assign)
    {
        var client=new BlockingClient();await Add("Member",client);var root=Source();
        string name="cancel-team-"+Guid.NewGuid().ToString("N");
        var board=MuxSwarm.State.TaskBoard.Open(name,Path.Combine(_dir,"taskboard"));
        var state=new TeamState{Name=name,Members=new(){"Member"}};
        var config=new TeamConfig{Name=name,Members=new(){"Member"},Coordination="taskboard",MemberContext="fresh",Mailbox=false};
        var method=typeof(TeamController).GetMethod("BuildTeamTools",BindingFlags.NonPublic|BindingFlags.Static)!;
        object?[] args={config,"Lead",new List<string>{"Member"},"taskboard",board,null,(Func<string,IChatClient>)(_=>client),new Dictionary<string,string>(),1,1,state,CancellationToken.None,null,null,null};
        var tools=(IList<AITool>)method.Invoke(null,args)!;
        var task=board.Create("task","body",null,assignee:"Member");
        try
        {
            var tool=tools.OfType<AIFunction>().Single(t=>t.Name==(assign?"task_assign":"team_dispatch"));
            Assert.DoesNotContain("invocationToken",tool.JsonSchema.ToString());
            AIFunctionArguments input=assign?new(){["taskId"]=task.Id,["agent"]="Member"}
                :new(){["assignments"]=new[]{new TeamAssignment("Member","work")}};
            var call=tool.InvokeAsync(input,root.Token).AsTask();
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));root.Cancel();await Cancelled(call);
            if(assign){Assert.Null(board.Get(task.Id)!.Owner);Assert.NotEqual(MuxSwarm.State.TeamTaskStatus.Done,board.Get(task.Id)!.Status);}
        }
        finally
        {
            (args[12] as AutoRunner)?.Stop();(args[13] as MemberRunner)?.Stop();
            string dir=TeamState.RootFor(name);if(Directory.Exists(dir))Directory.Delete(dir,true);
        }
    }

    private sealed class BlockingSummaryClient : IChatClient
    {
        internal TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)
        {Started.SetResult();await Task.Delay(Timeout.Infinite,cancellationToken);return new ChatResponse(new ChatMessage(ChatRole.Assistant,"unreachable"));}
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public object? GetService(Type type,object? key=null)=>null;
        public void Dispose(){}
    }
    [Fact]
    public async Task DelegationCompactionObservesOwnerInsteadOfFallingBackAfterCancel()
    {
        var client=new BlockingSummaryClient();var root=Source();
        var previous=MultiAgentOrchestrator.SwarmConfig;
        MultiAgentOrchestrator.SwarmConfig=new SwarmConfig();
        try
        {
            Task<string> task;
            using(ExecutionCancellation.Enter(root.Token))task=ResultCompactor.CompactAsync(new string('A',10000),charBudget:100,chatClient:client);
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));root.Cancel();await Cancelled(task);
        }
        finally {MultiAgentOrchestrator.SwarmConfig=previous;}
    }
    [Fact]
    public void CancelledWorkflowIgnoresLateCompletionJournalAndKillFailureIsNotSuccess()
    {
        var run=new MuxSwarm.State.WorkflowRun{Id="journal",Name="test",Mode="dynamic",State=MuxSwarm.State.WorkflowRunState.Cancelled,OwnerCancellationRequested=true};
        typeof(MuxSwarm.State.WorkflowRunRegistry).GetMethod("ApplyStatusLine",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{run,"{\"run\":\"done\"}"});
        Assert.Equal(MuxSwarm.State.WorkflowRunState.Cancelled,run.State);
        using var unassociated=new System.Diagnostics.Process();
        var failed=new MuxSwarm.State.WorkflowRun{Id="bad-handle",Name="test",Mode="dynamic",Driver=unassociated};
        MuxSwarm.State.WorkflowRunRegistry.CancelOwnedRun(failed);
        Assert.Equal(MuxSwarm.State.WorkflowRunState.Failed,failed.State);Assert.Contains("Could not stop",failed.Error);
    }

}
