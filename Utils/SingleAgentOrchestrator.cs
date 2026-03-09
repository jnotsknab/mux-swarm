using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace MuxSwarm.Utils;

public static class SingleAgentOrchestrator
{
    private const double CharsPerToken = 3.5;

    public static Common.AgentDefinition? AgentDef = null;

    public static Common.AgentDefinition? GetCurrSingleAgentDef(bool fromCfg = false)
    {   
        if (AgentDef != null && !fromCfg) return AgentDef;

        var paths = new[]
        {
            MultiAgentOrchestrator.SwarmConfPath,
            PlatformContext.SwarmPath
        };

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<SwarmConfig>(json);
                if (config?.SingleAgent != null)
                {
                    var def = Common.ParseSingleAgentDefinition(config);
                    if (!fromCfg) AgentDef = def;
                    return def;
                }
            }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"[AGENT] Failed to parse singleAgent from {path}: {ex.Message}");
            }
        }
        return null;
    }
    

    private static bool IsQuitCommand(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return true;

        var trimmed = input.Trim();
        return trimmed.Equals("/qc", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/qm", StringComparison.OrdinalIgnoreCase);
    }
    
    private static ModelOpts? GetSingleAgentModelOpts()
    {
        if (!File.Exists(MultiAgentOrchestrator.SwarmConfPath))
            return null;

        try
        {
            var json = File.ReadAllText(MultiAgentOrchestrator.SwarmConfPath);
            var swarm = JsonSerializer.Deserialize<SwarmConfig>(json);
            return swarm?.SingleAgent?.ModelOpts;
        }
        catch { return null; }
    }
    
    public static async Task ChatAgentAsync(
        IChatClient? client,
        CancellationToken cancellationToken,
        int maxIterations = 15,
        IList<McpClientTool>? mcpTools = null,
        bool showToolResultCalls = false,
        Func<string, IChatClient>? chatClientFactory = null,
        int autoCompactTokenThreshold = 80_000,
        bool persistSession = true,
        string? incomingGoal = null,
        bool continuous = false,
        string? goalId = null,
        uint minDelaySeconds = 0,
        uint persistIntervalSeconds = 0,
        uint sessionRetention = 0,
        bool prodMode = false,
        JsonElement? resumedSession = null,
        string? resumedSessionDir = null)
    {
        MuxConsole.WriteBanner(persistSession ? "AGENTIC CHAT INTERFACE" : "STATELESS AGENTIC CHAT INTERFACE");
        MuxConsole.WriteMuted("Type /qc to exit, /compact to compress context. Press [Escape] to cancel the current turn.");

        SkillLoader.LoadSkills();
        var singleAgentDef = GetCurrSingleAgentDef();

        if (singleAgentDef != null && singleAgentDef.CanDelegate)
            MuxConsole.WriteWarning($"[AGENT] {singleAgentDef.Name} is configured with delegation capabilities. Delegation is not supported in single-agent mode and will be disabled. All other capabilities remain unaffected.");

        IList<AITool> allTools = (mcpTools ?? Array.Empty<McpClientTool>()).Cast<AITool>().ToList();
        IList<AITool> filteredTools = singleAgentDef?.ToolFilter(allTools) ?? allTools;

        if (filteredTools.Count == 0)
            MuxConsole.WriteWarning($"[AGENT] Matched 0 tools. Check mcpServers in swarm.json singleAgent block.");
        else
            MuxConsole.WriteSuccess($"[AGENT] {filteredTools.Count} tools available");

        MuxConsole.WriteLine();
        MuxConsole.WriteRule();

        string initialGoal;
        if (!string.IsNullOrWhiteSpace(incomingGoal))
        {
            initialGoal = incomingGoal;
            MuxConsole.WriteMuted($"[GOAL] {(goalId != null ? $"({goalId}) " : "")}{initialGoal}");
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();

            MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");
            initialGoal = Console.ReadLine() ?? "";

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (IsQuitCommand(initialGoal))
        {
            MuxConsole.WriteSuccess("Exited from Chat interface successfully!");
            return;
        }

        if (string.IsNullOrEmpty(singleAgentDef?.SystemPromptPath))
        {
            MuxConsole.WriteError("[AGENT] singleAgent.promptPath not set in swarm.json.");
            return;
        }

        var systemPrompt = await Common.LoadPromptAsync(singleAgentDef.SystemPromptPath);
        
        if (continuous)
            systemPrompt += "\n\n[CONTINUOUS MODE] You are running in continuous autonomous mode. Use the sleep tool if you need to wait before continuing. When the task is complete, provide a final summary.";
        
        var listSkillsTool = AIFunctionFactory.Create(
            method: () =>
            {
                var skills = SkillLoader.GetSkillMetadata(singleAgentDef?.Name);
                return string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"));
            },
            name: "list_skills",
            description: "List all available skills with their descriptions. Call this first to discover what skills are available before calling read_skill."
        );

        var readSkillTool = AIFunctionFactory.Create(
            method: (
                [System.ComponentModel.Description("Name of the skill to load. Call list_skills first if you are unsure of available skill names.")]
                string skillName
            ) =>
            {
                var content = SkillLoader.ReadSkill(skillName);
                if (content != null)
                    return content;

                var available = SkillLoader.GetSkillMetadata(singleAgentDef?.Name);
                var listing = string.Join("\n", available.Select(s => $"- {s.Name}: {s.Description}"));
                return $"Skill '{skillName}' not found. Here are the currently available skills — call read_skill again with a valid name:\n{listing}";
            },
            name: "read_skill",
            description: "Read the full instructions for a skill by name. Call list_skills first to discover available skills. " +
                         "Read the relevant skill BEFORE starting a task to follow its best practices."
        );

        var singleAgentTools = (IList<AITool>)[listSkillsTool, readSkillTool, LocalAiFunctions.SleepTool, .. filteredTools];

        var agentChatOptions = new ChatOptions
        {
            Instructions = systemPrompt,
            Tools = [.. singleAgentTools!]
        };

        // Merge modelOpts from swarm.json if present
        var singleAgentOpts = GetSingleAgentModelOpts();
        if (singleAgentOpts is not null)
        {
            var modelChatOpts = singleAgentOpts.ToChatOptions();
            if (modelChatOpts is not null)
            {
                agentChatOptions.Temperature = modelChatOpts.Temperature;
                agentChatOptions.TopP = modelChatOpts.TopP;
                agentChatOptions.TopK = modelChatOpts.TopK;
                agentChatOptions.MaxOutputTokens = modelChatOpts.MaxOutputTokens;
                agentChatOptions.FrequencyPenalty = modelChatOpts.FrequencyPenalty;
                agentChatOptions.PresencePenalty = modelChatOpts.PresencePenalty;
                agentChatOptions.Seed = modelChatOpts.Seed;
            }
        }

        AIAgent? agent = client?.AsAIAgent(new ChatClientAgentOptions
        {
            Name = singleAgentDef.Name,
            ChatOptions = agentChatOptions
        });

        if (agent == null)
        {
            MuxConsole.WriteError($"[AGENT] Failed to initialize {singleAgentDef.Name}. Verify your configuration and API credentials.");
            return;
        }
        
        string currentGoal = initialGoal;
        
        var sessionTimestamp = resumedSessionDir != null
            ? Path.GetFileName(resumedSessionDir)
            : DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        
        var session = resumedSession.HasValue
            ? await agent.DeserializeSessionAsync(resumedSession.Value)
            : await agent.CreateSessionAsync();
        
        var conversationHistory = new List<ChatMessage>();

        IChatClient? compactionClient = null;
        ChatOptions? compactionChatOptions = null;
        bool compactionResolved = false;
        
        IChatClient? ResolveCompactionClient()
        {
            if (compactionResolved) return compactionClient;
            compactionResolved = true;

            if (chatClientFactory == null) return null;

            try
            {
                var swarmJson = File.ReadAllText(MultiAgentOrchestrator.SwarmConfPath);
                var swarmConfig = JsonSerializer.Deserialize<SwarmConfig>(swarmJson);

                if (swarmConfig?.CompactionAgent != null)
                {
                    if (!string.IsNullOrEmpty(swarmConfig.CompactionAgent.Model))
                        compactionClient = chatClientFactory(swarmConfig.CompactionAgent.Model);

                    if (swarmConfig.CompactionAgent.AutoCompactTokenThreshold > 0)
                        autoCompactTokenThreshold = swarmConfig.CompactionAgent.AutoCompactTokenThreshold;
                    
                    compactionChatOptions = swarmConfig.CompactionAgent.ModelOpts?.ToChatOptions();
                }

                var compactionModel = swarmConfig?.CompactionAgent?.Model;

                if (!string.IsNullOrEmpty(compactionModel))
                    compactionClient = chatClientFactory(compactionModel);
            }
            catch { /* no compaction available */ }

            return compactionClient;
        }

        async Task<bool> TryCompactAsync()
        {
            var cc = ResolveCompactionClient();
            if (cc == null)
            {
                MuxConsole.WriteWarning("No compaction model configured. Set compactionAgent in swarm.json.");
                return false;
            }

            int beforeTokens = Common.EstimateTokenCount(conversationHistory);

            ChatMessage? compactedMsg = null;

            await MuxConsole.WithSpinnerAsync("Compacting conversation history", async () =>
            {
                compactedMsg = await ResultCompactor.CompactConversationAsync(
                    conversationHistory, cc, chatOptions: compactionChatOptions);

                compactedMsg = new ChatMessage(ChatRole.User,
                    compactedMsg.Text + "\n\n[SYSTEM: This is a context restoration message. Do not respond. Await the next user message.]");

                session = await agent.CreateSessionAsync();
                conversationHistory.Clear();
                conversationHistory.Add(compactedMsg);

                await foreach (var _ in agent.RunStreamingAsync([compactedMsg], session)) { }
            });

            int afterTokens = (int)Math.Ceiling(
                (compactedMsg?.Text?.Length ?? 0) / 3.5);
            MuxConsole.WriteSuccess($"Compacted: ~{beforeTokens:N0} -> ~{afterTokens:N0} tokens");
            return true;
        }

        var lastPersistTime = DateTime.UtcNow;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            int estimatedTokens = Common.EstimateTokenCount(conversationHistory);
            if (estimatedTokens > autoCompactTokenThreshold)
            {
                MuxConsole.WriteInfo($"Context approaching limit (~{estimatedTokens:N0} tokens). Auto-compacting...");
                await TryCompactAsync();
            }

            List<ChatMessage> messages = [new(ChatRole.User, currentGoal)];
            conversationHistory.Add(messages[0]);

            int stuckCount = 0;

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var escapeListener = EscapeKeyListener.Start(turnCts, cancellationToken);

            bool wasInterrupted = false;

            try
            {
                for (int i = 0; i < maxIterations; i++)
                {
                    turnCts.Token.ThrowIfCancellationRequested();

                    StringBuilder responseText = new();

                    MuxConsole.WriteAgentTurnHeader(singleAgentDef.Name);

                    ThinkingIndicator? thinking = null;
                    bool startedStreaming = false;
                    bool currentlyStreaming = false;

                    try
                    {
                        thinking = MuxConsole.BeginThinking(singleAgentDef.Name);

                        var calledTools = new List<string>();

                        using var activityTimeout = ActivityTimeout.Start(TimeSpan.FromMinutes(3), turnCts.Token);

                        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session)
                                           .WithCancellation(activityTimeout.Token))
                        {
                            activityTimeout.Ping();

                            if (!string.IsNullOrEmpty(update.Text))
                            {
                                if (!currentlyStreaming)
                                {
                                    MuxConsole.BeginStreaming();
                                    currentlyStreaming = true;
                                    startedStreaming = true;
                                }

                                MuxConsole.WriteStream(update.Text);
                                responseText.Append(update.Text);
                            }

                            foreach (AIContent content in update.Contents)
                            {
                                if (content is FunctionCallContent functionCall)
                                {
                                    if (currentlyStreaming)
                                    {
                                        currentlyStreaming = false;
                                        thinking?.Dispose();
                                        thinking = MuxConsole.ResumeThinking(singleAgentDef.Name);
                                        calledTools.Add(functionCall.Name);
                                        thinking.UpdateStatus($"[calling: {string.Join(", ", calledTools)}]");
                                    }
                                    else
                                    {
                                        calledTools.Add(functionCall.Name);
                                        thinking?.UpdateStatus($"[calling: {string.Join(", ", calledTools)}]");
                                    }
                                }
                                else if (content is FunctionResultContent functionResult)
                                {
                                    if (!currentlyStreaming && thinking != null)
                                    {
                                        thinking.Dispose();
                                        thinking = MuxConsole.BeginThinking(singleAgentDef.Name);
                                        if (calledTools.Count > 0)
                                            thinking.UpdateStatus($"[calling: {string.Join(", ", calledTools)}]");
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (currentlyStreaming)
                        {
                            try { MuxConsole.EndStreaming(); } catch { /* ignore */ }
                        }

                        thinking?.Dispose();
                    }

                    MuxConsole.WriteAgentTurnFooter();

                    string response = responseText.ToString();

                    if (!string.IsNullOrWhiteSpace(response))
                        conversationHistory.Add(new ChatMessage(ChatRole.Assistant, response));

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        stuckCount++;
                        MuxConsole.WriteWarning($"Empty response ({stuckCount}/3)");

                        if (stuckCount >= 3)
                        {
                            MuxConsole.WriteError("Stuck repeatedly — aborting turn.");
                            break;
                        }

                        messages.Clear();
                        messages.Add(new ChatMessage(ChatRole.User,
                            "Your last response was empty. Please continue or summarize where you are."));
                        continue;
                    }

                    break;
                }
            }
            catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                wasInterrupted = true;
                MuxConsole.WriteLine();
                MuxConsole.WriteWarning("Turn cancelled by user (Escape key pressed).");
            }
            catch (Exception ex)
            {
                MuxConsole.WriteError(ex.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (wasInterrupted)
                MuxConsole.WriteInfo("Ready for next input.");

            bool shouldPersist = persistSession;
            if (shouldPersist && persistIntervalSeconds > 0)
                shouldPersist = (DateTime.UtcNow - lastPersistTime).TotalSeconds >= persistIntervalSeconds;

            if (shouldPersist)
            {
                await Common.PersistChatSessionAsync(
                    agent,
                    session,
                    sessionTimestamp,
                    resumedSession.HasValue ? Common.FindSessionDirectory(sessionTimestamp) : null);

                lastPersistTime = DateTime.UtcNow;
            }

            int currentTokens = Common.EstimateTokenCount(conversationHistory);
            MuxConsole.WriteMuted($"~{currentTokens:N0} tokens in context");
            
            //Cleanly Exit as goal was passed through cli args and continuous flag was not passed
            if (incomingGoal != null && !continuous)
                break;
            
            if (continuous)
            {
                if (minDelaySeconds > 0)
                {
                    await MuxConsole.WithSpinnerAsync($"Next iteration in {minDelaySeconds}s", async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(minDelaySeconds), cancellationToken);
                    });
                }

                currentGoal = "Continue working on the task. If complete, summarize your results.";
                continue;
            }

            MuxConsole.WriteRule();
            MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");

            cancellationToken.ThrowIfCancellationRequested();
            string? nextInput = Console.ReadLine();
            cancellationToken.ThrowIfCancellationRequested();

            if (IsQuitCommand(nextInput))
            {
                MuxConsole.WriteSuccess("Exited from Chat interface successfully!");
                break;
            }

            if (nextInput!.Trim().Equals("/compact", StringComparison.OrdinalIgnoreCase))
            {
                await TryCompactAsync();
                MuxConsole.WriteRule();
                MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");

                cancellationToken.ThrowIfCancellationRequested();
                nextInput = Console.ReadLine();
                cancellationToken.ThrowIfCancellationRequested();

                if (IsQuitCommand(nextInput))
                {
                    MuxConsole.WriteSuccess("Exited from Chat interface successfully!");
                    break;
                }
            }

            currentGoal = nextInput!;

        } while (!Environment.HasShutdownStarted);

        if (sessionRetention > 0)
            Common.PruneOldSessions(PlatformContext.SessionsDirectory, sessionRetention);
    }
}