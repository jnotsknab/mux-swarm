using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MuxSwarm.Setup;
using MuxSwarm.State;
using static MuxSwarm.Setup.Setup;

namespace MuxSwarm.Utils;

public static class CliCmdUtils
{
    public static void ShowKnowledgeGraph(Dictionary<string, McpClient> mcpClients, IList<McpClientTool>? mcpTools = null)
    {
        MuxConsole.WithSpinner("Fetching knowledge graph...", () =>
        {
            Task.Run(async () =>
            {
                var memoryClient = mcpClients.GetValueOrDefault("Memory");
                var readGraphTool = mcpTools?.FirstOrDefault(t => t.Name == "Memory_read_graph");

                if (readGraphTool != null && memoryClient != null)
                {
                    try
                    {
                        var result = await memoryClient.CallToolAsync(
                            readGraphTool.Name.Replace("Memory_", ""),
                            new Dictionary<string, object>()!
                        );
                        var text = string.Join("\n", result.Content
                            .OfType<TextContentBlock>()
                            .Select(b => b.Text));
                        MuxConsole.WritePanel("Knowledge Graph", text);
                    }
                    catch (Exception ex)
                    {
                        MuxConsole.WriteError($"Error reading memory: {ex.Message}");
                    }
                }
                else
                {
                    MuxConsole.WriteWarning("Memory client or tool not found.");
                }
            }).Wait();
        });
    }

    public static void HandleDockerExec(string cfgPath)
    {
        App.Config.IsUsingDockerForExec = !App.Config.IsUsingDockerForExec;
        MuxConsole.WriteInfo($"Docker Exec is now: {App.Config.IsUsingDockerForExec}");

        MuxConsole.WriteMuted(App.Config.IsUsingDockerForExec
            ? "Agents will route script execution, Python, and git operations through Docker containers. File I/O still uses Filesystem MCP directly."
            : "Agents will execute natively on the host. Docker sandbox is disabled.");

        Common.SaveConfig(App.Config);
        App.Config = LoadConfig(cfgPath);
        SwarmDefaults.PatchPromptPaths(App.Config);
    }

    public static void ShowExecutionLimits()
    {
        var l = ExecutionLimits.Current;
        var lines = string.Join("\n",
            "  Swarm / Parallel Swarm",
            $"    Progress Entry Budget:        {l.ProgressEntryBudget:N0} chars",
            $"    Cross-Agent Context Budget:   {l.CrossAgentContextBudget:N0} chars",
            $"    Progress Log Total Budget:    {l.ProgressLogTotalBudget:N0} chars",
            $"    Max Orchestrator Iterations:  {l.MaxOrchestratorIterations}",
            $"    Max Sub-Agent Iterations:     {l.MaxSubAgentIterations}",
            $"    Max Sub-Task Retries:         {l.MaxSubTaskRetries}",
            "",
            "  Single Agent",
            $"    Compaction Char Budget:       {l.CompactionCharBudget:N0} chars",
            $"    Compaction Max Message Chars: {l.CompactionMaxMessageChars:N0} chars",
            "",
            "  All Modes",
            $"    Max Stuck Count:              {l.MaxStuckCount}",
            $"    Activity Timeout:             {l.ActivityTimeoutSeconds}s"
        );
        MuxConsole.WritePanel("Execution Limits", lines);
    }

    public static void HandleSetMultiLineDelimiter(string? delim)
    {
        if (string.IsNullOrEmpty(delim))
        {
            MuxConsole.WriteWarning("Cannot set delimiter, invalid.");
            return;
        }

        MuxConsole.MultiLineDelimiter = delim;
        MuxConsole.UsingDelimiter = true;
        MuxConsole.WriteSuccess($"Multi-Line Delimiter set to: {delim}");
        MuxConsole.WriteMuted($"Paste your content, then type {delim} on its own line to send.");
    }

    public static void HandleMultiDelimiterToggle()
    {
        MuxConsole.UsingDelimiter = !MuxConsole.UsingDelimiter;
        MuxConsole.WriteInfo($"Multi-Line Delimiter is now: {MuxConsole.UsingDelimiter}");

        MuxConsole.WriteMuted(MuxConsole.UsingDelimiter
            ? $"Paste your content, then type {MuxConsole.MultiLineDelimiter} on its own line to send."
            : "Standard single-line input restored.");
    }


