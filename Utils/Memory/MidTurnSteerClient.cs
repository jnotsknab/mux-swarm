using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils.Memory;

/// <summary>
/// A delegating chat client that splices a user's MID-TURN steer ("by the way ...", Ctrl+N) into the
/// live turn on its very next model round-trip - the same in-place-mutation mechanism the deep-memory
/// <see cref="MidTurnReflectionClient"/> uses, but sourced from the user and re-checked on EVERY
/// round-trip. It is built INSIDE the function-invocation middleware (via .Use(...)), so unlike the
/// reflection wrapper - which sits outside the FIC and fires once per user turn - this is entered on
/// each internal model&lt;-&gt;tool round-trip. The FIC reuses+extends the SAME List&lt;ChatMessage&gt;
/// across those round-trips, so appending here reaches the model on the current and every subsequent
/// round-trip of the turn. That gives the steer WITHIN-TURN reach: the agent sees the note before it
/// finishes, so a user can correct course or add a forgotten detail without cancelling the turn.
///
/// The steer is APPENDED as a user message (not a leading system note) so it reads as the user just
/// speaking - the most natural, authoritative placement for "stop doing X" / "also do Y". The agent
/// thread does not persist what a sub-client splices in, so the orchestrator records the steer into
/// its own conversation history separately for cross-turn durability. Inert (byte-identical
/// pass-through) whenever the steer queue is empty, which is the overwhelmingly common case.
/// </summary>
public sealed class MidTurnSteerClient : DelegatingChatClient
{
    private readonly Func<string?> _drain;
    private readonly Action<string>? _onInjected;

    /// <summary>Wrap <paramref name="inner"/> with mid-turn steer injection. <paramref name="drain"/>
    /// returns the combined pending steer text (or null when none is queued); <paramref name="onInjected"/>
    /// is invoked once per injected note so the orchestrator can record it into its durable history.</summary>
    public MidTurnSteerClient(IChatClient inner, Func<string?> drain, Action<string>? onInjected = null) : base(inner)
    {
        _drain = drain;
        _onInjected = onInjected;
    }

    private List<ChatMessage> WithSteer(IEnumerable<ChatMessage> messages)
    {
        var list = messages as List<ChatMessage> ?? messages.ToList();
        try
        {
            var steer = _drain();
            if (!string.IsNullOrEmpty(steer))
            {
                // Append as the latest user message: after any assistant/tool round-trip content the
                // FIC has accumulated, the model's next call sees the fresh user instruction last.
                list.Add(new ChatMessage(ChatRole.User, steer));
                try { _onInjected?.Invoke(steer); } catch { /* durability record is best-effort */ }
            }
        }
        catch { /* best-effort; fall through to the untouched messages */ }
        return list;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(WithSteer(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(WithSteer(messages), options, cancellationToken);
}
