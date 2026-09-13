using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Explicit pruning command discoverability and no-first-goal leakage.</summary>
public class ContextPruneCommandTests
{
    [Theory]
    [InlineData("/prune")]
    [InlineData("/prune dupes")]
    [InlineData("/prune tools")]
    [InlineData("/prune stale")]
    public void Prune_IsSessionNativeAndGuardedBeforeFirstGoal(string command)
    {
        Assert.Contains(TuiCommands.All, e => e.Cmd == "/prune" && e.Scope == TuiCommands.Scope.SessionOnly);
        Assert.Equal(SingleAgentOrchestrator.FirstTurnInputKind.GuardedSessionNative,
            SingleAgentOrchestrator.ClassifyFirstTurnInput(command));
        Assert.Contains(TuiCommands.All, e => e.Cmd == command);
    }

    [Theory]
    [InlineData("/PRUNE", true, false)]
    [InlineData(" /prune\ttools ", true, false)]
    [InlineData("/prune unknown", true, true)]
    [InlineData("/prune tools stale", true, true)]
    [InlineData("/prunex", false, false)]
    public void Parser_HandlesInvalidOptionsWithoutDispatchingToModel(string input, bool recognized, bool invalid)
    {
        Assert.Equal(recognized, ContextPruner.TryParse(input, out _, out var error));
        Assert.Equal(invalid, error is not null);
    }

    [Fact]
    public void Help_ExplainsPruneOptionsAndNoModelCall()
    {
        Assert.Contains("/prune", Help.HelpText);
        Assert.Contains("dupes|tools|stale", Help.HelpText);
    }
}