    /// <summary>
    /// Updates the single agent configuration by allowing the user to select from available agent definitions.
    /// </summary>
    /// <remarks>
    /// This method retrieves all available agent definitions from the swarm configuration and combines them
    /// with the currently configured single agent definition. It then displays a numbered list of distinct agents
    /// and prompts the user to select one either by index number or by name. The selected agent becomes the new
    /// active single agent configuration.
    /// </remarks>/// <summary>
    /// Swaps the current agent used in single agent mode with a different agent from the available definitions.
    /// </summary>
    /// <remarks>
    /// This method retrieves all available agent definitions from the swarm configuration and combines them
    /// with the currently configured single agent (if any), removing duplicates by name. It displays a numbered
    /// list of available agents to the user and prompts for selection. The user can enter either the number
    /// corresponding to an agent in the list or type the agent's name directly. Once a valid selection is made,
    /// the method updates the <see cref="SingleAgentOrchestrator.AgentDef"/> property with the chosen agent.
    /// If no matching agent is found, a warning message is displayed and the current agent configuration remains unchanged.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the agent definitions cannot be retrieved from the swarm configuration.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the current single agent definition cannot be loaded from configuration.
    /// </exception>
    public static void HandleAgentSwap()
    {
        var agentDefs = Common.GetAgentDefinitions(PlatformContext.SwarmPath);
        var defaultDef = SingleAgentOrchestrator.GetCurrSingleAgentDef(fromCfg: true);

        var allDefs = defaultDef != null
            ? new[] { defaultDef }.Concat(agentDefs.Where(a => !a.Name.Equals(defaultDef.Name, StringComparison.OrdinalIgnoreCase)))
            : agentDefs;

        var distinctDefs = allDefs.DistinctBy(a => a.Name).ToList();
        var agentNames = string.Join("\n", distinctDefs.Select((a, i) => $"  [{i + 1}] {a.Name}"));
        MuxConsole.WritePanel("Enter a number or name of the agent to swap", agentNames);

        string choice = MuxConsole.Prompt("Agent: ");

        Common.AgentDefinition? matched = null;
        if (int.TryParse(choice, out var index) && index >= 1 && index <= distinctDefs.Count)
            matched = distinctDefs[index - 1];
        else
            matched = distinctDefs.FirstOrDefault(d => d.Name.Equals(choice, StringComparison.OrdinalIgnoreCase));

        if (matched != null)
        {
            SingleAgentOrchestrator.AgentDef = matched;
            MuxConsole.WriteSuccess($"Successfully updated singular agent mode to utilize: {matched.Name}");
        }
        else
        {
            MuxConsole.WriteWarning($"No agent found matching: {choice}");
        }
    }

