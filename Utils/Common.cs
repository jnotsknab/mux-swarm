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
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n{"─── Available MCP Tools (" + tools.Count + ") ───"}");
        foreach (var tool in tools.OrderBy(t => t.Name))
            Console.WriteLine($"  {tool.Name}");
        Console.WriteLine(new string('─', 40));
        Console.ResetColor();
    }

    public static List<MultiAgentOrchestrator.AgentDefinition> ParseAgentDefinitions(SwarmConfig config)
    {
        var definitions = new List<MultiAgentOrchestrator.AgentDefinition>();

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

            definitions.Add(new MultiAgentOrchestrator.AgentDefinition(
                Name: agent.Name,
                Description: agent.Description,
                SystemPromptPath: promptPath,
                CanDelegate: agent.CanDelegate,
                ToolFilter: toolFilter
            ));
        }

        return definitions;
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
    
    public static MultiAgentOrchestrator.AgentDefinition? ParseSingleAgentDefinition(SwarmConfig config)
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

        return new MultiAgentOrchestrator.AgentDefinition(
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

    public static string ReadGoalValue(string val)
        => File.Exists(val) ? File.ReadAllText(val) : val;

    public static bool TryNextUInt(string[] args, ref int i, out uint value)
    {
        value = 0;
        var next = NextValue(args, ref i);
        return next != null && uint.TryParse(next, out value);
    }

    public static async Task PersistSessionsAsync(
        AIAgent orchestratorAgent,
        AgentSession orchestratorSession,
        Dictionary<string, (AIAgent Agent, AgentSession Session, MultiAgentOrchestrator.AgentDefinition Def)> specialists,
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

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[AGENT SESSION] Saved to {sessionSubDir}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[AGENT SESSION] Save failed: {ex.Message}");
            Console.ResetColor();
        }
    }

    public static async Task PersistChatSessionAsync(AIAgent agent, AgentSession session, string sessionTimestamp)
    {
        try
        {
            var sessionSubDir = Path.Combine(MultiAgentOrchestrator.SessionDir, sessionTimestamp);
            Directory.CreateDirectory(sessionSubDir);

            var serialized = agent.SerializeSession(session);
            await File.WriteAllTextAsync(
                Path.Combine(sessionSubDir, "agent_session.json"),
                serialized.GetRawText());

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[AGENT SESSION] Saved to {sessionSubDir}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Agent Session save failed: {ex.Message}]");
            Console.ResetColor();
        }
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
    

    // NOTE: Yea this is fucked, ill read from file later im lazy
    public static void ListModelsHumanRFormat()
    {
        int page = 0;
        bool paginate = true;

        void PrintPage(int p)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║         AVAILABLE MODELS - Prices per 1M tokens             ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            switch (p)
            {
                case 0:
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  ★ FREE MODELS");
                    Console.ResetColor();
                    Console.WriteLine(new string('─', 64));
                    Console.WriteLine("    1. arcee-ai/trinity-large-preview:free (FREE)");
                    Console.WriteLine("    2. arcee-ai/trinity-mini:free (FREE)");
                    Console.WriteLine("    3. cognitivecomputations/dolphin-mistral-24b-venice-edition:free (FREE)");
                    Console.WriteLine("    4. deepseek/deepseek-r1-0528:free (FREE)");
                    Console.WriteLine("    5. google/gemma-3-12b-it:free (FREE)");
                    Console.WriteLine("    6. google/gemma-3-27b-it:free (FREE)");
                    Console.WriteLine("    7. google/gemma-3-4b-it:free (FREE)");
                    Console.WriteLine("    8. google/gemma-3n-e2b-it:free (FREE)");
                    Console.WriteLine("    9. google/gemma-3n-e4b-it:free (FREE)");
                    Console.WriteLine("   10. liquid/lfm-2.5-1.2b-instruct:free (FREE)");
                    Console.WriteLine("   11. liquid/lfm-2.5-1.2b-thinking:free (FREE)");
                    Console.WriteLine("   12. meta-llama/llama-3.2-3b-instruct:free (FREE)");
                    Console.WriteLine("   13. meta-llama/llama-3.3-70b-instruct:free (FREE)");
                    Console.WriteLine("   14. mistralai/mistral-small-3.1-24b-instruct:free (FREE)");
                    Console.WriteLine("   15. nousresearch/hermes-3-llama-3.1-405b:free (FREE)");
                    Console.WriteLine("   16. nvidia/nemotron-3-nano-30b-a3b:free (FREE)");
                    Console.WriteLine("   17. nvidia/nemotron-nano-12b-v2-vl:free (FREE)");
                    Console.WriteLine("   18. nvidia/nemotron-nano-9b-v2:free (FREE)");
                    Console.WriteLine("   19. openai/gpt-oss-120b:free (FREE)");
                    Console.WriteLine("   20. openai/gpt-oss-20b:free (FREE)");
                    Console.WriteLine("   21. openrouter/free (FREE)");
                    Console.WriteLine("   22. qwen/qwen3-235b-a22b-thinking-2507 (FREE)");
                    Console.WriteLine("   23. qwen/qwen3-4b:free (FREE)");
                    Console.WriteLine("   24. qwen/qwen3-coder:free (FREE)");
                    Console.WriteLine("   25. qwen/qwen3-next-80b-a3b-instruct:free (FREE)");
                    Console.WriteLine("   26. qwen/qwen3-vl-235b-a22b-thinking (FREE)");
                    Console.WriteLine("   27. qwen/qwen3-vl-30b-a3b-thinking (FREE)");
                    Console.WriteLine("   28. stepfun/step-3.5-flash:free (FREE)");
                    Console.WriteLine("   29. upstage/solar-pro-3:free (FREE)");
                    Console.WriteLine("   30. z-ai/glm-4.5-air:free (FREE)");
                    break;

                case 1:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  ◆ BUDGET  (input < $0.50/1M)");
                    Console.ResetColor();
                    Console.WriteLine(new string('─', 64));
                    Console.WriteLine("   31. alibaba/tongyi-deepresearch-30b-a3b (in:$0.09 / out:$0.45)");
                    Console.WriteLine("   32. allenai/molmo-2-8b (in:$0.20 / out:$0.20)");
                    Console.WriteLine("   33. allenai/olmo-2-0325-32b-instruct (in:$0.05 / out:$0.20)");
                    Console.WriteLine("   34. allenai/olmo-3-32b-think (in:$0.15 / out:$0.50)");
                    Console.WriteLine("   35. allenai/olmo-3-7b-instruct (in:$0.10 / out:$0.20)");
                    Console.WriteLine("   36. allenai/olmo-3-7b-think (in:$0.12 / out:$0.20)");
                    Console.WriteLine("   37. allenai/olmo-3.1-32b-instruct (in:$0.20 / out:$0.60)");
                    Console.WriteLine("   38. allenai/olmo-3.1-32b-think (in:$0.15 / out:$0.50)");
                    Console.WriteLine("   39. amazon/nova-2-lite-v1 (in:$0.30 / out:$2.50)");
                    Console.WriteLine("   40. amazon/nova-lite-v1 (in:$0.06 / out:$0.24)");
                    Console.WriteLine("   41. amazon/nova-micro-v1 (in:$0.04 / out:$0.14)");
                    Console.WriteLine("   42. anthropic/claude-3-haiku (in:$0.25 / out:$1.25)");
                    Console.WriteLine("   43. arcee-ai/spotlight (in:$0.18 / out:$0.18)");
                    Console.WriteLine("   44. arcee-ai/trinity-mini (in:$0.04 / out:$0.15)");
                    Console.WriteLine("   45. baidu/ernie-4.5-21b-a3b (in:$0.07 / out:$0.28)");
                    Console.WriteLine("   46. baidu/ernie-4.5-21b-a3b-thinking (in:$0.07 / out:$0.28)");
                    Console.WriteLine("   47. baidu/ernie-4.5-300b-a47b (in:$0.28 / out:$1.10)");
                    Console.WriteLine("   48. baidu/ernie-4.5-vl-28b-a3b (in:$0.14 / out:$0.56)");
                    Console.WriteLine("   49. baidu/ernie-4.5-vl-424b-a47b (in:$0.42 / out:$1.25)");
                    Console.WriteLine("   50. bytedance-seed/seed-1.6 (in:$0.25 / out:$2.00)");
                    Console.WriteLine("   51. bytedance-seed/seed-1.6-flash (in:$0.07 / out:$0.30)");
                    Console.WriteLine("   52. bytedance/ui-tars-1.5-7b (in:$0.10 / out:$0.20)");
                    Console.WriteLine("   53. cohere/command-r-08-2024 (in:$0.15 / out:$0.60)");
                    Console.WriteLine("   54. cohere/command-r7b-12-2024 (in:$0.04 / out:$0.15)");
                    Console.WriteLine("   55. deepseek/deepseek-chat (in:$0.32 / out:$0.89)");
                    Console.WriteLine("   56. deepseek/deepseek-chat-v3-0324 (in:$0.19 / out:$0.87)");
                    Console.WriteLine("   57. deepseek/deepseek-chat-v3.1 (in:$0.15 / out:$0.75)");
                    Console.WriteLine("   58. deepseek/deepseek-r1-0528 (in:$0.40 / out:$1.75)");
                    Console.WriteLine("   59. deepseek/deepseek-r1-distill-qwen-32b (in:$0.29 / out:$0.29)");
                    Console.WriteLine("   60. deepseek/deepseek-v3.1-terminus (in:$0.21 / out:$0.79)");
                    Console.WriteLine("   61. deepseek/deepseek-v3.1-terminus:exacto (in:$0.21 / out:$0.79)");
                    Console.WriteLine("   62. deepseek/deepseek-v3.2 (in:$0.26 / out:$0.38)");
                    Console.WriteLine("   63. deepseek/deepseek-v3.2-exp (in:$0.27 / out:$0.41)");
                    Console.WriteLine("   64. deepseek/deepseek-v3.2-speciale (in:$0.40 / out:$1.20)");
                    Console.WriteLine("   65. essentialai/rnj-1-instruct (in:$0.15 / out:$0.15)");
                    Console.WriteLine("   66. google/gemini-2.0-flash-001 (in:$0.10 / out:$0.40)");
                    Console.WriteLine("   67. google/gemini-2.0-flash-lite-001 (in:$0.07 / out:$0.30)");
                    Console.WriteLine("   68. google/gemini-2.5-flash (in:$0.30 / out:$2.50)");
                    Console.WriteLine("   69. google/gemini-2.5-flash-image (in:$0.30 / out:$2.50)");
                    Console.WriteLine("   70. google/gemini-2.5-flash-lite (in:$0.10 / out:$0.40)");
                    Console.WriteLine("   71. google/gemini-2.5-flash-lite-preview-09-2025 (in:$0.10 / out:$0.40)");
                    Console.WriteLine("   72. google/gemma-2-9b-it (in:$0.03 / out:$0.09)");
                    Console.WriteLine("   73. google/gemma-3-12b-it (in:$0.04 / out:$0.13)");
                    Console.WriteLine("   74. google/gemma-3-27b-it (in:$0.04 / out:$0.15)");
                    Console.WriteLine("   75. google/gemma-3-4b-it (in:$0.04 / out:$0.08)");
                    Console.WriteLine("   76. google/gemma-3n-e4b-it (in:$0.02 / out:$0.04)");
                    Console.WriteLine("   77. gryphe/mythomax-l2-13b (in:$0.06 / out:$0.06)");
                    Console.WriteLine("   78. ibm-granite/granite-4.0-h-micro (in:$0.02 / out:$0.11)");
                    Console.WriteLine("   79. inception/mercury (in:$0.25 / out:$1.00)");
                    Console.WriteLine("   80. inception/mercury-coder (in:$0.25 / out:$1.00)");
                    Console.WriteLine("   81. kwaipilot/kat-coder-pro (in:$0.21 / out:$0.83)");
                    Console.WriteLine("   82. liquid/lfm-2.2-6b (in:$0.01 / out:$0.02)");
                    Console.WriteLine("   83. liquid/lfm2-8b-a1b (in:$0.01 / out:$0.02)");
                    Console.WriteLine("   84. meituan/longcat-flash-chat (in:$0.20 / out:$0.80)");
                    Console.WriteLine("   85. meta-llama/llama-3-8b-instruct (in:$0.03 / out:$0.04)");
                    Console.WriteLine("   86. meta-llama/llama-3.1-70b-instruct (in:$0.40 / out:$0.40)");
                    Console.WriteLine("   87. meta-llama/llama-3.1-8b-instruct (in:$0.02 / out:$0.05)");
                    Console.WriteLine("   88. meta-llama/llama-3.2-11b-vision-instruct (in:$0.05 / out:$0.05)");
                    Console.WriteLine("   89. meta-llama/llama-3.2-1b-instruct (in:$0.03 / out:$0.20)");
                    Console.WriteLine("   90. meta-llama/llama-3.2-3b-instruct (in:$0.02 / out:$0.02)");
                    Console.WriteLine("   91. meta-llama/llama-3.3-70b-instruct (in:$0.10 / out:$0.32)");
                    Console.WriteLine("   92. meta-llama/llama-4-maverick (in:$0.15 / out:$0.60)");
                    Console.WriteLine("   93. meta-llama/llama-4-scout (in:$0.08 / out:$0.30)");
                    Console.WriteLine("   94. meta-llama/llama-guard-2-8b (in:$0.20 / out:$0.20)");
                    Console.WriteLine("   95. meta-llama/llama-guard-3-8b (in:$0.02 / out:$0.06)");
                    Console.WriteLine("   96. meta-llama/llama-guard-4-12b (in:$0.18 / out:$0.18)");
                    Console.WriteLine("   97. microsoft/phi-4 (in:$0.06 / out:$0.14)");
                    Console.WriteLine("   98. minimax/minimax-01 (in:$0.20 / out:$1.10)");
                    Console.WriteLine("   99. minimax/minimax-m1 (in:$0.40 / out:$2.20)");
                    Console.WriteLine("  100. minimax/minimax-m2 (in:$0.26 / out:$1.00)");
                    Console.WriteLine("  101. minimax/minimax-m2-her (in:$0.30 / out:$1.20)");
                    Console.WriteLine("  102. minimax/minimax-m2.1 (in:$0.27 / out:$0.95)");
                    Console.WriteLine("  103. minimax/minimax-m2.5 (in:$0.30 / out:$1.10)");
                    Console.WriteLine("  104. mistralai/codestral-2508 (in:$0.30 / out:$0.90)");
                    Console.WriteLine("  105. mistralai/devstral-2512 (in:$0.40 / out:$2.00)");
                    Console.WriteLine("  106. mistralai/devstral-medium (in:$0.40 / out:$2.00)");
                    Console.WriteLine("  107. mistralai/devstral-small (in:$0.10 / out:$0.30)");
                    Console.WriteLine("  108. mistralai/ministral-14b-2512 (in:$0.20 / out:$0.20)");
                    Console.WriteLine("  109. mistralai/ministral-3b-2512 (in:$0.10 / out:$0.10)");
                    Console.WriteLine("  110. mistralai/ministral-8b-2512 (in:$0.15 / out:$0.15)");
                    Console.WriteLine("  111. mistralai/mistral-7b-instruct (in:$0.20 / out:$0.20)");
                    Console.WriteLine("  112. mistralai/mistral-7b-instruct-v0.1 (in:$0.11 / out:$0.19)");
                    Console.WriteLine("  113. mistralai/mistral-7b-instruct-v0.2 (in:$0.20 / out:$0.20)");
                    Console.WriteLine("  114. mistralai/mistral-7b-instruct-v0.3 (in:$0.20 / out:$0.20)");
                    Console.WriteLine("  115. mistralai/mistral-medium-3 (in:$0.40 / out:$2.00)");
                    Console.WriteLine("  116. mistralai/mistral-medium-3.1 (in:$0.40 / out:$2.00)");
                    Console.WriteLine("  117. mistralai/mistral-nemo (in:$0.02 / out:$0.04)");
                    Console.WriteLine("  118. mistralai/mistral-saba (in:$0.20 / out:$0.60)");
                    Console.WriteLine("  119. mistralai/mistral-small-24b-instruct-2501 (in:$0.05 / out:$0.08)");
                    Console.WriteLine("  120. mistralai/mistral-small-3.1-24b-instruct (in:$0.35 / out:$0.56)");
                    Console.WriteLine("  121. mistralai/mistral-small-3.2-24b-instruct (in:$0.06 / out:$0.18)");
                    Console.WriteLine("  122. mistralai/mistral-small-creative (in:$0.10 / out:$0.30)");
                    Console.WriteLine("  123. mistralai/voxtral-small-24b-2507 (in:$0.10 / out:$0.30)");
                    Console.WriteLine("  124. moonshotai/kimi-k2-0905 (in:$0.40 / out:$2.00)");
                    Console.WriteLine("  125. moonshotai/kimi-k2-thinking (in:$0.47 / out:$2.00)");
                    Console.WriteLine("  126. moonshotai/kimi-k2.5 (in:$0.45 / out:$2.20)");
                    Console.WriteLine("  127. neversleep/llama-3.1-lumimaid-8b (in:$0.09 / out:$0.60)");
                    Console.WriteLine("  128. nex-agi/deepseek-v3.1-nex-n1 (in:$0.27 / out:$1.00)");
                    Console.WriteLine("  129. nousresearch/hermes-2-pro-llama-3-8b (in:$0.14 / out:$0.14)");
                    Console.WriteLine("  130. nousresearch/hermes-3-llama-3.1-70b (in:$0.30 / out:$0.30)");
                    Console.WriteLine("  131. nousresearch/hermes-4-70b (in:$0.13 / out:$0.40)");
                    Console.WriteLine("  132. nvidia/llama-3.3-nemotron-super-49b-v1.5 (in:$0.10 / out:$0.40)");
                    Console.WriteLine("  133. nvidia/nemotron-3-nano-30b-a3b (in:$0.05 / out:$0.20)");
                    Console.WriteLine("  134. nvidia/nemotron-nano-12b-v2-vl (in:$0.07 / out:$0.20)");
                    Console.WriteLine("  135. nvidia/nemotron-nano-9b-v2 (in:$0.04 / out:$0.16)");
                    Console.WriteLine("  136. openai/gpt-4.1-mini (in:$0.40 / out:$1.60)");
                    Console.WriteLine("  137. openai/gpt-4.1-nano (in:$0.10 / out:$0.40)");
                    Console.WriteLine("  138. openai/gpt-4o-mini (in:$0.15 / out:$0.60)");
                    Console.WriteLine("  139. openai/gpt-4o-mini-2024-07-18 (in:$0.15 / out:$0.60)");
                    Console.WriteLine("  140. openai/gpt-4o-mini-search-preview (in:$0.15 / out:$0.60)");
                    Console.WriteLine("  141. openai/gpt-5-mini (in:$0.25 / out:$2.00)");
                    Console.WriteLine("  142. openai/gpt-5-nano (in:$0.05 / out:$0.40)");
                    Console.WriteLine("  143. openai/gpt-5.1-codex-mini (in:$0.25 / out:$2.00)");
                    Console.WriteLine("  144. openai/gpt-oss-120b (in:$0.04 / out:$0.19)");
                    Console.WriteLine("  145. openai/gpt-oss-120b:exacto (in:$0.04 / out:$0.19)");
                    Console.WriteLine("  146. openai/gpt-oss-20b (in:$0.03 / out:$0.14)");
                    Console.WriteLine("  147. openai/gpt-oss-safeguard-20b (in:$0.07 / out:$0.30)");
                    Console.WriteLine("  148. opengvlab/internvl3-78b (in:$0.15 / out:$0.60)");
                    Console.WriteLine("  149. prime-intellect/intellect-3 (in:$0.20 / out:$1.10)");
                    Console.WriteLine("  150. qwen/qwen-2.5-72b-instruct (in:$0.12 / out:$0.39)");
                    Console.WriteLine("  151. qwen/qwen-2.5-7b-instruct (in:$0.04 / out:$0.10)");
                    Console.WriteLine("  152. qwen/qwen-2.5-coder-32b-instruct (in:$0.20 / out:$0.20)");
                    Console.WriteLine("  153. qwen/qwen-2.5-vl-7b-instruct (in:$0.20 / out:$0.20)");
                    Console.WriteLine("  154. qwen/qwen-plus (in:$0.40 / out:$1.20)");
                    Console.WriteLine("  155. qwen/qwen-plus-2025-07-28 (in:$0.40 / out:$1.20)");
                    Console.WriteLine("  156. qwen/qwen-plus-2025-07-28:thinking (in:$0.40 / out:$1.20)");
                    Console.WriteLine("  157. qwen/qwen-turbo (in:$0.05 / out:$0.20)");
                    Console.WriteLine("  158. qwen/qwen-vl-plus (in:$0.21 / out:$0.63)");
                    Console.WriteLine("  159. qwen/qwen2.5-coder-7b-instruct (in:$0.03 / out:$0.09)");
                    Console.WriteLine("  160. qwen/qwen2.5-vl-32b-instruct (in:$0.20 / out:$0.60)");
                    Console.WriteLine("  161. qwen/qwen2.5-vl-72b-instruct (in:$0.25 / out:$0.75)");
                    Console.WriteLine("  162. qwen/qwen3-14b (in:$0.06 / out:$0.24)");
                    Console.WriteLine("  163. qwen/qwen3-235b-a22b (in:$0.45 / out:$1.82)");
                    Console.WriteLine("  164. qwen/qwen3-235b-a22b-2507 (in:$0.07 / out:$0.10)");
                    Console.WriteLine("  165. qwen/qwen3-30b-a3b (in:$0.08 / out:$0.28)");
                    Console.WriteLine("  166. qwen/qwen3-30b-a3b-instruct-2507 (in:$0.09 / out:$0.30)");
                    Console.WriteLine("  167. qwen/qwen3-30b-a3b-thinking-2507 (in:$0.05 / out:$0.34)");
                    Console.WriteLine("  168. qwen/qwen3-32b (in:$0.08 / out:$0.24)");
                    Console.WriteLine("  169. qwen/qwen3-8b (in:$0.05 / out:$0.40)");
                    Console.WriteLine("  170. qwen/qwen3-coder (in:$0.22 / out:$1.00)");
                    Console.WriteLine("  171. qwen/qwen3-coder-30b-a3b-instruct (in:$0.07 / out:$0.27)");
                    Console.WriteLine("  172. qwen/qwen3-coder-flash (in:$0.30 / out:$1.50)");
                    Console.WriteLine("  173. qwen/qwen3-coder-next (in:$0.12 / out:$0.75)");
                    Console.WriteLine("  174. qwen/qwen3-coder:exacto (in:$0.22 / out:$1.80)");
                    Console.WriteLine("  175. qwen/qwen3-next-80b-a3b-instruct (in:$0.09 / out:$1.10)");
                    Console.WriteLine("  176. qwen/qwen3-next-80b-a3b-thinking (in:$0.15 / out:$1.20)");
                    Console.WriteLine("  177. qwen/qwen3-vl-235b-a22b-instruct (in:$0.20 / out:$0.88)");
                    Console.WriteLine("  178. qwen/qwen3-vl-30b-a3b-instruct (in:$0.13 / out:$0.52)");
                    Console.WriteLine("  179. qwen/qwen3-vl-32b-instruct (in:$0.10 / out:$0.42)");
                    Console.WriteLine("  180. qwen/qwen3-vl-8b-instruct (in:$0.08 / out:$0.50)");
                    Console.WriteLine("  181. qwen/qwen3-vl-8b-thinking (in:$0.12 / out:$1.36)");
                    Console.WriteLine("  182. qwen/qwen3.5-397b-a17b (in:$0.15 / out:$1.00)");
                    Console.WriteLine("  183. qwen/qwen3.5-plus-02-15 (in:$0.40 / out:$2.40)");
                    Console.WriteLine("  184. qwen/qwq-32b (in:$0.15 / out:$0.40)");
                    Console.WriteLine("  185. sao10k/l3-lunaris-8b (in:$0.04 / out:$0.05)");
                    Console.WriteLine("  186. stepfun/step-3.5-flash (in:$0.10 / out:$0.30)");
                    Console.WriteLine("  187. tencent/hunyuan-a13b-instruct (in:$0.14 / out:$0.57)");
                    Console.WriteLine("  188. thedrummer/cydonia-24b-v4.1 (in:$0.30 / out:$0.50)");
                    Console.WriteLine("  189. thedrummer/rocinante-12b (in:$0.17 / out:$0.43)");
                    Console.WriteLine("  190. thedrummer/unslopnemo-12b (in:$0.40 / out:$0.40)");
                    Console.WriteLine("  191. tngtech/deepseek-r1t2-chimera (in:$0.25 / out:$0.85)");
                    Console.WriteLine("  192. undi95/remm-slerp-l2-13b (in:$0.45 / out:$0.65)");
                    Console.WriteLine("  193. x-ai/grok-3-mini (in:$0.30 / out:$0.50)");
                    Console.WriteLine("  194. x-ai/grok-3-mini-beta (in:$0.30 / out:$0.50)");
                    Console.WriteLine("  195. x-ai/grok-4-fast (in:$0.20 / out:$0.50)");
                    Console.WriteLine("  196. x-ai/grok-4.1-fast (in:$0.20 / out:$0.50)");
                    Console.WriteLine("  197. x-ai/grok-code-fast-1 (in:$0.20 / out:$1.50)");
                    Console.WriteLine("  198. xiaomi/mimo-v2-flash (in:$0.09 / out:$0.29)");
                    Console.WriteLine("  199. z-ai/glm-4-32b (in:$0.10 / out:$0.10)");
                    Console.WriteLine("  200. z-ai/glm-4.5-air (in:$0.13 / out:$0.85)");
                    Console.WriteLine("  201. z-ai/glm-4.6 (in:$0.35 / out:$1.71)");
                    Console.WriteLine("  202. z-ai/glm-4.6:exacto (in:$0.44 / out:$1.76)");
                    Console.WriteLine("  203. z-ai/glm-4.6v (in:$0.30 / out:$0.90)");
                    Console.WriteLine("  204. z-ai/glm-4.7 (in:$0.38 / out:$1.70)");
                    Console.WriteLine("  205. z-ai/glm-4.7-flash (in:$0.06 / out:$0.40)");
                    break;

                case 2:
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine("  ◈ MID-TIER  ($0.50–$3.00/1M input)");
                    Console.ResetColor();
                    Console.WriteLine(new string('─', 64));
                    Console.WriteLine("  206. ai21/jamba-large-1.7 (in:$2.00 / out:$8.00)");
                    Console.WriteLine("  207. aion-labs/aion-1.0-mini (in:$0.70 / out:$1.40)");
                    Console.WriteLine("  208. aion-labs/aion-rp-llama-3.1-8b (in:$0.80 / out:$1.60)");
                    Console.WriteLine("  209. alfredpros/codellama-7b-instruct-solidity (in:$0.80 / out:$1.20)");
                    Console.WriteLine("  210. amazon/nova-premier-v1 (in:$2.50 / out:$12.50)");
                    Console.WriteLine("  211. amazon/nova-pro-v1 (in:$0.80 / out:$3.20)");
                    Console.WriteLine("  212. anthropic/claude-3.5-haiku (in:$0.80 / out:$4.00)");
                    Console.WriteLine("  213. anthropic/claude-haiku-4.5 (in:$1.00 / out:$5.00)");
                    Console.WriteLine("  214. arcee-ai/coder-large (in:$0.50 / out:$0.80)");
                    Console.WriteLine("  215. arcee-ai/maestro-reasoning (in:$0.90 / out:$3.30)");
                    Console.WriteLine("  216. arcee-ai/virtuoso-large (in:$0.75 / out:$1.20)");
                    Console.WriteLine("  217. cohere/command-a (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  218. cohere/command-r-plus-08-2024 (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  219. deepcogito/cogito-v2.1-671b (in:$1.25 / out:$1.25)");
                    Console.WriteLine("  220. deepseek/deepseek-r1 (in:$0.70 / out:$2.50)");
                    Console.WriteLine("  221. deepseek/deepseek-r1-distill-llama-70b (in:$0.70 / out:$0.80)");
                    Console.WriteLine("  222. eleutherai/llemma_7b (in:$0.80 / out:$1.20)");
                    Console.WriteLine("  223. google/gemini-2.5-pro (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  224. google/gemini-2.5-pro-preview (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  225. google/gemini-2.5-pro-preview-05-06 (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  226. google/gemini-3-flash-preview (in:$0.50 / out:$3.00)");
                    Console.WriteLine("  227. google/gemini-3-pro-image-preview (in:$2.00 / out:$12.00)");
                    Console.WriteLine("  228. google/gemini-3-pro-preview (in:$2.00 / out:$12.00)");
                    Console.WriteLine("  229. google/gemini-3.1-pro-preview (in:$2.00 / out:$12.00)");
                    Console.WriteLine("  230. google/gemma-2-27b-it (in:$0.65 / out:$0.65)");
                    Console.WriteLine("  231. inflection/inflection-3-pi (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  232. inflection/inflection-3-productivity (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  233. mancer/weaver (in:$0.75 / out:$1.00)");
                    Console.WriteLine("  234. meta-llama/llama-3-70b-instruct (in:$0.51 / out:$0.74)");
                    Console.WriteLine("  235. microsoft/wizardlm-2-8x22b (in:$0.62 / out:$0.62)");
                    Console.WriteLine("  236. mistralai/mistral-large (in:$2.00 / out:$6.00)");
                    Console.WriteLine("  237. mistralai/mistral-large-2407 (in:$2.00 / out:$6.00)");
                    Console.WriteLine("  238. mistralai/mistral-large-2411 (in:$2.00 / out:$6.00)");
                    Console.WriteLine("  239. mistralai/mistral-large-2512 (in:$0.50 / out:$1.50)");
                    Console.WriteLine("  240. mistralai/mixtral-8x22b-instruct (in:$2.00 / out:$6.00)");
                    Console.WriteLine("  241. mistralai/mixtral-8x7b-instruct (in:$0.54 / out:$0.54)");
                    Console.WriteLine("  242. mistralai/pixtral-large-2411 (in:$2.00 / out:$6.00)");
                    Console.WriteLine("  243. moonshotai/kimi-k2 (in:$0.50 / out:$2.40)");
                    Console.WriteLine("  244. moonshotai/kimi-k2-0905:exacto (in:$0.60 / out:$2.50)");
                    Console.WriteLine("  245. morph/morph-v3-fast (in:$0.80 / out:$1.20)");
                    Console.WriteLine("  246. morph/morph-v3-large (in:$0.90 / out:$1.90)");
                    Console.WriteLine("  247. neversleep/noromaid-20b (in:$1.00 / out:$1.75)");
                    Console.WriteLine("  248. nousresearch/hermes-3-llama-3.1-405b (in:$1.00 / out:$1.00)");
                    Console.WriteLine("  249. nousresearch/hermes-4-405b (in:$1.00 / out:$3.00)");
                    Console.WriteLine("  250. nvidia/llama-3.1-nemotron-70b-instruct (in:$1.20 / out:$1.20)");
                    Console.WriteLine("  251. nvidia/llama-3.1-nemotron-ultra-253b-v1 (in:$0.60 / out:$1.80)");
                    Console.WriteLine("  252. openai/gpt-3.5-turbo (in:$0.50 / out:$1.50)");
                    Console.WriteLine("  253. openai/gpt-3.5-turbo-0613 (in:$1.00 / out:$2.00)");
                    Console.WriteLine("  254. openai/gpt-3.5-turbo-instruct (in:$1.50 / out:$2.00)");
                    Console.WriteLine("  255. openai/gpt-4.1 (in:$2.00 / out:$8.00)");
                    Console.WriteLine("  256. openai/gpt-4o (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  257. openai/gpt-4o-2024-08-06 (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  258. openai/gpt-4o-2024-11-20 (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  259. openai/gpt-4o-audio-preview (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  260. openai/gpt-4o-search-preview (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  261. openai/gpt-5 (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  262. openai/gpt-5-chat (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  263. openai/gpt-5-codex (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  264. openai/gpt-5-image-mini (in:$2.50 / out:$2.00)");
                    Console.WriteLine("  265. openai/gpt-5.1 (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  266. openai/gpt-5.1-chat (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  267. openai/gpt-5.1-codex (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  268. openai/gpt-5.1-codex-max (in:$1.25 / out:$10.00)");
                    Console.WriteLine("  269. openai/gpt-5.2 (in:$1.75 / out:$14.00)");
                    Console.WriteLine("  270. openai/gpt-5.2-chat (in:$1.75 / out:$14.00)");
                    Console.WriteLine("  271. openai/gpt-5.2-codex (in:$1.75 / out:$14.00)");
                    Console.WriteLine("  272. openai/gpt-audio (in:$2.50 / out:$10.00)");
                    Console.WriteLine("  273. openai/gpt-audio-mini (in:$0.60 / out:$2.40)");
                    Console.WriteLine("  274. openai/o3 (in:$2.00 / out:$8.00)");
                    Console.WriteLine("  275. openai/o3-mini (in:$1.10 / out:$4.40)");
                    Console.WriteLine("  276. openai/o3-mini-high (in:$1.10 / out:$4.40)");
                    Console.WriteLine("  277. openai/o4-mini (in:$1.10 / out:$4.40)");
                    Console.WriteLine("  278. openai/o4-mini-deep-research (in:$2.00 / out:$8.00)");
                    Console.WriteLine("  279. openai/o4-mini-high (in:$1.10 / out:$4.40)");
                    Console.WriteLine("  280. perplexity/sonar (in:$1.00 / out:$1.00)");
                    Console.WriteLine("  281. perplexity/sonar-deep-research (in:$2.00 / out:$8.00)");
                    Console.WriteLine("  282. perplexity/sonar-reasoning-pro (in:$2.00 / out:$8.00)");
                    Console.WriteLine("  283. qwen/qwen-max (in:$1.60 / out:$6.40)");
                    Console.WriteLine("  284. qwen/qwen-vl-max (in:$0.80 / out:$3.20)");
                    Console.WriteLine("  285. qwen/qwen3-coder-plus (in:$1.00 / out:$5.00)");
                    Console.WriteLine("  286. qwen/qwen3-max (in:$1.20 / out:$6.00)");
                    Console.WriteLine("  287. qwen/qwen3-max-thinking (in:$1.20 / out:$6.00)");
                    Console.WriteLine("  288. relace/relace-apply-3 (in:$0.85 / out:$1.25)");
                    Console.WriteLine("  289. relace/relace-search (in:$1.00 / out:$3.00)");
                    Console.WriteLine("  290. sao10k/l3-euryale-70b (in:$1.48 / out:$1.48)");
                    Console.WriteLine("  291. sao10k/l3.1-euryale-70b (in:$0.65 / out:$0.75)");
                    Console.WriteLine("  292. sao10k/l3.3-euryale-70b (in:$0.65 / out:$0.75)");
                    Console.WriteLine("  293. switchpoint/router (in:$0.85 / out:$3.40)");
                    Console.WriteLine("  294. thedrummer/skyfall-36b-v2 (in:$0.55 / out:$0.80)");
                    Console.WriteLine("  295. writer/palmyra-x5 (in:$0.60 / out:$6.00)");
                    Console.WriteLine("  296. z-ai/glm-4.5 (in:$0.55 / out:$2.00)");
                    Console.WriteLine("  297. z-ai/glm-4.5v (in:$0.60 / out:$1.80)");
                    Console.WriteLine("  298. z-ai/glm-5 (in:$0.95 / out:$2.55)");
                    break;

                case 3:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  ▲ PREMIUM  ($3.00+/1M input)");
                    Console.ResetColor();
                    Console.WriteLine(new string('─', 64));
                    Console.WriteLine("  299. aion-labs/aion-1.0 (in:$4.00 / out:$8.00)");
                    Console.WriteLine("  300. alpindale/goliath-120b (in:$3.75 / out:$7.50)");
                    Console.WriteLine("  301. anthracite-org/magnum-v4-72b (in:$3.00 / out:$5.00)");
                    Console.WriteLine("  302. anthropic/claude-3.5-sonnet (in:$6.00 / out:$30.00)");
                    Console.WriteLine("  303. anthropic/claude-3.7-sonnet (in:$3.00 / out:$15.00)");
                    Console.WriteLine("  304. anthropic/claude-3.7-sonnet:thinking (in:$3.00 / out:$15.00)");
                    Console.WriteLine("  305. anthropic/claude-opus-4 (in:$15.00 / out:$75.00)");
                    Console.WriteLine("  306. anthropic/claude-opus-4.1 (in:$15.00 / out:$75.00)");
                    Console.WriteLine("  307. anthropic/claude-opus-4.5 (in:$5.00 / out:$25.00)");
                    Console.WriteLine("  308. anthropic/claude-opus-4.6 (in:$5.00 / out:$25.00)");
                    Console.WriteLine("  309. anthropic/claude-sonnet-4 (in:$3.00 / out:$15.00)");
                    Console.WriteLine("  310. anthropic/claude-sonnet-4.5 (in:$3.00 / out:$15.00)");
                    Console.WriteLine("  311. anthropic/claude-sonnet-4.6 (in:$3.00 / out:$15.00)");
                    Console.WriteLine("  312. meta-llama/llama-3.1-405b (in:$4.00 / out:$4.00)");
                    Console.WriteLine("  313. meta-llama/llama-3.1-405b-instruct (in:$4.00 / out:$4.00)");
                    Console.WriteLine("  314. openai/gpt-3.5-turbo-16k (in:$3.00 / out:$4.00)");
                    Console.WriteLine("  315. openai/gpt-4 (in:$30.00 / out:$60.00)");
                    Console.WriteLine("  316. openai/gpt-4-0314 (in:$30.00 / out:$60.00)");
                    Console.WriteLine("  317. openai/gpt-4-1106-preview (in:$10.00 / out:$30.00)");
                    Console.WriteLine("  318. openai/gpt-4-turbo (in:$10.00 / out:$30.00)");
                    Console.WriteLine("  319. openai/gpt-4-turbo-preview (in:$10.00 / out:$30.00)");
                    Console.WriteLine("  320. openai/gpt-4o-2024-05-13 (in:$5.00 / out:$15.00)");
                    Console.WriteLine("  321. openai/gpt-4o:extended (in:$6.00 / out:$18.00)");
                    Console.WriteLine("  322. openai/gpt-5-image (in:$10.00 / out:$10.00)");
                    Console.WriteLine("  323. openai/gpt-5-pro (in:$15.00 / out:$120.00)");
                    Console.WriteLine("  324. openai/gpt-5.2-pro (in:$21.00 / out:$168.00)");
                    Console.WriteLine("  325. openai/o1 (in:$15.00 / out:$60.00)");
                    Console.WriteLine("  326. openai/o1-pro (in:$150.00 / out:$600.00)");
                    Console.WriteLine("  327. openai/o3-deep-research (in:$10.00 / out:$40.00)");
                    Console.WriteLine("  328. openai/o3-pro (in:$20.00 / out:$80.00)");
                    Console.WriteLine("  329. perplexity/sonar-pro (in:$3.00 / out:$15.00)");
                    Console.WriteLine("  330. perplexity/sonar-pro-search (in:$3.00 / out:$15.00)");
                    Console.WriteLine("  331. raifle/sorcererlm-8x22b (in:$4.50 / out:$4.50)");
                    Console.WriteLine("  332. sao10k/l3.1-70b-hanami-x1 (in:$3.00 / out:$3.00)");
                    Console.WriteLine("  333. x-ai/grok-3 (in:$3.00 / out:$15.00)");
                    Console.WriteLine("  334. x-ai/grok-3-beta (in:$3.00 / out:$15.00)");
                    Console.WriteLine("  335. x-ai/grok-4 (in:$3.00 / out:$15.00)");
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("  LOCAL");
                    Console.WriteLine(new string('─', 64));
                    Console.WriteLine("       qwen3-16k (Ollama) - Local, free");
                    Console.ResetColor();
                    break;
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Page {p + 1}/4  |  [N] Next  [P] Prev  [Q] Quit  | Total: 335 models");
            Console.ResetColor();
        }

        while (paginate)
        {
            PrintPage(page);
            var key = Console.ReadKey(true).Key;
            switch (key)
            {
                case ConsoleKey.N: page = Math.Min(page + 1, 3); break;
                case ConsoleKey.P: page = Math.Max(page - 1, 0); break;
                case ConsoleKey.Q: paginate = false; break;
            }
        }
        Console.Clear();
    }
}