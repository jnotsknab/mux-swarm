using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Insight;

namespace MuxSwarm.Tests.Tests;

/// <summary>/insight max: context_map lists droppable blocks; context_drop queues them; Apply replaces them copy-on-write.</summary>
public class InsightMaxTests
{
    private static string Big(char c, int n = 2000) => new(c, n);

    /// <summary>system, user, [assistant call + tool result] x3 (big), assistant prose (big), user, tail.</summary>
    private static List<ChatMessage> History()
    {
        var h = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt " + Big('s')),
            new(ChatRole.User, "first request " + Big('u')),
        };
        for (int i = 0; i < 3; i++)
        {
            h.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}" })]));
            h.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{i}", $"FILE f{i} " + Big((char)('a' + i)))]));
        }
        h.Add(new ChatMessage(ChatRole.Assistant, "draft plan " + Big('d')));       // m8
        h.Add(new ChatMessage(ChatRole.User, "second request"));                     // m9
        h.Add(new ChatMessage(ChatRole.Assistant, "recent " + Big('r', ContextDrop.ProtectedTailChars)));  // protected tail
        return h;
    }

    [Fact]
    public void Blocks_ListToolResultsAndAssistantProse_NeverUserSystemOrTail()
    {
        var blocks = ContextDrop.Blocks(History());
        Assert.Equal(["m3", "m5", "m7", "m8"], blocks.Select(b => b.Id));
        Assert.Equal("read_file", blocks[0].Tool);
        Assert.Equal("assistant", blocks[3].Kind);
    }

    [Fact]
    public void ParallelReadsInOneMessage_AreDroppable_InALaterTurn()
    {
        // Live shape (2026-10-12 dogfood): one tool message carries two 32k parallel read results, then short
        // turns follow. A per-message tail walk protected that whole message forever; per-block it must not.
        var h = new List<ChatMessage>
        {
            new(ChatRole.User, "read f1 and f2"),
            new(ChatRole.Assistant, [new FunctionCallContent("a", "read_file"), new FunctionCallContent("b", "read_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("a", Big('a', 32000)), new FunctionResultContent("b", Big('b', 32000))]),
            new(ChatRole.Assistant, "markers 1 and 2"),
            new(ChatRole.User, "drop those"),
        };
        var blocks = ContextDrop.Blocks(h);
        Assert.Single(blocks);                                      // f1 droppable; f2 (newest 10k) still protected
        Assert.Equal((2, 0), (blocks[0].Message, blocks[0].Content));
        h.Add(new ChatMessage(ChatRole.Assistant, "ok"));
        h.Add(new ChatMessage(ChatRole.User, "and now?"));
        h.Add(new ChatMessage(ChatRole.Assistant, Big('n', 12000)));   // newer work fills the protected tail
        h.Add(new ChatMessage(ChatRole.User, "now"));
        Assert.Equal(2, ContextDrop.Blocks(h).Count(b => b.Message == 2));
    }

    [Fact]
    public void NewestBlock_IsProtected_EvenWhenHuge()
    {
        var h = History();
        h.Add(new ChatMessage(ChatRole.User, "third"));
        h.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("z", "read_file")]));
        h.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("z", Big('z', 30000))]));
        var blocks = ContextDrop.Blocks(h);
        Assert.DoesNotContain(blocks, b => b.Message >= 11);       // the 30k result fills the protected tail alone
        Assert.Contains(blocks, b => b.Message == 10);             // ...so the older 10k block becomes droppable
    }

    [Fact]
    public void ContentPartResults_AreSizedAndPreviewedByTheirText()
    {
        // MCP tools (Brave) return content parts; the map must show their text, not "AIContent[]".
        AIContent[] parts = [new TextContent("SEARCH HIT " + Big('q'))];
        var h = new List<ChatMessage>
        {
            new(ChatRole.User, "search"),
            new(ChatRole.Assistant, [new FunctionCallContent("s", "brave_web_search")]),
            new(ChatRole.Tool, [new FunctionResultContent("s", parts)]),
            new(ChatRole.Assistant, Big('n', ContextDrop.ProtectedTailChars)),
        };
        var block = Assert.Single(ContextDrop.Blocks(h));
        Assert.Equal("brave_web_search", block.Tool);
        Assert.StartsWith("SEARCH HIT", block.Preview);
        Assert.True(block.Chars > 2000);
        var (messages, dropped, _) = new ContextDrop(() => h).Apply(h, "x.muxprune");
        Assert.Equal(0, dropped);   // nothing queued: Apply alone never drops
    }

    [Fact]
    public void Map_ShowsIdsToolsAndSizes()
    {
        var drop = new ContextDrop(History);
        string map = drop.Map();
        Assert.Contains("m3 tool_result read_file", map);
        Assert.Contains("m8 assistant", map);
        Assert.DoesNotContain("m1 ", map);
        Assert.DoesNotContain("m0 ", map);
    }

    [Fact]
    public void Drop_QueuesValidIds_AndRejectsProtectedOrUnknown()
    {
        var drop = new ContextDrop(History);
        string reply = drop.Drop(["m3", "m1", "m99", "x"], "already summarized");
        Assert.Equal(1, drop.PendingCount);
        Assert.Contains("Queued 1 block(s)", reply);
        Assert.Contains("m1", reply[reply.IndexOf("Not droppable", StringComparison.Ordinal)..]);
        Assert.Contains("Pending drops", drop.Map());
    }

    [Fact]
    public void Apply_ReplacesOnlyQueuedBlocks_CopyOnWrite_KeepingCallIds()
    {
        var history = History();
        var drop = new ContextDrop(() => history);
        drop.Drop(["m5", "m8"], "done with it");
        var (messages, dropped, saved) = drop.Apply(history, @"C:\sandbox\prune-recovery\pre-drop-x.muxprune");

        Assert.Equal(2, dropped);
        Assert.True(saved > 3000);
        Assert.Equal(history.Count, messages.Count);
        var r = (FunctionResultContent)messages[5].Contents[0];
        Assert.Equal("c1", r.CallId);
        Assert.StartsWith(ContextDrop.NoticePrefix + " done with it;", r.Result!.ToString());
        Assert.Contains("pre-drop-x.txt", r.Result!.ToString());
        Assert.StartsWith(ContextDrop.NoticePrefix, messages[8].Text);
        Assert.Same(history[3], messages[3]);                                       // untouched message keeps identity
        Assert.StartsWith("FILE f1", ((FunctionResultContent)history[5].Contents[0]).Result!.ToString());   // original intact
        Assert.Equal(0, drop.PendingCount);
        Assert.DoesNotContain(ContextDrop.Blocks(messages), b => b.Message is 5 or 8);   // a notice is not droppable again
    }

    [Fact]
    public void Clear_ForgetsQueuedDrops()
    {
        var drop = new ContextDrop(History);
        drop.Drop(["m3"], "x");
        drop.Clear();
        Assert.Equal(0, drop.Apply(History(), "p.muxprune").Dropped);
    }

    [Fact]
    public void Tools_AreContextMapAndContextDrop()
    {
        Assert.Equal(["context_map", "context_drop"], new ContextDrop(History).Tools().Select(t => t.Name));
    }

    [Fact]
    public void MaxLine_ReportsDropped()
    {
        int saved = ContextGauge.Dropped;
        try
        {
            ContextGauge.Dropped = 3;
            Assert.EndsWith("| dropped 3]", ContextGauge.Line(InsightLevel.Max, 1000, 2000, null));
            Assert.DoesNotContain("dropped", ContextGauge.Line(InsightLevel.Full, 1000, 2000, null));
        }
        finally { ContextGauge.Dropped = saved; }
    }
}

