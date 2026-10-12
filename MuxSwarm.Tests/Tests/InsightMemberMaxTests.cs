using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Insight;
using MuxSwarm.Engine.Teams;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// /insight max for persistent team members: drops queued during a member's task are applied after it, under the
/// member lock, with a recovery snapshot - and member compaction (threshold, mechanism) is untouched.
/// </summary>
[Collection("ConsoleState")]
public class InsightMemberMaxTests : IDisposable
{
    private readonly AppConfig _config = App.Config;
    private readonly Dictionary<string, (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def)> _specialists = MultiAgentOrchestrator.Specialists;
    private readonly bool _stdio = MuxConsole.StdioMode;
    private readonly TextWriter _out = Console.Out;
    private readonly StringWriter _sink = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-member-max-" + Guid.NewGuid().ToString("N"));
    private readonly string _team = "member-max-" + Guid.NewGuid().ToString("N");

    public InsightMemberMaxTests()
    {
        Directory.CreateDirectory(_dir);
        App.Config = new AppConfig { Insight = { Level = "max" } };
        App.Config.Filesystem.SandboxPath = _dir;
        App.Config.Filesystem.AllowedPaths = [_dir];
        MuxConsole.StdioMode = true;
        Console.SetOut(_sink);
    }

    public void Dispose()
    {
        App.Config = _config; MultiAgentOrchestrator.Specialists = _specialists;
        MuxConsole.StdioMode = _stdio; Console.SetOut(_out);
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static string Big(char c, int n) => new(c, n);

    /// <summary>A warm member session: an old 3k read, then a newer 12k read that fills the protected tail.</summary>
    private async Task<AgentSession> SeedMember(string name)
    {
        var agent = new NullClient().AsAIAgent(new ChatClientAgentOptions { Name = name });
        var session = await agent.CreateSessionAsync();
        session.SetInMemoryChatHistory(
        [
            new ChatMessage(ChatRole.User, "task 1"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("a", "read_file")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("a", "OLD READ " + Big('o', 3000))]),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("b", "read_file")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("b", Big('n', 12000))]),
        ]);
        MultiAgentOrchestrator.Specialists = new() { [name] = (agent, session, new Common.AgentDefinition(name, "d", "x", false, t => t)) };
        return session;
    }

    [Fact]
    public void FreshMembers_GetNoDropList()
    {
        Assert.Null(new MemberContextManager(_team, "fresh", 0, null, null).DropFor("Alice"));
        Assert.NotNull(new MemberContextManager(_team, "persistent", 0, null, null).DropFor("Alice"));
    }

    [Fact]
    public async Task QueuedDrop_IsAppliedAfterTheTask_WithASnapshot_AndCompactionUnchanged()
    {
        var session = await SeedMember("Alice");
        var mgr = new MemberContextManager(_team, "persistent", 0, null, null);
        var drop = mgr.DropFor("Alice")!;
        Assert.Contains("m2 tool_result read_file", drop.Map());

        await mgr.RunAsync("Alice", _ =>
        {
            drop.Drop(["m2"], "old read");                    // the member calls context_drop during its task
            Assert.Equal(1, drop.PendingCount);
            return Task.FromResult("done");
        }, CancellationToken.None);

        Assert.True(session.TryGetInMemoryChatHistory(out var h));
        Assert.StartsWith(ContextDrop.NoticePrefix + " old read;", ((FunctionResultContent)h![2].Contents[0]).Result!.ToString());
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "prune-recovery"), "pre-drop-*.muxprune"));
        Assert.Equal(0, drop.PendingCount);
        // Compaction untouched: default threshold (200k after PR 12, 40k before) is far above this session.
        var st = mgr.Snapshot().Single(s => s.Name == "Alice");
        Assert.Equal(0, st.Compactions);
        Assert.Equal(MemberContextManager.EffectiveThreshold(0), mgr.ThresholdTokens);
    }

    [Fact]
    public async Task NoSnapshotPossible_DropsNothing()
    {
        App.Config.Filesystem.SandboxPath = "";            // no recovery sandbox -> no drop
        var session = await SeedMember("Bob");
        var mgr = new MemberContextManager(_team, "persistent", 0, null, null);
        var drop = mgr.DropFor("Bob")!;
        await mgr.RunAsync("Bob", _ => { drop.Drop(["m2"], "x"); return Task.FromResult("done"); }, CancellationToken.None);
        Assert.True(session.TryGetInMemoryChatHistory(out var h));
        Assert.StartsWith("OLD READ", ((FunctionResultContent)h![2].Contents[0]).Result!.ToString());
        Assert.Contains("context drop skipped", _sink.ToString());
    }

    private sealed class NullClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }
}
