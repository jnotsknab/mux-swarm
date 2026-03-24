using System.Text.Json;
using MuxSwarm.Utils;

namespace MuxSwarm.Setup;

/// <summary>
/// Generates and heals the default swarm.json.
/// Model IDs are derived from the user's configured LLM provider rather than hardcoded,
/// so the swarm is always compatible with whatever endpoint/provider the user chose.
/// </summary>
public static class SwarmDefaults
{
    private static readonly JsonSerializerOptions SwarmSerialOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Unconditionally writes a fresh swarm.json, overwriting any existing file.
    /// Used at the end of RunSetup to ensure model IDs match the just-configured provider.
    /// Returns false if the provider is unrecognized.
    /// </summary>
    public static bool ForceWrite(AppConfig config)
    {
        var swarmPath = PlatformContext.SwarmPath;

        var dir = Path.GetDirectoryName(swarmPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return WriteDefaultSwarmJson(swarmPath, config);
    }

    /// <summary>
    /// Ensures a valid swarm.json exists. Creates a default if missing, backs up and recreates if unparseable.
    /// Must be called AFTER endpoint/provider config is collected so model IDs can be resolved.
    /// </summary>
    public static void EnsurePresent(AppConfig config, bool healIfBroken = true)
    {
        var swarmPath = PlatformContext.SwarmPath;

        var dir = Path.GetDirectoryName(swarmPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(swarmPath))
        {
            WriteDefaultSwarmJson(swarmPath, config);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[SETUP] No swarm.json found. Wrote default swarm to: {swarmPath}");
            Console.ResetColor();
            return;
        }

        if (!healIfBroken) return;

        // If user mangled it beyond parse, back it up and recreate a valid default.
        try
        {
            _ = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(swarmPath), SwarmSerialOpts);
        }
        catch
        {
            var backup = swarmPath + $".bak_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            File.Copy(swarmPath, backup, overwrite: true);
            WriteDefaultSwarmJson(swarmPath, config);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[SETUP] swarm.json was invalid. Backed up to: {backup}");
            Console.WriteLine($"[SETUP] Rewrote default swarm.json to: {swarmPath}");
            Console.ResetColor();
        }
    }

