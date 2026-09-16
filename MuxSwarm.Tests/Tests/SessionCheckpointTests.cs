using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Regression coverage for the pre-turn session checkpoint (v0.14.1).
///
/// Before the fix, <see cref="Common.PersistChatSessionAsync"/> was reached only AFTER a turn
/// completed, so a brand-new session had no directory on disk until the first assistant response
/// returned - a crash or hard exit during turn 1 lost the exchange entirely. The orchestrator now
/// checkpoints at user-submit time by adding the goal to the session's in-memory history,
/// serializing, then RESTORING the prior history so the framework's own commit does not duplicate
/// the user message on success.
///
/// These pin the checkpoint's durability + no-duplicate contract and the supporting on-disk
/// guarantees (atomic replace, session-file-aware retention).
/// </summary>
public class SessionCheckpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-ckpt-" + Guid.NewGuid().ToString("N"));

    public SessionCheckpointTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done.");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static List<(string Role, string Text)> ReadMessages(JsonElement session)
    {
        var result = new List<(string, string)>();
        if (session.TryGetProperty("stateBag", out var bag)
            && bag.TryGetProperty("InMemoryChatHistoryProvider", out var prov)
            && prov.TryGetProperty("messages", out var msgs)
            && msgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in msgs.EnumerateArray())
            {
                var role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                var text = "";
                if (m.TryGetProperty("contents", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    foreach (var c in cs.EnumerateArray())
                        if (c.TryGetProperty("$type", out var t) && t.GetString() == "text"
                            && c.TryGetProperty("text", out var tx))
                            text += tx.GetString();
                result.Add((role, text));
            }
        }
        return result;
    }

    /// <summary>
    /// Mirrors the orchestrator's checkpoint: add goal -> serialize -> restore history. A fresh
    /// session exposes NO history (TryGet returns false), which is exactly the first-turn case, so
    /// the add must fall back to an empty list rather than being skipped.
    /// </summary>
    private static async Task<JsonElement> CheckpointAsync(AIAgent agent, AgentSession session, string goal)
    {
        bool hadHistory = session.TryGetInMemoryChatHistory(out var history) && history is not null;
        List<ChatMessage>? restore = hadHistory ? history!.ToList() : null;
        try
        {
            var pending = hadHistory ? history! : new List<ChatMessage>();
            pending.Add(new ChatMessage(ChatRole.User, goal));
            session.SetInMemoryChatHistory(pending);
            return await agent.SerializeSessionAsync(session);
        }
        finally
        {
            session.SetInMemoryChatHistory(restore ?? new List<ChatMessage>());
        }
    }

    [Fact]
    public async Task Checkpoint_PersistsUserGoal_BeforeAnyAssistantResponse()
    {
        // The whole point of the fix: the user's message is durable the instant they submit,
        // with no assistant turn having run yet.
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        var msgs = ReadMessages(await CheckpointAsync(agent, session, "my first goal"));

        Assert.Single(msgs);
        Assert.Equal("user", msgs[0].Role);
        Assert.Contains("my first goal", msgs[0].Text);
    }

    [Fact]
    public async Task FreshSession_ExposesNoHistory_SoCheckpointMustNotSkipInjection()
    {
        // Pins the framework behaviour that made the first version of this fix silently useless:
        // a newly created session returns FALSE from TryGetInMemoryChatHistory and serializes as
        // an empty stateBag. A checkpoint guarded on TryGet would therefore write nothing on the
        // FIRST turn - the exact case the checkpoint exists to protect. SetInMemoryChatHistory,
        // by contrast, works unconditionally.
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        Assert.False(session.TryGetInMemoryChatHistory(out var none) && none is not null);
        Assert.Empty(ReadMessages(await agent.SerializeSessionAsync(session)));

        session.SetInMemoryChatHistory(new List<ChatMessage> { new(ChatRole.User, "forced") });
        Assert.Contains(ReadMessages(await agent.SerializeSessionAsync(session)), m => m.Text.Contains("forced"));
    }

    [Fact]
    public async Task Checkpoint_OnFreshSession_LeavesNoResidualUserMessage()
    {
        // After checkpointing a brand-new session, the restore must leave it empty again so the
        // framework's commit is the only writer and the goal is not duplicated on success.
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        await CheckpointAsync(agent, session, "first ever goal");
        Assert.Empty(ReadMessages(await agent.SerializeSessionAsync(session)));

        await foreach (var _ in agent.RunStreamingAsync(
            new[] { new ChatMessage(ChatRole.User, "first ever goal") }, session)) { }

        var msgs = ReadMessages(await agent.SerializeSessionAsync(session));
        Assert.Equal(1, msgs.Count(m => m.Text.Contains("first ever goal")));
    }

    [Fact]
    public async Task Checkpoint_DoesNotDuplicateUserMessage_WhenTurnThenSucceeds()
    {
        // The framework commits the run's messages itself on success. If the checkpoint left its
        // injected copy behind, the goal would appear TWICE in the persisted session.
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        await CheckpointAsync(agent, session, "only once please");

        await foreach (var _ in agent.RunStreamingAsync(
            new[] { new ChatMessage(ChatRole.User, "only once please") }, session)) { }

        var msgs = ReadMessages(await agent.SerializeSessionAsync(session));
        Assert.Equal(1, msgs.Count(m => m.Text.Contains("only once please")));
        Assert.Contains(msgs, m => m.Role == "assistant");
    }

    [Fact]
    public async Task Checkpoint_PreservesPriorTurns()
    {
        // Restoring the snapshot must not clobber history from earlier completed turns.
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        await foreach (var _ in agent.RunStreamingAsync(
            new[] { new ChatMessage(ChatRole.User, "turn one") }, session)) { }

        var msgs = ReadMessages(await CheckpointAsync(agent, session, "turn two"));

        Assert.Contains(msgs, m => m.Text.Contains("turn one"));
        Assert.Contains(msgs, m => m.Text.Contains("turn two"));
    }

    [Fact]
    public async Task Checkpoint_RestoresSessionHistory_SoLaterSerializationIsUnchanged()
    {
        // After the checkpoint returns, the session must look exactly as it did before it ran -
        // ContextPruneSession's ownership check compares history identity/length.
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        await foreach (var _ in agent.RunStreamingAsync(
            new[] { new ChatMessage(ChatRole.User, "established") }, session)) { }

        var before = ReadMessages(await agent.SerializeSessionAsync(session));
        await CheckpointAsync(agent, session, "transient goal");
        var after = ReadMessages(await agent.SerializeSessionAsync(session));

        Assert.Equal(before.Count, after.Count);
        Assert.DoesNotContain(after, m => m.Text.Contains("transient goal"));
    }

    [Fact]
    public void HasSessionFile_DistinguishesRealSessionsFromBareDirectories()
    {
        var real = Path.Combine(_root, "2026-01-01_00-00-00");
        var bare = Path.Combine(_root, "2026-01-02_00-00-00");
        var foreign = Path.Combine(_root, "PlaywrightChromeProfile");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(real, "agent_session.json"), "{}");
        File.WriteAllText(Path.Combine(foreign, "prefs.json"), "{}");   // .json, but not a session

        Assert.True(Common.HasSessionFile(real));
        Assert.False(Common.HasSessionFile(bare));
        Assert.False(Common.HasSessionFile(foreign));
    }

    [Fact]
    public void PruneOldSessions_CountsSessions_NotBareDirectories()
    {
        // Retention 2 must keep the 2 newest REAL sessions. Checkpoint-created (empty) dirs and
        // foreign tool folders must not consume retention slots and evict real history.
        for (int i = 1; i <= 3; i++)
        {
            var d = Path.Combine(_root, $"2026-03-0{i}_00-00-00");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "agent_session.json"), "{}");
        }
        Directory.CreateDirectory(Path.Combine(_root, "PlaywrightChromeProfile"));
        Directory.CreateDirectory(Path.Combine(_root, "2026-03-09_00-00-00")); // in-progress checkpoint dir

        Common.PruneOldSessions(_root, 2);

        Assert.True(Directory.Exists(Path.Combine(_root, "2026-03-03_00-00-00")));
        Assert.True(Directory.Exists(Path.Combine(_root, "2026-03-02_00-00-00")));
        Assert.False(Directory.Exists(Path.Combine(_root, "2026-03-01_00-00-00")));
        // Non-session directories are never candidates, so they are left alone.
        Assert.True(Directory.Exists(Path.Combine(_root, "PlaywrightChromeProfile")));
        Assert.True(Directory.Exists(Path.Combine(_root, "2026-03-09_00-00-00")));
    }

    [Fact]
    public async Task PersistChatSession_LeavesNoPartialFile_AndWritesValidJson()
    {
        // The write is temp + atomic replace; a reader must never see a ".partial" in the
        // "*.json" glob every session consumer uses, and the final file must parse.
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        await foreach (var _ in agent.RunStreamingAsync(
            new[] { new ChatMessage(ChatRole.User, "persist me") }, session)) { }

        var dir = Path.Combine(_root, "atomic-target");
        await Common.PersistChatSessionAsync(agent, session, "ignored-timestamp", dir, quiet: true);

        var file = Path.Combine(dir, "agent_session.json");
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetFiles(dir, "*.partial"));
        Assert.Single(Directory.GetFiles(dir, "*.json"));
        var parsed = JsonDocument.Parse(File.ReadAllText(file));   // throws if truncated
        Assert.Contains(ReadMessages(parsed.RootElement), m => m.Text.Contains("persist me"));
    }

    [Fact]
    public async Task PersistChatSession_OverwritesCleanly_OnRepeatedSaves()
    {
        // Repeated checkpoints target the same path; File.Move(overwrite) must replace rather
        // than throw, and must not accumulate stray files.
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        var dir = Path.Combine(_root, "repeat-target");

        for (int i = 0; i < 3; i++)
        {
            await foreach (var _ in agent.RunStreamingAsync(
                new[] { new ChatMessage(ChatRole.User, $"goal {i}") }, session)) { }
            await Common.PersistChatSessionAsync(agent, session, "ignored", dir, quiet: true);
        }

        Assert.Single(Directory.GetFiles(dir, "*.json"));
        Assert.Empty(Directory.GetFiles(dir, "*.partial"));
        var msgs = ReadMessages(JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "agent_session.json"))).RootElement);
        Assert.Contains(msgs, m => m.Text.Contains("goal 2"));
    }
}
