using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils.Memory;

/// <summary>
/// A delegating chat client that injects freshly-gathered deep-memory reflections MID-TURN. Wrapped
/// INSIDE the function-invocation middleware (added after UseFunctionInvocation in the builder), so
/// the inner GetResponse/GetStreamingResponse is invoked once per model<->tool round-trip - giving a
/// natural, frequent injection point right after each tool result, without waiting for the next user
/// turn. On each call it prepends only the NOT-YET-INJECTED reflections (ReflectionInjector.BuildDelta,
/// token-capped by injectTokenBudget); the per-session injected-id set makes repeat round-trips with
/// no new memory a no-op. Lead session only, deep mode only - otherwise byte-identical pass-through.
/// </summary>
public sealed class MidTurnReflectionClient : DelegatingChatClient
{
    private readonly string _agentName;

    public MidTurnReflectionClient(IChatClient inner, string agentName) : base(inner)
        => _agentName = agentName;

    private List<ChatMessage> WithDelta(IEnumerable<ChatMessage> messages)
    {
        var list = messages as List<ChatMessage> ?? messages.ToList();
        try
        {
            var delta = ReflectionInjector.BuildDelta(_agentName, isLead: true);
            if (!string.IsNullOrEmpty(delta))
            {
                // New list so we never mutate the caller's collection; prepend as a system note.
                var injected = new List<ChatMessage>(list.Count + 1)
                {
                    new(ChatRole.System, delta)
                };
                injected.AddRange(list);
                return injected;
            }
        }
        catch { /* best-effort; fall through to the untouched messages */ }
        return list;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(WithDelta(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(WithDelta(messages), options, cancellationToken);
}
