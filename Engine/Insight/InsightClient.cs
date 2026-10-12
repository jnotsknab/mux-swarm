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
                StampResult(result, level, list);
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

    /// <summary>
    /// Append the line to a tool result without changing its shape: a string (or a JSON string, which is what
    /// AIFunctionFactory returns) gets the text appended; a content-part list/array (MCP tools) gets an extra
    /// text part; anything else (structured JSON, images only, null) is left untouched for this call.
    /// Never stamps twice.
    /// </summary>
    private static void StampResult(FunctionResultContent result, InsightLevel level, IReadOnlyList<ChatMessage> list)
    {
        if (ContextPruner.ResultText(result).Any(p => p.Text.Contains(Marker, StringComparison.Ordinal))) return;
        switch (result.Result)
        {
            case string text when ContextGauge.Line(level, list) is { } line:
                result.Result = text + "\n\n" + line;
                break;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } json   // AIFunctionFactory strings
                when ContextGauge.Line(level, list) is { } line:
                result.Result = json.GetString() + "\n\n" + line;
                break;
            case IList<AIContent> parts when ContextGauge.Line(level, list) is { } line:
                var part = new TextContent("\n\n" + line);
                if (parts.IsReadOnly) result.Result = new List<AIContent>(parts) { part };   // arrays are fixed-size
                else parts.Add(part);
                break;
        }
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
