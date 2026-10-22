using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

// /setmodel inside a live session: Swarm.json is updated, the lead moves to the new model with the
// session unchanged, and changed specialists are rebuilt.
[Collection("ConsoleState")]
public sealed class SetModelMidSessionTests : IDisposable
{
    private static readonly FieldInfo SwarmOverride = typeof(PlatformContext).GetField("_swarmPathOverride", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _prevPath = SwarmOverride.GetValue(null);
    private readonly SwarmConfig? _prevSwarm = App.SwarmConfig;
    private readonly TextWriter _out = Console.Out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-setmodel-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public SetModelMidSessionTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Swarm.json");
        File.WriteAllText(_file, """
            { "singleAgent": { "name": "MuxAgent", "model": "model-a", "modelOpts": { "reasoning": { "effort": "high" } } },
              "orchestrator": { "model": "orch-a" },
              "agents": [ { "name": "CodeAgent", "model": "code-a", "description": "c", "custom": 7 },
                          { "name": "WebAgent", "model": "web-a", "description": "w" } ] }
            """);
        SwarmOverride.SetValue(null, _file);
        Console.SetOut(TextWriter.Null);
        MuxConsole.StdioMode = true;
    }

    public void Dispose()
    {
        SwarmOverride.SetValue(null, _prevPath);
        App.SwarmConfig = _prevSwarm;
        Console.SetOut(_out);
        MuxConsole.StdioMode = false;
        MuxConsole.InputOverride = Console.In;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private JsonElement Swarm() => JsonDocument.Parse(File.ReadAllText(_file)).RootElement;

    [Fact]
    public async Task OneArg_SwitchesTheLead_KeepsEffortAndUnknownFields()
    {
        var models = new Dictionary<string, string>();
        var changes = await ModelSwitch.RunAsync("/setmodel model-b", models, "MuxAgent", _file);
        var c = Assert.Single(changes);
        Assert.True(c.IsLead);
        Assert.Equal(("singleAgent", "model-b"), (c.Id, c.Model));
        Assert.Null(c.Effort);                                     // effort unchanged -> not re-applied
        Assert.Equal("model-b", Swarm().GetProperty("singleAgent").GetProperty("model").GetString());
        Assert.Equal("high", Swarm().GetProperty("singleAgent").GetProperty("modelOpts").GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(7, Swarm().GetProperty("agents")[0].GetProperty("custom").GetInt32());
    }

    [Fact]
    public async Task TwoArgs_TargetAnyAgent_ByClosestName_AndRefreshTheModelMap()
    {
        var models = new Dictionary<string, string> { ["CodeAgent"] = "code-a" };
        var changes = await ModelSwitch.RunAsync("/setmodel code code-b", models, "MuxAgent", _file);
        var c = Assert.Single(changes);
        Assert.False(c.IsLead);
        Assert.Equal("CodeAgent", c.Label);
        Assert.Equal("code-b", models["CodeAgent"]);
        Assert.Equal("code-b", Swarm().GetProperty("agents")[0].GetProperty("model").GetString());
        // An orchestrator-led session (swarm) treats the orchestrator slot as the lead.
        var orch = Assert.Single(await ModelSwitch.RunAsync("/setmodel orch-b", models, leadName: null, _file));
        Assert.True(orch.IsLead);
        Assert.Equal("orch-b", models["Orchestrator"]);
    }

    [Fact]
    public async Task NamedLead_UsesItsOwnAgentEntry()
    {
        var changes = await ModelSwitch.RunAsync("/setmodel code-z", null, "CodeAgent", _file);
        Assert.Equal("agents/0", Assert.Single(changes).Id);
        Assert.True(changes[0].IsLead);
        Assert.Equal("model-a", Swarm().GetProperty("singleAgent").GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("/setmodel nosuchagent x")]
    [InlineData("/setmodel CodeAgent")]                           // agent without a model: usage, not a model id
    public async Task BadInput_ChangesNothing(string line)
    {
        var before = File.ReadAllBytes(_file);
        Assert.Empty(await ModelSwitch.RunAsync(line, null, "MuxAgent", _file));
        Assert.Equal(before, File.ReadAllBytes(_file));
    }

    [Fact]
    public async Task Rebuild_KeepsInstructionsToolsAndOptions_AndTheSessionMovesUnchanged()
    {
        var a = new Recorder("a");
        var options = new ChatOptions { Instructions = "SYS", Tools = [AIFunctionFactory.Create(() => 1, "t1")], Temperature = 0.3f };
        AIAgent agent = a.AsAIAgent(new ChatClientAgentOptions { Name = "Lead", ChatOptions = options });
        var session = await agent.CreateSessionAsync();
        await foreach (var _ in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "first")], session)) { }

        var b = new Recorder("b");
        ModelSwitch.ApplyEffort(options, "low");                  // the live options carry later /effort edits
        var next = ModelSwitch.Rebuild(agent, b, options);
        var moved = await ModelSwitch.MoveSessionAsync(agent, session, next);
        await foreach (var _ in next.RunStreamingAsync([new ChatMessage(ChatRole.User, "second")], moved)) { }

        Assert.Equal("Lead", next.Name);
        Assert.Equal("SYS", b.Options!.Instructions);
        Assert.Equal("t1", Assert.Single(b.Options.Tools!).Name);
        Assert.Equal(0.3f, b.Options.Temperature);
        Assert.Equal(ReasoningEffort.Low, b.Options.Reasoning?.Effort);
        Assert.Equal(["first", "reply from a", "second"], b.Messages!.Select(m => m.Text));   // history carried, verbatim
        Assert.Empty(a.Messages!.Where(m => m.Text == "second"));
    }

