using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>
/// Plain-text views of a session's history for recovery after compaction or /prune:
/// a greppable transcript (real newlines, role-labelled) and a deterministic tool-call manifest.
/// Built from the typed history by code, never by a model, so nothing in it is invented.
/// </summary>
internal static class SessionSnapshotText
{
    internal const int ManifestRecentCalls = 40;
    internal const int ManifestFiles = 30;
    internal const int ArgChars = 120;

    internal sealed record ToolCall(string Name, string CallId, string Args);

    /// <summary>Every tool call in order, with arguments flattened to one line.</summary>
    internal static List<ToolCall> ToolCalls(IEnumerable<ChatMessage> history) =>
        history.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Select(c => new ToolCall(c.Name, c.CallId ?? "", OneLine(ArgsText(c))))
            .ToList();

    /// <summary>Path-like values found in tool-call arguments (files touched or referenced), first-seen order.</summary>
    internal static List<string> Files(IEnumerable<ChatMessage> history)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var c in history.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
            foreach (Match m in PathLike.Matches(ArgsText(c)))
            {
                string p = m.Value.TrimEnd('.', ',', ';', ':', ')', '\'', '"');
                if (p.Length > 3 && seen.Add(p)) files.Add(p);
            }
        return files;
    }

    /// <summary>Section for a turn interrupted by mid-turn compaction: its messages are not committed to the
    /// session yet, so the goal and the tools it ran so far are listed explicitly. Empty when not mid-turn.</summary>
    internal static string InProgress(string? goal, IReadOnlyList<string>? toolNames)
    {
        if (string.IsNullOrWhiteSpace(goal) && (toolNames is null || toolNames.Count == 0)) return "";
        var sb = new StringBuilder();
        sb.AppendLine("[IN-PROGRESS TURN at compaction (not yet in the session history)]");
        if (!string.IsNullOrWhiteSpace(goal)) sb.AppendLine("Goal: " + Cut(OneLine(goal), 500));
        if (toolNames is { Count: > 0 })
            sb.AppendLine($"Tools run so far ({toolNames.Count}): " + string.Join(", ",
                toolNames.GroupBy(n => n, StringComparer.Ordinal).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)));
        sb.Append("[END IN-PROGRESS TURN]");
        return sb.ToString();
    }

    /// <summary>Full plain-text dump: manifest of every tool call, then the role-labelled transcript.
    /// <paramref name="inProgress"/> (see <see cref="InProgress"/>) is placed first when present.</summary>
    internal static string FullText(IReadOnlyList<ChatMessage> history, string? inProgress = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(inProgress)) sb.AppendLine(inProgress);
        var calls = ToolCalls(history);
        sb.AppendLine($"# Session snapshot: {history.Count} messages, {calls.Count} tool calls");
        sb.AppendLine("## Tool calls (name | callId | args)");
        foreach (var c in calls) sb.AppendLine($"- {c.Name} | {c.CallId} | {c.Args}");
        sb.AppendLine("## Files referenced");
        foreach (var f in Files(history)) sb.AppendLine("- " + f);
        sb.AppendLine("## Transcript");
        foreach (var m in history)
            foreach (var content in m.Contents)
            {
                switch (content)
                {
                    case TextContent t when !string.IsNullOrWhiteSpace(t.Text):
                        sb.AppendLine($"[{m.Role.Value}] {t.Text}");
                        break;
                    case FunctionCallContent fc:
                        sb.AppendLine($"[{m.Role.Value} -> tool {fc.Name} #{fc.CallId}] {ArgsText(fc)}");
                        break;
                    case FunctionResultContent fr:
                        sb.AppendLine($"[tool result #{fr.CallId}] {fr.Result}");
                        break;
                }
            }
        return sb.ToString();
    }

    /// <summary>Compact manifest appended to a compaction summary so the agent knows a detail exists
    /// before it searches the snapshot. Bounded: counts per tool, up to <see cref="ManifestFiles"/> files,
    /// the last <see cref="ManifestRecentCalls"/> calls (args cut to <see cref="ArgChars"/> chars).</summary>
    internal static string Manifest(IReadOnlyList<ChatMessage> history, string? textSnapshotPath)
    {
        var calls = ToolCalls(history);
        if (calls.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("[TOOL MANIFEST: generated from the session, not summarized]");
        sb.AppendLine("Calls by tool: " + string.Join(", ", calls.GroupBy(c => c.Name, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} x{g.Count()}")));
        var files = Files(history);
        if (files.Count > 0)
        {
            sb.AppendLine("Files referenced:");
            foreach (var f in files.Take(ManifestFiles)) sb.AppendLine("- " + f);
            if (files.Count > ManifestFiles) sb.AppendLine($"- ... {files.Count - ManifestFiles} more");
        }
        int skip = Math.Max(0, calls.Count - ManifestRecentCalls);
        sb.AppendLine(skip > 0 ? $"Last {ManifestRecentCalls} of {calls.Count} tool calls:" : "Tool calls:");
        foreach (var c in calls.Skip(skip))
            sb.AppendLine($"- {c.Name} #{c.CallId}: {Cut(c.Args, ArgChars)}");
        if (skip > 0)
            sb.AppendLine(textSnapshotPath is null
                ? $"({skip} earlier calls omitted.)"
                : $"({skip} earlier calls omitted; all calls are listed in {textSnapshotPath}.)");
        sb.Append("[END TOOL MANIFEST]");
        return sb.ToString();
    }

    private static readonly Regex PathLike = new(
        @"(?<![\w:/\\])(?:[A-Za-z]:[\\/]|\\\\|~/|\.{1,2}[\\/]|/(?=[^\s/]+/))[^\s""'<>|*?\r\n]+(?:[\\/][^\s""'<>|*?\r\n]+)*",
        RegexOptions.Compiled);

    private static string ArgsText(FunctionCallContent c) =>
        c.Arguments is null ? "" : string.Join(" ", c.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));

    private static string OneLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