    /// <summary>
    /// Updates the model identifier for a selected agent or component in the swarm configuration.
    /// </summary>
    /// <remarks>
    /// This method reads the swarm configuration from the Swarm.json file, displays a list of available
    /// agents and components (CompactionAgent, SingleAgent, Orchestrator, and any configured Agents),
    /// prompts the user to select one, and then allows the user to specify a new model identifier.
    /// The updated configuration is persisted back to the file.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the swarm configuration file cannot be deserialized.
    /// </exception>
    public static void HandleModelSwap()
    {
        var json = File.ReadAllText(PlatformContext.SwarmPath);
        var config = JsonSerializer.Deserialize<SwarmConfig>(json)
            ?? throw new InvalidOperationException("Failed to deserialize Swarm.json");

        var slots = new List<(string Label, string? CurrentModel, Action<string> Setter)>();

        if (config.CompactionAgent != null)
            slots.Add(("CompactionAgent", config.CompactionAgent.Model, v => config.CompactionAgent.Model = v));

        if (config.SingleAgent != null)
            slots.Add((config.SingleAgent.Name.Length > 0 ? config.SingleAgent.Name : "SingleAgent", config.SingleAgent.Model, v => config.SingleAgent.Model = v));

        if (config.Orchestrator != null)
            slots.Add(("Orchestrator", config.Orchestrator.Model, v => config.Orchestrator.Model = v));

        foreach (var agent in config.Agents)
            slots.Add((agent.Name, agent.Model, v => agent.Model = v));

        if (slots.Count == 0)
        {
            MuxConsole.WriteWarning("No agents or orchestrator found in swarm.json");
            return;
        }

        var lines = string.Join("\n", slots.Select((s, i) =>
            $"  [{i + 1}] {s.Label} ({s.CurrentModel ?? "not set"})"));

        MuxConsole.WritePanel("Select an agent to update its model", lines);

        var choice = MuxConsole.Prompt("Agent: ");

        int selectedIndex = -1;
        if (int.TryParse(choice, out var num) && num >= 1 && num <= slots.Count)
            selectedIndex = num - 1;
        else
            selectedIndex = slots.FindIndex(s => s.Label.Equals(choice, StringComparison.OrdinalIgnoreCase));

        if (selectedIndex < 0)
        {
            MuxConsole.WriteWarning($"No agent found matching: {choice}");
            return;
        }

        var selected = slots[selectedIndex];

        MuxConsole.WriteBody("Provide a valid model ID for your configured provider. Examples by provider:");
        MuxConsole.WriteMuted("  OpenRouter:  openai/gpt-5.1-codex-max, google/gemini-2.5-pro-preview, anthropic/claude-sonnet-4.6, meta-llama/llama-4-maverick");
        MuxConsole.WriteMuted("  OpenAI:      gpt-5.1, gpt-4o, gpt-4o-mini");
        MuxConsole.WriteMuted("  Anthropic:   claude-opus-4-6, claude-sonnet-4-6, claude-haiku-4-5-20251001");
        MuxConsole.WriteMuted("  Google:      gemini-2.5-pro, gemini-2.5-flash");
        MuxConsole.WriteMuted("  DeepSeek:    deepseek-chat, deepseek-reasoner");
        MuxConsole.WriteLine();

        var modelId = MuxConsole.Prompt("Model ID: ");

        if (string.IsNullOrWhiteSpace(modelId))
        {
            MuxConsole.WriteWarning("No model ID provided.");
            return;
        }

        selected.Setter(modelId);

        File.WriteAllText(PlatformContext.SwarmPath, JsonSerializer.Serialize(config, CfgSerialOpts));

        MuxConsole.WriteSuccess($"Updated {selected.Label} model to: {modelId}");
    }

