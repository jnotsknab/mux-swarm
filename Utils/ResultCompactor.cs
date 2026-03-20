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
        // ── Tier 1: Structured fields (best case — already compact) ──
        //TODO: Maybe make this less strict as there should always be a completion summary in theory.
        if (!string.IsNullOrWhiteSpace(completionSummary))
        {
            var structured = new StringBuilder();
            structured.Append($"[{completionStatus ?? "success"}] ");
            structured.Append(completionSummary);

            if (!string.IsNullOrWhiteSpace(completionArtifacts))
                structured.Append($" | artifacts: {completionArtifacts}");

            if (structured.Length <= charBudget)
                return structured.ToString();

            rawResult = structured.ToString();
        }

        // Short results don't need compaction
        if (rawResult.Length <= charBudget)
            return rawResult;

        string extracted = ExtractTopLines(rawResult, charBudget);

        if (extracted.Length <= charBudget || chatClient == null)
            return extracted;

        return await LlmSummarizeAsync(chatClient, rawResult, charBudget, chatOptions);
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
        ChatOptions? chatOptions = null)
    {
        int charBudget = ExecutionLimits.Current.CompactionCharBudget;
        int maxMsgChars = ExecutionLimits.Current.CompactionMaxMessageChars;

        var transcript = new StringBuilder();
        foreach (var msg in history)
        {
            string role = msg.Role == ChatRole.User ? "User" : "Agent";
            string text = msg.Text ?? "";
            if (text.Length > maxMsgChars)
                text = text[..maxMsgChars] + "...";
            transcript.AppendLine($"[{role}]: {text}");
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
                    """),
                new(ChatRole.User, transcript.ToString())
            };

            var response = await chatClient.GetResponseAsync(messages, chatOptions);
            summary = response.Text ?? transcript.ToString();
        }
        catch
        {
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
        int totalChars = 0;

        foreach (var item in scored)
        {
            if (totalChars + item.Line.Length + 1 > charBudget)
            {
                if (selected.Count == 0)
                {
                    selected.Add((item.Line[..Math.Min(item.Line.Length, charBudget)], item.Index, item.Score));
                }
                break;
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
            string input = text.Length > 4000 ? text[..4000] + "\n[...truncated]" : text;

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
}
