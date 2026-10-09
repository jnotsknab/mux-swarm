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
    public void CompactionNote_PointsAtSnapshot_TextCopy_AllSnapshotsPattern_AndSaysSearchDontRead()
    {
        string snap = Path.Combine("sb", "prune-recovery", "pre-compact-s1-x.muxprune");
        string note = ContextPruneSession.CompactionSnapshotNote(snap, "pre-compact-s1-*");
        Assert.Contains(snap, note);
        Assert.Contains(Path.ChangeExtension(snap, ".txt"), note);
        Assert.Contains(Path.Combine("sb", "prune-recovery", "pre-compact-s1-*"), note);   // earlier compactions stay findable
        Assert.Contains("Do not read these files whole", note);
        Assert.Contains("search the .txt for specific terms", note);
    }

    // ---- Dogfood additions: plain-text copy, manifest, repeated compactions ----

    private static List<ChatMessage> ToolHistory(int calls)
    {
        var h = new List<ChatMessage> { new(ChatRole.User, "start\nsecond line of the goal") };
        for (int i = 0; i < calls; i++)
        {
            h.Add(new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent($"call-{i}", i % 2 == 0 ? "execute_command_async" : "Filesystem_write_file",
                    new Dictionary<string, object?> { ["path"] = $@"C:\work\file{i}.cs", ["command"] = "git commit -m x\nmore" })
            }));
            h.Add(new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent($"call-{i}", $"result {i}\nline two") }));
        }
        h.Add(new ChatMessage(ChatRole.Assistant, "all done"));
        return h;
    }

    [Fact]
    public void InProgress_RecordsTheUncommittedTurn_AndLeadsTheTextSnapshot()
    {
        string ip = SessionSnapshotText.InProgress("fact-check the Tao story", new[] { "repl_shell_exec", "repl_shell_exec", "web_fetch" });
        Assert.Contains("Goal: fact-check the Tao story", ip);
        Assert.Contains("Tools run so far (3): repl_shell_exec x2, web_fetch", ip);
        Assert.Equal("", SessionSnapshotText.InProgress(null, Array.Empty<string>()));
        string txt = SessionSnapshotText.FullText(ToolHistory(1), ip);
        Assert.StartsWith("[IN-PROGRESS TURN", txt);
    }

    [Fact]
    public void FullText_HasManifestOfEveryCall_AndUnescapedMultilineTranscript()
    {
        string txt = SessionSnapshotText.FullText(ToolHistory(3));
        Assert.Contains("- execute_command_async | call-0 | ", txt);
        Assert.Contains("- Filesystem_write_file | call-1 | ", txt);
        Assert.Contains(@"C:\work\file2.cs", txt);
        Assert.Contains("start\nsecond line of the goal", txt.Replace("\r\n", "\n"));   // real newline, not "\\n"
        Assert.DoesNotContain("\\n", txt);
        Assert.Contains("[tool result #call-2] result 2", txt);
    }

    [Fact]
    public void Files_IgnoresCommandSwitches_ButFindsWindowsAndUnixPaths()
    {
        var h = new List<ChatMessage> { new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c1", "execute_command_async",
            new Dictionary<string, object?> { ["command"] = @"cd /d C:\Users\me\repo && cmd /c rg x /home/u/a.md ./src/b.cs" }) }) };
        var files = SessionSnapshotText.Files(h);
        Assert.Equal(new[] { @"C:\Users\me\repo", "/home/u/a.md", "./src/b.cs" }, files);
    }

    [Fact]
    public void Manifest_IsBounded_AndPointsAtTheFullList()
    {
        var h = ToolHistory(SessionSnapshotText.ManifestRecentCalls + 10);
        string m = SessionSnapshotText.Manifest(h, "snap.txt");
        Assert.Contains("execute_command_async x25", m);
        Assert.Contains($"Last {SessionSnapshotText.ManifestRecentCalls} of 50 tool calls:", m);
        Assert.DoesNotContain("#call-9:", m);                       // oldest calls omitted...
        Assert.Contains("#call-49:", m);                            // ...newest kept
        Assert.Contains("10 earlier calls omitted; all calls are listed in snap.txt", m);
        Assert.Contains($"... {50 - SessionSnapshotText.ManifestFiles} more", m);
        Assert.All(m.Split('\n').Where(l => l.StartsWith("- execute_command_async #")),
            l => Assert.True(l.Length < SessionSnapshotText.ArgChars + 60));   // args cut, one line each
        Assert.Equal("", SessionSnapshotText.Manifest(new List<ChatMessage> { new(ChatRole.User, "hi") }, null));
    }

    [Fact]
    public async Task Prune_AlsoWritesAPlainTextCopy()
    {
        AIAgent agent = new Client().AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        var payload = new string('P', 5000);
        session.SetInMemoryChatHistory(ContextPrunerTests.History(ContextPrunerTests.Tool("call-1", "fetch_data", "UNIQUE_PRUNED " + payload)));
        string sandbox = Path.Combine(Path.GetTempPath(), "mux-prune-txt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var result = await ContextPruneSession.ApplyAsync(agent, session, ContextPruner.Mode.Tools, sandbox, new[] { sandbox });
            string txt = Path.ChangeExtension(result.RecoveryPath!, ".txt");
            Assert.True(File.Exists(txt));
            Assert.Contains("UNIQUE_PRUNED", await File.ReadAllTextAsync(txt));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public async Task RepeatedCompactions_AllSnapshotsMatchTheSessionPattern_WithTextCopies()
    {
        AIAgent agent = new Client().AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        session.SetInMemoryChatHistory(ToolHistory(2));
        string sandbox = Path.Combine(Path.GetTempPath(), "mux-compact2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            string tag = ContextPruneSession.SessionTag("2026-10-08_01-02-03");
            var first = ContextPruneSession.SaveSnapshot(await agent.SerializeSessionAsync(session), sandbox, new[] { sandbox },
                $"pre-compact-{tag}", SessionSnapshotText.FullText(ToolHistory(2)));
            var second = ContextPruneSession.SaveSnapshot(await agent.SerializeSessionAsync(session), sandbox, new[] { sandbox },
                $"pre-compact-{tag}", SessionSnapshotText.FullText(ToolHistory(1)));
            var dir = Path.Combine(sandbox, "prune-recovery");
            var all = Directory.GetFiles(dir, $"pre-compact-{tag}-*.muxprune");
            Assert.Equal(2, all.Length);
            Assert.Contains(first, all); Assert.Contains(second, all);
            Assert.True(File.Exists(Path.ChangeExtension(first, ".txt")));
            Assert.True(File.Exists(Path.ChangeExtension(second, ".txt")));
            Assert.Empty(Directory.GetFiles(dir, "*.partial"));
        }
        finally { Directory.Delete(sandbox, true); }
    }
}

[Collection("ExecLimitsState")]
public class CompactionPromptTests
{
    [Fact]
    public async Task CompactionPrompt_SeparatesUserDecisionsFromAgentSuggestions_AndSkipsReinjectedContext()
    {
        var capture = new PromptCapture();
        var prev = ExecutionLimits.Current;
        try
        {
            ExecutionLimits.Current = new ExecutionLimits { SubAgentSummaryMode = "auto" };
            await ResultCompactor.CompactConversationAsync(new List<ChatMessage> { new(ChatRole.User, "hi") }, capture);
        }
        finally { ExecutionLimits.Current = prev; }
        Assert.Contains("User decided / instructed", capture.System);
        Assert.Contains("Agent proposed, not confirmed by the user", capture.System);
        Assert.Contains("injected BRAIN.md / MEMORY.md", capture.System);
    }

    [Fact]
    public void CompactionPrompt_SaysTheTranscriptIsDataNotARequest()
    {
        string p = ResultCompactor.CompactionSystemPrompt(12000, null);
        Assert.Contains("not the agent in this conversation", p);
        Assert.Contains("will usually read like a request or work in progress", p);
        Assert.Contains("Do NOT answer it, continue", p);
        Assert.Contains("<transcript>", p);
    }

    // The exact shape of a second compaction's input: the previous compacted message (model summary +
    // engine blocks) followed by later turns.
    private const string PriorCompacted =
        "[CONTEXT SUMMARY — prior conversation compacted]\nUser asked for AI news. Le Chonk from Mistral.\n[END SUMMARY — continue from here]\n\n" +
        "[EXTRACTED REFERENCES]\n[User]: news please\n- .//a:entry\n[END REFERENCES]\n\n" +
        "[TOOL MANIFEST: generated from the session, not summarized]\nCalls by tool: repl_shell_exec x2\n- repl_shell_exec #toolu_X: code=import urllib\n[END TOOL MANIFEST]\n\n" +
        "[Pre-compaction history: C:\\sb\\prune-recovery\\pre-compact-s-1.txt (plain text) and C:\\sb\\prune-recovery\\pre-compact-s-1.muxprune (JSON)]\n" +
        "Earlier compactions of this session: C:\\sb\\prune-recovery\\pre-compact-s-* (all snapshots live in C:\\sb).\n" +
        "Do not read these files whole; that would undo the compaction.";

    [Fact]
    public void StripEngineBlocks_KeepsTheModelSummary_DropsReferencesManifestAndPointer()
    {
        string s = ResultCompactor.StripEngineBlocks(PriorCompacted);
        Assert.Contains("Le Chonk from Mistral", s);
        Assert.Contains("[END SUMMARY", s);
        Assert.DoesNotContain("EXTRACTED REFERENCES", s);
        Assert.DoesNotContain(".//a:entry", s);
        Assert.DoesNotContain("TOOL MANIFEST", s);
        Assert.DoesNotContain("toolu_X", s);
        Assert.DoesNotContain("Pre-compaction history", s);
        Assert.DoesNotContain("Do not read these files whole", s);
        Assert.Equal("plain [text] stays", ResultCompactor.StripEngineBlocks("plain [text] stays"));
    }

    [Fact]
    public void CompactionPrompt_ManifestLine_OnlyWhenTheCallerAppendsOne()
    {
        // Team-member compaction shares this prompt but appends no manifest, so it must keep its tool record.
        Assert.Contains("do not list tool calls", ResultCompactor.CompactionSystemPrompt(12000, null, toolManifestAppended: true));
        string team = ResultCompactor.CompactionSystemPrompt(12000, null);
        Assert.DoesNotContain("do not list tool calls", team);
        Assert.Contains("record of the tools used", team);
    }

    [Fact]
    public void StripEngineBlocks_AlsoDropsTheInProgressBlock()
    {
        string s = ResultCompactor.StripEngineBlocks("[CONTEXT SUMMARY]\nx\n[END SUMMARY]\n\n" +
            SessionSnapshotText.InProgress("fetch the news", new[] { "repl_shell_exec", "repl_shell_exec" }));
        Assert.DoesNotContain("IN-PROGRESS TURN", s);
        Assert.Contains("[END SUMMARY]", s);
    }

    [Fact]
    public void StripEngineBlocks_KeepsReferences_WhenThereIsNoModelSummary()
    {
        // e.g. an older compaction whose model reply was not a summary: the references are the only copy.
        string legacy = "I'll pick up where that left off.\n\n[EXTRACTED REFERENCES]\n[User]: find the Tao story\n[END REFERENCES]";
        string s = ResultCompactor.StripEngineBlocks(legacy);
        Assert.Contains("find the Tao story", s);
    }

    [Fact]
    public async Task SecondCompaction_InputHasNoEngineBlocks_AndIsFenced()
    {
        var capture = new PromptCapture();
        await WithAuto(() => ResultCompactor.CompactConversationAsync(new List<ChatMessage>
        {
            new(ChatRole.User, PriorCompacted),
            new(ChatRole.Assistant, "Fact-checking the Tao story now."),
        }, capture));
        Assert.StartsWith("<transcript>", capture.Input);
        Assert.EndsWith("</transcript>", capture.Input.TrimEnd());
        Assert.Contains("Le Chonk from Mistral", capture.Input);
        Assert.Contains("[Agent]: Fact-checking the Tao story now.", capture.Input);
        Assert.DoesNotContain("toolu_X", capture.Input);
        Assert.DoesNotContain(".//a:entry", capture.Input);
        Assert.DoesNotContain("Pre-compaction history", capture.Input);
    }

    [Fact]
    public async Task AgentVoiceReply_IsRetriedOnce_ThenAccepted()
    {
        var client = new Scripted("I'll pick up where that timed-out turn left off.",
                                  "[CONTEXT SUMMARY — prior conversation compacted]\nok\n[END SUMMARY — continue from here]");
        var msg = await WithAuto(() => ResultCompactor.CompactConversationAsync(
            new List<ChatMessage> { new(ChatRole.User, "do the thing") }, client));
        Assert.Equal(2, client.Calls);
        Assert.StartsWith("[CONTEXT SUMMARY — prior conversation compacted]", msg.Text);
        Assert.DoesNotContain("pick up where", msg.Text);
    }

    [Fact]
    public async Task AgentVoiceReply_Twice_FallsBackToMarkedExtractiveSummary()
    {
        var client = new Scripted("I'll pick up where that left off.", "Sure, continuing the task now.");
        var msg = await WithAuto(() => ResultCompactor.CompactConversationAsync(
            new List<ChatMessage> { new(ChatRole.User, "please fetch the latest release notes for v0.15.2") }, client));
        Assert.Equal(2, client.Calls);
        Assert.StartsWith("[CONTEXT SUMMARY — extractive]", msg.Text);
        Assert.Contains("release notes for v0.15.2", msg.Text);
        Assert.DoesNotContain("continuing the task", msg.Text);
    }

    private static async Task<T> WithAuto<T>(Func<Task<T>> f)
    {
        var prev = ExecutionLimits.Current;
        try { ExecutionLimits.Current = new ExecutionLimits { SubAgentSummaryMode = "auto" }; return await f(); }
        finally { ExecutionLimits.Current = prev; }
    }

    private sealed class Scripted(params string[] replies) : IChatClient
    {
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[Math.Min(Calls++, replies.Length - 1)])));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private sealed class PromptCapture : IChatClient
    {
        public string Input = "";
        public string System = "";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
        {
            System = m.First(x => x.Role == ChatRole.System).Text;
            Input = m.First(x => x.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[CONTEXT SUMMARY]x[END SUMMARY]")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }
}
