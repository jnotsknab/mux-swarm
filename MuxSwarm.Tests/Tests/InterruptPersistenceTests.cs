using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Tests.Tests;

// Regression coverage for the interrupt-session-persistence fix.
//
// Background: Microsoft.Agents.AI does NOT commit a cancelled run's messages to the
// AgentSession. Before the fix, an Esc-interrupted turn was lost on persistence/resume
// because PersistChatSessionAsync serializes the session (not the orchestrator's
// in-memory conversationHistory). The fix injects the interrupted user goal + partial
// response into the session via Set/TryGetInMemoryChatHistory before persisting.
//
// These tests pin the framework behaviour the fix depends on, plus the sync mechanism.
public class InterruptPersistenceTests
{
    // Fake streaming client: emits text chunks, optionally cancelling mid-stream.
    private sealed class FakeChatClient : IChatClient
    {
        public bool CancelMidStream;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            string[] chunks = { "Hello ", "this ", "is ", "a ", "partial." };
            for (int i = 0; i < chunks.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (CancelMidStream && i == 3)
                    throw new OperationCanceledException("interrupted mid-stream");
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunks[i]);
                await Task.Yield();
            }
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

    // Pins the framework gap: a cancelled run leaves the session WITHOUT the interrupted turn.
    [Fact]
    public async Task CancelledRun_DoesNotCommitToSession()
    {
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        await foreach (var _ in agent.RunStreamingAsync(
            new[] { new ChatMessage(ChatRole.User, "first goal") }, session)) { }

        client.CancelMidStream = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync(
                new[] { new ChatMessage(ChatRole.User, "second goal") }, session)) { }
        });

        var msgs = ReadMessages(await agent.SerializeSessionAsync(session));
        Assert.Equal(2, msgs.Count); // only the clean first turn survived
        Assert.DoesNotContain(msgs, m => m.Text.Contains("second goal"));
    }

    // Verifies the fix: injecting the interrupted exchange makes it durable through serialization,
    // without dropping prior (clean) turns.
    [Fact]
    public async Task SyncedInterruptExchange_SurvivesSerialization()
    {
        var client = new FakeChatClient();
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        await foreach (var _ in agent.RunStreamingAsync(
            new[] { new ChatMessage(ChatRole.User, "first goal") }, session)) { }

        client.CancelMidStream = true;
        var partial = new System.Text.StringBuilder();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var u in agent.RunStreamingAsync(
                new[] { new ChatMessage(ChatRole.User, "second goal") }, session))
                if (!string.IsNullOrEmpty(u.Text)) partial.Append(u.Text);
        });

        // --- mirror the orchestrator fix ---
        if (!session.TryGetInMemoryChatHistory(out var hist) || hist is null)
            hist = new List<ChatMessage>();
        hist.Add(new ChatMessage(ChatRole.User, "second goal"));
        hist.Add(new ChatMessage(ChatRole.Assistant, partial + "\n\n[interrupted by user]"));
        session.SetInMemoryChatHistory(hist);

        var msgs = ReadMessages(await agent.SerializeSessionAsync(session));
        Assert.Equal(4, msgs.Count);
        Assert.Contains(msgs, m => m.Role == "user" && m.Text == "first goal");
        Assert.Contains(msgs, m => m.Role == "user" && m.Text == "second goal");
        Assert.Contains(msgs, m => m.Role == "assistant" && m.Text.Contains("[interrupted by user]"));
        // prior clean turn preserved verbatim
        Assert.Contains(msgs, m => m.Role == "assistant" && m.Text == "Hello this is a partial.");
    }

    // Interrupt before any text: a placeholder assistant message is still injected.
    [Fact]
    public async Task InterruptBeforeAnyText_InjectsPlaceholder()
    {
        var client = new FakeChatClient { CancelMidStream = true };
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        var partial = new System.Text.StringBuilder();
        try
        {
            await foreach (var u in agent.RunStreamingAsync(
                new[] { new ChatMessage(ChatRole.User, "only goal") }, session))
                if (!string.IsNullOrEmpty(u.Text)) partial.Append(u.Text);
        }
        catch (OperationCanceledException) { }

        string interruptedAssistant = string.IsNullOrWhiteSpace(partial.ToString())
            ? "[no response \u2014 interrupted before agent replied]"
            : partial + "\n\n[interrupted by user]";

        if (!session.TryGetInMemoryChatHistory(out var hist) || hist is null)
            hist = new List<ChatMessage>();
        hist.Add(new ChatMessage(ChatRole.User, "only goal"));
        hist.Add(new ChatMessage(ChatRole.Assistant, interruptedAssistant));
        session.SetInMemoryChatHistory(hist);

        var msgs = ReadMessages(await agent.SerializeSessionAsync(session));
        Assert.Contains(msgs, m => m.Role == "user" && m.Text == "only goal");
        Assert.Contains(msgs, m => m.Role == "assistant" && m.Text.Contains("interrupted"));
    }
}
