using Microsoft.Extensions.AI;
using MuxSwarm.State;

namespace MuxSwarm.Utils;

public static class LocalAiFunctions
{
    public static AIFunction ListSkillsTool = null!;
    public static AIFunction ReadSkillTool = null!;
    public static AIFunction SleepTool = null!;
    public static AIFunction MuxRefreshTool = null!;
    public static AIFunction AskUserTool = null!;
    public static AIFunction ReadDelegationTool = null!;

    private static readonly SemaphoreSlim _askUserGate = new(1, 1);

    static LocalAiFunctions()
    {
        CreateSkillFuncs();
        CreateReadDelegationTool();
    }

    /// <summary>
    /// read_delegation: surgical reader over a sub-agent delegation whose full raw output was spilled
    /// to disk and returned to the lead only as a pointer/handle (size-tiered context passing). Lets
    /// the lead pull specific detail on demand instead of carrying the whole blob in context.
    /// </summary>
    private static void CreateReadDelegationTool()
    {
        ReadDelegationTool = AIFunctionFactory.Create(
            method: (
                [System.ComponentModel.Description("The d:Agent#N handle from a delegation pointer (e.g. d:WebAgent#3).")]
                string handle,
                [System.ComponentModel.Description("Optional regex/substring to grep within the raw output; returns matching lines plus a small context window.")]
                string? pattern,
                [System.ComponentModel.Description("Optional: return only the first N lines (used when no pattern is given).")]
                int? head,
                [System.ComponentModel.Description("Optional: return only the last N lines (used when no pattern is given).")]
                int? tail
            ) =>
            {
                if (string.IsNullOrWhiteSpace(handle))
                    return "[read_delegation] a handle is required (e.g. d:WebAgent#3 from a delegation pointer).";
                return DelegationStore.ReadSlice(handle, pattern, head, tail, DelegationStore.ReadMaxChars);
            },
            name: "read_delegation",
            description: "Read the FULL raw output of a prior sub-agent delegation that was spilled to disk and " +
                         "returned to you only as a pointer/handle. Use to pull specific detail on demand without " +
                         "loading everything: pass a 'pattern' to grep, or 'head'/'tail' for line slices. Output is bounded.");
    }

