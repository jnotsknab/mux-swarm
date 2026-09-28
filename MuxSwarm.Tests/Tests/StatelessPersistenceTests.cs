using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Regression coverage for the v0.14.2 /stateless fixes: /tag and /detach must never create a session
/// directory for a stateless session (it would surface as an empty session in the resume picker).
/// </summary>
public class StatelessPersistenceTests
{
    private sealed class NullChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static string SessionPath(string ts) => Path.Combine(PlatformContext.SessionsDirectory, ts);

    private static string NewTimestamp() => "stateless-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Tag_OnStatelessSession_CreatesNoSessionDirectory()
    {
        AIAgent agent = new NullChatClient().AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();
        string ts = NewTimestamp();

        await SingleAgentOrchestrator.HandleTagAsync("/tag release", ts, session, agent,
            "test-model", chatClientFactory: null, persistSession: false, CancellationToken.None);

        Assert.False(Directory.Exists(SessionPath(ts)));
    }

    [Fact]
    public async Task Detach_PersistsOnlyWhenStateful()
    {
        AIAgent agent = new NullChatClient().AsAIAgent(new ChatClientAgentOptions { Name = "T" });
        var session = await agent.CreateSessionAsync();

        string stateless = NewTimestamp();
        await SingleAgentOrchestrator.PersistUnlessStatelessAsync(false, agent, session, stateless);
        Assert.False(Directory.Exists(SessionPath(stateless)));

        // Positive control: the same call with persistence on writes exactly where the test looks.
        string stateful = NewTimestamp();
        try
        {
            await SingleAgentOrchestrator.PersistUnlessStatelessAsync(true, agent, session, stateful);
            Assert.True(File.Exists(Path.Combine(SessionPath(stateful), "agent_session.json")));
        }
        finally
        {
            try { Directory.Delete(SessionPath(stateful), recursive: true); } catch { /* best effort */ }
        }
    }
}
