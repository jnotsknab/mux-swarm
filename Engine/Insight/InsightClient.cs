using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine.Insight;

/// <summary>
/// Stamps the insight line onto the newest message of every model request: appended to the last tool
/// result, or as an extra text part of the user message on a turn's first call. Built inside the
/// function-invocation middleware, so it runs on every model call of a turn.
///
/// The stamp mutates the message IN PLACE, so the agent session commits exactly what the model was sent
/// and the line becomes part of the replayed history. That keeps the provider prompt cache intact
/// (measured: same cached share as no line); a line that is sent but not saved, or that moves each call,
/// breaks the cache. Each message is stamped once; older lines stay as they were. Inert when insight is off.
/// </summary>
internal sealed class InsightClient(IChatClient inner, Func<InsightLevel> level) : DelegatingChatClient(inner)
{
    /// <summary>Prefix every insight line starts with (also how an already-stamped message is recognized).</summary>
    internal const string Marker = "[mux context:";

    /// <summary>Stamp the newest message (if not already stamped). Returns the messages to send.</summary>
    internal static IEnumerable<ChatMessage> Stamp(IEnumerable<ChatMessage> messages, InsightLevel level)
    {
        if (level == InsightLevel.Off) return messages;
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (list.Count == 0) return list;
        var last = list[^1];
        try
        {
            if (last.Contents.OfType<FunctionResultContent>().LastOrDefault() is { } result)
            {
                string text = result.Result?.ToString() ?? "";
                if (!text.Contains(Marker, StringComparison.Ordinal) && ContextGauge.Line(level, list) is { } line)
                    result.Result = text + "\n\n" + line;
            }
            else if (last.Role == ChatRole.User
                     && !last.Contents.OfType<TextContent>().Any(t => t.Text?.Contains(Marker, StringComparison.Ordinal) == true))
            {
                ContextGauge.PromptStarted(SingleAgentOrchestrator.SessionTokens);
                if (ContextGauge.Line(level, list) is { } line)
                    last.Contents.Add(new TextContent("\n\n" + line));
            }
        }
        catch { /* best effort: never fail a model call over the gauge */ }
        return list;
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Stamp(messages, level()), options, cancellationToken);

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(Stamp(messages, level()), options, cancellationToken);
}