/// <summary>End to end: a real lead turn at insight max, scripted model calls context_map then context_drop.</summary>
[Collection("ConsoleState")]
public class InsightMaxTurnTests : IDisposable
{
    private readonly AppConfig _config = App.Config;
    private readonly ExecutionLimits _limits = ExecutionLimits.Current;
    private readonly Common.AgentDefinition? _def = SingleAgentOrchestrator.AgentDef;
    private readonly IList<ModelContextProtocol.Client.McpClientTool>? _mcp = App.McpTools;
    private readonly AutoInject.Mode _inject = AutoInject.Current;
    private readonly bool _giga = App.GigaMode;
    private readonly bool _stdio = MuxConsole.StdioMode;
    private readonly TextWriter _out = Console.Out;
    private readonly StringWriter _sink = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-insight-max-" + Guid.NewGuid().ToString("N"));

    public InsightMaxTurnTests()
    {
        Directory.CreateDirectory(_dir);
        App.Config = new AppConfig { Insight = { Level = "max" } };
        App.Config.Filesystem.SandboxPath = _dir;
        App.Config.Filesystem.AllowedPaths = [_dir];
        ExecutionLimits.Current = new ExecutionLimits();
        App.McpTools = new List<ModelContextProtocol.Client.McpClientTool>();
        AutoInject.Current = AutoInject.Mode.None;
        App.GigaMode = false;
        MuxConsole.StdioMode = true;
        Console.SetOut(_sink);
        SingleAgentOrchestrator.AgentDef = new Common.AgentDefinition("Lead", "test", "unused", false,
            tools => [.. tools, AIFunctionFactory.Create((string path) => $"FILE {path} " + new string('x', 3000), "read_file"),
                AIFunctionFactory.Create(() => new string('p', ContextDrop.ProtectedTailChars), "pad")]);
    }

