using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// v0.15.2: a turn that ends without completing (watchdog timeout, provider/tool failure) must never
/// vanish from the persisted session. Before the fix the activity watchdog only re-armed on streamed
/// model output, so a long tool wait (ask_user) tripped it; the failure landed in the generic catch,
/// the framework had not committed the run, and the end-of-turn persist wrote the PRE-turn history.
/// </summary>
public class TurnLossTests
{
    // ---- ActivityTimeout: suspend/resume (ask_user must not count against the watchdog) ----

    [Fact]
    public async Task Suspended_Watchdog_DoesNotFire_UntilResumed()
    {
        using var t = ActivityTimeout.Start(TimeSpan.FromMilliseconds(150), CancellationToken.None);
        t.Suspend();
        await Task.Delay(500);
        Assert.False(t.Token.IsCancellationRequested);

        t.Resume();
        await Task.Delay(100);
        Assert.False(t.Token.IsCancellationRequested);   // full window re-armed on resume
        await Task.Delay(400);
        Assert.True(t.Token.IsCancellationRequested);
        Assert.True(t.Expired);
    }

    [Fact]
    public async Task Ping_WhileSuspended_DoesNotRearm()
    {
        using var t = ActivityTimeout.Start(TimeSpan.FromMilliseconds(150), CancellationToken.None);
        t.Suspend();
        t.Ping();   // a trailing stream update must not restart the countdown under a pending ask_user
        await Task.Delay(500);
        Assert.False(t.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Unsuspended_Watchdog_StillFires()
    {
        using var t = ActivityTimeout.Start(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Task.Delay(400);
        Assert.True(t.Expired);
    }

    // ---- Failure record: the turn survives serialization ----

    private sealed class Client : IChatClient
    {
        public bool Throw;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m,
            ChatOptions? o = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "working ");
            await Task.Yield();
            if (Throw) throw new OperationCanceledException("watchdog");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done.");
        }
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private static List<(string Role, string Text)> Read(JsonElement s)
    {
        var r = new List<(string, string)>();
        if (s.TryGetProperty("stateBag", out var bag) && bag.TryGetProperty("InMemoryChatHistoryProvider", out var p)
            && p.TryGetProperty("messages", out var ms))
            foreach (var m in ms.EnumerateArray())
            {
                string text = "";
                foreach (var c in m.GetProperty("contents").EnumerateArray())
                    if (c.TryGetProperty("text", out var tx)) text += tx.GetString();
                r.Add((m.GetProperty("role").GetString()!, text));
            }
        return r;
    }

    [Fact]
    public async Task FailedTurn_IsRecorded_InsteadOfVanishing()
    {
        var client = new Client();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        await foreach (var _ in agent.RunStreamingAsync(new[] { new ChatMessage(ChatRole.User, "first goal") }, session)) { }

        client.Throw = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync(new[] { new ChatMessage(ChatRole.User, "long goal") }, session)) { }
        });

        // Old behaviour (pinned): the failed run is simply absent.
        Assert.DoesNotContain(Read(await agent.SerializeSessionAsync(session)), m => m.Text.Contains("long goal"));

        // Fix: what the orchestrator now does in its catch before the end-of-turn persist.
        string note = TurnFailureRecord.Note("timed out after 7200s with no model or tool activity", "working ",
            new[] { "execute_command_async", "repl_shell_exec", "repl_shell_exec", "ask_user" });
        TurnFailureRecord.Append(session, "long goal", note, preTurnCount: 2);

        var msgs = Read(await agent.SerializeSessionAsync(session));
        Assert.Equal(4, msgs.Count);                                   // prior clean turn kept
        Assert.Equal(("user", "long goal"), msgs[2]);
        Assert.Contains("[turn ended: timed out after 7200s", msgs[3].Text);
        Assert.Contains("working", msgs[3].Text);                      // partial text kept
        Assert.Contains("repl_shell_exec x2", msgs[3].Text);           // tools that ran are listed
    }

    [Fact]
    public async Task FailureRecord_WorksOnAFreshSession()
    {
        // A fresh session exposes no history (TryGet == false); the record must not be skipped.
        AIAgent agent = new Client().AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        TurnFailureRecord.Append(session, "goal", TurnFailureRecord.Note("failed: boom", "", Array.Empty<string>()),
            TurnFailureRecord.Count(session));
        var msgs = Read(await agent.SerializeSessionAsync(session));
        Assert.Equal(2, msgs.Count);
        Assert.Equal("[turn ended: failed: boom]", msgs[1].Text);
    }

    [Fact]
    public async Task FailureAfterCommit_DoesNotDuplicateTheGoal()
    {
        AIAgent agent = new Client().AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        int pre = TurnFailureRecord.Count(session);
        await foreach (var _ in agent.RunStreamingAsync(new[] { new ChatMessage(ChatRole.User, "goal") }, session)) { }
        TurnFailureRecord.Append(session, "goal", "[turn ended: cancelled (session ending)]", pre);
        var msgs = Read(await agent.SerializeSessionAsync(session));
        Assert.Single(msgs, m => m.Role == "user");
        Assert.Equal("[turn ended: cancelled (session ending)]", msgs[^1].Text);
    }

    [Fact]
    public async Task RepeatedIdenticalGoal_ThatFails_IsStillRecorded()
    {
        // Same text as the previous (completed) turn: the record must not mistake it for a committed goal.
        AIAgent agent = new Client().AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        await foreach (var _ in agent.RunStreamingAsync(new[] { new ChatMessage(ChatRole.User, "retry it") }, session)) { }
        int pre = TurnFailureRecord.Count(session);
        TurnFailureRecord.Append(session, "retry it", "[turn ended: failed: boom]", pre);
        var msgs = Read(await agent.SerializeSessionAsync(session));
        Assert.Equal(2, msgs.Count(m => m.Role == "user"));
    }

    // ---- Compaction snapshot ----

    [Fact]
    public async Task PreCompactionSnapshot_IsIndented_Greppable_AndRecoverable()
    {
        AIAgent agent = new Client().AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        session.SetInMemoryChatHistory(new List<ChatMessage>
        {
            new(ChatRole.User, "find the NEEDLE-7731 detail"),
            new(ChatRole.Assistant, "ok"),
        });
        string sandbox = Path.Combine(Path.GetTempPath(), "mux-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var serialized = await agent.SerializeSessionAsync(session);
            string path = ContextPruneSession.SaveSnapshot(serialized, sandbox, new[] { sandbox }, "pre-compact");

            Assert.StartsWith("pre-compact-", Path.GetFileName(path));
            Assert.EndsWith(".muxprune", path);   // not *.json: must not change session discovery / resumability
            var lines = await File.ReadAllLinesAsync(path);
            Assert.True(lines.Length > 5);        // indented, so a grep returns a line, not the whole file
            Assert.Contains(lines, l => l.Contains("NEEDLE-7731") && l.Length < 200);

            var recovered = await agent.DeserializeSessionAsync(JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement);
            Assert.True(recovered.TryGetInMemoryChatHistory(out var h));
            Assert.Contains(h!, m => m.Text.Contains("NEEDLE-7731"));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public void CompactionNote_PointsAtSnapshot_AndSaysSearchDontRead()
    {
        string note = ContextPruneSession.CompactionSnapshotNote(@"C:\sb\prune-recovery\pre-compact-x.muxprune");
        Assert.Contains(@"C:\sb\prune-recovery\pre-compact-x.muxprune", note);
        Assert.Contains("Do not read this file whole", note);
        Assert.Contains("search it for specific terms", note);
    }
}
