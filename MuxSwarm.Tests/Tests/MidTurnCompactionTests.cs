using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Mid-turn compaction (executionLimits.midTurnCompaction). Before the fix the threshold check's
/// <c>break</c> only left the per-update contents loop, so a crossing was serviced at the END of the turn:
/// the agent ran every tool, wrote its final answer, then was compacted and told "continue", which made it
/// redo the whole task. The stream must stop right after the tool results of the model call that crossed
/// the threshold, and a final answer must never be cut.
/// </summary>
public class MidTurnCompactionCutTests
{
    private static FunctionCallContent Call(string id) => new(id, "read", new Dictionary<string, object?> { ["path"] = id });
    private static FunctionResultContent Result(string id) => new(id, "content of " + id);
    private static UsageContent Usage() => new(new UsageDetails { TotalTokenCount = 100 });

    [Fact]
    public void UsageOverThreshold_CutsAfterThatCallsToolResult()
    {
        var cut = new MidTurnCompactionCut();
        Assert.False(cut.Observe([Call("c0")], overThreshold: false));
        Assert.False(cut.Observe([Usage()], overThreshold: true));   // armed, tool not run yet
        Assert.True(cut.Observe([Result("c0")], overThreshold: true));
    }

    [Fact]
    public void UnderThreshold_NeverCuts()
    {
        var cut = new MidTurnCompactionCut();
        Assert.False(cut.Observe([Call("c0")], false));
        Assert.False(cut.Observe([Usage()], false));
        Assert.False(cut.Observe([Result("c0")], false));
    }

    [Fact]
    public void ParallelCalls_WaitForEveryResult()
    {
        var cut = new MidTurnCompactionCut();
        cut.Observe([Call("a"), Call("b")], false);
        cut.Observe([Usage()], true);
        Assert.False(cut.Observe([Result("a")], true));
        Assert.True(cut.Observe([Result("b")], true));
    }

    [Fact]
    public void FinalAnswerOverThreshold_IsNeverCut()
    {
        // A model call with no tool calls is the final answer: no result ever follows, so the stream ends normally.
        var cut = new MidTurnCompactionCut();
        Assert.False(cut.Observe([new TextContent("DONE")], true));
        Assert.False(cut.Observe([Usage()], true));
    }
}

/// <summary>Drives a real single-agent turn with a scripted model through the full function-invocation stack.</summary>
[Collection("ConsoleState")]
public class MidTurnCompactionTurnTests : IDisposable
{
    private readonly FieldInfo _swarmPath = typeof(PlatformContext).GetField("_swarmPathOverride", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _oldPath;
    private readonly AppConfig _config = App.Config;
    private readonly SwarmConfig? _swarm = App.SwarmConfig;
    private readonly ExecutionLimits _limits = ExecutionLimits.Current;
    private readonly Common.AgentDefinition? _def = SingleAgentOrchestrator.AgentDef;
    private readonly IList<ModelContextProtocol.Client.McpClientTool>? _mcp = App.McpTools;
    private readonly AutoInject.Mode _inject = AutoInject.Current;
    private readonly bool _giga = App.GigaMode;
    private readonly bool _stdio = MuxConsole.StdioMode;
    private readonly TextWriter _out = Console.Out;
    private readonly StringWriter _sink = new();
    // ResolveCompactionClient reads MultiAgentOrchestrator.SwarmConfPath, a static readonly path fixed when that type
    // first initializes, so a path override set here may never reach it. Write the config there and restore it after;
    // the ConsoleState collection runs with nothing else in parallel.
    private readonly string _confPath = MultiAgentOrchestrator.SwarmConfPath;
    private readonly byte[]? _confBytes;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-midturn-" + Guid.NewGuid().ToString("N"));

    public MidTurnCompactionTurnTests()
    {
        _oldPath = _swarmPath.GetValue(null);
        Directory.CreateDirectory(_dir);
        App.Config = new AppConfig();   // no sandbox: compaction proceeds without a snapshot
        App.SwarmConfig = new SwarmConfig { CompactionAgent = new CompacterConfig { Model = "compactor" } };
        string json = System.Text.Json.JsonSerializer.Serialize(App.SwarmConfig);
        string path = Path.Combine(_dir, "Swarm.json");
        File.WriteAllText(path, json);
        _swarmPath.SetValue(null, path);
        _confBytes = File.Exists(_confPath) ? File.ReadAllBytes(_confPath) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(_confPath)!);
        File.WriteAllText(_confPath, json);
        ExecutionLimits.Current = new ExecutionLimits();   // midTurnCompaction defaults to true
        App.McpTools = new List<ModelContextProtocol.Client.McpClientTool>();
        AutoInject.Current = AutoInject.Mode.None;
        App.GigaMode = false;
        MuxConsole.StdioMode = true;
        Console.SetOut(_sink);
        SingleAgentOrchestrator.AgentDef = new Common.AgentDefinition("Lead", "test", "unused", false,
            tools => [.. tools, AIFunctionFactory.Create((string path) => "content of " + path, "read_file")]);
    }

