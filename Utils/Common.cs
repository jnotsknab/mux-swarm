using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using static MuxSwarm.Setup.Setup;

namespace MuxSwarm.Utils;

public static class Common
{
    public static void LogAvailableTools(IList<McpClientTool> tools)
    {
        var listing = string.Join("\n", tools.OrderBy(t => t.Name).Select(t => $"  {t.Name}"));
        MuxConsole.WritePanel($"Available MCP Tools ({tools.Count})", listing);
    }

    public record AgentDefinition(
        string Name,
        string Description,
        string SystemPromptPath,
        bool CanDelegate,
        Func<IList<AITool>, IList<AITool>> ToolFilter
    );

    public static List<AgentDefinition> GetAgentDefinitions(string swarmConfPath)
    {
        if (File.Exists(swarmConfPath))
        {
            try
            {
                var json = File.ReadAllText(swarmConfPath);
                var config = JsonSerializer.Deserialize<SwarmConfig>(json);

                if (config?.Agents != null && config.Agents.Count > 0)
                {
                    //HACK: compact agent is outside agents array we need to account for it too i.e. +1
                    MuxConsole.WriteInfo($"Loaded {config.Agents.Count + 1} agents from swarm.json");
                    return ParseAgentDefinitions(config);
                }
            }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"Failed to parse swarm.json: {ex.Message}");
                MuxConsole.WriteWarning("Falling back to default agents...");
            }
        }
        else
        {
            MuxConsole.WriteWarning($"swarm.json not found at {swarmConfPath}");
            MuxConsole.WriteWarning("Using default agent definitions...");
        }

        return new List<AgentDefinition>();
    }

    public static List<ChatMessage> ExtractMessagesFromSession(JsonElement serializedSession)
    {
        var messages = new List<ChatMessage>();

        if (serializedSession.TryGetProperty("chatHistoryProviderState", out var storeState)
            && storeState.TryGetProperty("messages", out var msgArray)
            && msgArray.ValueKind == JsonValueKind.Array)
        {

            foreach (var msg in msgArray.EnumerateArray())
            {
                var role = msg.TryGetProperty("role", out var r) ? r.GetString() : null;
                if (role == null) continue;

                var text = "";
                if (msg.TryGetProperty("contents", out var contents)
                    && contents.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in contents.EnumerateArray())
                    {
                        if (c.TryGetProperty("$type", out var t) && t.GetString() == "text"
                                                                 && c.TryGetProperty("text", out var txt))
                        {
                            text += txt.GetString();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(text))
                {
                    var chatRole = role == "assistant" ? ChatRole.Assistant : ChatRole.User;
                    messages.Add(new ChatMessage(chatRole, text));
                }
            }
        }

        return messages;
    }

    public static List<AgentDefinition> ParseAgentDefinitions(SwarmConfig config)
    {
        var definitions = new List<AgentDefinition>();

        foreach (var agent in config.Agents)
        {
            string promptPath;
            if (!string.IsNullOrEmpty(agent.PromptPath))
            {
                promptPath = Path.IsPathRooted(agent.PromptPath)
                    ? agent.PromptPath
                    : Path.Combine(MultiAgentOrchestrator.PromptsDir, agent.PromptPath);
            }
            else
            {
                promptPath = Path.Combine(MultiAgentOrchestrator.PromptsDir, $"{agent.Name.ToLowerInvariant()}.md");
            }

            Func<IList<AITool>, IList<AITool>> toolFilter = tools =>
            {
                var filtered = new List<AITool>();

                foreach (var tool in tools)
                {
                    bool include = false;

                    foreach (var server in agent.McpServers)
                    {
                        if (tool.Name.StartsWith($"{server}_", StringComparison.OrdinalIgnoreCase))
                        {
                            include = true;
                            break;
                        }
                    }

                    if (!include && agent.ToolPatterns.Count > 0)
                    {
                        foreach (var pattern in agent.ToolPatterns)
                        {
                            if (pattern.EndsWith("*"))
                            {
                                var prefix = pattern[..^1];
                                if (tool.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    include = true;
                                    break;
                                }
                            }
                            else if (tool.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                include = true;
                                break;
                            }
                        }
                    }

                    if (agent.McpServers.Count == 0 && agent.ToolPatterns.Count == 0)
                        include = true;

                    if (include)
                        filtered.Add(tool);
                }

                return filtered;
            };

            definitions.Add(new AgentDefinition(
                Name: agent.Name,
                Description: agent.Description,
                SystemPromptPath: promptPath,
                CanDelegate: agent.CanDelegate,
                ToolFilter: toolFilter
            ));
        }

        return definitions;
    }

    public static string? ResolveDirCaseInsensitive(string parent, string name)
    {
        if (!Directory.Exists(parent)) return null;
        return Directory.GetDirectories(parent)
            .FirstOrDefault(d => Path.GetFileName(d)
                .Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static IList<AITool> ApplyToolFilter(
        IList<AITool> tools,
        IList<string>? mcpServers,
        IList<string>? toolPatterns,
        bool includeAllWhenEmpty = false)
    {
        var servers = mcpServers ?? Array.Empty<string>();
        var patterns = toolPatterns ?? Array.Empty<string>();

        if (servers.Count == 0 && patterns.Count == 0)
            return includeAllWhenEmpty ? tools.ToList() : new List<AITool>();

        var filtered = new List<AITool>();

        foreach (var tool in tools)
        {
            bool include = false;

            foreach (var server in servers)
            {
                if (tool.Name.StartsWith($"{server}_", StringComparison.OrdinalIgnoreCase))
                {
                    include = true;
                    break;
                }
            }

            if (!include && patterns.Count > 0)
            {
                foreach (var pattern in patterns)
                {
                    if (pattern.EndsWith("*", StringComparison.Ordinal))
                    {
                        var prefix = pattern[..^1];
                        if (tool.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            include = true;
                            break;
                        }
                    }
                    else if (tool.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        include = true;
                        break;
                    }
                }
            }

            if (include)
                filtered.Add(tool);
        }

        return filtered;
    }

    public static AgentDefinition? ParseSingleAgentDefinition(SwarmConfig config)
    {
        var agent = config.SingleAgent;
        if (agent == null) return null;

        string promptPath;
        if (!string.IsNullOrEmpty(agent.PromptPath))
        {
            promptPath = Path.IsPathRooted(agent.PromptPath)
                ? agent.PromptPath
                : Path.Combine(MultiAgentOrchestrator.PromptsDir, agent.PromptPath);
        }
        else
        {
            promptPath = Path.Combine(MultiAgentOrchestrator.PromptsDir, $"{agent.Name.ToLowerInvariant()}.md");
        }

        Func<IList<AITool>, IList<AITool>> toolFilter = tools =>
        {
            var filtered = new List<AITool>();
            foreach (var tool in tools)
            {
                bool include = false;
                foreach (var server in agent.McpServers)
                {
                    if (tool.Name.StartsWith($"{server}_", StringComparison.OrdinalIgnoreCase))
                    { include = true; break; }
                }
                if (!include && agent.ToolPatterns.Count > 0)
                {
                    foreach (var pattern in agent.ToolPatterns)
                    {
                        if (pattern.EndsWith("*"))
                        {
                            if (tool.Name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
                            { include = true; break; }
                        }
                        else if (tool.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                        { include = true; break; }
                    }
                }
                if (agent.McpServers.Count == 0 && agent.ToolPatterns.Count == 0)
                    include = true;
                if (include) filtered.Add(tool);
            }
            return filtered;
        };

        return new AgentDefinition(
            Name: agent.Name,
            Description: agent.Description ?? "",
            SystemPromptPath: promptPath,
            CanDelegate: false,
            ToolFilter: toolFilter
        );
    }

    public static bool LooksLikeEnvVarName(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Z_][A-Z0-9_]*$");

    public static string GetOsFriendlyName()
    {
        if (PlatformContext.IsWindows) return "Windows";
        if (PlatformContext.IsMac) return "macOS";
        if (PlatformContext.IsLinux) return "Linux";
        return Environment.OSVersion.Platform.ToString();
    }

    public static async Task<string> LoadPromptAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[WARNING] Prompt path was empty/null.");
            Console.ResetColor();
            return "You are a helpful AI assistant. (Prompt path missing.)";
        }

        path = path.Trim().Trim('"'); // handles accidental quotes

        // Expand ~ on Unix-like systems
        if (!PlatformContext.IsWindows && path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, path[2..]);
        }

        // Normalize common prefixes so we don't double-combine Prompts/Agents
        var normalized = NormalizePromptPath(path);

        var candidates = GetPromptCandidates(normalized)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                var content = await File.ReadAllTextAsync(candidate);
                return TokenInjector.InjectTokens(content);
            }
        }

        var expected = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(PlatformContext.PromptsDirectory, normalized));

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[WARNING] Prompt file not found. Tried:");
        foreach (var c in candidates)
            Console.WriteLine($"  - {c}");
        Console.WriteLine($"[WARNING] Expected (convention): {expected}");
        Console.ResetColor();

        return $"You are a helpful AI assistant. (Prompt file missing: {expected})";
    }

    private static IEnumerable<string> GetPromptCandidates(string normalizedPath)
    {
        // 1) absolute path (or already rooted)
        if (Path.IsPathRooted(normalizedPath))
        {
            yield return normalizedPath;

            // Repair case: rooted path accidentally contains "\Prompts\Agents\Prompts\Agents\"
            var repaired = RepairDoubleAgents(normalizedPath);
            if (!string.Equals(repaired, normalizedPath, StringComparison.OrdinalIgnoreCase))
                yield return repaired;

            yield break;
        }

        // 2) convention: <Base>/Prompts/Agents/<file>
        yield return Path.Combine(PlatformContext.PromptsDirectory, normalizedPath);

        // 3) base dir fallback
        yield return Path.Combine(PlatformContext.BaseDirectory, normalizedPath);
    }

    private static string NormalizePromptPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p))
            return p;

        // Keep original for rooted-path detection
        var original = p.Trim().Trim('"');

        // If it's an absolute path, don't try to normalize prefixes here.
        if (Path.IsPathRooted(original))
            return original;

        // Normalize separators for searching
        var s = original.Replace('\\', '/');

        // Remove leading "./"
        if (s.StartsWith("./", StringComparison.Ordinal))
            s = s[2..];

        // Support multiple conventions (strip the LAST matching prefix)
        var aliases = new[]
        {
            "Prompts/Agents/",
            "/Prompts/Agents/",
            "Agents/",
            "/Agents/",
            "Prompts/",
            "/Prompts/"
        };

        foreach (var pre in aliases)
        {
            var idx = s.LastIndexOf(pre, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                s = s[(idx + pre.Length)..];
                break;
            }
        }

        // Convert back to OS separators
        if (PlatformContext.IsWindows)
            s = s.Replace('/', '\\');

        return s;
    }

    private static string RepairDoubleAgents(string path)
    {
        // Normalize to '/'
        var s = path.Replace('\\', '/');

        const string doubled = "Prompts/Agents/Prompts/Agents/";
        const string single = "Prompts/Agents/";

        while (s.IndexOf(doubled, StringComparison.OrdinalIgnoreCase) >= 0)
            s = ReplaceIgnoreCase(s, doubled, single);

        return PlatformContext.IsWindows ? s.Replace('/', '\\') : s;
    }

    private static string ReplaceIgnoreCase(string input, string find, string replace)
    {
        int idx = 0;
        while (true)
        {
            idx = input.IndexOf(find, idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            input = input.Remove(idx, find.Length).Insert(idx, replace);
            idx += replace.Length;
        }
        return input;
    }

    private static string? NextValue(string[] args, ref int i)
        => (i + 1 < args.Length) ? args[++i] : null;

    public static bool? NextBool(string[] args, ref int i)
    {
        var v = (i + 1 < args.Length) ? args[i + 1] : null;
        if (v != null && bool.TryParse(v, out var b)) { i++; return b; }
        return null;
    }

    public static void PruneOldSessions(string sessionDir, uint retention)
    {
        var dirs = Directory.GetDirectories(sessionDir)
            .Where(d => !Path.GetFileName(d).Equals("state", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d)
            .Skip((int)retention)
            .ToList();

        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException ex)
            {
                MuxConsole.WriteWarning($"Failed to delete session dir '{dir}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                MuxConsole.WriteWarning($"Access denied deleting session dir '{dir}': {ex.Message}");
            }
        }
    }

    public static string ReadGoalValue(string val)
        => File.Exists(val) ? File.ReadAllText(val) : val;

    public static bool TryNextUInt(string[] args, ref int i, out uint value)
    {
        value = 0;
        if (i + 1 >= args.Length) return false;
        if (!uint.TryParse(args[i + 1], out value)) return false;
        i++;
        return true;
    }

    public static async Task PersistSessionsAsync(
        AIAgent orchestratorAgent,
        AgentSession orchestratorSession,
        Dictionary<string, (AIAgent Agent, AgentSession Session, AgentDefinition Def)> specialists,
        string? seshDir = null)
    {
        try
        {
            var sessionSubDir = !string.IsNullOrEmpty(seshDir)
                ? seshDir
                : Path.Combine(MultiAgentOrchestrator.SessionDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));

            Directory.CreateDirectory(sessionSubDir);

            var serialized = orchestratorAgent.SerializeSession(orchestratorSession);
            await File.WriteAllTextAsync(
                Path.Combine(sessionSubDir, "orchestrator_session.json"),
                serialized.GetRawText());

            foreach (var (name, (agent, session, _)) in specialists)
            {
                serialized = agent.SerializeSession(session);
                await File.WriteAllTextAsync(
                    Path.Combine(sessionSubDir, $"{name.ToLower().Replace(" ", "_")}_session.json"),
                    serialized.GetRawText());
            }

            MuxConsole.WriteSuccess($"[AGENT SESSION] Saved to {sessionSubDir}");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[AGENT SESSION] Save failed: {ex.Message}");
        }
    }

    public static string? FindSessionDirectory(string sessionTimestamp)
    {
        try
        {
            var sessionsRoot = PlatformContext.SessionsDirectory;

            if (string.IsNullOrWhiteSpace(sessionTimestamp) || !Directory.Exists(sessionsRoot))
                return null;

            var directPath = Path.Combine(sessionsRoot, sessionTimestamp);
            if (Directory.Exists(directPath))
                return directPath;

            var match = new DirectoryInfo(sessionsRoot)
                .GetDirectories()
                .FirstOrDefault(d =>
                    d.Name.Equals(sessionTimestamp, StringComparison.OrdinalIgnoreCase));

            return match?.FullName;
        }
        catch
        {
            return null;
        }
    }

    public static async Task PersistChatSessionAsync(AIAgent agent, AgentSession session, string sessionTimestamp, string? existingSessionDir = null)
    {
        try
        {
            var sessionSubDir = Path.Combine(MultiAgentOrchestrator.SessionDir, sessionTimestamp);
            if (!string.IsNullOrEmpty(existingSessionDir)) sessionSubDir = existingSessionDir;

            Directory.CreateDirectory(sessionSubDir);

            var serialized = agent.SerializeSession(session);
            await File.WriteAllTextAsync(
                Path.Combine(sessionSubDir, "agent_session.json"),
                serialized.GetRawText());

            MuxConsole.WriteSuccess($"[AGENT SESSION] Saved to {sessionSubDir}");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[AGENT SESSION] Save failed: {ex.Message}");
        }
    }

    public static string GetFirstUserMessage(string sessionFile, int maxLength = 60)
    {
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(sessionFile));
            var messages = doc.RootElement
                .GetProperty("chatHistoryProviderState")
                .GetProperty("messages");

            foreach (var msg in messages.EnumerateArray())
            {
                if (msg.TryGetProperty("role", out var role) && role.GetString() == "user" &&
                    msg.TryGetProperty("contents", out var contents))
                {
                    foreach (var content in contents.EnumerateArray())
                    {
                        if (content.TryGetProperty("$type", out var t) && t.GetString() == "text" &&
                            content.TryGetProperty("text", out var text))
                        {
                            var str = text.GetString() ?? "";
                            return str.Length > maxLength ? str[..maxLength] + "..." : str;
                        }
                    }
                }
            }
        }
        catch { }

        return "No preview";
    }

    public static int EstimateTokenCount(JsonElement sessionData, float charsPerToken = 2.5f)
    {
        return (int)Math.Ceiling(sessionData.ToString().Length / charsPerToken);
    }

    public static int EstimateTokenCount(IReadOnlyList<ChatMessage> history, float charsPerToken = 2.5f)
    {
        int totalChars = 0;
        foreach (var msg in history)
        {
            foreach (var content in msg.Contents)
            {
                if (content is TextContent text)
                    totalChars += text.Text?.Length ?? 0;
            }
        }
        return (int)Math.Ceiling(totalChars / charsPerToken);
    }

    public static void StartExternalWatchdog(string[] args, string baseDir, CancellationTokenSource cts)
    {
        string hbPath = Path.Combine(baseDir, "watchdog.heartbeat");
        var originalArgs = string.Join(" ", args.Select(a => $"\"{a}\""));

        File.WriteAllText(hbPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        Console.WriteLine($"[WATCHDOG] Heartbeat written to: {hbPath}");

        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(PlatformContext.BaseDirectory, "runtime", $"Watchdog{PlatformContext.ExecutableExtension}"),
            Arguments = $"\"{Path.Combine(baseDir, $"MuxSwarm{PlatformContext.ExecutableExtension}")}\" \"{hbPath}\" {originalArgs}",
            UseShellExecute = true,
            CreateNoWindow = false
        });

        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(hbPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
                await Task.Delay(10_000, cts.Token);
            }
        }, cts.Token);
    }

    public static void SaveConfig(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, CfgSerialOpts);
            File.WriteAllText(PlatformContext.ConfigPath, json);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Failed to save config: {ex.Message}");
            Console.ResetColor();
            throw;
        }
    }

    public static async Task RawChatProbeAsync(string normalizedEndpoint, string apiKey, string modelName)
    {
        var url = normalizedEndpoint.TrimEnd('/') + "/v1/chat/completions";
        using var http = new HttpClient();

        // OpenRouter: Authorization is required; referer/title are optional but helpful.
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "http://localhost");
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "QweCrossPlatTest");

        var payload = new
        {
            model = modelName,
            messages = new[] { new { role = "user", content = "Reply with exactly: OK" } },
            max_tokens = 10
        };

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var resp = await http.PostAsync(url, content);
        var body = await resp.Content.ReadAsStringAsync();

        Console.WriteLine($"[RAW PROBE] POST {url}");
        Console.WriteLine($"[RAW PROBE] Status: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        Console.WriteLine("[RAW PROBE] Body (first 2k):");
        Console.WriteLine(body.Length > 2000 ? body[..2000] : body);
    }
}