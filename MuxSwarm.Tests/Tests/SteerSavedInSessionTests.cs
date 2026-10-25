using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Memory;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>A Ctrl+N steer reaches the model mid-turn through <see cref="MidTurnSteerClient"/>, but the agent framework
/// never commits a note a delegating client appended: the saved session lacked it, so after /resume the model saw
/// replies to a message that was not there. The orchestrator now restores each note right after the message it followed.</summary>
[Collection("ConsoleState")]
public sealed class SteerSavedInSessionTests
{
    // First call asks for a tool; the second (after the tool result, where the steer lands) answers with text.
    private sealed class ToolThenText : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (_calls++ == 0) yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "noop", new Dictionary<string, object?>())]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "done, with the code word");
        }
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private static async Task<(AIAgent Agent, AgentSession Session)> RunTurnWithSteerAsync(Action<IReadOnlyList<ChatMessage>, ChatMessage>? onInjected)
    {
        bool queued = false;
        var tool = AIFunctionFactory.Create(() => { queued = true; return "tool ok"; }, "noop");   // steer typed while the tool runs
        var client = new ToolThenText().AsBuilder().UseFunctionInvocation()
            .Use(inner => new MidTurnSteerClient(inner, () => { if (!queued) return null; queued = false; return "code word ZEBRA"; }, onInjected))
            .Build();
        var agent = client.AsAIAgent(new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [tool] } });
        var session = await agent.CreateSessionAsync();
        await foreach (var _ in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "go")], session)) { }
        return (agent, session);
    }

    private static List<(string Role, string Text)> Saved(JsonElement root) =>
        Common.ExtractMessagesFromSession(root).Select(m => (m.Role.Value, m.Text ?? "")).ToList();

    [Fact]
    public async Task Framework_DoesNotCommitTheSteerNote()
    {
        var (agent, session) = await RunTurnWithSteerAsync(null);
        Assert.DoesNotContain(Saved(await agent.SerializeSessionAsync(session)), m => m.Text.Contains("ZEBRA"));
    }

    [Fact]
    public async Task RestoredSteer_IsSaved_RightAfterTheToolResultItFollowed()
    {
        SingleAgentOrchestrator.TurnSteers.Take();
        var (agent, session) = await RunTurnWithSteerAsync(SingleAgentOrchestrator.RecordSteer);
        SingleAgentOrchestrator.RestoreTurnSteers(session);

        Assert.True(session.TryGetInMemoryChatHistory(out var history));
        int tool = history!.FindIndex(m => m.Role == ChatRole.Tool);
        int note = history.FindIndex(m => m.Role == ChatRole.User && (m.Text ?? "").Contains("ZEBRA"));
        Assert.True(tool >= 0 && note == tool + 1, string.Join(" | ", history.Select(m => $"{m.Role}:{m.Text}")));
        Assert.Equal(ChatRole.Assistant, history[note + 1].Role);   // the reply to the steer comes after it
        Assert.Single(Saved(await agent.SerializeSessionAsync(session)), m => m.Text.Contains("ZEBRA"));
        Assert.True(SingleAgentOrchestrator.TurnSteers.IsEmpty);

        SingleAgentOrchestrator.RestoreTurnSteers(session);   // nothing left to restore: no duplicate
        Assert.Single(Saved(await agent.SerializeSessionAsync(session)), m => m.Text.Contains("ZEBRA"));
    }

    [Fact]
    public void RestoreInto_PutsNotesAfterTheirAnchor_InOrder_AndAtTheEndWhenTheAnchorIsGone()
    {
        var a = new ChatMessage(ChatRole.Tool, "a");
        var b = new ChatMessage(ChatRole.Assistant, "b");
        var n1 = new ChatMessage(ChatRole.User, "n1");
        var n2 = new ChatMessage(ChatRole.User, "n2");
        var lost = new ChatMessage(ChatRole.User, "lost");
        var history = new List<ChatMessage> { a, b };
        int added = SteerLog.RestoreInto(history, [(a, n1), (a, n2), (new ChatMessage(ChatRole.Tool, "gone"), lost)]);
        Assert.Equal(3, added);
        Assert.Equal(["a", "n1", "n2", "b", "lost"], history.Select(m => m.Text));
        Assert.Equal(0, SteerLog.RestoreInto(history, [(a, n1)]));
    }
}