    public void Dispose()
    {
        _swarmPath.SetValue(null, _oldPath);
        if (_confBytes is null) File.Delete(_confPath); else File.WriteAllBytes(_confPath, _confBytes);
        App.Config = _config; App.SwarmConfig = _swarm; ExecutionLimits.Current = _limits;
        SingleAgentOrchestrator.AgentDef = _def; App.McpTools = _mcp; AutoInject.Current = _inject; App.GigaMode = _giga;
        MuxConsole.StdioMode = _stdio; Console.SetOut(_out);
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Scripted model: reads f0..f{files-1} one call at a time, skipping files the context shows as already read
    /// (a tool result, or the compaction tool manifest's <c>path=fN</c>), then answers DONE. Reports usage after every call, sized by how much tool output it was sent.
    /// Answers DONE after <see cref="MaxCalls"/> calls so a redo loop ends with an assertion, not a hang.
    /// </summary>
    private sealed class ScriptedModel(int files) : IChatClient
    {
        private const int MaxCalls = 20;
        public readonly List<string> Reads = new();
        public int Answers;
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var list = messages.ToList();
            string all = string.Join("\n", list.Select(x => x.Text)
                .Concat(list.SelectMany(x => x.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString())));
            int next = ++_calls > MaxCalls ? -1 : Enumerable.Range(0, files)
                .FirstOrDefault(i => !all.Contains($"content of f{i}") && !all.Contains($"path=f{i}"), -1);
            if (next >= 0)
            {
                Reads.Add($"f{next}");
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(Guid.NewGuid().ToString("N"), "read_file", new Dictionary<string, object?> { ["path"] = $"f{next}" })]);
            }
            else
            {
                Answers++;
                yield return new ChatResponseUpdate(ChatRole.Assistant, "DONE");
            }
            int toolResults = list.SelectMany(x => x.Contents).OfType<FunctionResultContent>().Count();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { TotalTokenCount = 1_000 + 10_000 * toolResults })]);
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    /// <summary>Compaction model: returns a summary and records every transcript it was asked to compact.</summary>
    private sealed class Compactor(ScriptedModel model) : IChatClient
    {
        public readonly List<string> Transcripts = new();
        public readonly List<int> ReadsAtCompaction = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
        {
            Transcripts.Add(m.Last().Text ?? "");
            ReadsAtCompaction.Add(model.Reads.Count);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "[CONTEXT SUMMARY — prior conversation compacted]\nreading files\n[END SUMMARY — continue from here]")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private static async Task RunTurn(ScriptedModel model, Compactor compactor, int threshold)
    {
        var client = model.AsBuilder().UseFunctionInvocation().Build();
        await SingleAgentOrchestrator.ChatAgentAsync(client, CancellationToken.None, maxIterations: 3,
            systemPromptOverride: "test", chatClientFactory: _ => compactor, autoCompactTokenThreshold: threshold,
            persistSession: false, incomingGoal: "read f0..f5 then say DONE")
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CrossingMidTurn_CompactsAfterThatToolResult_AndTheTurnContinuesWithoutRedoingWork()
    {
        var model = new ScriptedModel(files: 6);
        var compactor = new Compactor(model);

        // Usage after call k (k results sent) = 1k + 10k*k: call 3 reports 31k > 25k, after it asked for f3.
        await RunTurn(model, compactor, threshold: 25_000);

        Assert.Equal(["f0", "f1", "f2", "f3", "f4", "f5"], model.Reads);  // every file read exactly once
        Assert.Equal(1, model.Answers);                              // one final answer, no redo
        // Compacted once, right after f3's result (4 reads): the call that would ask for f4 was never sent first.
        // The turn then continued at f4 because the summary's tool manifest lists f0..f3 as already read.
        Assert.Equal([4], compactor.ReadsAtCompaction);
    }

    [Fact]
    public async Task CrossingOnTheFinalAnswer_DoesNotCutOrRestartTheTurn()
    {
        var model = new ScriptedModel(files: 2);
        var compactor = new Compactor(model);

        // Call 1 (asks for f1, 1 result sent) reports 11k < 15k; call 2 is the final answer at 21k > 15k.
        await RunTurn(model, compactor, threshold: 15_000);

        Assert.Equal(["f0", "f1"], model.Reads);
        Assert.Equal(1, model.Answers);              // no "continue" restart
        Assert.Empty(compactor.Transcripts);         // not compacted mid-turn; the next turn would compact
    }
}
