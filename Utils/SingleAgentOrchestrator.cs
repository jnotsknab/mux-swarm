using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace MuxSwarm.Utils;

public static class SingleAgentOrchestrator
{
    /// <summary>Rough estimate of tokens per character for budget tracking.</summary>
    private const double CharsPerToken = 3.5;

    private static MultiAgentOrchestrator.AgentDefinition? GetSingleAgentDefinition()
    {
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
                    return Common.ParseSingleAgentDefinition(config);
            }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"[AGENT] Failed to parse singleAgent from {path}: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>Estimates token count from a message list based on character length.</summary>
    private static int EstimateTokens(IReadOnlyList<ChatMessage> history)
    {
        int totalChars = 0;
        foreach (var msg in history)
            totalChars += (msg.Text ?? "").Length;

        return (int)(totalChars / CharsPerToken);
    }

    public static async Task ChatAgentAsync(
        IChatClient? client,
        CancellationToken cancellationToken,
        int maxIterations = 15,
        IList<McpClientTool>? mcpTools = null,
        bool showToolResultCalls = false,
        Func<string, IChatClient>? chatClientFactory = null,
        int autoCompactTokenThreshold = 80_000)
    {
        MuxConsole.WriteBanner("AGENTIC CHAT INTERFACE");
        MuxConsole.WriteMuted("Type /qc to exit, /compact to compress context. Press [Escape] to cancel the current turn.");

        var baseDir = PlatformContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );

        SkillLoader.LoadSkills(baseDir);
        var singleAgentDef = GetSingleAgentDefinition();

        IList<AITool> allTools = (mcpTools ?? Array.Empty<McpClientTool>()).Cast<AITool>().ToList();
        IList<AITool> filteredTools = singleAgentDef?.ToolFilter(allTools) ?? allTools;

        if (filteredTools.Count == 0)
            MuxConsole.WriteWarning($"[AGENT] Matched 0 tools. Check mcpServers in swarm.json singleAgent block.");
        else
            MuxConsole.WriteSuccess($"[AGENT] {filteredTools.Count} tools available");

        MuxConsole.WriteLine();
        MuxConsole.WriteRule();
        MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");
        string initialGoal = Console.ReadLine() ?? "USER DID NOT PROVIDE YOU WITH A GOAL";

        if (string.IsNullOrEmpty(initialGoal) || initialGoal.Trim().Equals("/qc", StringComparison.OrdinalIgnoreCase)) return;

        if (string.IsNullOrEmpty(singleAgentDef?.SystemPromptPath))
        {
            MuxConsole.WriteError("[AGENT] singleAgent.promptPath not set in swarm.json.");
            return;
        }

        var systemPrompt = await Common.LoadPromptAsync(singleAgentDef.SystemPromptPath);

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

        var singleAgentTools = (IList<AITool>)[listSkillsTool, readSkillTool, ..filteredTools];

        AIAgent agent = client.AsAIAgent(
            name: "MuxAgent",
            instructions: systemPrompt,
            tools: [..singleAgentTools!]
        );

        var session = await agent.CreateSessionAsync();
        string currentGoal = initialGoal;
        var sessionTimestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        // ── Conversation tracking for compaction ──
        var conversationHistory = new List<ChatMessage>();

        // ── Resolve compaction client (lazy, once) ──
        IChatClient? compactionClient = null;
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

            int beforeTokens = EstimateTokens(conversationHistory);

            await MuxConsole.WithSpinnerAsync("Compacting conversation history", async () =>
            {
                var compacted = await ResultCompactor.CompactConversationAsync(
                    conversationHistory, cc);

                session = await agent.CreateSessionAsync();
                conversationHistory.Clear();
                conversationHistory.Add(compacted);

                await foreach (var _ in agent.RunStreamingAsync([compacted], session)) { }
            });
            int afterTokens = EstimateTokens(conversationHistory);
            MuxConsole.WriteSuccess($"Compacted: ~{beforeTokens:N0} -> ~{afterTokens:N0} tokens");
            return true;
        }

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ── Auto-compact if approaching token budget ──
            int estimatedTokens = EstimateTokens(conversationHistory);
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

                    MuxConsole.WriteAgentTurnHeader("MuxAgent");

                    ThinkingIndicator? thinking = null;
                    bool startedStreaming = false;
                    bool currentlyStreaming = false;

                    try
                    {
                        thinking = MuxConsole.BeginThinking("MuxAgent");

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
                                        thinking = MuxConsole.ResumeThinking("MuxAgent");
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
                                        thinking = MuxConsole.BeginThinking("MuxAgent");
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

                    // Track assistant response for compaction
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

            // Save session
            await Common.PersistChatSessionAsync(agent, session, sessionTimestamp);

            // ── Show token estimate ──
            int currentTokens = EstimateTokens(conversationHistory);
            MuxConsole.WriteMuted($"~{currentTokens:N0} tokens in context");

            MuxConsole.WriteRule();
            MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");

            string? nextInput = Console.ReadLine();

            if (string.IsNullOrEmpty(nextInput) || nextInput.Trim().Equals("/qc", StringComparison.OrdinalIgnoreCase))
                break;

            // ── Manual compact command ──
            if (nextInput.Trim().Equals("/compact", StringComparison.OrdinalIgnoreCase))
            {
                await TryCompactAsync();
                MuxConsole.WriteRule();
                MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");
                nextInput = Console.ReadLine();

                if (string.IsNullOrEmpty(nextInput) || nextInput.Trim().Equals("/qc", StringComparison.OrdinalIgnoreCase))
                    break;
            }

            currentGoal = nextInput;

        } while (!Environment.HasShutdownStarted);
    }
}