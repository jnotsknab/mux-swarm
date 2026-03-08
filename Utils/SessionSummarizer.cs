using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuxSwarm.Utils;

/// <summary>
/// Ports the Python summarize_session.py logic to C#.
/// Reads session JSON files and produces a markdown summary
/// suitable for injection into the orchestrator system prompt.
/// </summary>
public static class SessionSummarizer
{
    // ── Artifact Patterns ────────────────────────────────────────────────

    private static IEnumerable<Regex> GetArtifactPatterns()
    {
        foreach (var path in App.Config.Filesystem.AllowedPaths ?? [])
        {
            var escaped = Regex.Escape(path);
            yield return new Regex($@"{escaped}[^\s""',)]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "filesystem_write_file",
        "filesystem_create_directory",
        "filesystem_move_file",
    };

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a rolling session context string from the N most recent session
    /// directories, suitable for injection into the orchestrator system prompt.
    /// </summary>
    /// <param name="sessionsPath">Root directory containing timestamped session folders.</param>
    /// <param name="count">Number of most recent sessions to include.</param>
    public static string BuildRollingContext(string sessionsPath, int count = 3)
    {
        if (!Directory.Exists(sessionsPath))
            return string.Empty;

        var sessionDirs = Directory.GetDirectories(sessionsPath)
            .OrderByDescending(d => d)
            .Take(count)
            .ToList();

        if (sessionDirs.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Recent Session Context");
        sb.AppendLine($"_Last {sessionDirs.Count} session(s) — newest first. Use for orientation only._");
        sb.AppendLine();

        for (int i = 0; i < sessionDirs.Count; i++)
        {
            string dir = sessionDirs[i];
            string timestamp = Path.GetFileName(dir);
            string summary = SummarizeSession(dir);

            sb.AppendLine(new string('=', 60));
            sb.AppendLine($"### Session {i + 1} of {sessionDirs.Count}: {timestamp}");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine(summary);
            sb.AppendLine();
        }

        sb.AppendLine(new string('=', 60));
        sb.AppendLine("### End of Session Context");
        sb.AppendLine(new string('=', 60));

        return sb.ToString();
    }

    /// <summary>
    /// Summarizes a single session directory into markdown.
    /// </summary>
    /// <param name="sessionDirPath">Path to a single timestamped session directory.</param>
    public static string SummarizeSession(string sessionDirPath)
    {
        var sessionFiles = Directory.GetFiles(sessionDirPath, "*_session.json", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        if (sessionFiles.Count == 0)
            return "No session files found.";

        string sessionType = sessionFiles.Count > 1 ? "swarm" : "chat";
        var lines = new List<string>
        {
            $"**Type:** {sessionType} | **Directory:** `{sessionDirPath}`",
            ""
        };

        foreach (string sessionFile in sessionFiles)
        {
            JsonDocument? doc = LoadJson(sessionFile);
            if (doc == null) continue;

            string agentName = Path.GetFileNameWithoutExtension(sessionFile)
                .Replace("_session", "", StringComparison.OrdinalIgnoreCase);

            var messages = doc.RootElement
                .TryGetProperty("chatHistoryProviderState", out var state) &&
                state.TryGetProperty("messages", out var msgs) &&
                msgs.ValueKind == JsonValueKind.Array
                    ? msgs.EnumerateArray().ToList()
                    : [];

            var toolCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var delegatedAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var artifacts = new HashSet<string>();
            string? lastOutcome = null;
            string? lastAssistantText = null;
            string? lastDelegatedTo = null;
            int lastDelegationIdx = -1;
            int lastAssistantTextIdx = -1;
            int msgIdx = 0;
            var pendingWriteCalls = new HashSet<string>();

            foreach (var message in messages)
            {
                string role = message.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                var contents = message.TryGetProperty("contents", out var c) &&
                               c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().ToList()
                    : [];

                // Capture last assistant text for fallback outcome
                if (role == "assistant")
                {
                    var chunks = contents
                        .Where(c => c.TryGetProperty("$type", out var t) && t.GetString() == "text")
                        .Select(c =>
                        {
                            if (c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                                return t.GetString();
                            if (c.TryGetProperty("content", out var co) && co.ValueKind == JsonValueKind.String)
                                return co.GetString();
                            return null;
                        })
                        .Where(s => s != null)
                        .ToList();

                    if (chunks.Count > 0)
                    {
                        lastAssistantText = string.Join(" ", chunks).Trim();
                        lastAssistantTextIdx = msgIdx;
                    }
                }

                msgIdx++;

                foreach (var content in contents)
                {
                    string ctype = content.TryGetProperty("$type", out var ct) ? ct.GetString() ?? "" : "";

                    if (ctype == "functionCall")
                    {
                        string name = content.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        string callId = content.TryGetProperty("callId", out var ci) ? ci.GetString() ?? "" : "";

                        if (!string.IsNullOrEmpty(name))
                        {
                            toolCalls[name] = toolCalls.GetValueOrDefault(name) + 1;

                            if (name == "delegate_to_agent")
                            {
                                string? agent = ParseAgentName(content);
                                if (agent != null)
                                {
                                    delegatedAgents.Add(agent);
                                    lastDelegatedTo = agent;
                                    lastDelegationIdx = msgIdx;
                                }
                            }

                            if (name == "signal_task_complete")
                            {
                                string? outcome = ParseSignalOutcome(content);
                                if (outcome != null)
                                    lastOutcome = outcome;
                            }

                            if (IsWriteTool(name) && !string.IsNullOrEmpty(callId))
                                pendingWriteCalls.Add(callId);
                        }
                    }
                    else if (ctype == "functionResult")
                    {
                        string callId = content.TryGetProperty("callId", out var ci) ? ci.GetString() ?? "" : "";
                        if (pendingWriteCalls.Contains(callId))
                        {
                            foreach (string s in ExtractStrings(content))
                                foreach (string artifact in FindArtifacts(s))
                                    artifacts.Add(artifact);

                            pendingWriteCalls.Remove(callId);
                        }
                    }
                }
            }

            // Fallback: pull artifact paths from signal_task_complete summary
            if (artifacts.Count == 0 && lastOutcome != null)
                foreach (string artifact in FindArtifacts(lastOutcome))
                    artifacts.Add(artifact);

            string finalOutcome = lastOutcome
                ?? (lastDelegatedTo != null && lastDelegationIdx > lastAssistantTextIdx
                    ? $"Delegated to: {lastDelegatedTo}"
                    : lastAssistantText ?? "No outcome captured.");

            if (toolCalls.Count == 0 && artifacts.Count == 0 && lastOutcome == null)
                continue;

            lines.Add($"#### Agent: {agentName}");
            lines.Add($"- Tool calls: {FormatToolCalls(toolCalls)}");
            lines.Add($"- Delegated agents: {FormatSet(delegatedAgents)}");
            lines.Add($"- Artifacts: {FormatSet(artifacts)}");
            lines.Add($"- Final outcome: {finalOutcome}");
            lines.Add("");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Generates a detailed, human-readable markdown report for a single session directory.
    /// Unlike SummarizeSession, this preserves the full conversation flow including
    /// tool arguments, results, and assistant reasoning.
    /// </summary>
    public static string GenerateDetailedReport(string sessionDirPath)
    {
        var sessionFiles = Directory.GetFiles(sessionDirPath, "*_session.json", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        if (sessionFiles.Count == 0)
            return "No session files found.";

        string sessionType = sessionFiles.Count > 1 ? "Swarm" : "Single Agent";
        string timestamp = Path.GetFileName(sessionDirPath);

        var sb = new StringBuilder();
        sb.AppendLine($"# Session Report: {timestamp}");
        sb.AppendLine($"**Type:** {sessionType} | **Agents:** {sessionFiles.Count}");
        sb.AppendLine();

        foreach (string sessionFile in sessionFiles)
        {
            JsonDocument? doc = LoadJson(sessionFile);
            if (doc == null) continue;

            string agentName = Path.GetFileNameWithoutExtension(sessionFile)
                .Replace("_session", "", StringComparison.OrdinalIgnoreCase);

            var messages = doc.RootElement
                .TryGetProperty("chatHistoryProviderState", out var state) &&
                state.TryGetProperty("messages", out var msgs) &&
                msgs.ValueKind == JsonValueKind.Array
                    ? msgs.EnumerateArray().ToList()
                    : [];

            if (messages.Count == 0) continue;

            sb.AppendLine($"## Agent: {agentName}");
            sb.AppendLine($"**Messages:** {messages.Count}");
            sb.AppendLine();

            int turnNumber = 0;

            foreach (var message in messages)
            {
                string role = message.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                string author = message.TryGetProperty("authorName", out var a) ? a.GetString() ?? "" : "";
                string createdAt = message.TryGetProperty("createdAt", out var ts) ? ts.GetString() ?? "" : "";

                var contents = message.TryGetProperty("contents", out var c) &&
                               c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().ToList()
                    : [];

                string roleLabel = role switch
                {
                    "user" => "User",
                    "assistant" => author.Length > 0 ? author : "Assistant",
                    "tool" => "Tool Result",
                    _ => role
                };

                if (role == "user") turnNumber++;

                sb.AppendLine($"### [{roleLabel}]{(createdAt.Length > 0 ? $" — {createdAt}" : "")}");

                foreach (var content in contents)
                {
                    string ctype = content.TryGetProperty("$type", out var ct) ? ct.GetString() ?? "" : "";

                    switch (ctype)
                    {
                        case "text":
                            {
                                string text = content.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                                if (string.IsNullOrWhiteSpace(text)) continue;

                                // Trim injected system preamble for readability
                                if (role == "user" && text.Contains("## Filesystem Write Rules"))
                                {
                                    int subTaskIdx = text.IndexOf("Sub-task:", StringComparison.OrdinalIgnoreCase);
                                    if (subTaskIdx >= 0)
                                        text = text[subTaskIdx..];
                                    else
                                        text = "*[System preamble omitted]*\n" + text;
                                }

                                sb.AppendLine(text.Trim());
                                break;
                            }
                        case "functionCall":
                            {
                                string name = content.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                string args = content.TryGetProperty("arguments", out var ar)
                                    ? FormatJson(ar)
                                    : "";

                                sb.AppendLine($"**Tool Call:** `{name}`");
                                if (!string.IsNullOrWhiteSpace(args))
                                {
                                    sb.AppendLine("```json");
                                    sb.AppendLine(args);
                                    sb.AppendLine("```");
                                }
                                break;
                            }
                        case "functionResult":
                            {
                                string resultText = ExtractResultText(content);
                                sb.AppendLine($"**Result:**");
                                sb.AppendLine("```");
                                sb.AppendLine(resultText.Trim());
                                sb.AppendLine("```");
                                break;
                            }
                    }
                }

                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extracts readable text from a functionResult content block.
    /// </summary>
    private static string ExtractResultText(JsonElement content)
    {
        // Check for nested result.content[].text pattern
        if (content.TryGetProperty("result", out var result))
        {
            if (result.ValueKind == JsonValueKind.String)
                return result.GetString() ?? "";

            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("content", out var contentArray) &&
                contentArray.ValueKind == JsonValueKind.Array)
            {
                return string.Join("\n", contentArray.EnumerateArray()
                    .Where(e => e.TryGetProperty("text", out _))
                    .Select(e => e.GetProperty("text").GetString() ?? ""));
            }

            // Fallback: result.structuredContent.content
            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("structuredContent", out var sc) &&
                sc.TryGetProperty("content", out var scContent))
            {
                return scContent.GetString() ?? "";
            }

            // Last resort: serialize the whole result
            return result.GetRawText();
        }

        return string.Join("\n", ExtractStrings(content));
    }

    /// <summary>
    /// Pretty-prints a JsonElement for display. Handles both object and string-encoded JSON.
    /// </summary>
    private static string FormatJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string raw = element.GetString() ?? "";
            try
            {
                var parsed = JsonDocument.Parse(raw);
                return JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return raw;
            }
        }

        if (element.ValueKind == JsonValueKind.Object || element.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

        return element.GetRawText();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool IsWriteTool(string name)
    {
        string lower = name.ToLowerInvariant();
        return WriteTools.Contains(lower) || lower.Contains("write") ||
               lower.Contains("save") || lower.Contains("create_dir");
    }

    private static JsonDocument? LoadJson(string path)
    {
        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonDocument.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ExtractStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString() ?? "";
                break;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    foreach (var s in ExtractStrings(prop.Value))
                        yield return s;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var s in ExtractStrings(item))
                        yield return s;
                break;
        }
    }

    private static IEnumerable<string> FindArtifacts(string text)
    {
        foreach (var pattern in GetArtifactPatterns())
            foreach (Match match in pattern.Matches(text))
            {
                string m = match.Value.TrimEnd('.', ',', ';', ')', '`', '\'', '"');
                if (!string.IsNullOrEmpty(m))
                    yield return m;
            }
    }

    private static string? ParseAgentName(JsonElement functionCall)
    {
        if (!functionCall.TryGetProperty("arguments", out var args))
            return null;

        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("agentName", out var n) &&
            n.ValueKind == JsonValueKind.String)
            return n.GetString();

        if (args.ValueKind == JsonValueKind.String)
        {
            try
            {
                var parsed = JsonDocument.Parse(args.GetString()!);
                if (parsed.RootElement.TryGetProperty("agentName", out var pn))
                    return pn.GetString();
            }
            catch { }
        }

        return null;
    }

    private static string? ParseSignalOutcome(JsonElement functionCall)
    {
        if (!functionCall.TryGetProperty("arguments", out var args))
            return null;

        JsonElement root;
        if (args.ValueKind == JsonValueKind.String)
        {
            try { root = JsonDocument.Parse(args.GetString()!).RootElement; }
            catch { return null; }
        }
        else if (args.ValueKind == JsonValueKind.Object)
        {
            root = args;
        }
        else return null;

        string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        string summary = root.TryGetProperty("summary", out var su) ? su.GetString() ?? "" : "";
        string artifactStr = root.TryGetProperty("artifacts", out var a) ? a.GetString() ?? "" : "";

        var parts = new[] { status, summary }.Where(p => !string.IsNullOrEmpty(p));
        string outcome = string.Join(": ", parts);

        if (!string.IsNullOrEmpty(artifactStr) && !string.IsNullOrEmpty(outcome))
            outcome += $" (artifacts: {artifactStr})";

        return string.IsNullOrEmpty(outcome) ? null : outcome;
    }

    private static string FormatToolCalls(Dictionary<string, int> calls)
    {
        if (calls.Count == 0) return "None";
        return string.Join(", ", calls.OrderBy(k => k.Key)
            .Select(k => k.Value > 1 ? $"{k.Key} (x{k.Value})" : k.Key));
    }

    private static string FormatSet(IEnumerable<string> items)
    {
        var sorted = items.OrderBy(s => s).ToList();
        return sorted.Count > 0 ? string.Join(", ", sorted) : "None";
    }
}