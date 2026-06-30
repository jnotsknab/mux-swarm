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
        // /dockerexec is now shorthand for the sandbox backend: toggle between host and docker. The
        // docker-sandbox directive skill set + the IsUsingDockerForExec flag track the backend.
        bool turningOn = App.Config.Sandbox.Backend.Trim().ToLowerInvariant() is "host" or "" or "none";
        ApplySandboxBackend(turningOn ? "docker" : "host", image: null, cfgPath);
    }

    /// <summary>
    /// /sandbox [backend] [image] - hot-swap the execution sandbox backend (host/docker/podman/nerdctl/
    /// gvisor/kata/bwrap/firejail/sandbox-exec/custom). Validates the new backend BEFORE applying; on failure
    /// the current backend is untouched and the error is surfaced. With no args, prints current status.
    /// </summary>
    public static void HandleSandbox(string userInput, string cfgPath)
    {
        var parts = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            var s = App.Config.Sandbox;
            MuxConsole.WriteInfo($"Sandbox backend: {s.Backend}");
            if (!string.Equals(s.Backend, "host", StringComparison.OrdinalIgnoreCase))
            {
                MuxConsole.WriteMuted($"  image: {s.Image}");
                MuxConsole.WriteMuted(s.AllowedDomains.Count > 0
                    ? $"  network: allowlist [{string.Join(", ", s.AllowedDomains)}]"
                    : $"  network: {(s.Network ? "open" : "air-gapped")}");
                var mounts = MuxSwarm.Utils.NativeTools.SandboxBackend.ResolveMounts(App.Config.Filesystem);
                if (mounts.Count > 0)
                {
                    MuxConsole.WriteMuted($"  mounts (from filesystem.securityMode={App.Config.Filesystem.SecurityMode}):");
                    foreach (var m in mounts)
                        MuxConsole.WriteMuted($"    {m.HostPath} -> {m.GuestPath} {(m.ReadOnly ? "[ro]" : "[rw]")}");
                }
            }
            var err = MuxSwarm.Utils.NativeTools.SandboxBackend.Validate(s);
            MuxConsole.WriteMuted(err is null ? "  status: ready" : $"  status: NOT READY - {err}");
            MuxConsole.WriteMuted("Usage: /sandbox <host|docker|podman|nerdctl|gvisor|kata|bwrap|firejail|sandbox-exec|custom> [image]");
            return;
        }
        string backend = parts[1].Trim().ToLowerInvariant();
        string? image = parts.Length >= 3 ? parts[2].Trim() : null;
        ApplySandboxBackend(backend, image, cfgPath);
    }

    private static void ApplySandboxBackend(string backend, string? image, string cfgPath)
    {
        // Build a candidate config and VALIDATE before committing - never half-apply an unusable backend.
        var candidate = new SandboxConfig
        {
            Backend = backend,
            Image = image ?? App.Config.Sandbox.Image,
            Network = App.Config.Sandbox.Network,
            AllowedDomains = App.Config.Sandbox.AllowedDomains,
            Command = App.Config.Sandbox.Command,
        };
        var err = MuxSwarm.Utils.NativeTools.SandboxBackend.Validate(candidate);
        if (err is not null)
        {
            MuxConsole.WriteWarning($"Sandbox backend '{backend}' not applied: {err}");
            return;
        }

        App.Config.Sandbox = candidate;
        // Keep the legacy docker-exec flag in sync so the preamble's docker directive + bundled-docker
        // skill set track any container backend (docker/podman/nerdctl/gvisor all imply 'use the sandbox').
        bool containerized = backend is not ("host" or "none" or "");
        App.Config.IsUsingDockerForExec = containerized;

        Common.SaveConfig(App.Config);
        App.Config = LoadConfig(cfgPath);
        SwarmDefaults.PatchPromptPaths(App.Config);
        SkillLoader.LoadSkills();   // swap bundled vs bundled-docker so the directive set matches
        // Re-resolve the authoritative sandbox state so the preamble (ACTIVE block + /work + /host/*
        // mounts) tracks the new backend instead of the stale loose intent flag.
        MuxSwarm.Utils.NativeTools.SandboxRuntime.Refresh();

        MuxConsole.WriteInfo($"Sandbox backend is now: {backend}");
        MuxConsole.WriteMuted(containerized
            ? "New agent sessions run shell + Python execution inside the sandbox. Existing live sessions keep their current backend until they end."
            : "Agents execute natively on the host. Sandbox disabled.");
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
            $"    Max Tool Iterations / Turn:   {(l.MaxToolIterationsPerTurn > 0 ? l.MaxToolIterationsPerTurn.ToString() : "unlimited")}",
            $"    Max Auto-Continues / Turn:    {l.MaxAutoContinuesPerTurn}",
            "",
            "  All Modes",
            $"    Max Stuck Count:              {l.MaxStuckCount}",
            $"    Activity Timeout:             {l.ActivityTimeoutSeconds}s",

            "  Context Injection Posture",
            $"    Mode:              {l.ContextInjection}"
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

    public static bool HandleToggleSingleModeSubAgents(bool current, bool parallel = false)
    {
        current = !current;

        if (current && parallel)
        {
            MuxConsole.WriteInfo("Parallel Sub-Agent Delegation for the standard /agent interface has been enabled.");
            return current;
        }

        if (current && !parallel)
        {
            MuxConsole.WriteInfo("Sub-Agent Delegation for the standard /agent interface has been enabled.");
            return current;
        }

        MuxConsole.WriteInfo($"{(parallel ? "Parallel " : "")}Sub-Agent Delegation for the standard /agent interface has been disabled.");
        return current;
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
        var idxW = distinctDefs.Count.ToString().Length;
        var agentNames = string.Join("\n", distinctDefs.Select((a, i) => $"{(i + 1).ToString().PadLeft(idxW)}  {a.Name}"));
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

    public static void HandleMaxP()
    {
        string input = MuxConsole.Prompt("Max number of parallel Agents: ", defaultValue: "4");
        try
        {
            int maxP = int.Parse(input);

            if (maxP > 0)
            {
                App.MaxDegreeParallelism = maxP;
                MuxConsole.WriteSuccess($"Successfully updated Max Parallelism to: {maxP} - Agents with parallel delegation capabilities can now spawn up to {maxP} subagents at once.");
                return;
            }
            MuxConsole.WriteError("Max Parallelism must be greater than 0");
        }
        catch (Exception _)
        {
            MuxConsole.WriteError("Error setting Max Parallel Agents, ensure value is a positive integer");
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

        var idxW = slots.Count.ToString().Length;
        var lines = string.Join("\n", slots.Select((s, i) =>
            $"{(i + 1).ToString().PadLeft(idxW)}  {s.Label} ({s.CurrentModel ?? "not set"})"));

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
        MuxConsole.WriteInfo($"Active provider: {current} ({(App.ActiveProvider is { } ap ? ProviderEndpointLabel(ap) : "no endpoint")})");
        MuxConsole.WriteLine();

        var pIdxW = providers.Count.ToString().Length;
        var lines = string.Join("\n", providers.Select((p, i) =>
            $"{(i + 1).ToString().PadLeft(pIdxW)}  {p.Name} — {ProviderEndpointLabel(p)}{(p.Name.Equals(current, StringComparison.OrdinalIgnoreCase) ? " (active)" : "")}"));

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
        MuxConsole.WriteSuccess($"Provider switched to: {matched.Name} ({ProviderEndpointLabel(matched)}), be sure to update your model ID's in Swarm.json");

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

    /// <summary>
    /// Lightweight catalog of resumable single-agent sessions (id + first-user-message
    /// preview), newest first, for the live "/resume" autocomplete preview. Best-effort:
    /// returns an empty list on any IO error.
    /// </summary>
    public static List<(string Id, string Preview)> GetResumableSessions()
    {
        var outList = new List<(string, string)>();
        try
        {
            string sessionsDir = PlatformContext.SessionsDirectory;
            if (!Directory.Exists(sessionsDir)) return outList;
            foreach (var d in Directory.GetDirectories(sessionsDir)
                         .Where(d => Directory.GetFiles(d, "*.json").Length <= 2)
                         .OrderByDescending(d => d))
            {
                var id = Path.GetFileName(d);
                string preview = "";
                try
                {
                    var file = Directory.GetFiles(d, "*.json").FirstOrDefault();
                    if (file != null) preview = Common.GetFirstUserMessage(file);
                }
                catch { /* preview optional */ }
                // Fold any session tags into the preview so the /resume palette both shows and
                // fuzzy-matches them (the sidecar is .muxtag, invisible to the *.json detector).
                var tagLabel = SessionTags.TagLabel(d);
                if (!string.IsNullOrEmpty(tagLabel))
                    preview = string.IsNullOrEmpty(preview) ? $"#{tagLabel}" : $"#{tagLabel} - {preview}";
                outList.Add((id, preview));
            }
        }
        catch { /* best-effort */ }
        return outList;
    }

    public static (JsonElement data, string sessionDir)? HandleSessionResume(string? sessionId = null)
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

        // Non-interactive path: a session id was supplied (e.g. "/resume <id>" from
        // the web app's Resume button). Match by folder name and skip the prompt.
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var direct = sessionDirs.FirstOrDefault(d =>
                Path.GetFileName(d).Equals(sessionId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (direct == null)
            {
                MuxConsole.WriteWarning($"No resumable single-agent session found matching: {sessionId}");
                return null;
            }
            return LoadResumeSession(direct);
        }

        var idxW = sessionDirs.Count.ToString().Length;
        var lines = string.Join("\n", sessionDirs.Select((d, i) =>
        {
            var timestamp = Path.GetFileName(d);
            var file = Directory.GetFiles(d, "*.json").First();
            var size = new FileInfo(file).Length;
            var preview = Common.GetFirstUserMessage(file);
            // Collapse embedded newlines/runs of whitespace so a multi-line first message stays a
            // single list row (otherwise it splits into stray dot-prefixed orphan rows).
            preview = System.Text.RegularExpressions.Regex.Replace(preview ?? "", @"\s+", " ").Trim();
            var tagLabel = SessionTags.TagLabel(d);
            var tagPrefix = string.IsNullOrEmpty(tagLabel) ? "" : $"#{tagLabel} — ";
            var idxLabel = (i + 1).ToString().PadLeft(idxW);
            return $"{idxLabel}  {timestamp} ({size / 1024}KB) — {tagPrefix}{preview}";
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

        return LoadResumeSession(selectedDir);
    }

    /// <summary>Load a session's persisted state from its directory for resume.</summary>
    private static (JsonElement data, string sessionDir)? LoadResumeSession(string selectedDir)
    {
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
            ? string.Join("\n", agentModels.Select(kvp => $"  {kvp.Key} -> {kvp.Value}"))
            : "  not resolved";

        var lines = string.Join("\n",
            $"Provider     {provider?.Name ?? "not set"} ({provider?.Endpoint ?? "no endpoint"})",
            $"Agent        {agent?.Name ?? "default"}",
            $"Models",
            modelLines,
            $"Tools        {toolCount}",
            $"Skills       {skillCount}",
            $"Sessions     {sessionCount}",
            $"Docker Exec  {(App.Config.IsUsingDockerForExec ? "enabled" : "disabled")}",
            $"Sandbox      {App.Config.Sandbox.Backend}"
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

        // TUI: clean per-skill preview (name + one-line description) via the live-region
        // driver; classic/stdio fall back to the single panel.
        if (MuxConsole.RenderTuiSkills(skills.Select(s => (s.Name, s.Description)).ToList()))
            return;

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

    // /installskill: install a skill into the live skills dir, by curated name or from a GitHub URL.
    // Bare invocation lists curated installable names. Network-resilient (never throws to the menu).
    public static async Task HandleInstallSkillAsync(string userInput)
    {
        var parts = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // strip the command token + an optional "overwrite"/"--overwrite" flag
        bool overwrite = false;
        var rest = new List<string>();
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Equals("--overwrite", StringComparison.OrdinalIgnoreCase)
                || parts[i].Equals("overwrite", StringComparison.OrdinalIgnoreCase))
                overwrite = true;
            else
                rest.Add(parts[i]);
        }

        if (rest.Count == 0)
        {
            List<string> names = new();
            await MuxConsole.WithSpinnerAsync("Listing curated skills", async () =>
            {
                names = await SkillInstaller.ListCuratedAsync();
            });
            if (names.Count == 0)
            {
                MuxConsole.WriteWarning("Could not reach the curated skill sources (check your network).");
                MuxConsole.WriteMuted("Usage: /installskill <name> | /installskill <github-tree-url> [overwrite]");
                return;
            }
            MuxConsole.WritePanel("Installable skills (curated)",
                string.Join("\n", names.Select(n => "- " + n))
                + "\n\nInstall with: /installskill <name>  (add 'overwrite' to replace an existing one)");
            return;
        }

        string target = rest[0];
        string result = "";
        bool isUrl = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        await MuxConsole.WithSpinnerAsync($"Installing skill '{target}'", async () =>
        {
            result = isUrl
                ? await SkillInstaller.InstallFromUrlAsync(target, overwrite)
                : await SkillInstaller.InstallByNameAsync(target, overwrite);
        });

        if (result.StartsWith("Installed ", StringComparison.Ordinal))
            MuxConsole.WriteSuccess(result);
        else
            MuxConsole.WriteWarning(result);
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

    public static void HandleContextInject()
    {
        var choice = MuxConsole.Select("Auto-inject context for this session", new[]
        {
            "Full memory (BRAIN.md + MEMORY.md)",
            "Working memory only (MEMORY.md)",
            "None (preamble and system prompt only)",
            "Custom (file path or inline text)"
        });

        AutoInject.Current = choice switch
        {
            "Full memory (BRAIN.md + MEMORY.md)" => AutoInject.Mode.Full,
            "Working memory only (MEMORY.md)" => AutoInject.Mode.WorkingMemory,
            "None (preamble and system prompt only)" => AutoInject.Mode.None,
            _ => AutoInject.Mode.Custom
        };

        if (AutoInject.Current == AutoInject.Mode.Custom)
        {
            var input = MuxConsole.AskText("File path(s) comma separated or text to inject", null);
            string[] segments = input.Split(",", StringSplitOptions.TrimEntries);
            if (segments.Any(s => File.Exists(s)))
            {
                AutoInject.CustomContent = string.Join("\n", segments.Select(s =>
                    File.Exists(s) ? File.ReadAllText(s) : s));

                MuxConsole.WriteSuccess($"Auto-inject set , new sessions will be affected: {AutoInject.Current}");
                return;
            }

            AutoInject.CustomContent = input;
        }

        MuxConsole.WriteSuccess($"Auto-inject set, new sessions will be affected: {AutoInject.Current}");
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

    /// <summary>
    /// Build a relative-path file index of the current working directory for the live "@" file
    /// picker. Skips heavy / noise directories (.git, bin, obj, node_modules, .vs, etc.), caps
    /// the result so a giant repo can't blow up the picker, and returns forward-slash relative
    /// paths sorted shortest-first. Best-effort: any IO error yields an empty list.
    /// </summary>
    public static List<string> GetWorkspaceFiles(int cap = 4000)
    {
        var outList = new List<string>();
        try
        {
            // Index the configured workspace root (defaults to CWD; overridable via --workspace),
            // so an alias that launches mux from its install dir still points "@" at the project.
            string root = PlatformContext.WorkspaceRoot;
            var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", "bin", "obj", "node_modules", ".vs", ".vscode", ".idea",
                "packages", "dist", "build", ".depot", "TestResults", "__pycache__", ".venv",
            };

            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && outList.Count < cap)
            {
                var dir = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(dir); }
                catch { continue; }

                foreach (var entry in entries)
                {
                    if (outList.Count >= cap) break;
                    var name = Path.GetFileName(entry);
                    bool isDir = Directory.Exists(entry);
                    if (isDir)
                    {
                        if (skipDirs.Contains(name) || name.StartsWith('.')) continue;
                        stack.Push(entry);
                    }
                    else
                    {
                        var rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
                        outList.Add(rel);
                    }
                }
            }
        }
        catch { /* best-effort */ }

        outList.Sort((a, b) => a.Length != b.Length
            ? a.Length - b.Length
            : string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        return outList;
    }

    /// <summary>
    /// List configured teams (swarm.json teams[]) plus any persisted/resumable teams found in
    /// the install-dir Teams directory. Read-only - launching is /teams &lt;name&gt;.
    /// </summary>
    public static void HandleListTeams(SwarmConfig? config)
    {
        var configured = config?.Teams ?? new List<TeamConfig>();
        if (configured.Count == 0)
        {
            MuxConsole.WriteWarning("No teams configured. Add a \"teams\" array to swarm.json (see docs).");
        }
        else
        {
            MuxConsole.WriteInfo($"Configured teams ({configured.Count}):");
            foreach (var t in configured)
            {
                var members = t.Members is { Count: > 0 } ? string.Join(", ", t.Members) : "(none)";
                var lead = string.IsNullOrWhiteSpace(t.Lead) ? "Orchestrator" : t.Lead;
                MuxConsole.WriteMuted($"  {t.Name}  [{t.Coordination}]  lead={lead}  members: {members}");
                if (!string.IsNullOrWhiteSpace(t.Description))
                    MuxConsole.WriteMuted($"      {t.Description}");
            }
            MuxConsole.WriteMuted("  Launch with: /teams <name>");
        }

        var live = MuxSwarm.Utils.Teams.TeamState.LoadAll();
        if (live.Count > 0)
        {
            MuxConsole.WriteInfo($"Persisted/resumable teams ({live.Count}):");
            foreach (var s in live)
                MuxConsole.WriteMuted($"  {s.Name}  [{s.Coordination}]  status={s.Status}  last active {s.LastActive:yyyy-MM-dd HH:mm}");
        }
    }

    /// <summary>Display label for a provider's endpoint column: OAuth providers route direct, no URL.</summary>
    private static string ProviderEndpointLabel(ProviderConfig p) =>
        !string.IsNullOrWhiteSpace(p.AuthType) && p.AuthType.StartsWith("oauth", StringComparison.OrdinalIgnoreCase)
            ? $"direct {p.AuthType} (no endpoint)"
            : (p.Endpoint ?? "no endpoint");

    /// <summary>
    /// /login [provider] - subscription OAuth login via the local CLIProxyAPI sidecar. With no arg, shows a
    /// picker of the proxy's supported providers (claude, codex, kimi, ...). Ensures the sidecar is up,
    /// runs its native browser OAuth, and on success registers the single local 'cliproxy' provider entry
    /// (subsequent logins just join the proxy's dynamic router, routed by model id).
    /// </summary>
    public static async Task HandleLoginAsync(string userInput, string cfgPath)
    {
        var parts = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? providerId = parts.Length >= 2 ? parts[1].Trim().ToLowerInvariant() : null;

        var supported = MuxSwarm.Utils.Proxy.CliProxyManager.LoginProviders.Keys.ToList();
        if (providerId is null)
        {
            var labels = supported.ToList();
            labels.Add("cancel");
            string pick = MuxConsole.Select("Log in with which subscription provider?", labels);
            if (pick.StartsWith("cancel")) { MuxConsole.WriteMuted("Login cancelled."); return; }
            providerId = pick;
        }

        if (!MuxSwarm.Utils.Proxy.CliProxyManager.LoginProviders.ContainsKey(providerId))
        {
            MuxConsole.WriteWarning($"Unsupported provider '{providerId}'. Supported: {string.Join(", ", supported)}.");
            return;
        }

        MuxConsole.WriteInfo($"Starting the local CLIProxyAPI sidecar and opening your browser to log in with {providerId}...");
        MuxConsole.WriteMuted("(Subscription OAuth reuses the official client id - same posture as other subscription tools.)");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            bool ok = await MuxSwarm.Utils.Proxy.CliProxyManager.LoginAsync(providerId, cts.Token);
            if (!ok)
            {
                MuxConsole.WriteWarning($"Login for '{providerId}' did not complete.");
                return;
            }

            // First successful cliproxy login registers the single local provider entry; subsequent logins
            // just join the proxy's dynamic router (no new entry needed - it routes by model id).
            bool added = RegisterCliProxyProvider(cfgPath);
            if (added)
                MuxConsole.WriteSuccess($"Logged in. Registered local provider 'cliproxy' -> {MuxSwarm.Utils.Proxy.CliProxyManager.OpenAiEndpoint}.");
            else
                MuxConsole.WriteSuccess($"Logged in. '{providerId}' added to the cliproxy dynamic router (select it by model id).");
            MuxConsole.WriteMuted("Set your agent model id to a provider model (e.g. claude-opus-4-6, gpt-5-codex) in Swarm.json / via /model, then activate 'cliproxy' via /provider.");
        }
        catch (OperationCanceledException)
        {
            MuxConsole.WriteWarning("Login timed out / cancelled.");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"Login failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures the single local 'cliproxy' provider entry exists in config, pointing at the managed loopback
    /// endpoint with its api key resolved from the env var the manager sets at spawn. Returns true if it was
    /// newly added (false if it already existed). The entry is a plain OpenAI-compatible provider - the
    /// unchanged CreateOpenAiClient path serves it; the sidecar is lazily ensured at request time.
    /// </summary>
    private static bool RegisterCliProxyProvider(string cfgPath)
    {
        const string name = "cliproxy";
        var config = LoadConfig(cfgPath);
        config.LlmProviders ??= new List<ProviderConfig>();

        var existing = config.LlmProviders.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        string endpoint = MuxSwarm.Utils.Proxy.CliProxyManager.OpenAiEndpoint
            ?? $"http://127.0.0.1:{MuxSwarm.Utils.Proxy.CliProxyManager.PreferredPort}/v1";

        if (existing is not null)
        {
            existing.Endpoint = endpoint;
            existing.ApiKeyEnvVar = MuxSwarm.Utils.Proxy.CliProxyManager.ClientKeyEnvVar;
            File.WriteAllText(PlatformContext.ConfigPath, JsonSerializer.Serialize(config, CfgSerialOpts));
            App.Config = LoadConfig(cfgPath);
            return false;
        }

        config.LlmProviders.Add(new ProviderConfig
        {
            Name = name,
            Enabled = true,
            Endpoint = endpoint,
            ApiKeyEnvVar = MuxSwarm.Utils.Proxy.CliProxyManager.ClientKeyEnvVar,
        });
        File.WriteAllText(PlatformContext.ConfigPath, JsonSerializer.Serialize(config, CfgSerialOpts));
        App.Config = LoadConfig(cfgPath);
        return true;
    }

    /// <summary>
    /// /ping [provider] - test a configured provider's connectivity. For an OAuth provider it ensures a
    /// valid (refreshed) token and lists models via the provider endpoint; reports OK + latency or the
    /// precise error. With no arg, pings every provider that has a stored OAuth credential.
    /// </summary>
    public static async Task HandlePingAsync(string userInput)
    {
        var parts = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? providerId = parts.Length >= 2 ? parts[1].Trim().ToLowerInvariant() : null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await MuxSwarm.Utils.Proxy.CliProxyManager.EnsureRunningAsync(cts.Token);
            sw.Stop();
            MuxConsole.WriteInfo($"  cliproxy sidecar: OK  {sw.ElapsedMilliseconds}ms  ({MuxSwarm.Utils.Proxy.CliProxyManager.OpenAiEndpoint})");

            var files = await MuxSwarm.Utils.Proxy.CliProxyManager.GetAuthFilesAsync(cts.Token);
            if (providerId is not null)
            {
                bool ready = await MuxSwarm.Utils.Proxy.CliProxyManager.IsProviderReadyAsync(providerId, cts.Token);
                MuxConsole.WriteInfo($"  {providerId}: {(ready ? "READY" : "not logged in - run /login " + providerId)}");
            }
            else if (files.Count == 0)
            {
                MuxConsole.WriteMuted("  No providers logged in yet. Run /login <provider>.");
            }
            else
            {
                foreach (var f in files)
                {
                    string state = f.Disabled ? "disabled" : f.Unavailable ? "unavailable" : f.Status is { Length: > 0 } ? f.Status : "ready";
                    string who = f.Email is { Length: > 0 } e ? $" ({e})" : "";
                    MuxConsole.WriteInfo($"  {f.Provider}: {state}{who}");
                }
            }
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"  cliproxy ping FAILED - {ex.Message}");
        }
    }

    /// <summary>
    /// /proxy [status|update] - manage the local CLIProxyAPI sidecar. `status` (default) reports the pinned
    /// version, running state + endpoint, and per-provider auth readiness. `update` re-downloads + verifies
    /// the pinned binary and restarts the sidecar if it was running.
    /// </summary>
    public static async Task HandleProxyAsync(string userInput)
    {
        var parts = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length >= 2 ? parts[1].Trim().ToLowerInvariant() : "status";

        try
        {
            if (sub == "update")
            {
                MuxConsole.WriteInfo($"Updating CLIProxyAPI to the pinned v{MuxSwarm.Utils.Proxy.CliProxyManager.PinnedVersion}...");
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await MuxSwarm.Utils.Proxy.CliProxyManager.UpdateAsync(cts.Token);
                MuxConsole.WriteSuccess($"CLIProxyAPI is now at v{MuxSwarm.Utils.Proxy.CliProxyManager.PinnedVersion}.");
                return;
            }

            // status
            MuxConsole.WriteInfo($"CLIProxyAPI pinned version: v{MuxSwarm.Utils.Proxy.CliProxyManager.PinnedVersion}");
            MuxConsole.WriteMuted($"  binary present: {MuxSwarm.Utils.Proxy.CliProxyManager.IsBinaryPresent} ({MuxSwarm.Utils.Proxy.CliProxyManager.ExecutablePath})");
            if (MuxSwarm.Utils.Proxy.CliProxyManager.IsRunning)
            {
                MuxConsole.WriteMuted($"  running: yes -> {MuxSwarm.Utils.Proxy.CliProxyManager.OpenAiEndpoint}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var files = await MuxSwarm.Utils.Proxy.CliProxyManager.GetAuthFilesAsync(cts.Token);
                if (files.Count == 0) MuxConsole.WriteMuted("  providers: none logged in (run /login <provider>)");
                foreach (var f in files)
                    MuxConsole.WriteMuted($"  provider {f.Provider}: {(f.Disabled ? "disabled" : f.Unavailable ? "unavailable" : f.Status is { Length: > 0 } s ? s : "ready")}");
            }
            else
            {
                MuxConsole.WriteMuted("  running: no (starts lazily on first use or /login)");
            }
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"/proxy {sub} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// /memory and /deep: deep-memory (reflectionAgent) status + toggle. Replaces the old bare
    /// knowledge-graph JSON dump, which now lives behind "/memory show". Persists reflectionAgent.mode
    /// to Swarm.json so the user never edits files.
    ///   /memory               -> status (mode, model, budget, poll, scope, store health, count)
    ///   /memory deep | /deep  -> enable deep mode (persist)
    ///   /memory standard      -> back to standard (persist); "/deep off" is the alias
    ///   /memory show          -> the legacy KG JSON dump (power users)
    /// </summary>
    public static void HandleMemory(
        string userInput, Dictionary<string, McpClient> mcpClients, IList<McpClientTool>? mcpTools = null)
    {
        var parts = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "/memory";
        string? sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;

        // "/deep" with no arg == enable; "/deep off|standard" == disable.
        if (cmd == "/deep")
            sub = sub is "off" or "standard" or "0" or "false" ? "standard" : "deep";

        switch (sub)
        {
            case "deep":
                SetMemoryMode("deep");
                break;
            case "standard":
            case "off":
                SetMemoryMode("standard");
                break;
            case "show":
            case "graph":
            case "kg":
                ShowKnowledgeGraph(mcpClients, mcpTools);
                break;
            case "set":
                // /memory set <key> <value> - mutate + persist a reflectionAgent tunable.
                SetMemoryOption(parts.Length > 2 ? parts[2] : null, parts.Length > 3 ? string.Join(' ', parts[3..]) : null);
                break;
            case null:
            case "status":
                ShowMemoryStatus();
                break;
            default:
                MuxConsole.WriteWarning("Usage: /memory [deep|standard|show|set <key> <value>] (or /deep [off]).");
                MuxConsole.WriteMuted("  set keys: model, budget, poll, floor, scope, max, historyWindow, maxDigsPerTick, digMaxFilesScanned, digMaxMatches, digMaxReadChars");
                break;
        }
    }

    private static void ShowMemoryStatus()
    {
        var s = MuxSwarm.Utils.Memory.ReflectionGatherer.Status();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"mode            : {(s.deep ? "deep" : "standard")}");
        sb.AppendLine($"reflection model: {s.model}");
        sb.AppendLine($"inject budget   : {s.budget} tokens");
        sb.AppendLine($"poll interval   : {s.poll}s");
        sb.AppendLine($"relevance floor : {s.floor:0.##}");
        sb.AppendLine($"scope           : {s.scope}");
        sb.AppendLine($"history window  : {s.historyWindow} msgs");
        sb.AppendLine($"reflections     : {s.count} / {s.max} max  in {MuxSwarm.Utils.Memory.ReflectionStore.Directory}");
        sb.AppendLine($"last reflection : {(s.last is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "(none yet)")}");
        sb.AppendLine($"chroma (semantic): {(s.chroma ? "connected" : "absent - lexical fallback")}");
        sb.AppendLine($"knowledge graph : {(s.kg ? "connected" : "absent - filesystem only")}");
        if (!s.deep)
            sb.AppendLine("\nDeep memory is OFF. Enable with: /memory deep   (or /deep)");
        MuxConsole.WritePanel("Deep Memory", sb.ToString().TrimEnd());
    }


    /// <summary>
    /// /memory set &lt;key&gt; &lt;value&gt; - mutate a single reflectionAgent tunable and persist it to
    /// Swarm.json. Validates the value per key (numeric ranges, scope enum) and confirms. Never
    /// throws into the caller. Keys mirror the /memory status display + the Swarm.json schema.
    /// </summary>
    private static void SetMemoryOption(string? key, string? rawValue)
    {
        key = (key ?? string.Empty).Trim().ToLowerInvariant();
        var val = (rawValue ?? string.Empty).Trim();
        if (key.Length == 0 || val.Length == 0)
        {
            MuxConsole.WriteWarning("Usage: /memory set <key> <value>");
            MuxConsole.WriteMuted("  keys: model, budget, poll, floor, scope, max, historyWindow, maxDigsPerTick, digMaxFilesScanned, digMaxMatches, digMaxReadChars");
            return;
        }

        try
        {
            var swarm = App.SwarmConfig ?? new SwarmConfig();
            swarm.ReflectionAgent ??= new MuxSwarm.Utils.ReflectionConfig();
            var r = swarm.ReflectionAgent;

            bool Int(out int n) => int.TryParse(val, out n);
            string applied;

            switch (key)
            {
                case "model":
                    r.Model = val.Equals("null", StringComparison.OrdinalIgnoreCase) || val.Equals("default", StringComparison.OrdinalIgnoreCase)
                        ? null : val;
                    applied = $"model = {r.Model ?? "(orchestrator default)"}";
                    break;
                case "budget":
                case "injecttokenbudget":
                    if (!Int(out var b) || b < 100) { MuxConsole.WriteWarning("budget must be an integer >= 100 (tokens)."); return; }
                    r.InjectTokenBudget = b; applied = $"inject budget = {b} tokens";
                    break;
                case "poll":
                case "pollintervalseconds":
                    if (!Int(out var p) || p < 10) { MuxConsole.WriteWarning("poll must be an integer >= 10 (seconds)."); return; }
                    r.PollIntervalSeconds = p; applied = $"poll interval = {p}s";
                    break;
                case "floor":
                case "relevancefloor":
                    if (!double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) || f < 0 || f > 1)
                    { MuxConsole.WriteWarning("floor must be a number between 0 and 1."); return; }
                    r.RelevanceFloor = f; applied = $"relevance floor = {f:0.##}";
                    break;
                case "scope":
                    var sc = val.ToLowerInvariant();
                    if (sc != "lead" && sc != "all") { MuxConsole.WriteWarning("scope must be 'lead' or 'all'."); return; }
                    r.Scope = sc; applied = $"scope = {sc}";
                    break;
                case "max":
                case "maxreflections":
                    if (!Int(out var mx) || mx < 10) { MuxConsole.WriteWarning("max must be an integer >= 10."); return; }
                    r.MaxReflections = mx; applied = $"maxReflections = {mx}";
                    break;
                case "historywindow":
                    if (!Int(out var hw) || hw < 4) { MuxConsole.WriteWarning("historyWindow must be an integer >= 4 (messages)."); return; }
                    r.HistoryWindow = hw; applied = $"historyWindow = {hw} msgs";
                    break;
                case "maxdigspertick":
                    if (!Int(out var md) || md < 0) { MuxConsole.WriteWarning("maxDigsPerTick must be an integer >= 0."); return; }
                    r.MaxDigsPerTick = md; applied = $"maxDigsPerTick = {md}";
                    break;
                case "digmaxfilesscanned":
                    if (!Int(out var df) || df < 50) { MuxConsole.WriteWarning("digMaxFilesScanned must be an integer >= 50."); return; }
                    r.DigMaxFilesScanned = df; applied = $"digMaxFilesScanned = {df}";
                    break;
                case "digmaxmatches":
                    if (!Int(out var dm) || dm < 1) { MuxConsole.WriteWarning("digMaxMatches must be an integer >= 1."); return; }
                    r.DigMaxMatches = dm; applied = $"digMaxMatches = {dm}";
                    break;
                case "digmaxreadchars":
                    if (!Int(out var dr) || dr < 200) { MuxConsole.WriteWarning("digMaxReadChars must be an integer >= 200."); return; }
                    r.DigMaxReadChars = dr; applied = $"digMaxReadChars = {dr}";
                    break;
                default:
                    MuxConsole.WriteWarning($"Unknown memory key '{key}'. Keys: model, budget, poll, floor, scope, max, historyWindow, maxDigsPerTick, digMaxFilesScanned, digMaxMatches, digMaxReadChars.");
                    return;
            }

            File.WriteAllText(PlatformContext.SwarmPath, JsonSerializer.Serialize(swarm, CfgSerialOpts));
            App.SwarmConfig = swarm;
            MuxConsole.WriteSuccess($"Deep memory: {applied}  (persisted to Swarm.json).");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"Failed to update memory option: {ex.Message}");
        }
    }

    private static void SetMemoryMode(string mode)
    {
        try
        {
            var swarm = App.SwarmConfig ?? new SwarmConfig();
            swarm.ReflectionAgent ??= new MuxSwarm.Utils.ReflectionConfig();
            swarm.ReflectionAgent.Mode = mode;
            // Keep the top-level alias in sync so the persisted file is unambiguous.
            swarm.MemoryMode = mode;
            File.WriteAllText(PlatformContext.SwarmPath, JsonSerializer.Serialize(swarm, CfgSerialOpts));
            App.SwarmConfig = swarm;

            if (mode == "deep")
            {
                bool chroma = MuxSwarm.Utils.Memory.ReflectionStore.ChromaAvailable();
                bool kg = MuxSwarm.Utils.Memory.ReflectionStore.KgAvailable();
                MuxConsole.WriteSuccess("Deep memory ENABLED. Background reflection gatherer is active.");
                MuxConsole.WriteMuted(chroma || kg
                    ? $"  accelerators: {(chroma ? "chroma " : "")}{(kg ? "knowledge-graph" : "")}".TrimEnd()
                    : "  accelerators: none connected - using filesystem reflections (lexical recall).");
            }
            else
            {
                MuxConsole.WriteSuccess("Deep memory set to STANDARD. Reflection subsystem is inert.");
            }
        }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"Failed to update memory mode: {ex.Message}");
        }
    }
}
