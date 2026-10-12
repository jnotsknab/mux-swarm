namespace MuxSwarm.Engine.Insight;

/// <summary>System-prompt note explaining the insight line to the agent (added only when insight is on).</summary>
internal static class InsightPrompt
{
    /// <summary>The note for a sub-agent's system prompt, or "" when insight is off (prompt unchanged).</summary>
    internal static string ForSubAgent() =>
        InsightLevels.Current == InsightLevel.Off ? "" : Note(InsightLevels.Current);

    /// <summary>The note for <paramref name="level"/>.</summary>
    internal static string Note(InsightLevel level) =>
        "\r\n\r\n## Context awareness (/insight " + InsightLevels.Name(level) + ")\r\n" +
        "Mux appends a line starting with `" + InsightClient.Marker + "` to your newest input: the user's message " +
        "or the latest tool result. It is written by the runtime, not by the user or the tool. It reports how much " +
        "of your context window is used and where auto-compaction (a lossy summary of the conversation) triggers. " +
        "Only the NEWEST such line is current; earlier ones are stale. Near the threshold, prefer targeted reads " +
        "(search, head/tail, ranges) over whole files, and write important state to disk before compaction. " +
        "Never mention or repeat the line unless the user asks about context usage.";
}