/// <summary>Deterministic reduction must preserve protected context, identities and call/result structure.</summary>
public class ContextPrunerTests
{
    internal static Microsoft.Extensions.AI.ChatMessage Message(string role, string text)
        => new(new Microsoft.Extensions.AI.ChatRole(role), text);
    internal static List<Microsoft.Extensions.AI.ChatMessage> History(params Microsoft.Extensions.AI.ChatMessage[] middle)
    {
        var history = new List<Microsoft.Extensions.AI.ChatMessage> { Message("system", "Instructions"), Message("user", "Original goal") };
        history.AddRange(middle);
        history.AddRange(new[] { Message("user", "recent one"), Message("assistant", new string('R', 6000)), Message("user", "recent two"), Message("assistant", new string('S', 6000)) });
        return history;
    }
    internal static Microsoft.Extensions.AI.ChatMessage[] Tool(string id, string name, object result, string path = "/work/data.txt", int? head = null)
        => new[]
        {
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
                new List<Microsoft.Extensions.AI.AIContent> { new Microsoft.Extensions.AI.FunctionCallContent(id, name,
                    new Dictionary<string, object?> { ["path"] = path, ["head"] = head }) }),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Tool,
                new List<Microsoft.Extensions.AI.AIContent> { new Microsoft.Extensions.AI.FunctionResultContent(id, result) })
        };

    [Fact]
    public void Dupes_ExactSubstantialBlocksRetainLastCopyAndAllRoles()
    {
        string text = new('X', 1200);
        var history = History(Message("assistant", text), Message("user", "middle"), Message("assistant", text));
        var plan = ContextPruner.Create(history, ContextPruner.Mode.Dupes);
        Assert.Equal(1, plan.Dupes); Assert.Equal(history.Count, plan.Messages.Count);
        Assert.Same(history[4], plan.Messages[4]);
        Assert.StartsWith(ContextPruner.NoticePrefix, plan.Messages[2].Text);
        Assert.Equal(text, history[2].Text);
        Assert.True(plan.CharactersSaved > 0);
        Assert.Same(history[0], plan.Messages[0]); Assert.Same(history[1], plan.Messages[1]);
        Assert.Equal(0, ContextPruner.Create(plan.Messages, ContextPruner.Mode.All).Blocks);
    }

    [Fact]
    public void Tools_PreserveEnvelopeCallImagesErrorsProtectedReadsAndUnknownShapes()
    {
        var parts = new List<Microsoft.Extensions.AI.AIContent>
        {
            new Microsoft.Extensions.AI.TextContent(new string('L', 5000)),
            new Microsoft.Extensions.AI.DataContent(new byte[] { 1, 2, 3 }, "image/png")
        };
        var ordinary = Tool("a", "fetch_data", parts);
        var protectedRead = Tool("b", "Filesystem_read_text_file", new string('K', 5000), "/repo/AGENTS.md");
        var error = Tool("c", "fetch_data", "[ERROR] " + new string('E', 5000));
        var unknown = Tool("d", "fetch_data", new { text = new string('U', 5000) });
        var history = History(ordinary.Concat(protectedRead).Concat(error).Concat(unknown).ToArray());
        var plan = ContextPruner.Create(history, ContextPruner.Mode.Tools);
        Assert.Equal(1, plan.Tools);
        Assert.Same(history[2], plan.Messages[2]);
        var changed = Assert.IsType<Microsoft.Extensions.AI.FunctionResultContent>(plan.Messages[3].Contents[0]);
        Assert.Equal("a", changed.CallId);
        var content = Assert.IsAssignableFrom<IList<Microsoft.Extensions.AI.AIContent>>(changed.Result);
        Assert.Same(parts[1], content[1]);
        Assert.StartsWith(ContextPruner.NoticePrefix, Assert.IsType<Microsoft.Extensions.AI.TextContent>(content[0]).Text);
        for (int i = 4; i < history.Count; i++) Assert.Same(history[i], plan.Messages[i]);
    }

    [Fact]
    public void Stale_OnlySameReadArgumentsWithSuccessfulNewerReadAndRetainReplacementInAllMode()
    {
        var old = Tool("old", "Filesystem_read_text_file", new string('A', 5000));
        var newer = Tool("new", "Filesystem_read_text_file", new string('B', 5000));
        var history = History(old.Concat(newer).ToArray());
        var plan = ContextPruner.Create(history, ContextPruner.Mode.All);
        Assert.Equal(1, plan.Stale); Assert.Equal(0, plan.Tools);
        Assert.Same(history[5], plan.Messages[5]);
        var different = History(old.Concat(Tool("other", "Filesystem_read_text_file", new string('B', 5000), head: 30)).ToArray());
        Assert.Equal(0, ContextPruner.Create(different, ContextPruner.Mode.Stale).Blocks);
        var failed = History(old.Concat(Tool("fail", "Filesystem_read_text_file", "[ERROR] gone")).ToArray());
        Assert.Equal(0, ContextPruner.Create(failed, ContextPruner.Mode.Stale).Blocks);
    }

    [Fact]
    public void RepeatingAll_NeverDeletesTheLastCopyRetainedForDuplicateOrStaleReference()
    {
        var history = History(Tool("a", "Filesystem_read_text_file", new string('A', 5000))
            .Concat(Tool("b", "Filesystem_read_text_file", new string('B', 5000))).ToArray());
        var first = ContextPruner.Create(history, ContextPruner.Mode.All);
        var second = ContextPruner.Create(first.Messages, ContextPruner.Mode.All);
        Assert.Equal(0, second.Blocks);
        Assert.Contains(second.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(), r => r.Result!.ToString() == new string('B', 5000));
    }

    [Fact]
    public void All_ProcessesStaleChainsWithoutKeepingIntermediateSupersededReads()
    {
        var history = History(Tool("a", "Filesystem_read_text_file", new string('A', 5000))
            .Concat(Tool("b", "Filesystem_read_text_file", new string('B', 5000)))
            .Concat(Tool("c", "Filesystem_read_text_file", new string('C', 5000))).ToArray());
        var plan = ContextPruner.Create(history, ContextPruner.Mode.All);
        Assert.Equal(2, plan.Stale); Assert.Equal(0, plan.Tools);
    }

    [Fact]
    public void StalePreservesEveryPartOfNewerReplacementObservation()
    {
        var replacement = new List<AIContent> { new TextContent(new string('B', 5000)), new TextContent(new string('C', 5000)) };
        var history = History(Tool("a", "Filesystem_read_text_file", new string('A', 5000))
            .Concat(Tool("b", "Filesystem_read_text_file", replacement)).ToArray());
        var plan = ContextPruner.Create(history, ContextPruner.Mode.All);
        Assert.Equal(1, plan.Stale); Assert.Equal(0, plan.Tools);
        Assert.Same(history[5], plan.Messages[5]);
        Assert.Equal(0, ContextPruner.Create(plan.Messages, ContextPruner.Mode.All).Blocks);
    }

    [Theory]
    [InlineData("Skills/custom/reference.md")]
    [InlineData("Prompts/codeagent.md")]
    [InlineData("Skills\\custom\\reference.md")]
    public void RelativeInstructionDirectoriesRemainProtected(string path)
    {
        var history = History(Tool("a", "Filesystem_read_text_file", new string('A', 5000), path)
            .Concat(Tool("b", "Filesystem_read_text_file", new string('B', 5000), path)).ToArray());
        Assert.Equal(0, ContextPruner.Create(history, ContextPruner.Mode.All).Blocks);
    }

    [Fact]
    public void PreviouslyElidedOrPartlyElidedReadCannotJustifyStaleRemoval()
    {
        var history = History(Tool("a", "Filesystem_read_text_file", new string('A', 2000))
            .Concat(Tool("b", "Filesystem_read_text_file", new string('B', 5000))).ToArray());
        var tools = ContextPruner.Create(history, ContextPruner.Mode.Tools);
        Assert.Equal(1, tools.Tools);
        Assert.Equal(0, ContextPruner.Create(tools.Messages, ContextPruner.Mode.Stale).Blocks);
        var partial = new List<AIContent> { new TextContent("[pruned:tools; omitted]"), new TextContent(new string('C', 5000)) };
        var multipart = History(Tool("a", "Filesystem_read_text_file", new string('A', 2000))
            .Concat(Tool("b", "Filesystem_read_text_file", partial)).ToArray());
        Assert.Equal(0, ContextPruner.Create(multipart, ContextPruner.Mode.Stale).Blocks);
    }

    [Fact]
    public void TailAndOriginalGoalAndInstructionsRemainProtected()
    {
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        { Message("system", new string('T', 5000)), Message("developer", new string('T', 5000)), Message("user", new string('T', 5000)) };
        messages.AddRange(Tool("a", "fetch_data", new string('T', 5000)));
        messages.Add(Message("user", "second goal")); messages.Add(Message("assistant", new string('T', 5000)));
        Assert.Equal(0, ContextPruner.Create(messages, ContextPruner.Mode.All).Blocks);
    }

    [Fact]
    public void DedupeOnlyChangesMatchingTextContentNotSurroundingIntentOrMetadata()
    {
        string text = new('D', 1500);
        var old = new ChatMessage(ChatRole.User, new List<AIContent> { new TextContent("Keep this instruction"), new TextContent(text) })
            { AuthorName = "User", MessageId = "id", CreatedAt = DateTimeOffset.UtcNow };
        var history = History(old, Message("assistant", text));
        var plan = ContextPruner.Create(history, ContextPruner.Mode.Dupes);
        Assert.Equal(1, plan.Dupes);
        Assert.Same(old.Contents[0], plan.Messages[2].Contents[0]);
        Assert.Equal(old.CreatedAt, plan.Messages[2].CreatedAt);
        Assert.Equal(old.MessageId, plan.Messages[2].MessageId);
        Assert.Equal(text, ((TextContent)old.Contents[1]).Text);
    }

    [Fact]
    public void ErrorsOrMalformedCallGroupsRemainUntouched()
    {
        var missing = new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("missing", new string('X', 5000)) });
        var duplicate = Tool("repeat", "Filesystem_read_text_file", new string('A', 5000));
        var extra = Tool("repeat", "Filesystem_read_text_file", new string('B', 5000));
        var markedError = Tool("error", "read_data", new string('X', 5000));
        ((FunctionResultContent)markedError[1].Contents[0]).AdditionalProperties = new() { ["isError"] = true };
        var history = History(new[] { missing }.Concat(duplicate).Concat(extra).Concat(markedError).ToArray());
        Assert.Equal(0, ContextPruner.Create(history, ContextPruner.Mode.All).Blocks);
    }

    [Fact]
    public void MemoryScaffoldDoesNotHideOrReplaceOriginalUserGoalProtection()
    {
        string text = new('G', 2000);
        var history = History(Message("assistant", text), Message("assistant", text));
        history[1] = Message("user", text);
        history.Insert(1, Message("user", "[DEEP MEMORY] " + new string('M', 5000)));
        var plan = ContextPruner.Create(history, ContextPruner.Mode.All);
        Assert.Same(history[1], plan.Messages[1]); Assert.Same(history[2], plan.Messages[2]);
    }

    [Fact]
    public void NoopAndSmallDuplicatesKeepSameHistoryReference()
    {
        var history = History(Message("assistant", "same"), Message("assistant", "same"));
        var plan = ContextPruner.Create(history, ContextPruner.Mode.All);
        Assert.Same(history, plan.Messages); Assert.Equal(0, plan.Blocks);
        Assert.Equal(0, plan.CharactersSaved);
    }
}