    public static AIFunction CreateAnalyzeImageTool(
        Func<string, IChatClient> chatClientFactory,
        string? visionModel = null)
    {
        return AIFunctionFactory.Create(
            method: async (
                [System.ComponentModel.Description("Absolute file path to the image to analyze. Must be within an allowed directory.")]
                string imagePath,
                [System.ComponentModel.Description("Optional specific question or focus for the analysis. If omitted, provides a general description.")]
                string? prompt
            ) =>
            {
                if (!File.Exists(imagePath))
                    return $"File not found: {imagePath}";

                var ext = Path.GetExtension(imagePath).ToLowerInvariant();
                var mimeType = ext switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    _ => (string?)null
                };

                if (mimeType is null)
                    return $"Unsupported image format: {ext}. Supported: png, jpg, jpeg, gif, webp, bmp.";

                var modelId = visionModel;
                if (string.IsNullOrEmpty(modelId))
                {
                    var agentDef = SingleAgentOrchestrator.GetCurrSingleAgentDef();
                    if (agentDef != null)
                    {
                        var models = Common.GetAgentDefinitions(PlatformContext.SwarmPath);
                    }
                    modelId = App.ActiveProvider != null ? null : null;
                }

                if (string.IsNullOrEmpty(modelId))
                    return "No vision model configured. Set 'visionModel' in swarm.json.";

                try
                {
                    var imageBytes = await File.ReadAllBytesAsync(imagePath);
                    var analysisPrompt = string.IsNullOrWhiteSpace(prompt)
                        ? "Describe what you see in this image in detail."
                        : prompt;

                    var message = new ChatMessage(ChatRole.User, [
                        new DataContent(imageBytes, mimeType),
                        new TextContent(analysisPrompt)
                    ]);

                    var client = chatClientFactory(modelId);
                    var response = await client.GetResponseAsync([message]);

                    var result = response?.Text;
                    return string.IsNullOrWhiteSpace(result)
                        ? "Vision model returned an empty response."
                        : $"[Analysis of {Path.GetFileName(imagePath)}]\n{result}";
                }
                catch (Exception ex)
                {
                    return $"Failed to analyze image: {ex.Message}";
                }
            },
            name: "analyze_image",
            description: "Analyze an image file using a vision-capable model. Returns a text description of the image contents. " +
                         "The image is processed in a separate call and does not enter conversation context. " +
                         "Use this after capturing screenshots or when you need to understand visual content in a file."
        );
    }

    private static void CreateSkillFuncs()
    {
        ListSkillsTool = AIFunctionFactory.Create(
            method: (
                [System.ComponentModel.Description(
                    "When true, include each skill's one-line description (more tokens). Default false returns names only.")]
                bool withDescriptions = false
            ) =>
            {
                var skills = SkillLoader.GetSkillMetadata();
                return withDescriptions
                    ? string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"))
                    : string.Join("\n", skills.Select(s => $"- {s.Name}"));
            },
            name: "list_skills",
            description: "List available skill NAMES (cheap, names-only by default). Call this first to discover "
                       + "what skills exist. Pass withDescriptions=true to also get each skill's one-line description, "
                       + "or call read_skill <name> for a skill's full instructions."
        );

        ReadSkillTool = AIFunctionFactory.Create(
            method: (
                [System.ComponentModel.Description("Name of the skill to load. Call list_skills first if you are unsure of available skill names.")]
                string skillName
            ) =>
            {
                var content = SkillLoader.ReadSkill(skillName);
                if (content != null)
                    return content;

                var available = SkillLoader.GetSkillMetadata();
                var listing = string.Join("\n", available.Select(s => $"- {s.Name}: {s.Description}"));
                return $"Skill '{skillName}' not found. Here are the currently available skills — call read_skill again with a valid name:\n{listing}";
            },
            name: "read_skill",
            description: "Read the full instructions for a skill by name. Call list_skills first to discover available skills. " +
                         "Read the relevant skill BEFORE starting a task to follow its best practices."
        );

        SleepTool = AIFunctionFactory.Create(
            method: async (
                [System.ComponentModel.Description("Seconds to pause.")]
                int seconds,
                // Bound automatically by the function-invocation middleware to the turn's token, so
                // Esc (the EscapeKeyListener cancels the turn CTS) interrupts the sleep instead of
                // blocking until it elapses. Fallback: when no cancellable token is supplied (default
                // CancellationToken.None), this behaves exactly as the prior uninterruptible delay.
                CancellationToken cancellationToken = default
                ) =>
            {
                if (seconds <= 0) return "Slept for 0 seconds.";
                var startedUtc = DateTime.UtcNow;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
                    return $"Slept for {seconds} seconds.";
                }
                catch (OperationCanceledException)
                {
                    // Turn cancelled mid-sleep: report actual elapsed so the model's time budgeting stays honest.
                    int elapsed = (int)Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds);
                    return $"Sleep interrupted after {elapsed}s of {seconds}s requested (cancelled).";
                }
            },
            name: "system_sleep",
            description: "Pause execution for N seconds without consuming tokens. Use for scheduled intervals or fixed backoff. " +
                         "When waiting on a background shell job or the Python worker, PREFER wait_job_progress / wait_python_progress " +
                         "(or check_job_status / check_python_status): they block until real progress and return only new output, " +
                         "which is strictly better than sleeping blindly."
        );

        MuxRefreshTool = AIFunctionFactory.Create(
            method: async (
                [System.ComponentModel.Description("True to refresh skills.")] bool refreshSkills,
                [System.ComponentModel.Description("True to refresh MCP servers.")] bool refreshMcpServers,
                [System.ComponentModel.Description("True to refresh Config.json and Swarm.json.")] bool refreshConfigs,
                [System.ComponentModel.Description("True to refresh / restart the Mux Swarm Daemon")] bool refreshDaemon
            ) =>
            {
                if (refreshSkills) CliCmdUtils.ReloadSkills();
                if (refreshMcpServers) await CliCmdUtils.ReloadMcpServersAsync(App.InitMcpServersAsync, App.ConfigPath);

                if (refreshConfigs)
                {
                    App.Config = Setup.Setup.LoadConfig(App.ConfigPath);
                    App.SwarmConfig = Setup.Setup.LoadSwarm();
                }

                if (refreshDaemon)
                {
                    App.DaemonRunner?.DisposeAsync();
                    if (App.Config.Daemon != null) App.DaemonRunner = new(App.Config.Daemon);

                    if (App.ServePort > 0)
                    {
                        foreach (var trigger in App.Config.Daemon.Triggers
                                     .Where(t => t.Type == "status" && t.Restart &&
                                                 t.Check != null && t.Check.Contains($":{App.ServePort}")))
                        {
                            App.DaemonRunner?.RegisterRestart(trigger.Check!,
                                () => ServeMode.StartAsync(App.ServePort));
                        }
                    }

                    App.DaemonRunner?.Start(
                        chatClientFactory: modelId => App.CreateChatClient(modelId),
                        mcpTools: App.GetMcpTools()!.Cast<AITool>().ToList(),
                        agentModels: Common.LoadAgentModels());
                }

                // A refresh reconnects MCP servers and rebuilds App.McpTools, but the CURRENTLY-RUNNING
                // agent captured its tool list at session construction and does NOT re-read it mid-session.
                // So if an MCP server had FAILED, reconnecting it here will not surface its tools into this
                // live session - the user must exit and re-enter (e.g. /qc then /resume) to rebuild the agent
                // session object and re-inject the recovered tools. Surface that explicitly on an MCP refresh.
                string note = refreshMcpServers
                    ? " NOTE: if an MCP server was failing, its tools will NOT propagate into this live session"
                      + " - exit and re-enter the session (e.g. /qc then /resume) to rebuild the agent and pick"
                      + " up the reconnected tools."
                    : "";

                return $"Skills Refreshed is: {refreshSkills}, MCP Servers Refreshed is: {refreshMcpServers}, Config Files Refreshed is: {refreshConfigs}. System refresh complete.{note}";

            },
            name: "mux_refresh",
            description: "Refresh the Mux-Swarm runtime. Selectively reload skills, MCP servers, and/or config files (config.json, swarm.json). " +
             "Call this after writing or modifying a skill file, updating swarm topology, changing model assignments, or adding MCP servers. " +
             "Changes take effect immediately without restarting the runtime."
        );

        AskUserTool = AIFunctionFactory.Create(
            method: async (
                [System.ComponentModel.Description(
                    "The question to present to the user. Be specific about what you need to proceed.")]
                string question,

                [System.ComponentModel.Description(
                    "Question type: 'text' for free-form input (default), 'confirm' for yes/no, " +
                    "'select' for single choice from the options list, 'multi_select' for multiple choices.")]
                string? type = "text",

                [System.ComponentModel.Description(
                    "Options separated by '|' for 'select' or 'multi_select' types. " +
                    "Example: 'Yes, go ahead|Not yet|Skip this step'. Ignored for 'text' and 'confirm'.")]
                string? options = null,

                [System.ComponentModel.Description(
                    "Default value if the user provides no input. For 'confirm', use 'yes' or 'no'.")]
                string? defaultValue = null
            ) =>
            {
                var normalized = (type ?? "text").Trim().ToLowerInvariant();

                var tcs = new TaskCompletionSource<string>();

                var thread = new Thread(() =>
                {
                    try
                    {
                        MuxConsole.WriteLine();

                        string result = normalized switch
                        {
                            "confirm" => ((Func<string>)(() =>
                            {
                                var cc = MuxConsole.ConfirmChoice(question,
                                    defaultValue?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) ?? true);
                                if (cc.Cancelled) return "User cancelled the prompt (no yes/no answer given).";
                                return cc.Value == "yes" ? "User confirmed: yes" : "User declined: no";
                            }))(),
                            "select" => MuxConsole.AskSelect(question, options),
                            "multi_select" => MuxConsole.AskMultiSelect(question, options),
                            _ => ((Func<string>)(() =>
                            {
                                var r = MuxConsole.AskText(question, defaultValue);
                                return r;
                            }))()
                        };

                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetResult($"Error reading input: {ex.Message}");
                    }
                })
                {
                    IsBackground = true,
                    Name = "AskUser-IO"
                };

                try
                {
                    EscapeKeyListener.Pause();
                    StdinCancelMonitor.Instance?.Pause();
                    thread.Start();
                    return await tcs.Task;
                }
                finally
                {
                    EscapeKeyListener.Resume();
                    StdinCancelMonitor.Instance?.Resume();
                }
            },
            name: "ask_user",
            description:
                "Pause and ask the user a question. Use when you need clarification, want to confirm a plan " +
                "before executing, need the user to choose between approaches, or require info that cannot be " +
                "inferred from context. Supports 'text' (free-form), 'confirm' (yes/no), 'select' (pick one), " +
                "and 'multi_select' (pick several). Do NOT use for trivial decisions you can make yourself. " +
                "DO use before destructive operations or when multiple valid approaches exist. " +
                "Note: for confirm/select/multi_select the user may CANCEL the prompt or enter a CUSTOM " +
                "free-text response outside the offered options; handle either result gracefully without erroring."
        );
    }
}