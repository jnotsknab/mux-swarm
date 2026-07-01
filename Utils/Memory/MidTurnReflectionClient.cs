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

    private async Task<List<ChatMessage>> WithDeltaAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var list = messages as List<ChatMessage> ?? messages.ToList();
        try
        {
            // Hybrid semantic+lexical delta. Runs at the tool-call cadence (seconds), so the Chroma
            // query - which overlaps the model's own reasoning/tool time - adds no felt latency.
            var delta = await ReflectionInjector.BuildDeltaAsync(_agentName, isLead: true, ct);
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

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => await base.GetResponseAsync(await WithDeltaAsync(messages, cancellationToken), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var withDelta = await WithDeltaAsync(messages, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(withDelta, options, cancellationToken))
            yield return update;
    }
}
