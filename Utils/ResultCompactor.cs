using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Intelligent result compaction for multi-agent context passing.
/// 
/// Strategy (in priority order):
///   1. STRUCTURED: If signal_task_complete fields exist, use status + summary + artifacts.
///      This is already maximally compact and high-signal. ~50-200 tokens.
///   2. EXTRACTIVE: Score each line/sentence by information density and keep the top-N
///      within a token budget. Prioritizes actionable content (paths, errors, statuses,
///      data) over filler (thinking, restating the task, pleasantries).
///   3. LLM SUMMARIZATION: If the result exceeds the budget even after extraction,
///      optionally call the LLM to compress. Costs one API call but produces the best
///      compaction for complex multi-paragraph results.
/// </summary>
public static class ResultCompactor
{
    // ── Configuration ────────────────────────────────────────────────────

    /// <summary>Approximate character budget for compacted results (not a hard limit).</summary>
    private const int DefaultCharBudget = 800;

    /// <summary>Lines shorter than this are likely noise.</summary>
    private const int MinLineLength = 10;

    // ── Scoring weights for extractive compaction ────────────────────────

    private static readonly (Regex Pattern, double Weight, string Label)[] _scoringRules =
    [
        // High value: status/result indicators
        (new Regex(@"\b(success|fail|error|warning|complete|created|updated|deleted|found|not found)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 3.0, "status"),

        // High value: file paths, URLs, identifiers
        (new Regex(@"[/\\][\w./-]+\.\w{1,5}\b", RegexOptions.Compiled), 2.5, "filepath"),
        (new Regex(@"https?://\S+", RegexOptions.Compiled), 2.0, "url"),

        // High value: structured data (JSON-like, key:value)
        (new Regex(@"[""']?\w+[""']?\s*[:=]\s*.+", RegexOptions.Compiled), 1.5, "kv-pair"),

        // Medium value: numbers, counts, measurements
        (new Regex(@"\b\d+(\.\d+)?\s*(files?|items?|rows?|bytes?|KB|MB|GB|ms|seconds?|errors?|warnings?|lines?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 2.0, "metric"),

        // Medium value: code/technical identifiers
        (new Regex(@"\b[A-Z][a-z]+[A-Z]\w+\b", RegexOptions.Compiled), 1.0, "camelCase"),
        (new Regex(@"`[^`]+`", RegexOptions.Compiled), 1.0, "code-ref"),

        // Low value / penalties: filler phrases
        (new Regex(@"\b(I will|I'll|Let me|Going to|Now I|Sure|Okay|Alright|Of course)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), -1.5, "filler"),
        
        // Penalty: restating the task
        (new Regex(@"\b(you asked|the task|the goal|the sub-task|my job is to)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), -2.0, "restate"),

        // High value: hashes / long hex identifiers (build SHAs, commit ids, checksums)
        (new Regex(@"\b[0-9a-fA-F]{7,64}\b", RegexOptions.Compiled), 2.5, "hash"),

        // High value: exceptions / stack-trace lines / explicit failures
        (new Regex(@"(Exception|Error|Traceback|stack trace|at [\w.]+\([^)]*\)|errno|exit code|non-zero)", RegexOptions.IgnoreCase | RegexOptions.Compiled), 3.0, "exception"),

        // High value: decisions / outcomes / next steps
        (new Regex(@"\b(decided|chose|because|result|outcome|conclusion|next step|TODO|FIXME|blocked|blocker|done|completed|verified|passing|failing|green|red)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 2.0, "decision"),

        // Medium value: markdown headings and code fences (structure anchors)
        (new Regex(@"^\s*(#{1,6}\s|```)", RegexOptions.Compiled), 1.0, "structure"),

        // Medium value: line/column references (e.g. file.cs:123)
        (new Regex(@"\b[\w./\\-]+:\d+\b", RegexOptions.Compiled), 1.5, "loc-ref"),
    ];

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Compacts an agent result for passing as context to another agent.
    /// Priority: structured fields → extractive → LLM fallback (if client provided).
    /// </summary>
    public static async Task<string> CompactAsync(
        string rawResult,
        string? completionStatus = null,
        string? completionSummary = null,
        string? completionArtifacts = null,
        int charBudget = DefaultCharBudget,
        IChatClient? chatClient = null,
        ChatOptions? chatOptions = null)
    {
        // ── Tier 1: Structured prefix — prepend summary to rawResult, then fall through ──
        if (!string.IsNullOrWhiteSpace(completionSummary))
        {
            var prefix = new StringBuilder();
            prefix.Append($"[{completionStatus ?? "success"}] ");
            prefix.Append(completionSummary);

            if (!string.IsNullOrWhiteSpace(completionArtifacts))
                prefix.Append($" | artifacts: {completionArtifacts}");

            rawResult = prefix + "\n\n" + rawResult;
        }

        // Short results don't need compaction
        if (rawResult.Length <= charBudget)
            return rawResult;

        // Resolve the configured compaction policy (swarm.json executionLimits.subAgentSummaryMode).
        string mode = (ExecutionLimits.Current.SubAgentSummaryMode ?? "auto").Trim().ToLowerInvariant();
        bool extractiveOnly = mode == "extractive";

        // Extractive pass is always computed: it is the whole answer in "extractive" mode, the
        // supplemental high-signal reference block in "auto"/"llm", and the fallback when no LLM
        // client is available.
        string extracted = ExtractTopLines(rawResult, charBudget);

        // No LLM available, or extractive-only mode requested -> return the extract.
        if (extractiveOnly || chatClient == null)
            return extracted;

        // "auto"/"llm": summarize, then APPEND the extracted references so concrete
        // paths/errors/identifiers the summary may have elided still reach the lead. The combined
        // output is capped to the budget (summary keeps the lion's share; references get whatever
        // headroom remains, with a floor so they are never squeezed to nothing).
        string summary = await LlmSummarizeAsync(chatClient, rawResult, charBudget, chatOptions);
        return MergeSummaryAndReferences(summary, extracted, charBudget);
    }

    /// <summary>
    /// Compacts a conversation history into a concise context summary for passing as context to another agent.
    /// </summary>
    /// <param name="history">
    ///     The conversation history to compact. Each message should contain a role and text content.
    /// </param>
    /// <param name="chatClient">
    ///     The chat client used to generate a condensed summary of the conversation. If the summarization fails, a fallback extractive method is used.
    /// </param>
    /// <param name="chatOptions"></param>
    /// <returns>
    /// A <see cref="ChatMessage"/> with the <see cref="ChatRole.User"/> role containing the compacted context summary wrapped with markers indicating the start and end of the summary.
    /// </returns>
    public static async Task<ChatMessage> CompactConversationAsync(
        IReadOnlyList<ChatMessage> history,
        IChatClient chatClient,
        ChatOptions? chatOptions = null,
        string? instruction = null)
    {
        int charBudget = ExecutionLimits.Current.CompactionCharBudget;
        int? maxContentPassed = App.SwarmConfig?.CompactionAgent?.ModelOpts?.MaxOutputTokens;

        var transcript = new StringBuilder();
        foreach (var msg in history)
        {
            string role = msg.Role == ChatRole.User ? "User" : "Agent";
            string text = msg.Text ?? "";
            if (text.Length > maxContentPassed)
                text = text[(Range)(..maxContentPassed)] + "...";
            transcript.AppendLine($"[{role}]: {text}");
        }

        // Honor the configured compaction policy: "extractive" never calls the LLM.
        string mode = (ExecutionLimits.Current.SubAgentSummaryMode ?? "auto").Trim().ToLowerInvariant();
        if (mode == "extractive")
        {
            var extractiveOnly = ExtractTopLines(transcript.ToString(), charBudget);
            return new ChatMessage(ChatRole.User,
                $"[CONTEXT SUMMARY — extractive]\n{extractiveOnly}\n[END SUMMARY — continue from here]");
        }

        string summary;
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    $"""
                    Compress the following conversation into a structured context summary under {charBudget} characters.
                    
                    MUST preserve:
                    - All key decisions made and their reasoning
                    - File paths, artifact locations, and outputs produced
                    - Current state of work and next steps discussed
                    - User preferences, constraints, and corrections expressed
                    - Technical details: model names, config values, tool names, error messages
                    - Any unresolved issues or open questions
                    
                    Drop: greetings, pleasantries, reasoning chains, verbose tool call details, repeated information, markdown formatting.
                    
                    Format as a structured summary with labeled sections. Output ONLY the summary wrapped in:
                    [CONTEXT SUMMARY — prior conversation compacted]
                    ...content...
                    [END SUMMARY — continue from here]
                    """
                    + (string.IsNullOrWhiteSpace(instruction)
                        ? string.Empty
                        : $"\n\nAdditional instruction from the user: {instruction.Trim()}")),
                new(ChatRole.User, transcript.ToString())
            };

            var response = await chatClient.GetResponseAsync(messages, chatOptions);
            summary = response.Text ?? transcript.ToString();

            var extracted = ExtractTopLines(transcript.ToString(), charBudget / 2);
            summary += $"\n\n[EXTRACTED REFERENCES]\n{extracted}\n[END REFERENCES]";

        }
        catch (Exception ex)
        {
            MuxConsole.WriteMuted($"  [Compaction] LLM summary failed, using extractive fallback: {ex.Message}");
            summary = ExtractTopLines(transcript.ToString(), charBudget);
        }

        return new ChatMessage(ChatRole.User, summary);
    }

    /// <summary>
    /// Selects and extracts the highest-priority lines from the text to fit within a specified character budget.
    /// Lines are scored based on relevance, with preference given to earlier lines and lines meeting a minimum length threshold.
    /// </summary>
    /// <param name="text">The text content to extract lines from.</param>
    /// <param name="charBudget">The maximum number of characters allowed in the returned string.</param>
    /// <returns>A condensed string containing the selected lines that fit within the character budget, including a suffix indicating the number of omitted lines if any lines were dropped.</returns>
    private static string ExtractTopLines(string text, int charBudget)
    {
        var lines = text.Split('\n')
            .Select((line, index) => (Line: line.Trim(), Index: index))
            .Where(x => x.Line.Length >= MinLineLength)
            .ToList();

        if (lines.Count == 0)
            return text.Length > charBudget ? text[..charBudget] + "..." : text;

        var scored = lines.Select(x => (
            x.Line,
            x.Index,
            Score: ScoreLine(x.Line, x.Index, lines.Count)
        )).OrderByDescending(x => x.Score).ToList();

        var selected = new List<(string Line, int Index, double Score)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalChars = 0;

        foreach (var item in scored)
        {
            // Skip exact-duplicate lines so repeated content does not consume the budget twice.
            if (!seen.Add(item.Line))
                continue;

            if (totalChars + item.Line.Length + 1 > charBudget)
            {
                if (selected.Count == 0)
                {
                    selected.Add((item.Line[..Math.Min(item.Line.Length, charBudget)], item.Index, item.Score));
                }
                // Keep scanning: a later, shorter high-signal line may still fit the remaining budget.
                if (totalChars >= charBudget) break;
                continue;
            }

            selected.Add(item);
            totalChars += item.Line.Length + 1;
        }

        var ordered = selected.OrderBy(x => x.Index).Select(x => x.Line);

        int droppedLines = lines.Count - selected.Count;
        var result = string.Join("\n", ordered);

        if (droppedLines > 0)
            result += $"\n[...{droppedLines} lower-priority lines omitted]";

        return result;
    }

    /// <summary>
    /// Scores a single line based on information density heuristics.
    /// </summary>
    private static double ScoreLine(string line, int lineIndex, int totalLines)
    {
        double score = 1.0;

        foreach (var (pattern, weight, _) in _scoringRules)
        {
            if (pattern.IsMatch(line))
                score += weight;
        }

        if (lineIndex <= 2)
            score += 1.0;
        if (lineIndex >= totalLines - 3)
            score += 1.5;

        if (line.Length > 300)
            score -= 1.0;
        if (line.Length > 500)
            score -= 1.5;

        if (Regex.IsMatch(line, @"^[\s]*[-•*]\s"))
            score += 0.5;
        if (Regex.IsMatch(line, @"^[\s]*\d+[.)]\s"))
            score += 0.5;

        return score;
    }


    private static async Task<string> LlmSummarizeAsync(
        IChatClient chatClient, string text, int charBudget, ChatOptions? chatOptions = null)
    {
        try
        {
            int? max = App.SwarmConfig?.CompactionAgent?.ModelOpts?.MaxOutputTokens;
            string input = text.Length > max ? text[(Range)(..max)] + "\n[...truncated]" : text;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    $"""
                    You are a compaction engine. Summarize the following agent result into 
                    a concise report under {charBudget} characters. Preserve:
                    - Final status (success/failure/partial)
                    - Key outputs (file paths, URLs, identifiers, data values)
                    - Errors or warnings
                    - Any decisions made or actions taken
                    
                    Drop: thinking/reasoning, task restatement, pleasantries, tool call details.
                    Output ONLY the summary, no preamble.
                    """),
                new(ChatRole.User, input)
            };

            var response = await chatClient.GetResponseAsync(messages, chatOptions);
            string summary = response.Text ?? "";

            return string.IsNullOrWhiteSpace(summary)
                ? ExtractTopLines(text, charBudget)
                : summary;
        }
        catch
        {
            return ExtractTopLines(text, charBudget);
        }
    }

    // ── Summary + references merge ────────────────────────────────────────

    /// <summary>
    /// Combines an LLM summary with a signal-scored extractive reference block, capped to the
    /// char budget. The summary is primary; the reference block is appended with whatever budget
    /// remains (down to a minimum floor) so concrete artifacts (paths, errors, identifiers) the
    /// summary may have dropped still reach the consuming agent. Deduplicates reference lines that
    /// already appear verbatim in the summary to avoid wasting budget on repeats.
    /// </summary>
    private static string MergeSummaryAndReferences(string summary, string extracted, int charBudget)
    {
        summary = (summary ?? string.Empty).TrimEnd();

        if (string.IsNullOrWhiteSpace(summary))
            return extracted;
        if (string.IsNullOrWhiteSpace(extracted))
            return summary;

        // Drop reference lines whose content is already present in the summary text.
        var refLines = extracted.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Trim().Length > 0 && !summary.Contains(l.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (refLines.Count == 0)
            return summary.Length > charBudget ? summary[..charBudget] : summary;

        const string head = "\n\n[EXTRACTED REFERENCES]\n";
        const string tail = "\n[END REFERENCES]";

        // Budget left for the reference block after the summary + framing. Keep a floor so the
        // references are never squeezed entirely away (they are the high-signal part for the lead).
        int floor = Math.Min(400, charBudget / 4);
        int remaining = charBudget - summary.Length - head.Length - tail.Length;
        int refBudget = Math.Max(floor, remaining);

        var sb = new StringBuilder();
        int used = 0;
        foreach (var line in refLines)
        {
            if (used + line.Length + 1 > refBudget)
                break;
            sb.Append(line).Append('\n');
            used += line.Length + 1;
        }
        string refBlock = sb.ToString().TrimEnd('\n');
        if (refBlock.Length == 0)
            return summary;

        return summary + head + refBlock + tail;
    }
}