    [Fact]
    public async Task ChangedSpecialists_AreRebuiltOnTheNewModel_OthersUntouched()
    {
        AIAgent code = new Recorder("old").AsAIAgent(new ChatClientAgentOptions { Name = "CodeAgent", ChatOptions = new() { Instructions = "CODE" } });
        AIAgent web = new Recorder("old").AsAIAgent(new ChatClientAgentOptions { Name = "WebAgent" });
        var def = new Common.AgentDefinition("x", "x", "x", false, t => t);
        var specialists = new Dictionary<string, (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def)>
        {
            ["CodeAgent"] = (code, await code.CreateSessionAsync(), def),
            ["WebAgent"] = (web, await web.CreateSessionAsync(), def),
        };
        var built = new List<string>();
        await ModelSwitch.RebuildSpecialistsAsync(specialists,
            [new ModelSwitch.Change("agents/0", "CodeAgent", "code-b", "max", false)], m => { built.Add(m); return new Recorder(m); });
        Assert.Equal(["code-b"], built);
        Assert.NotSame(code, specialists["CodeAgent"].Agent);
        Assert.Same(web, specialists["WebAgent"].Agent);
        var o = (ChatClientAgentOptions)specialists["CodeAgent"].Agent.GetService(typeof(ChatClientAgentOptions))!;
        Assert.Equal("CODE", o.ChatOptions!.Instructions);
        Assert.Equal("max", ReasoningEffortControl.GetLabel(o.ChatOptions));
    }

    [Fact]
    public void SetModel_IsAvailableInSessions_AndTheWebKeepsTheSession()
    {
        Assert.True(MuxSwarm.Engine.Tui.TuiCommands.IsSessionNative("/setmodel"));
        Assert.True(MuxSwarm.Engine.Tui.TuiCommands.IsReplOnly("/setmodel"));   // and still at the menu
        Assert.True(ModelSwitch.IsCommand("/setmodel"));
        Assert.True(ModelSwitch.IsCommand("/SETMODEL code x"));
        Assert.False(ModelSwitch.IsCommand("/setmodelx"));
    }

    private sealed class Recorder(string name) : IChatClient
    {
        public ChatOptions? Options;
        public List<ChatMessage>? Messages;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken c = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [EnumeratorCancellation] CancellationToken c = default)
        {
            Options = o; Messages = m.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "reply from " + name);
        }
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task LiveSession_SecondTurnRunsOnTheNewModel_WithTheWholeHistory()
    {
        var clients = new Dictionary<string, Recorder>();
        IChatClient Factory(string m) => clients.TryGetValue(m, out var c) ? c : clients[m] = new Recorder(m);
        var defsField = typeof(MultiAgentOrchestrator).GetField("AgentDefs", BindingFlags.NonPublic | BindingFlags.Static)!;
        var savedDefs = defsField.GetValue(null);
        var oldDef = SingleAgentOrchestrator.AgentDef; var oldMcp = App.McpTools; var oldInject = AutoInject.Current; var oldGiga = App.GigaMode;
        try
        {
            defsField.SetValue(null, new List<Common.AgentDefinition>());
            App.McpTools = new List<ModelContextProtocol.Client.McpClientTool>();
            AutoInject.Current = AutoInject.Mode.None; App.GigaMode = false;
            SingleAgentOrchestrator.AgentDef = new Common.AgentDefinition("MuxAgent", "test", "unused", false, _ => Array.Empty<AITool>());
            MuxConsole.InputOverride = new StringReader("first\n/setmodel model-b\nsecond\n/qc\n");
            await SingleAgentOrchestrator.ChatAgentAsync(Factory("model-a"), CancellationToken.None, maxIterations: 1,
                systemPromptOverride: "SYS", chatClientFactory: Factory, persistSession: false, leadClientFactory: Factory)
                .WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            defsField.SetValue(null, savedDefs); SingleAgentOrchestrator.AgentDef = oldDef; App.McpTools = oldMcp;
            AutoInject.Current = oldInject; App.GigaMode = oldGiga;
        }
        Assert.Equal(["first"], clients["model-a"].Messages!.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        var b = clients["model-b"];
        Assert.Equal(["first", "reply from model-a", "second"], b.Messages!.Where(m => m.Role != ChatRole.System).Select(m => m.Text));
        Assert.Contains("SYS", b.Options!.Instructions);
        Assert.Equal("model-b", Swarm().GetProperty("singleAgent").GetProperty("model").GetString());
    }
}