    public static void HandleProviderSwap()
    {
        var config = LoadConfig(PlatformContext.ConfigPath);
        var providers = config.LlmProviders.Where(p => p.Enabled).ToList();

        if (providers.Count == 0)
        {
            MuxConsole.WriteWarning("No enabled providers found in config.");
            return;
        }

        var current = App.ActiveProvider?.Name ?? "none";
        MuxConsole.WriteInfo($"Active provider: {current} ({App.ActiveProvider?.Endpoint ?? "no endpoint"})");
        MuxConsole.WriteLine();

        var lines = string.Join("\n", providers.Select((p, i) =>
            $"  [{i + 1}] {p.Name} — {p.Endpoint ?? "no endpoint"}{(p.Name.Equals(current, StringComparison.OrdinalIgnoreCase) ? " (active)" : "")}"));

        MuxConsole.WritePanel("Select a provider or press Enter to keep current", lines);

        var choice = MuxConsole.Prompt("Provider: ");

        if (string.IsNullOrWhiteSpace(choice))
        {
            MuxConsole.WriteMuted("No change — keeping current provider.");
            return;
        }

        ProviderConfig? matched = null;
        if (int.TryParse(choice, out var index) && index >= 1 && index <= providers.Count)
            matched = providers[index - 1];
        else
            matched = providers.FirstOrDefault(p => p.Name.Equals(choice, StringComparison.OrdinalIgnoreCase));

        if (matched == null)
        {
            MuxConsole.WriteWarning($"No provider found matching: {choice}");
            return;
        }

        App.ActiveProvider = matched;
        MuxConsole.WriteSuccess($"Provider switched to: {matched.Name} ({matched.Endpoint}), be sure to update your model ID's in Swarm.json");

        string setDefault = MuxConsole.Prompt("Set as default provider? (y/n): ");
        if (setDefault?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
        {
            config.LlmProviders.Remove(matched);
            config.LlmProviders.Insert(0, matched);

            File.WriteAllText(PlatformContext.ConfigPath, JsonSerializer.Serialize(config, CfgSerialOpts));

            MuxConsole.WriteSuccess($"{matched.Name} is now the default provider.");
        }
    }

    public static void HandleInteractiveWorkflow()
    {
        string pathToLoad = MuxConsole.Prompt("Enter the path to your workflow file: ");

        if (string.IsNullOrEmpty(pathToLoad))
        {
            MuxConsole.WriteWarning("No workflow file path provided.");
            return;
        }

        Workflow workflow = WorkflowHelper.Load(pathToLoad);

        if (string.IsNullOrEmpty(workflow.Name) || workflow.Steps.Count == 0)
        {
            MuxConsole.WriteWarning("The workflow you provided is invalid!");
            return;
        }

        MuxConsole.WriteSuccess($"Loaded {workflow.Name} ({workflow.Steps.Count} steps) - Running workflow in 3 seconds...");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        WorkflowHelper.RunWorkflow(workflow);

    }

    public static (JsonElement data, string sessionDir)? HandleSessionResume()
    {
        string sessionsDir = PlatformContext.SessionsDirectory;

        if (!Directory.Exists(sessionsDir))
        {
            MuxConsole.WriteWarning("No sessions directory found.");
            return null;
        }

        List<string> sessionDirs = Directory.GetDirectories(sessionsDir)
            .Where(d => Directory.GetFiles(d, "*.json").Length <= 2)
            .OrderByDescending(d => d)
            .ToList();

        if (sessionDirs.Count == 0)
        {
            MuxConsole.WriteWarning("No single-agent sessions found.");
            return null;
        }

        var lines = string.Join("\n", sessionDirs.Select((d, i) =>
        {
            var timestamp = Path.GetFileName(d);
            var file = Directory.GetFiles(d, "*.json").First();
            var size = new FileInfo(file).Length;
            var preview = Common.GetFirstUserMessage(file);
            return $"  [{i + 1}] {timestamp} ({size / 1024}KB) — {preview}";
        }));

        MuxConsole.WritePanel("Select a session to resume or press Enter to cancel", lines);

        var choice = MuxConsole.Prompt("Session: ");

        if (string.IsNullOrWhiteSpace(choice))
        {
            MuxConsole.WriteMuted("No session selected.");
            return null;
        }

        string? selectedDir = null;
        if (int.TryParse(choice, out var index) && index >= 1 && index <= sessionDirs.Count)
            selectedDir = sessionDirs[index - 1];
        else
            selectedDir = sessionDirs.FirstOrDefault(d =>
                Path.GetFileName(d).Equals(choice, StringComparison.OrdinalIgnoreCase));

        if (selectedDir == null)
        {
            MuxConsole.WriteWarning($"No session found matching: {choice}");
            return null;
        }

        var sessionFile = Directory.GetFiles(selectedDir, "*.json").First();

        try
        {
            var json = File.ReadAllText(sessionFile);
            var doc = JsonDocument.Parse(json);
            MuxConsole.WriteSuccess($"Loaded session: {Path.GetFileName(selectedDir)}");
            return (doc.RootElement.Clone(), selectedDir);
        }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"Failed to load session: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Displays the current status of the Mux-Swarm application in a formatted panel.
    /// </summary>
    /// <param name="mcpTools">The optional list of MCP client tools available to the application.</param>
    /// <param name="agentModels">A dictionary mapping agent names to their corresponding model identifiers.</param>
    public static void HandleStatus(IList<McpClientTool>? mcpTools, Dictionary<string, string> agentModels)
    {
        var provider = App.ActiveProvider;
        var agent = SingleAgentOrchestrator.AgentDef;
        var sessionsDir = PlatformContext.SessionsDirectory;

        var sessionCount = Directory.Exists(sessionsDir)
            ? Directory.GetDirectories(sessionsDir).Length
            : 0;

        var toolCount = mcpTools?.Count ?? 0;
        var skillCount = SkillLoader.GetSkillMetadata().Count;

        var modelLines = agentModels.Count > 0
            ? string.Join("\n", agentModels.Select(kvp => $"               {kvp.Key} -> {kvp.Value}"))
            : "               not resolved";

        var lines = string.Join("\n",
            $"  Provider:    {provider?.Name ?? "not set"} ({provider?.Endpoint ?? "no endpoint"})",
            $"  Agent:       {agent?.Name ?? "default"}",
            $"  Models:",
            modelLines,
            $"  Tools:       {toolCount}",
            $"  Skills:      {skillCount}",
            $"  Sessions:    {sessionCount}",
            $"  Docker Exec: {(App.Config.IsUsingDockerForExec ? "enabled" : "disabled")}"
        );

        MuxConsole.WritePanel("Mux-Swarm Status", lines);
    }

    public static void ShowLoadedSkills()
    {
        var skills = SkillLoader.GetSkillMetadata();
        if (skills.Count == 0)
        {
            MuxConsole.WriteWarning("No skills loaded.");
            MuxConsole.WriteMuted($"Skills directory: {PlatformContext.SkillsDirectory}");
            return;
        }

        var maxNameLen = skills.Max(s => s.Name.Length);
        var text = string.Join("\n", skills.Select(s => $"  {s.Name.PadRight(maxNameLen)}   {s.Description}"));
        MuxConsole.WritePanel($"Loaded Skills ({skills.Count})", text);
    }

    public static void ListSessions()
    {
        var sessionsDir = PlatformContext.SessionsDirectory;

        if (!Directory.Exists(sessionsDir))
        {
            MuxConsole.WriteWarning("No sessions directory found.");
            MuxConsole.WriteMuted($"Expected: {sessionsDir}");
            return;
        }

        var sessionDirs = Directory.GetDirectories(sessionsDir)
            .OrderByDescending(d => d)
            .ToList();

        if (sessionDirs.Count == 0)
        {
            MuxConsole.WriteWarning("No sessions found.");
            return;
        }

        var lines = new List<string>();

        foreach (var dir in sessionDirs)
        {
            string name = Path.GetFileName(dir);
            int fileCount = Directory.GetFiles(dir, "*_session.json", SearchOption.AllDirectories).Length;
            string type = fileCount > 1 ? "swarm" : "single";
            lines.Add($"  {name}  ({type}, {fileCount} agent{(fileCount != 1 ? "s" : "")})");
        }

        var text = string.Join("\n", lines);
        MuxConsole.WritePanel($"Sessions ({sessionDirs.Count})", text);
        MuxConsole.WriteMuted("Use /report <id> to generate a full audit report.");
    }

    public static void ReloadSkills()
    {
        MuxConsole.WithSpinner("Reloading skills...", () =>
        {
            SkillLoader.LoadSkills();
        });

        var skills = SkillLoader.GetSkillMetadata();
        if (skills.Count == 0)
        {
            MuxConsole.WriteWarning("No skills found after reload.");
            MuxConsole.WriteMuted($"Skills directory: {PlatformContext.SkillsDirectory}");
        }
        else
        {
            MuxConsole.WriteSuccess($"Reloaded {skills.Count} skills.");
        }
    }

    public static async Task ReloadMcpServersAsync(
        Func<AppConfig, Task<bool>> initMcpServers,
        string configPath)
    {
        MuxConsole.WriteInfo("Reloading MCP servers...");

        App.Config = LoadConfig(configPath);

        bool success = await initMcpServers(App.Config);

        if (success)
            MuxConsole.WriteSuccess("MCP servers reloaded.");
        else
            MuxConsole.WriteError("One or more MCP servers failed to reconnect.");
    }

    public static async Task HandleOnboard(
        Func<string, IChatClient> chatClientFactory,
        string singleAgentModel,
        IList<McpClientTool>? mcpTools,
        CancellationToken ct)
    {
        var contextDirectory = PlatformContext.ContextDirectory;
        var onboardPromptPath = Path.Combine(contextDirectory, "ONBOARD.md");

        if (!File.Exists(onboardPromptPath))
        {
            MuxConsole.WriteError($"ONBOARD.md not found at: {onboardPromptPath}");
            return;
        }

        var brainPath = Path.Combine(contextDirectory, "BRAIN.md");
        var memoryPath = Path.Combine(contextDirectory, "MEMORY.md");
        bool existing = File.Exists(brainPath) || File.Exists(memoryPath);

        if (existing)
        {
            var confirm = MuxConsole.Prompt("Existing BRAIN.md/MEMORY.md found. Update? (y/n): ");
            if (!confirm.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                MuxConsole.WriteMuted("Onboarding cancelled.");
                return;
            }
        }

        var rawPrompt = File.ReadAllText(onboardPromptPath);
        var resolvedPrompt = TokenInjector.InjectTokens(rawPrompt);

        MuxConsole.WriteInfo("Starting onboarding session...");
        MuxConsole.WriteMuted(existing
            ? $"Updating existing profile in: {contextDirectory}"
            : $"Files will be written to: {contextDirectory}");

        MuxConsole.InputOverride = new FallbackReader("Begin onboarding.", MuxConsole.InputOverride);

        await SingleAgentOrchestrator.ChatAgentAsync(
            client: chatClientFactory(singleAgentModel),
            ct,
            maxIterations: 3,
            mcpTools: mcpTools,
            continuous: false,
            systemPromptOverride: resolvedPrompt
        );
    }
    
    public static async Task HandleFullReload(Func<AppConfig, Task<bool>> initMcpServers,
        string configPath)
    {
        await ReloadMcpServersAsync(initMcpServers, configPath);
        ReloadSkills();
    }

    public static int HandleContToggle(bool toggle)
    {
        if (!toggle)
        {
            MuxConsole.WriteSuccess("Continuous execution has successfully been disabled!");
            return 300;
        }

        int minDelay = int.Parse(MuxConsole.Prompt("Enter a minimum delay in seconds between continuous iterations (default 300) : ", "300"));
        if (minDelay <= 0)
        {
            MuxConsole.WriteError("Minimum delay must be greater than 0, default of 300 utilized.");
            return 300;
        }
                    
        MuxConsole.WriteSuccess($"Continuous execution has successfully been enabled with a minimum delay of {minDelay} seconds between iterations!");
        return minDelay;
    }
    
    /// <summary>
    /// Generates report files from session data stored in the sessions directory.
    /// </summary>
    /// <param name="sessionId">
    /// The optional session identifier. If provided, generates a report only for the specified session.
    /// If null or empty, generates reports for all available sessions sorted in reverse chronological order.
    /// </param>
    public static void GenerateSessionReports(string? sessionId = null)
    {
        var sessionsDir = PlatformContext.SessionsDirectory;

        if (!Directory.Exists(sessionsDir))
        {
            MuxConsole.WriteWarning("No sessions directory found.");
            MuxConsole.WriteMuted($"Expected: {sessionsDir}");
            return;
        }

        var reportsDir = Path.Combine(PlatformContext.BaseDirectory, "Reports");
        Directory.CreateDirectory(reportsDir);

        List<string> sessionDirs;

        if (!string.IsNullOrEmpty(sessionId))
        {
            var target = Directory.GetDirectories(sessionsDir)
                .FirstOrDefault(d => Path.GetFileName(d)
                    .Equals(sessionId, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                MuxConsole.WriteWarning($"Session '{sessionId}' not found.");
                MuxConsole.WriteMuted($"Check: {sessionsDir}");
                return;
            }

            sessionDirs = [target];
        }
        else
        {
            sessionDirs = Directory.GetDirectories(sessionsDir)
                .OrderByDescending(d => d)
                .ToList();
        }

        if (sessionDirs.Count == 0)
        {
            MuxConsole.WriteWarning("No sessions found.");
            return;
        }

        int generated = 0;

        MuxConsole.WithSpinner($"Generating reports for {sessionDirs.Count} session(s)...", () =>
        {
            foreach (var dir in sessionDirs)
            {
                try
                {
                    string report = SessionSummarizer.GenerateDetailedReport(dir);
                    if (string.IsNullOrWhiteSpace(report)) continue;

                    string fileName = $"{Path.GetFileName(dir)}.md";
                    string outputPath = Path.Combine(reportsDir, fileName);
                    File.WriteAllText(outputPath, report);
                    generated++;
                }
                catch (Exception ex)
                {
                    MuxConsole.WriteWarning($"Failed to generate report for {Path.GetFileName(dir)}: {ex.Message}");
                }
            }
        });

        if (generated == 0)
        {
            MuxConsole.WriteWarning("No reports generated — sessions may be empty.");
        }
        else
        {
            MuxConsole.WriteSuccess($"Generated {generated} report(s) in {reportsDir}");
        }
    }
}