    private static bool WriteDefaultSwarmJson(string swarmPath, AppConfig config)
    {
        // Resolve model IDs from user's provider config rather than hardcoding.
        // Falls back to generic placeholders if nothing is configured yet.
        var models = ResolveModelIds(config);

        if (
            string.IsNullOrEmpty(models.OrchestratorModel) ||
            string.IsNullOrEmpty(models.AgentModel) ||
            string.IsNullOrEmpty(models.LightModel)
            )
        {
            PrintUnknownProviderWarning(swarmPath);
            return false;
        }

        static string P(string file) => Path.Combine("Prompts", "Agents", file).Replace("\\", "/");

        bool HasEnabled(string name) =>
            config.McpServers.TryGetValue(name, out var s) && s.Enabled;

        bool HasAny(string name) =>
            config.McpServers.ContainsKey(name);


        var singleAgentServers = new List<string>();
        if (HasEnabled("Filesystem")) singleAgentServers.Add("Filesystem");
        if (HasEnabled("Memory")) singleAgentServers.Add("Memory");
        if (HasEnabled("ChromaDB")) singleAgentServers.Add("ChromaDB");
        if (HasEnabled("BraveSearchMCP")) singleAgentServers.Add("BraveSearchMCP");
        if (HasEnabled("ReplShellMcp")) singleAgentServers.Add("ReplShellMcp");


        var orchestratorToolPatterns = new[] { "Filesystem_list_allowed_directories", "Filesystem_read_file", "Filesystem_read_text_file", "Filesystem_search_files", "Filesystem_list_directory" };

        var agents = new List<object>();

        // WebAgent
        {
            var mcp = new List<string>();
            if (HasEnabled("BraveSearchMCP")) mcp.Add("BraveSearchMCP");
            if (HasEnabled("Fetch")) mcp.Add("Fetch");
            if (HasEnabled("Filesystem")) mcp.Add("Filesystem");

            agents.Add(new
            {
                name = "WebAgent",
                description = "Handles web browsing, research, screen interaction, and internet-based tasks.",
                promptPath = P("web_agent.md"),
                model = models.AgentModel,
                mcpServers = mcp,
                canDelegate = true,
                toolPatterns = Array.Empty<string>()
            });
        }

        // CodeAgent
        {
            var mcp = new List<string>();
            if (HasEnabled("Filesystem")) mcp.Add("Filesystem");
            if (HasEnabled("BraveSearchMCP")) mcp.Add("BraveSearchMCP");
            if (HasEnabled("Fetch")) mcp.Add("Fetch");
            if (HasEnabled("ReplShellMcp")) mcp.Add("ReplShellMcp");

            /*var toolPatterns = PlatformContext.IsWindows
                ? new[] { "Windows_Shell" }
                : new[] { "Shell_" };*/

            agents.Add(new
            {
                name = "CodeAgent",
                description = "Handles code generation, editing, review, debugging, and file read/write for development tasks.",
                promptPath = P("code_agent.md"),
                model = models.AgentModel,
                mcpServers = mcp,
                canDelegate = true,
                toolPatterns = Array.Empty<string>()
            });
        }

        // MemoryAgent
        {
            var mcp = new List<string>();
            if (HasEnabled("Memory")) mcp.Add("Memory");
            if (HasEnabled("ChromaDB")) mcp.Add("ChromaDB");
            if (HasEnabled("Filesystem")) mcp.Add("Filesystem");

            agents.Add(new
            {
                name = "MemoryAgent",
                description = "Manages persistent knowledge — storing, retrieving, and searching entities, relations, and observations.",
                promptPath = P("memory_agent.md"),
                model = models.LightModel,
                mcpServers = mcp,
                canDelegate = false,
                toolPatterns = Array.Empty<string>()
            });
        }

        // DataAnalysisAgent (only if ReplShellMcp exists at all)
        if (HasAny("ReplShellMcp"))
        {
            var mcp = new List<string>();
            if (HasEnabled("Filesystem")) mcp.Add("Filesystem");
            if (HasEnabled("ReplShellMcp")) mcp.Add("ReplShellMcp");
            if (HasEnabled("Fetch")) mcp.Add("Fetch");
            if (HasEnabled("BraveSearchMCP")) mcp.Add("BraveSearchMCP");


            agents.Add(new
            {
                name = "DataAnalysisAgent",
                description =
                    "Specialized to in-session computation tasks through Python REPL MCP: data transformation, statistical analysis, numerical operations, and processing structured data (CSV, JSON). Results are returned immediately — no files are created or persisted.",
                promptPath = P("data_agent.md"),
                model = models.AgentModel,
                mcpServers = mcp,
                canDelegate = false,
                toolPatterns = Array.Empty<string>()
            });
        }

        var swarm = new
        {
            compactionAgent = new
            {
                model = models.CompactionModel,
                autoCompactTokenThreshold = 80000
            },
            singleAgent = new
            {
                name = "MuxAgent",
                promptPath = P("chat_prompt.md"),
                model = models.AgentModel,
                mcpServers = singleAgentServers,
                toolPatterns = Array.Empty<string>()
            },
            orchestrator = new
            {
                promptPath = P("od_fast.md"),
                model = models.OrchestratorModel,
                toolPatterns = orchestratorToolPatterns
            },
            agents
        };

        File.WriteAllText(swarmPath, JsonSerializer.Serialize(swarm, SwarmSerialOpts));
        return true;
    }

