using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils.Memory;

/// <summary>
/// A delegating chat client that surfaces freshly-gathered deep-memory reflections MID-TURN - the
/// EPHEMERAL tier of the two-tier injection model. It wraps the already-built function-invocation
/// client (App.CreateChatClient wraps it OUTSIDE UseFunctionInvocation), so GetResponse is entered
/// once per user turn - but it mutates the run's shared message list IN PLACE, and the function-
/// invocation middleware reuses+extends that SAME list across every internal model<->tool round-trip.
/// Verified by live probe: an in-place insert on the single outer call is visible to the model on
/// EVERY subsequent round-trip of the turn. This gives WITHIN-TURN reach only - the agent thread does
/// NOT persist messages a sub-client splices in (also probe-verified), so this injection evaporates at
/// the turn boundary. That is intentional: durability is the ORCHESTRATOR's job (it prepends
/// ReflectionInjector.BuildDurableDeltaAsync into the messages the agent records, which DO replay every
/// turn). Here we call BuildDeltaAsync, which tracks the per-turn EPHEMERAL id set (distinct from the
/// durable set) - so the same reflection can be surfaced live this turn AND then made durable next turn
/// with no duplication. Lead session only, deep mode only - otherwise byte-identical pass-through.
/// </summary>
public sealed class MidTurnReflectionClient : DelegatingChatClient
{
    private readonly string _agentName;

    public MidTurnReflectionClient(IChatClient inner, string agentName) : base(inner)
        => _agentName = agentName;

    private async Task<List<ChatMessage>> WithDeltaAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        // EPHEMERAL tier: we wrap the built function-invocation client (App wraps us OUTSIDE it),
        // so this runs once per turn - but the FIC reuses+extends the SAME List<ChatMessage> across
        // every internal model<->tool round-trip, so an in-place insert reaches the model on all of
        // them (within-turn). The agent thread does NOT persist what we splice in, so this evaporates
        // at the turn boundary by design - the orchestrator's BuildDurableDeltaAsync handles cross-turn
        // persistence. BuildDeltaAsync marks the per-turn ephemeral id set only. If the caller ever
        // hands us a non-List (defensive), we materialize one for this call.
        var list = messages as List<ChatMessage> ?? messages.ToList();
        try
        {
            // Hybrid semantic+lexical delta. Runs at the tool-call cadence (seconds), so the Chroma
            // query - which overlaps the model's own reasoning/tool time - adds no felt latency.
            var delta = await ReflectionInjector.BuildDeltaAsync(_agentName, isLead: true, ct);
            if (!string.IsNullOrEmpty(delta))
            {
                // Insert as a system note. Place after any leading system message(s)
                // (the preamble) so tool/user ordering downstream is preserved, and so
                // the reflection sits alongside the standing system context.
                int at = 0;
                while (at < list.Count && list[at].Role == ChatRole.System) at++;
                list.Insert(at, new ChatMessage(ChatRole.System, delta));
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
