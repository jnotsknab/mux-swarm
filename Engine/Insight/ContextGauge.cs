using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine.Insight;

/// <summary>
/// The lead session's context numbers as one line of text for the agent (<c>/insight</c>). Reads the same
/// authoritative values as the footer and <c>/api/status</c> (<see cref="SingleAgentOrchestrator.SessionTokens"/>
/// against <see cref="SingleAgentOrchestrator.AutoCompactThreshold"/>), so every view agrees.
/// </summary>
internal static class ContextGauge
{
    private static readonly object Gate = new();
    private static uint _promptBaseline, _lastPromptDelta;

    /// <summary>Percent of <paramref name="threshold"/> used by <paramref name="tokens"/> (0 when there is no threshold).</summary>
    internal static double Percent(uint tokens, int threshold) => threshold > 0 ? (double)tokens / threshold * 100.0 : 0;

    /// <summary>
    /// A new prompt (user message) is being sent: the context growth since the previous prompt becomes the
    /// "prev turn" figure. Compaction shrinks the total, which counts as no growth.
    /// </summary>
    internal static void PromptStarted(uint tokens)
    {
        lock (Gate)
        {
            _lastPromptDelta = tokens > _promptBaseline ? tokens - _promptBaseline : 0;
            _promptBaseline = tokens;
        }
    }

    /// <summary>
    /// The insight line for <paramref name="level"/>, or null when off. <paramref name="messages"/> (the request
    /// being sent) is only read at full and above, for the share of context held by tool output.
    /// </summary>
    internal static string? Line(InsightLevel level, IReadOnlyList<ChatMessage>? messages = null) =>
        Line(level, SingleAgentOrchestrator.SessionTokens, SingleAgentOrchestrator.AutoCompactThreshold, messages);

    /// <summary>The insight line for explicit numbers (see <see cref="Line(InsightLevel, IReadOnlyList{ChatMessage}?)"/>).</summary>
    internal static string? Line(InsightLevel level, uint tokens, int threshold, IReadOnlyList<ChatMessage>? messages)
    {
        if (level == InsightLevel.Off) return null;
        string line = threshold > 0
            ? $"[mux context: {K(tokens)}/{K((uint)threshold)} ({Percent(tokens, threshold):0}%), auto-compact at {K((uint)threshold)}"
            : $"[mux context: {K(tokens)}, auto-compact off";
        if (level >= InsightLevel.Full)
        {
            uint delta;
            lock (Gate) delta = _lastPromptDelta;
            line += $" | prev turn +{K(delta)}";
            if (messages is not null) line += $" | tool output {ToolShare(messages):0}%";
            line += $" | compactions {CostLedger.Snapshot().Sum(r => r.SessCompactions)}";
        }
        return line + "]";
    }

    /// <summary>Percent of the request's text characters that are tool results.</summary>
    internal static double ToolShare(IReadOnlyList<ChatMessage> messages)
    {
        long tool = 0, all = 0;
        foreach (var content in messages.SelectMany(m => m.Contents))
        {
            long n = content switch
            {
                TextContent t => t.Text?.Length ?? 0,
                FunctionResultContent r => ContextPruner.ResultText(r).Sum(p => (long)p.Text.Length),
                FunctionCallContent c => c.Arguments?.Sum(a => a.Key.Length + (a.Value?.ToString()?.Length ?? 0)) ?? 0,
                _ => 0,
            };
            all += n;
            if (content is FunctionResultContent) tool += n;
        }
        return all > 0 ? tool * 100.0 / all : 0;
    }

    /// <summary>Token count as "46.7k" / "950".</summary>
    internal static string K(uint tokens) => tokens >= 1000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString();
}
