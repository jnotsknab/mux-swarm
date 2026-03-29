using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

public static class LocalAiFunctions
{
    public static AIFunction ListSkillsTool = null!;
    public static AIFunction ReadSkillTool = null!;
    public static AIFunction SleepTool = null!;
    public static AIFunction MuxRefreshTool = null!;
    public static AIFunction AskUserTool = null!;

    static LocalAiFunctions()
    {
        CreateSkillFuncs();
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
            method: () =>
            {
                var skills = SkillLoader.GetSkillMetadata();
                return string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"));
            },
            name: "list_skills",
            description: "List all available skills with their descriptions. Call this first to discover what skills are available before calling read_skill."
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
                int seconds
                ) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                return $"Slept for {seconds} seconds.";
            },
            name: "system_sleep",
            description: "Pause execution for N minutes without consuming tokens. Use between polling cycles, while waiting for long-running processes, or for scheduled intervals."
        );

        MuxRefreshTool = AIFunctionFactory.Create(
            method: async (
                [System.ComponentModel.Description("True to refresh skills.")] bool refreshSkills,
                [System.ComponentModel.Description("True to refresh MCP servers.")] bool refreshMcpServers,
                [System.ComponentModel.Description("True to refresh Config.json and Swarm.json.")] bool refreshConfigs
            ) =>
            {
                if (refreshSkills) CliCmdUtils.ReloadSkills();
                if (refreshMcpServers) await CliCmdUtils.ReloadMcpServersAsync(App.InitMcpServersAsync, App.ConfigPath);

                if (refreshConfigs)
                {
                    App.Config = Setup.Setup.LoadConfig(App.ConfigPath);
                    App.SwarmConfig = Setup.Setup.LoadSwarm();
                }
                return $"Skills Refreshed is: {refreshSkills}, MCP Servers Refreshed is: {refreshMcpServers}, Config Files Refreshed is: {refreshConfigs}. System refresh complete. ";

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
                    "Comma-separated list of options for 'select' or 'multi_select' types. Ignored for 'text' and 'confirm'.")]
                string? options = null,

                [System.ComponentModel.Description(
                    "Default value if the user provides no input. For 'confirm', use 'yes' or 'no'.")]
                string? defaultValue = null
            ) =>
            {   
                MuxConsole.WriteMuted($"[ask_user] question={question}, type={type}, options={options}, default={defaultValue}");
                var normalized = (type ?? "text").Trim().ToLowerInvariant();

                var tcs = new TaskCompletionSource<string>();

                var thread = new Thread(() =>
                {
                    try
                    {
                        MuxConsole.WriteLine();
                        MuxConsole.WriteMarkup(
                            $"  [#{MuxConsole.PromptColor}]\u27f5  Agent is requesting your input[/]",
                            "Agent is requesting your input");

                        string result = normalized switch
                        {
                            "confirm" => MuxConsole.Confirm(question,
                                defaultValue?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) ?? true)
                                ? "User confirmed: yes" : "User declined: no",
                            "select" => MuxConsole.AskSelect(question, options),
                            "multi_select" => MuxConsole.AskMultiSelect(question, options),
                            _ => MuxConsole.AskText(question, defaultValue)
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

                StdinCancelMonitor.Instance?.Pause();
                EscapeKeyListener.Pause();
                try
                {
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
                "DO use before destructive operations or when multiple valid approaches exist."
        );
    }
}