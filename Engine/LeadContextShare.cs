using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>
/// Builds the lead-context block handed to a sub-agent when context sharing is armed (/split)
/// AND the lead opts in per delegation (inheritLeadContext: true). The lead's in-memory session
/// history is rendered as one clearly-delimited, role-tagged text block that is PREPENDED to the
/// sub-task, so the sub-agent starts with what the lead already knows instead of re-running
/// lookups - and the lead does not have to hand-summarize its window into the prompt. Injection
/// is a delimited block (not replayed chat history): robust across providers and immune to
/// tool-call/result pairing invariants. Tail-biased capping keeps the MOST RECENT context when
/// the window exceeds the budget.
/// </summary>
internal static class LeadContextShare
{
    /// <summary>Character budget for the shared block (~35-40k tokens). Constant in v1.</summary>
    internal const int MaxChars = 150_000;

    internal const string BlockHeader = "=== LEAD CONTEXT (shared by the delegating lead; newest last) ===";
    internal const string BlockFooter = "=== END LEAD CONTEXT ===";
    internal const string OmissionMarker = "[... earlier lead context omitted to fit the sharing budget ...]";

    /// <summary>
    /// Render the lead's history to a delimited block. System messages are excluded (the
    /// sub-agent has its own persona; injecting the lead's would cross-wire roles). Tool calls
    /// and results are compacted to single bracketed lines. Returns "" for empty history.
    /// </summary>
    public static string BuildBlock(IEnumerable<ChatMessage>? history, int maxChars = MaxChars)
    {
        if (history is null) return "";
        var parts = new List<string>();
        foreach (var m in history)
        {
            if (m.Role == ChatRole.System) continue;
            var sb = new System.Text.StringBuilder();
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case TextContent t when !string.IsNullOrWhiteSpace(t.Text):
                        sb.AppendLine(t.Text.TrimEnd());
                        break;
                    case FunctionCallContent fc:
                        sb.AppendLine($"[tool call: {fc.Name}]");
                        break;
                    case FunctionResultContent fr:
                        string res = fr.Result?.ToString() ?? "";
                        if (res.Length > 4000) res = res[..4000] + " [...result truncated in shared context...]";
                        if (!string.IsNullOrWhiteSpace(res)) sb.AppendLine($"[tool result] {res.TrimEnd()}");
                        break;
                }
            }
            string body = sb.ToString().TrimEnd();
            if (body.Length == 0) continue;
            string role = m.Role == ChatRole.User ? "USER" : m.Role == ChatRole.Assistant ? "LEAD" : m.Role.Value.ToUpperInvariant();
            parts.Add($"## {role}\n{body}");
        }
        if (parts.Count == 0) return "";

        // Tail-biased assembly: walk newest-first until the budget is spent, then restore order.
        var kept = new List<string>();
        int used = 0;
        bool omitted = false;
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            int cost = parts[i].Length + 2;
            if (used + cost > maxChars && kept.Count > 0) { omitted = true; break; }
            if (used + cost > maxChars)
            {
                // A single over-budget newest part is tail-kept (most recent content wins).
                kept.Add(parts[i][^Math.Min(parts[i].Length, maxChars)..]);
                used = maxChars;
                omitted = i > 0;
                break;
            }
            kept.Add(parts[i]);
            used += cost;
        }
        kept.Reverse();
        var outSb = new System.Text.StringBuilder();
        outSb.AppendLine(BlockHeader);
        if (omitted) outSb.AppendLine(OmissionMarker);
        outSb.AppendLine(string.Join("\n\n", kept));
        outSb.Append(BlockFooter);
        return outSb.ToString();
    }

    /// <summary>Prepend a non-empty context block to a sub-task; identity when the block is empty.</summary>
    public static string WrapTask(string task, string contextBlock)
        => string.IsNullOrEmpty(contextBlock) ? task : contextBlock + "\n\nYOUR TASK:\n" + task;
}