    public void Dispose()
    {
        App.Config = _config; ExecutionLimits.Current = _limits; SingleAgentOrchestrator.AgentDef = _def;
        App.McpTools = _mcp; AutoInject.Current = _inject; App.GigaMode = _giga; MuxConsole.StdioMode = _stdio; Console.SetOut(_out);
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Turn 1 reads f0, then pads past the protected tail. Turn 2: map, drop the f0 result, answer.
    /// The drop lands after turn 2.</summary>
    private sealed class Model : IChatClient
    {
        public readonly List<string> Seen = new();
        public string? Map;
        public string LastRequest = "";
        public string? LastF0Result;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var list = messages.ToList();
            var results = list.SelectMany(x => x.Contents).OfType<FunctionResultContent>().ToList();
            string lastUser = list.Last(x => x.Role == ChatRole.User).Text;
            Seen.Add(string.Join("|", list.Select(x => x.Role.Value)));
            LastRequest = string.Join("\n", list.SelectMany(x => x.Contents).Select(c =>
                c is FunctionResultContent r ? r.Result?.ToString() : (c as TextContent)?.Text));
            LastF0Result = results.FirstOrDefault(r => r.CallId == "r0")?.Result?.ToString();
            FunctionCallContent? call = null;
            if (lastUser.StartsWith("read", StringComparison.Ordinal) && !results.Any(r => r.CallId == "r0"))
                call = new("r0", "read_file", new Dictionary<string, object?> { ["path"] = "f0" });
            else if (lastUser.StartsWith("read", StringComparison.Ordinal) && !results.Any(r => r.CallId == "pad"))
                call = new("pad", "pad", new Dictionary<string, object?>());
            else if (lastUser.StartsWith("tidy", StringComparison.Ordinal) && !results.Any(r => r.CallId == "map"))
                call = new("map", "context_map", new Dictionary<string, object?>());
            else if (lastUser.StartsWith("tidy", StringComparison.Ordinal) && !results.Any(r => r.CallId == "drop"))
            {
                Map = results.First(r => r.CallId == "map").Result?.ToString();
                string id = Map!.Split('\n').First(l => l.Contains("read_file")).Split(' ')[0];
                call = new("drop", "context_drop", new Dictionary<string, object?> { ["ids"] = new[] { id }, ["reason"] = "read already" });
            }
            if (call is not null) yield return new ChatResponseUpdate(ChatRole.Assistant, [call]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "DONE");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task ContextDrop_IsAppliedToTheSessionAfterTheTurn_WithASnapshot()
    {
        var model = new Model();
        var client = model.AsBuilder().UseFunctionInvocation()
            .Use(inner => new InsightClient(inner, () => InsightLevels.Current)).Build();
        // Two goals through one session via the scripted-input hook, then quit.
        var prevInput = MuxConsole.InputOverride;
        MuxConsole.InputOverride = new StringReader("read f0\ntidy up\nthanks\n/qc\n");
        try
        {
            await SingleAgentOrchestrator.ChatAgentAsync(client, CancellationToken.None, maxIterations: 3,
                systemPromptOverride: "test", persistSession: false)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { MuxConsole.InputOverride = prevInput; }

        Assert.NotNull(model.Map);
        string[] snapshots = Directory.GetFiles(Path.Combine(_dir, "prune-recovery"), "pre-drop-*.muxprune");
        Assert.Single(snapshots);
        Assert.Contains("FILE f0", File.ReadAllText(Path.ChangeExtension(snapshots[0], ".txt")));
        Assert.Contains("Agent dropped 1 context block", _sink.ToString());
        Assert.Equal(1, ContextGauge.Dropped);
        // Turn 3's request is built from the session: the f0 result is now the notice, the original is gone.
        Assert.StartsWith(ContextDrop.NoticePrefix + " read already;", model.LastF0Result);
        Assert.DoesNotContain(new string('x', 3000), model.LastRequest);   // the 3k-char body is gone everywhere
    }
}