/// <summary>Real Microsoft.Agents.AI history, serialization and next-request proof; only model boundary is fake.</summary>
public class ContextPruneSessionTests
{
    private sealed class Client : Microsoft.Extensions.AI.IChatClient
    {
        public int Requests;
        public List<Microsoft.Extensions.AI.ChatMessage> Last = new();
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests++; Last = messages.ToList();
            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(ContextPrunerTests.Message("assistant", "ack")));
        }
        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++; Last = messages.ToList();
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "ack");
            await Task.CompletedTask;
        }
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }
    private static string CreateSandbox()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mux-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); return dir;
    }

    [Fact]
    public async Task Prune_ChangesNextActualRequestAndSerializedResumeButSnapshotRecoversOriginal()
    {
        var client = new Client();
        var agent = client.AsAIAgent(
            new Microsoft.Agents.AI.ChatClientAgentOptions { Name = "T", ChatOptions = new() { Instructions = "retain instructions" } });
        var session = await agent.CreateSessionAsync();
        string payload = "UNIQUE_BIG_TOOL " + new string('P', 5000);
        var history = ContextPrunerTests.History(ContextPrunerTests.Tool("call-1", "fetch_data", payload));
        session.SetInMemoryChatHistory(history);
        string sandbox = CreateSandbox();
        try
        {
            var result = await ContextPruneSession.ApplyAsync(agent, session, ContextPruner.Mode.Tools, sandbox, new[] { sandbox });
            Assert.Equal(0, client.Requests);
            Assert.Equal(1, result.Plan.Tools);
            Assert.NotNull(result.RecoveryPath);
            Assert.EndsWith(".muxprune", result.RecoveryPath);
            Assert.Empty(Directory.GetFiles(sandbox, "*.partial", SearchOption.AllDirectories));
            using var snapshot = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.RecoveryPath!));
            var originalSession = await agent.DeserializeSessionAsync(snapshot.RootElement);
            Assert.True(originalSession.TryGetInMemoryChatHistory(out var recovered));
            Assert.Contains(recovered!.SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionResultContent>(),
                r => r.Result!.ToString()!.Contains("UNIQUE_BIG_TOOL"));
            var serialized = await agent.SerializeSessionAsync(session);
            Assert.DoesNotContain("UNIQUE_BIG_TOOL", serialized.GetRawText());
            var resumed = await agent.DeserializeSessionAsync(serialized);
            await foreach (var _ in agent.RunStreamingAsync(new[] { ContextPrunerTests.Message("user", "continue") }, resumed)) { }
            Assert.Equal(1, client.Requests);
            var toolResult = Assert.Single(client.Last.SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionResultContent>());
            Assert.Equal("call-1", toolResult.CallId);
            Assert.Contains(ContextPruner.NoticePrefix, toolResult.Result!.ToString());
            Assert.Single(client.Last.SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionCallContent>());
            Assert.Equal(payload, Assert.IsType<Microsoft.Extensions.AI.FunctionResultContent>(history[3].Contents[0]).Result);
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public async Task ResumedMcpConvertedTextAndImageBlocks_PruneTextWithoutLosingImage()
    {
        var client = new Client();
        var agent = client.AsAIAgent(new Microsoft.Agents.AI.ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        var blocks = ModelContextProtocol.AIContentExtensions.ToAIContents(new ModelContextProtocol.Protocol.ContentBlock[]
        {
            new ModelContextProtocol.Protocol.TextContentBlock { Text = new string('M', 5000) },
            new ModelContextProtocol.Protocol.ImageContentBlock { Data = System.Text.Encoding.UTF8.GetBytes("AQID"), MimeType = "image/png" }
        }).ToList();
        session.SetInMemoryChatHistory(ContextPrunerTests.History(ContextPrunerTests.Tool("a", "mcp_read", blocks)));
        var serialized = await agent.SerializeSessionAsync(session);
        var resumed = await agent.DeserializeSessionAsync(serialized);
        Assert.True(resumed.TryGetInMemoryChatHistory(out var before));
        var beforeResult = Assert.IsType<FunctionResultContent>(before![3].Contents[0]);
        Assert.NotNull(beforeResult.Result);
        var plan = ContextPruner.Create(before, ContextPruner.Mode.Tools);
        Assert.True(plan.Tools == 1, System.Text.Json.JsonSerializer.Serialize(beforeResult.Result).Replace(new string('M', 5000), "<text>"));
        string original = System.Text.Json.JsonSerializer.Serialize(beforeResult.Result);
        string changed = System.Text.Json.JsonSerializer.Serialize(((FunctionResultContent)plan.Messages[3].Contents[0]).Result);
        Assert.Contains("AQID", original); Assert.Contains("AQID", changed);
        Assert.DoesNotContain(new string('M', 100), changed);
        Assert.Contains(ContextPruner.NoticePrefix, changed);
    }

    [Fact]
    public async Task RetainedToolCopyRemainsProtectedAcrossSerializeResumeAndSecondPrune()
    {
        var agent = new Client().AsAIAgent(new Microsoft.Agents.AI.ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        session.SetInMemoryChatHistory(ContextPrunerTests.History(ContextPrunerTests.Tool("a", "Filesystem_read_text_file", new string('A', 5000))
            .Concat(ContextPrunerTests.Tool("b", "Filesystem_read_text_file", new string('B', 5000))).ToArray()));
        string sandbox = CreateSandbox();
        try
        {
            var first = await ContextPruneSession.ApplyAsync(agent, session, ContextPruner.Mode.All, sandbox, new[] { sandbox });
            var resumed = await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session));
            var second = await ContextPruneSession.ApplyAsync(agent, resumed, ContextPruner.Mode.All, sandbox, new[] { sandbox });
            Assert.Equal(1, first.Plan.Stale); Assert.Equal(0, second.Plan.Blocks);
            Assert.Single(Directory.GetFiles(sandbox, "*.muxprune", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public async Task FailedBackupOrCancelledOperation_DoesNotMutateSessionOrCallModel()
    {
        var client = new Client();
        var agent = client.AsAIAgent( new Microsoft.Agents.AI.ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        var history = ContextPrunerTests.History(ContextPrunerTests.Tool("a", "fetch_data", new string('X', 5000)));
        session.SetInMemoryChatHistory(history);
        string sandbox = CreateSandbox();
        try
        {
            string before = (await agent.SerializeSessionAsync(session)).GetRawText();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ContextPruneSession.ApplyAsync(agent, session,
                ContextPruner.Mode.Tools, sandbox, new[] { Path.Combine(sandbox, "elsewhere") }));
            Assert.Equal(before, (await agent.SerializeSessionAsync(session)).GetRawText());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContextPruneSession.ApplyAsync(agent, session,
                ContextPruner.Mode.Tools, sandbox, new[] { sandbox }, new CancellationToken(true)));
            Assert.Equal(before, (await agent.SerializeSessionAsync(session)).GetRawText());
            Assert.Empty(Directory.GetFiles(sandbox, "*", SearchOption.AllDirectories));
            Assert.Equal(0, client.Requests);
        }
        finally { Directory.Delete(sandbox, true); }
    }

    [Fact]
    public async Task Noop_NeedsNoRecoveryPathAndKeepsSessionUntouched()
    {
        var client = new Client();
        var agent = client.AsAIAgent( new Microsoft.Agents.AI.ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        session.SetInMemoryChatHistory(ContextPrunerTests.History());
        string before = (await agent.SerializeSessionAsync(session)).GetRawText();
        var result = await ContextPruneSession.ApplyAsync(agent, session, ContextPruner.Mode.All, "", Array.Empty<string>());
        Assert.Equal(0, result.Plan.Blocks); Assert.Null(result.RecoveryPath); Assert.Equal(0, client.Requests);
        Assert.Equal(before, (await agent.SerializeSessionAsync(session)).GetRawText());
    }
}


[Collection("ConsoleState")]
public class ContextPruneScopeTests
{
    [Theory]
    [InlineData("/prune")]
    [InlineData("/prune\ttools")]
    [InlineData("/prune stale")]
    public async Task SharedUnsupportedModeDispatchHandlesPruneWithoutModelOrMenuHandoff(string command)
    {
        bool stdio = MuxConsole.StdioMode;
        var output = Console.Out;
        var pending = SingleAgentOrchestrator.PendingReplCommand;
        using var capture = new StringWriter();
        try
        {
            MuxConsole.StdioMode = true; Console.SetOut(capture);
            var result = await MetaCommandDispatch.TryHandleAsync(command);
            Assert.Equal(MetaCommandDispatch.Result.Handled, result);
            Assert.Equal(pending, SingleAgentOrchestrator.PendingReplCommand);
            Assert.Contains("idle single-agent", capture.ToString());
        }
        finally { MuxConsole.StdioMode = stdio; Console.SetOut(output); }
    }
}
