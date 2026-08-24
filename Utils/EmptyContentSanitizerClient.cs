using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Delegating chat client that strips empty / whitespace-only <see cref="TextContent"/> parts from the
/// OUTBOUND message history just before it reaches the wire. Some providers (notably Kimi / Moonshot via
/// the CLIProxy OpenAI-compatible path) reject any message whose serialized content array contains an
/// empty text part with HTTP 400 "text content is empty".
///
/// The empty part is an artifact, not data: when a model streams a leading empty text delta before its
/// reasoning/answer (Kimi does this), Microsoft.Extensions.AI accumulates it into the assistant turn as an
/// empty <see cref="TextContent"/> alongside the real reply and (separately) a <see cref="TextReasoningContent"/>.
/// Turn 1 has no prior assistant turn so it succeeds; every SUBSEQUENT turn replays that assistant message
/// and 400s. Persisted session history keeps the original parts (reasoning is needed for resume/audit); we
/// only sanitize the copy we are about to send.
///
/// Contract:
///  - Only empty/whitespace-only <see cref="TextContent"/> parts are removed. All other content
///    (non-empty text, reasoning, function calls, function results, data) passes through verbatim.
///  - A message is never dropped and never mutated in place: only messages that actually change are
///    rebuilt as new <see cref="ChatMessage"/> instances (preserving role, author, id, and other contents);
///    unchanged messages pass through by reference, so the caller's live history is untouched.
///  - If stripping would leave a message with NO contents at all, one empty text part is retained so the
///    role/turn is preserved (this only arises for an assistant turn that was entirely empty text).
///
/// Provider-agnostic: harmless for endpoints that accept empty parts, so it is wired unconditionally.
/// </summary>
public sealed class EmptyContentSanitizerClient : DelegatingChatClient
{
    public EmptyContentSanitizerClient(IChatClient inner) : base(inner) { }

    private static bool IsEmptyText(AIContent content) =>
        content is TextContent tc && string.IsNullOrWhiteSpace(tc.Text);

    /// <summary>
    /// Returns a message list with empty text parts removed. Returns the SAME reference when nothing needed
    /// changing (the common case), so no allocation on clean histories.
    /// </summary>
    private static IEnumerable<ChatMessage> Sanitize(IEnumerable<ChatMessage> messages)
    {
        // Detect first whether any change is needed; avoid rebuilding the list if not.
        List<ChatMessage>? rebuilt = null;
        int index = 0;
        foreach (var msg in messages)
        {
            bool hasEmpty = false;
            foreach (var c in msg.Contents)
                if (IsEmptyText(c)) { hasEmpty = true; break; }

            if (hasEmpty)
            {
                // Materialize the untouched prefix on first change.
                if (rebuilt is null)
                {
                    rebuilt = new List<ChatMessage>();
                    int j = 0;
                    foreach (var prev in messages)
                    {
                        if (j >= index) break;
                        rebuilt.Add(prev);
                        j++;
                    }
                }

                var kept = new List<AIContent>(msg.Contents.Count);
                foreach (var c in msg.Contents)
                    if (!IsEmptyText(c)) kept.Add(c);

                // Never drop the whole message: preserve the turn with a single empty text part.
                if (kept.Count == 0) kept.Add(new TextContent(string.Empty));

                var clone = new ChatMessage(msg.Role, kept)
                {
                    AuthorName = msg.AuthorName,
                    MessageId = msg.MessageId,
                    AdditionalProperties = msg.AdditionalProperties,
                };
                rebuilt.Add(clone);
            }
            else
            {
                rebuilt?.Add(msg);
            }
            index++;
        }

        return rebuilt ?? messages;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Sanitize(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(Sanitize(messages), options, cancellationToken);
}