    private static void PrintUnknownProviderWarning(string swarmPath)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine("  Your API endpoint wasn't recognized as a known provider.");
        Console.WriteLine("  MuxSwarm can't auto-detect the correct model ID format.");
        Console.WriteLine();
        Console.WriteLine("  You'll need to manually edit your swarm config to set the");
        Console.WriteLine("  correct model IDs for your provider.");
        Console.WriteLine();
        Console.WriteLine($"  Swarm config path: {swarmPath}");
        Console.WriteLine();
        Console.WriteLine("  Each agent, the orchestrator, and the singleAgent all have a");
        Console.WriteLine("  \"model\" field that must match your provider's model ID format.");
        Console.WriteLine();
        Console.WriteLine("  Examples by provider:");
        Console.WriteLine("    OpenRouter:  anthropic/claude-sonnet-4-6");
        Console.WriteLine("    Anthropic:   claude-sonnet-4-6");
        Console.WriteLine("    OpenAI:      gpt-4o");
        Console.WriteLine("    Ollama:      llama3");
        Console.WriteLine();
        Console.ResetColor();
    }

    /// <summary>
    /// Resolves the appropriate model ID conventions based on the user's configured endpoint.
    /// Different providers use different model ID formats (OpenRouter, OpenAI, Anthropic, Ollama, etc.).
    /// Returns sensible placeholders when no provider is configured yet.
    /// </summary>
    private static SwarmModelSet ResolveModelIds(AppConfig config)
    {
        var endpoint = App.ActiveProvider?.Endpoint?.ToLowerInvariant()
                       ?? config.LlmProviders.FirstOrDefault(p => p.Enabled)?.Endpoint?.ToLowerInvariant()
                       ?? "";

        if (endpoint.Contains("openrouter.ai"))
        {
            return new SwarmModelSet
            {
                OrchestratorModel = "google/gemini-3.1-pro-preview",
                AgentModel = "google/gemini-3.1-pro-preview",
                LightModel = "google/gemini-3-flash-preview",
                CompactionModel = "google/gemini-3-flash-preview"
            };
        }

        if (endpoint.Contains("anthropic.com"))
        {
            return new SwarmModelSet
            {
                OrchestratorModel = "claude-sonnet-4-6",
                AgentModel = "claude-opus-4-6",
                LightModel = "claude-haiku-4-5-20251001",
                CompactionModel = "claude-haiku-4-5-20251001"
            };
        }

        if (endpoint.Contains("openai.com"))
        {
            return new SwarmModelSet
            {
                OrchestratorModel = "gpt-5.2-2025-12-11",
                AgentModel = "gpt-5.2-2025-12-11",
                LightModel = "gpt-5-mini-2025-08-07",
                CompactionModel = "gpt-5-mini-2025-08-07"
            };
        }

        if (endpoint.Contains("localhost") || endpoint.Contains("127.0.0.1") || endpoint.Contains("ollama"))
        {
            return new SwarmModelSet
            {
                OrchestratorModel = "llama3",
                AgentModel = "llama3",
                LightModel = "llama3",
                CompactionModel = "llama3"
            };
        }

        // Generic / unknown provider, issue warning
        return new SwarmModelSet();
    }

    /// <summary>
    /// Patches only the promptPath fields in an existing swarm.json to swap between
    /// standard and _docker variants. Preserves all other user customizations.
    /// </summary>
    public static void PatchPromptPaths(AppConfig config)
    {
        var swarmPath = PlatformContext.SwarmPath;
        if (!File.Exists(swarmPath)) return;

        var json = File.ReadAllText(swarmPath);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            PatchElement(writer, doc.RootElement, config);
        }

        File.WriteAllText(swarmPath, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static void PatchElement(Utf8JsonWriter writer, JsonElement element, AppConfig config)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    if (prop.Name == "promptPath" && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var original = prop.Value.GetString() ?? "";
                        var patched = SwapPromptVariant(original, config.IsUsingDockerForExec);
                        writer.WriteStringValue(patched);
                    }
                    else
                    {
                        PatchElement(writer, prop.Value, config);
                    }
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    PatchElement(writer, item, config);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string SwapPromptVariant(string promptPath, bool useDocker)
    {
        var dir = Path.GetDirectoryName(promptPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(promptPath);
        var ext = Path.GetExtension(promptPath);

        if (useDocker)
        {
            // Already docker variant
            if (name.EndsWith("_docker")) return promptPath;

            var dockerName = $"{name}_docker{ext}";
            var dockerPath = Path.Combine(dir, dockerName).Replace("\\", "/");
            var fullPath = Path.Combine(PlatformContext.BaseDirectory, dockerPath);
            return File.Exists(fullPath) ? dockerPath : promptPath;
        }
        
        // Strip _docker suffix if present
        if (!name.EndsWith("_docker")) return promptPath;

        var standardName = $"{name[..^7]}{ext}";
        var standardPath = Path.Combine(dir, standardName).Replace("\\", "/");
        return standardPath;
    }

    private class SwarmModelSet
    {
        public string OrchestratorModel { get; init; } = "";
        public string AgentModel { get; init; } = "";
        public string LightModel { get; init; } = "";
        public string CompactionModel { get; init; } = "";
    }
}
