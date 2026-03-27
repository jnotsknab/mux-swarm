using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using MuxSwarm.State;

namespace MuxSwarm.Utils;

/// <summary>
/// Multi-agent swarm system 
/// </summary>
public static class MultiAgentOrchestrator
{
    //TODO: Refactor this mess..

    public static readonly string PromptsDir = PlatformContext.PromptsDirectory;
    public static readonly string SessionDir = PlatformContext.SessionsDirectory;
    public static readonly string SwarmConfPath = PlatformContext.SwarmPath;
    private static readonly string FallbackOrchPromptPath = Path.Combine(PromptsDir, "orchestrator.md");
    private static bool _sessionDirty = false;
    private static uint _swarmTokens;
    private static string OrchestratorModelId;

    /// <summary>Max chars for truncated tool results shown in the indicator.</summary>
    private const int ToolResultPreviewLength = 250;

    /// <summary>
    /// Captures a compacted delegation result with structured metadata.
    /// </summary>
    private record DelegationResult(
        string AgentName,
        string CompactedResult,
        string? Status,
        string? Summary,
        string? Artifacts
    );

    /// <summary>
    /// Tracks retry state per sub-task key (agentName + task hash) to inject
    /// progressively stronger recovery hints on each retry.
    /// </summary>
    private record RetryState(int AttemptCount, string? LastFailureReason);

    /// <summary>Truncate and collapse whitespace for tool result previews.</summary>
    private static string TruncateResult(string? result, int maxLength = ToolResultPreviewLength)
    {
        if (string.IsNullOrEmpty(result)) return "";
        string clean = System.Text.RegularExpressions.Regex.Replace(result.Trim(), @"\s+", " ");
        return clean.Length > maxLength ? clean[..maxLength] + "..." : clean;
    }


    private static IList<AITool> GetOrchestratorFilteredToolsFromConfig(IList<AITool> mcpTools)
    {
        if (!File.Exists(SwarmConfPath))
            return new List<AITool>(); // safe default

        try
        {
            var json = File.ReadAllText(SwarmConfPath);
            var config = JsonSerializer.Deserialize<SwarmConfig>(json);

            var orch = config?.Orchestrator;
            if (orch == null)
                return new List<AITool>();

            return Common.ApplyToolFilter(
                tools: mcpTools,
                mcpServers: orch.McpServers,
                toolPatterns: orch.ToolPatterns,
                includeAllWhenEmpty: false
            );
        }
        catch
        {
            return new List<AITool>();
        }
    }

    private static string GetOrchestratorPromptPath()
    {
        if (File.Exists(SwarmConfPath))
        {
            try
            {
                var json = File.ReadAllText(SwarmConfPath);
                var config = JsonSerializer.Deserialize<SwarmConfig>(json);

                if (config?.Orchestrator?.PromptPath != null)
                {
                    var path = config.Orchestrator.PromptPath;
                    return Path.IsPathRooted(path) ? path : Path.Combine(PromptsDir, path);
                }
            }
            catch { /* use default */ }
        }
        return FallbackOrchPromptPath;
    }

    //Core Orchestration

    public static async Task RunAsync(
        Func<string, IChatClient> chatClientFactory,
        IList<AITool> mcpTools,
        Dictionary<string, string> agentModels,
        int maxOrchestratorIterations = -1,
        int maxSubAgentIterations = -1,
        bool prodMode = false,
        string? incomingGoal = null,
        bool continuous = false,
        string? goalId = null,
        uint minDelaySeconds = 300,
        uint persistIntervalSeconds = 60,
        uint sessionRetention = 10,
        CancellationToken cancellationToken = default)
    {
        var agentDefs = Common.GetAgentDefinitions(SwarmConfPath);

        if (maxOrchestratorIterations < 0) maxOrchestratorIterations = ExecutionLimits.Current.MaxOrchestratorIterations;
        if (maxSubAgentIterations < 0) maxSubAgentIterations = ExecutionLimits.Current.MaxSubAgentIterations;

        SwarmConfig? swarmConfig = null;
        try
        {
            swarmConfig = JsonSerializer.Deserialize<SwarmConfig>(File.ReadAllText(SwarmConfPath));
        }
        catch {/*Defaults*/ }

        /*
        ExecutionLimits.Current = swarmConfig?.ExecutionLimits ?? new();
        */

        string orchestratorPromptPath = GetOrchestratorPromptPath();

        // SkillLoader.LoadSkills();

        var specialists = new Dictionary<string, (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def)>();

        var taskCompleteTool = AIFunctionFactory.Create(
            method: (
                [Description("Status of the task: 'success', 'failure', or 'partial'")] string status,
                [Description("Summary of what was accomplished or why it failed")] string summary,
                [Description("Optional comma-separated list of file paths or identifiers produced")] string? artifacts
            ) =>
            {
                if (status == "success")
                    MuxConsole.WriteTaskComplete("Task", summary);
                else
                    MuxConsole.WriteWarning($"Task {status}: {summary}");

                if (!string.IsNullOrEmpty(artifacts))
                    MuxConsole.WriteMuted($"  Artifacts: {artifacts}");

                return "Task marked as complete.";
            },
            name: "signal_task_complete",
            description: "Call this when the current goal or sub-task is completed or has failed. " +
                        "Provide a status ('success', 'failure', or 'partial'), a summary of what was accomplished, " +
                        "and optionally any file paths or identifiers produced."
        );

        var delegationResults = new List<DelegationResult>();
        var retryRegistry = new Dictionary<string, RetryState>();
        _sessionDirty = false;
        
        HookWorker.Enqueue(new HookEvent
        {
            Event = "session_start",
            Agent = "Orchestrator",
            Summary = "swarm",
            Text = incomingGoal,
            Timestamp = DateTimeOffset.UtcNow
        });
        
        using var sessionSpan = OtelTracer.GetSource().StartActivity("swarm_session");
        sessionSpan?.SetTag("mode", "swarm");
        sessionSpan?.SetTag("goal_id", goalId);
        sessionSpan?.SetTag("continuous", continuous);
        sessionSpan?.SetTag("max_iterations", maxOrchestratorIterations);
        OtelMetrics.SessionsStarted.Add(1, new KeyValuePair<string, object?>("mode", "swarm"));

        IChatClient? compactionClient = null;
        try
        {
            var compactionModel = agentModels.GetValueOrDefault("Compaction", agentModels["Orchestrator"]);
            compactionClient = chatClientFactory(compactionModel);
        }
        catch { /* falls back to extractive only */ }

        ChatOptions? compactionChatOptions = swarmConfig?.CompactionAgent?.ModelOpts?.ToChatOptions();

        async Task<string> ExecuteDelegation(
            string agentName,
            string task,
            string callerName,
            bool restrictToSpecialists)
        {
            if (restrictToSpecialists && agentName == "Orchestrator")
                return "[ERROR] Sub-agents cannot delegate back to the Orchestrator.";

            if (!specialists.TryGetValue(agentName, out var specialist))
            {
                var available = restrictToSpecialists
                    ? string.Join(", ", specialists.Keys.Where(k => k != "Orchestrator"))
                    : string.Join(", ", specialists.Keys);
                return $"[ERROR] Unknown agent '{agentName}'. Available agents: {available}";
            }

            if (agentName == callerName)
                return $"[ERROR] Agent '{callerName}' cannot delegate to itself.";

            string retryKey = $"{agentName}:{Math.Abs(task.GetHashCode())}";
            retryRegistry.TryGetValue(retryKey, out var retryState);
            int attemptNumber = (retryState?.AttemptCount ?? 0) + 1;
            
            using var delegationSpan = OtelTracer.GetSource().StartActivity("delegation");
            delegationSpan?.SetTag("from", callerName);
            delegationSpan?.SetTag("to", agentName);
            delegationSpan?.SetTag("attempt", attemptNumber);
            var delegationSw = Stopwatch.StartNew();
            
            MuxConsole.WriteDelegation(callerName, agentName, task);

            if (attemptNumber > 1)
                MuxConsole.WriteWarning($"RETRY {attemptNumber}/{ExecutionLimits.Current.MaxSubTaskRetries} — Prior failure: {retryState?.LastFailureReason ?? "unknown"}");

            string enrichedTask = task;
            await MuxConsole.WithSpinnerAsync($"Preparing context for {agentName}", async () =>
            {
                enrichedTask = await EnrichTaskWithCrossAgentContext(
                    task, agentName, delegationResults, compactionClient, compactionChatOptions);

                if (attemptNumber > 1 && retryState != null)
                    enrichedTask = InjectRetryHint(enrichedTask, attemptNumber, retryState.LastFailureReason);
            });

            var (rawResult, status, summary, artifacts) = await RunSubAgentAsync(
                specialist, enrichedTask, maxSubAgentIterations, cancellationToken, prodMode: prodMode);

            bool succeeded = status == "success";
            if (!succeeded)
            {
                retryRegistry[retryKey] = new RetryState(
                    AttemptCount: attemptNumber,
                    LastFailureReason: summary ?? "No summary provided"
                );

                if (attemptNumber >= ExecutionLimits.Current.MaxSubTaskRetries)
                    MuxConsole.WriteError($"{agentName} failed {ExecutionLimits.Current.MaxSubTaskRetries} times on this sub-task.");
            }
            else
            {
                retryRegistry.Remove(retryKey);
            }

            string compacted = "";
            await MuxConsole.WithSpinnerAsync($"Compacting {agentName} result", async () =>
            {
                compacted = await ResultCompactor.CompactAsync(
                    rawResult,
                    completionStatus: status,
                    completionSummary: summary,
                    completionArtifacts: artifacts,
                    charBudget: ExecutionLimits.Current.ProgressEntryBudget,
                    chatClient: compactionClient,
                    chatOptions: compactionChatOptions);
            });

            delegationResults.Add(new DelegationResult(agentName, compacted, status, summary, artifacts));

            if (prodMode)
                compacted = $"[[START_AGENT_TURN]]{agentName}[[END_AGENT_NAME]]{compacted}[[END_AGENT_TURN]]";

            if (!succeeded && attemptNumber >= ExecutionLimits.Current.MaxSubTaskRetries)
            {
                compacted += $"\n[RETRY_EXHAUSTED] {agentName} failed {ExecutionLimits.Current.MaxSubTaskRetries} attempts. " +
                             $"Last reason: {retryState?.LastFailureReason ?? summary}. " +
                             "Consider a different approach or agent, or surface this to the user.";
            }
            
            delegationSw.Stop();
            OtelMetrics.Delegations.Add(1,
                new KeyValuePair<string, object?>("from", callerName),
                new KeyValuePair<string, object?>("to", agentName));
            OtelMetrics.DelegationDuration.Record(delegationSw.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("to", agentName));

            if (!succeeded)
                OtelMetrics.SubTaskRetries.Add(1, new KeyValuePair<string, object?>("agent", agentName));
            
            return string.IsNullOrWhiteSpace(compacted)
                ? $"[{agentName} completed but returned no output]"
                : compacted;
        }

        var delegateTool = AIFunctionFactory.Create(
            method: async (
                [Description("Name of the specialist agent to delegate to")] string agentName,
                [Description("The specific sub-task or instruction for the agent")] string task
            ) =>
            {
                return await ExecuteDelegation(agentName, task, "Orchestrator", restrictToSpecialists: false);
            },
            name: "delegate_to_agent",
            description: "Delegates a sub-task to a specialist agent and returns their result. " +
                         "Use this to assign work to the appropriate agent based on the task type."
        );

        var subAgentDelegateTool = AIFunctionFactory.Create(
            method: async (
                [Description("Name of the specialist agent to delegate to. Cannot delegate to Orchestrator.")] string agentName,
                [Description("The specific sub-task or instruction for the specialist agent")] string task
            ) =>
            {
                return await ExecuteDelegation(agentName, task, "SubAgent", restrictToSpecialists: true);
            },
            name: "delegate_to_agent",
            description: "Delegate a sub-task to a specialist agent by name. Use when a task would be better handled by another agent based on their specialization, or when offloading would improve efficiency. Cannot delegate to the Orchestrator."
        );

        //Build specialist agents
        await MuxConsole.WithSpinnerAsync("Initializing specialist agents", async () =>
        {
            foreach (var def in agentDefs)
            {
                string prompt = await Common.LoadPromptAsync(def.SystemPromptPath);

                if (def.CanDelegate)
                {
                    var roster = agentDefs
                        .Where(a => a.Name != def.Name && a.Name != "Orchestrator")
                        .Select(a => $"- {a.Name}: {a.Description}")
                        .ToList();

                    prompt += "\n\n(NOTICE) You have sub-agent delegation enabled via the delegate_to_agent tool. Available agents you can delegate to:\n"
                              + string.Join("\n", roster);

                    prompt += """

                              ## Memory Policy (MANDATORY)

                              **At the START of your task:** If relevant prior context has not already been provided to you, delegate to MemoryAgent to search for related decisions, research, or artifacts before beginning work. Skip this if context was already passed in your task instructions.

                              **At the END of your task:** It is recommended you delegate to MemoryAgent to persist your findings before calling signal_task_complete. Better safe than sorry when coming to your memory. Pass it:
                              - Task name and outcome (success/failure)
                              - Key findings or decisions made
                              - Artifact paths written to NAS (if any)
                              - A summary of what you did and which agents helped

                              """;
                }

                IList<AITool> filteredTools = def.ToolFilter(mcpTools);

                if (filteredTools.Count == 0)
                    MuxConsole.WriteWarning($"{def.Name} matched 0 tools. Check mcpServers in swarm.json or ToolFilter. (Sub-Agent Delegation is {(def.CanDelegate ? "enabled" : "disabled")})");
                else
                    MuxConsole.WriteSuccess($"{def.Name} has {filteredTools.Count} tools available. (Sub-Agent Delegation is {(def.CanDelegate ? "enabled" : "disabled")})");

                string modelId = agentModels.GetValueOrDefault(def.Name, agentModels["Orchestrator"]);
                IChatClient client = chatClientFactory(modelId);

                var analyzeImageTool = LocalAiFunctions.CreateAnalyzeImageTool(chatClientFactory, modelId);

                var listSkillsTool = AIFunctionFactory.Create(
                    method: () =>
                    {
                        var skills = SkillLoader.GetSkillMetadata(def.Name);
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

                        var available = SkillLoader.GetSkillMetadata(def.Name);
                        var listing = string.Join("\n", available.Select(s => $"- {s.Name}: {s.Description}"));
                        return $"Skill '{skillName}' not found. Here are the currently available skills — call read_skill again with a valid name:\n{listing}";
                    },
                    name: "read_skill",
                    description: "Read the full instructions for a skill by name. Call list_skills first to discover available skills. " +
                                 "Read the relevant skill BEFORE starting a task to follow its best practices."
                );

                var agentTools = def.CanDelegate
                    ? (IList<AITool>)[taskCompleteTool, listSkillsTool, readSkillTool, LocalAiFunctions.SleepTool, LocalAiFunctions.MuxRefreshTool, analyzeImageTool, subAgentDelegateTool, .. filteredTools]
                    : (IList<AITool>)[taskCompleteTool, listSkillsTool, readSkillTool, LocalAiFunctions.SleepTool, LocalAiFunctions.MuxRefreshTool, analyzeImageTool, .. filteredTools];

                var agentChatOptions = new ChatOptions
                {
                    Instructions = prompt,
                    Tools = agentTools
                };

                var agentConfigOpts = swarmConfig?.Agents?
                    .FirstOrDefault(a => a.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase))
                    ?.ModelOpts;

                if (agentConfigOpts?.ToChatOptions() is { } modelChatOpts)
                {
                    agentChatOptions.Temperature = modelChatOpts.Temperature;
                    agentChatOptions.TopP = modelChatOpts.TopP;
                    agentChatOptions.TopK = modelChatOpts.TopK;
                    agentChatOptions.MaxOutputTokens = modelChatOpts.MaxOutputTokens;
                    agentChatOptions.FrequencyPenalty = modelChatOpts.FrequencyPenalty;
                    agentChatOptions.PresencePenalty = modelChatOpts.PresencePenalty;
                    agentChatOptions.Seed = modelChatOpts.Seed;
                    agentChatOptions.Reasoning = modelChatOpts.Reasoning;
                    agentChatOptions.AdditionalProperties = modelChatOpts.AdditionalProperties;
                }

                var agent = client.AsAIAgent(new ChatClientAgentOptions
                {
                    Name = def.Name,
                    ChatOptions = agentChatOptions
                });

                var session = await agent.CreateSessionAsync();
                specialists[def.Name] = (agent, session, def);
            }
        });

        //Build the orchestrator

        string orchestratorPrompt = await Common.LoadPromptAsync(orchestratorPromptPath);

        string agentRoster = string.Join("\n", agentDefs.Select(d =>
            $"  - {d.Name}: {d.Description}"));
        orchestratorPrompt += $"\n\nAvailable agents:\n{agentRoster}";

        orchestratorPrompt += """


                              ## Sleep Tool
                              You have access to `system_sleep(seconds)` which pauses execution without consuming tokens or timing out.

                              Use it when:
                              - Waiting between polling cycles (check a condition, sleep, recheck)
                              - A delegated task involves a long-running process and you need to wait before following up
                              - Pacing a continuous loop to avoid hammering APIs or burning tokens

                              In continuous or extended mode goals, sleep is your primary mechanism for controlling loop cadence.
                              A sleeping swarm costs nothing. Prefer sleep over rapid retries.
                              """;

        // Load or create continuous state
        CurrentStateMetadata? state = null;
        if (continuous)
        {
            goalId ??= Guid.NewGuid().ToString("N")[..8];
            state = ContinuousStateManager.Load(goalId, SessionDir)
                    ?? new CurrentStateMetadata(
                        goalId, incomingGoal!, 0, DateTime.MinValue,
                        DateTime.Now, "running", minDelaySeconds);

            if (state.Status == "sleeping" && state.NextWakeAt > DateTime.Now)
            {
                var remaining = state.NextWakeAt - DateTime.Now;
                await MuxConsole.WithSpinnerAsync($"Resuming — sleeping remaining {remaining.TotalSeconds:F0}s", async () =>
                {
                    await Task.Delay(remaining, cancellationToken);
                });
            }

            if (state.Iteration > 0)
                orchestratorPrompt += $"""

                                       [CONTINUOUS RESUME]
                                       Goal ID: {goalId}
                                       Iteration: {state.Iteration}
                                       Last completed: {state.LastCompletedAt:yyyy-MM-dd HH:mm:ss}
                                       """;
        }

        string sessionContext = SessionSummarizer.BuildRollingContext(SessionDir);
        if (!string.IsNullOrEmpty(sessionContext))
            orchestratorPrompt += $"\n\n{sessionContext}";

        string orchestratorModelId = agentModels["Orchestrator"];
        OrchestratorModelId = orchestratorModelId;
        IChatClient orchestratorClient = chatClientFactory(orchestratorModelId);

        var orchestratorFilteredTools = GetOrchestratorFilteredToolsFromConfig(mcpTools);

        if (orchestratorFilteredTools.Count == 0)
            MuxConsole.WriteMuted("Orchestrator has 0 MCP tools (ToolFilter). Using built-in tools only.");
        else
            MuxConsole.WriteSuccess($"Orchestrator has {orchestratorFilteredTools.Count} MCP tools available");

        var orchestratorTools = (IList<AITool>)[
            delegateTool,
            taskCompleteTool,
            LocalAiFunctions.ListSkillsTool,
            LocalAiFunctions.ReadSkillTool,
            LocalAiFunctions.SleepTool,
            LocalAiFunctions.MuxRefreshTool,
            ..orchestratorFilteredTools
        ];

        var orchChatOptions = new ChatOptions
        {
            Instructions = orchestratorPrompt,
            Tools = orchestratorTools
        };

        if (swarmConfig?.Orchestrator?.ModelOpts?.ToChatOptions() is { } orchModelOpts)
        {
            orchChatOptions.Temperature = orchModelOpts.Temperature;
            orchChatOptions.TopP = orchModelOpts.TopP;
            orchChatOptions.TopK = orchModelOpts.TopK;
            orchChatOptions.MaxOutputTokens = orchModelOpts.MaxOutputTokens;
            orchChatOptions.FrequencyPenalty = orchModelOpts.FrequencyPenalty;
            orchChatOptions.PresencePenalty = orchModelOpts.PresencePenalty;
            orchChatOptions.Seed = orchModelOpts.Seed;
            orchChatOptions.Reasoning = orchModelOpts.Reasoning;
            orchChatOptions.AdditionalProperties = orchModelOpts.AdditionalProperties;
        }

        var orchestratorAgent = orchestratorClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "Orchestrator",
            ChatOptions = orchChatOptions
        });

        var orchestratorSession = await orchestratorAgent.CreateSessionAsync();

        AgentSession currentOrchestratorSession = orchestratorSession;
        string currentIterationSessionDir = string.Empty;

        await using var persister = new SeshPersistor(
            async () =>
            {
                if (!_sessionDirty || string.IsNullOrEmpty(currentIterationSessionDir)) return;
                await Common.PersistSessionsAsync(orchestratorAgent, currentOrchestratorSession, specialists, currentIterationSessionDir);
            },
            intervalSeconds: (int)persistIntervalSeconds
        );

        if (continuous)
            persister.Start();

        bool goalFromArgs = !string.IsNullOrEmpty(incomingGoal);

        if (!prodMode && !goalFromArgs)
            MuxConsole.WriteBody("Agent Swarm system ready. Enter a goal (or /qm to quit):");

        if (!prodMode)
            MuxConsole.WriteMuted("Press [Escape] during execution to cancel the current goal.");

        MuxConsole.WriteLine();
        MuxConsole.WriteRule();

        //Main loop 
        while (!cancellationToken.IsCancellationRequested)
        {
            string goal;

            if (goalFromArgs)
            {
                goal = incomingGoal!;
            }
            else
            {
                if (!prodMode) MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");
                string? input = MuxConsole.ReadInput();

                if (string.IsNullOrEmpty(input) || input.Trim().Equals("/qm", StringComparison.OrdinalIgnoreCase) ||
                    input.Trim().Equals("/qc", StringComparison.OrdinalIgnoreCase))
                {
                    MuxConsole.WriteSuccess("Exited from Swarm interface successfully!");
                    break;
                }


                goal = File.Exists(input) ? File.ReadAllText(input) : input;
                
                HookWorker.Enqueue(new HookEvent
                {
                    Event = "user_input",
                    Agent = "Orchestrator",
                    Text = goal,
                    Timestamp = DateTimeOffset.UtcNow
                });
                OtelMetrics.GoalsReceived.Add(1, new KeyValuePair<string, object?>("mode", "swarm"));

            }

            // Fresh sessions per iteration
            await MuxConsole.WithSpinnerAsync("Preparing new sessions", async () =>
            {
                orchestratorSession = await orchestratorAgent.CreateSessionAsync();
                currentOrchestratorSession = orchestratorSession;

                foreach (var key in specialists.Keys)
                {
                    var (agent, _, def) = specialists[key];
                    specialists[key] = (agent, await agent.CreateSessionAsync(), def);
                }
            });

            delegationResults.Clear();
            retryRegistry.Clear();
            _sessionDirty = false;

            //reset upon new goal
            _swarmTokens = 0;

            currentIterationSessionDir = Path.Combine(SessionDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            if (state != null)
            {
                state.Status = "running";
                ContinuousStateManager.WriteAtomic(goalId!, state, SessionDir);
            }

            //Create a linked CTS for Escape key cancellation
            using var goalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IDisposable? escapeListener = null;

            if (!prodMode)
                escapeListener = EscapeKeyListener.Start(goalCts, cancellationToken);

            StdinCancelMonitor.Instance?.SetActiveTurnCts(goalCts);

            bool wasInterrupted = false;

            try
            {
                await RunOrchestratedGoalAsync(
                    goal: goal,
                    orchestratorAgent: orchestratorAgent,
                    orchestratorSession: orchestratorSession,
                    specialists: specialists,
                    delegationResults: delegationResults,
                    compactionClient: compactionClient,
                    maxOrchestratorIterations: continuous ? int.MaxValue : maxOrchestratorIterations,
                    maxSubAgentIterations: maxSubAgentIterations,
                    cancellationToken: goalCts.Token,
                    prodMode: prodMode,
                    continuous: continuous
                );
            }
            catch (OperationCanceledException) when (goalCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                wasInterrupted = true;
                MuxConsole.WriteLine();
                MuxConsole.WriteWarning("Goal cancelled by user (Escape key pressed).");
                MuxConsole.WriteInfo("Any work completed by agents before interruption has been preserved to sessions dir.");
            }
            finally
            {
                StdinCancelMonitor.Instance?.ClearActiveTurnCts();
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_sessionDirty)
            {
                await MuxConsole.WithSpinnerAsync(
                    wasInterrupted ? "Persisting partial progress" : "Persisting sessions",
                    async () =>
                    {
                        await Common.PersistSessionsAsync(orchestratorAgent, orchestratorSession, specialists, currentIterationSessionDir);
                    });

                MuxConsole.WriteMuted($"{_swarmTokens:N0} tokens used this goal");
                _sessionDirty = false;
            }

            if (continuous && state != null)
            {
                state.Iteration++;
                state.LastCompletedAt = DateTime.Now;
                state.NextWakeAt = DateTime.Now.AddSeconds(state.MinDelaySeconds);
                state.Status = wasInterrupted ? "interrupted" : "sleeping";
                ContinuousStateManager.WriteAtomic(goalId!, state, SessionDir);
                Common.PruneOldSessions(SessionDir, sessionRetention);
            }

            if (goalFromArgs && !continuous)
                break;

            if (continuous && wasInterrupted)
            {
                MuxConsole.WriteInfo("Continuous mode stopped due to user interruption.");
                break;
            }

            MuxConsole.WriteRule();

            if (continuous)
            {
                MuxConsole.WriteInfo($"Iteration {state!.Iteration} complete. Enforced delay: {minDelaySeconds}s. Next run: {state.NextWakeAt:HH:mm:ss}");

                await MuxConsole.WithSpinnerAsync($"Sleeping {minDelaySeconds}s until next iteration...", async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(minDelaySeconds), cancellationToken);
                });
            }
        }

        // Graceful shutdown
        if (continuous && state != null)
        {
            if (cancellationToken.IsCancellationRequested)
                ContinuousStateManager.MarkStopped(goalId!, state, SessionDir);
            else
                ContinuousStateManager.Clear(goalId!, SessionDir);
        }
        
        HookWorker.Enqueue(new HookEvent
        {
            Event = "session_end",
            Agent = "Orchestrator",
            Summary = cancellationToken.IsCancellationRequested ? "interrupted" : "complete",
            Timestamp = DateTimeOffset.UtcNow
        });
        
        sessionSpan?.SetTag("outcome", cancellationToken.IsCancellationRequested ? "interrupted" : "complete");
        sessionSpan?.SetTag("total_tokens", _swarmTokens);
        sessionSpan?.SetTag("delegations", delegationResults.Count);

        if (cancellationToken.IsCancellationRequested)
            OtelMetrics.SessionsFailed.Add(1, new KeyValuePair<string, object?>("mode", "swarm"));
        else
            OtelMetrics.SessionsCompleted.Add(1, new KeyValuePair<string, object?>("mode", "swarm"));
    }


    private static string InjectRetryHint(string task, int attemptNumber, string? lastFailureReason)
    {
        var hint = new StringBuilder();
        hint.AppendLine();
        hint.AppendLine("---");
        hint.AppendLine($"[RETRY ATTEMPT {attemptNumber}/{ExecutionLimits.Current.MaxSubTaskRetries}]");

        if (!string.IsNullOrWhiteSpace(lastFailureReason))
            hint.AppendLine($"Previous attempt failed with: {lastFailureReason}");

        hint.AppendLine(attemptNumber switch
        {
            2 => "Review what went wrong and try a different approach. Check available skills before proceeding.",
            3 => "Prior attempts have failed. Significantly change your strategy — " +
                 "try a simpler intermediate step, verify prerequisites, or use a different tool.",
            _ => "This is a final attempt. Decompose the problem further, verify all prerequisites exist, " +
                 "and use the most conservative approach available. If a sub-step fails, report exactly why " +
                 "so the orchestrator can re-route."
        });

        hint.AppendLine("---");
        return task + hint.ToString();
    }

    // ── Cross-Agent Context Enrichment ───────────────────────────────────

    private static async Task<string> EnrichTaskWithCrossAgentContext(
        string originalTask,
        string targetAgentName,
        List<DelegationResult> priorResults,
        IChatClient? compactionClient,
        ChatOptions? compactionChatOptions = null)
    {
        if (priorResults.Count == 0)
            return originalTask;

        var relevantResults = priorResults
            .Where(r => !r.AgentName.Equals(targetAgentName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (relevantResults.Count == 0)
            return originalTask;

        var context = new StringBuilder();
        context.AppendLine(originalTask);
        context.AppendLine();
        context.AppendLine("--- Context from prior agents ---");

        int budgetRemaining = ExecutionLimits.Current.CrossAgentContextBudget;

        foreach (var result in relevantResults)
        {
            if (budgetRemaining <= 0) break;

            string entry = result.CompactedResult;
            if (entry.Length > budgetRemaining)
            {
                entry = await ResultCompactor.CompactAsync(
                    entry,
                    result.Status, result.Summary, result.Artifacts,
                    charBudget: budgetRemaining,
                    chatClient: compactionClient,
                    chatOptions: compactionChatOptions);
            }

            context.AppendLine($"[{result.AgentName}]: {entry}");
            budgetRemaining -= entry.Length + result.AgentName.Length + 5;
        }

        int included = relevantResults.Count(r =>
            context.ToString().Contains($"[{r.AgentName}]"));
        int omitted = relevantResults.Count - included;
        if (omitted > 0)
            context.AppendLine($"[...{omitted} lower-priority lines omitted]");

        context.AppendLine("--- End context ---");
        return context.ToString();
    }

    // Goal Execution

    private static async Task RunOrchestratedGoalAsync(
        string goal,
        AIAgent orchestratorAgent,
        AgentSession orchestratorSession,
        Dictionary<string, (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def)> specialists,
        List<DelegationResult> delegationResults,
        IChatClient? compactionClient,
        int maxOrchestratorIterations,
        int maxSubAgentIterations,
        CancellationToken cancellationToken,
        bool prodMode = false,
        bool continuous = false)
    {
        if (!prodMode)
        {
            /*QweConsole.WriteRule("Orchestrator Processing");
            QweConsole.WriteBody(goal);*/
            MuxConsole.WriteLine();
        }
        else
        {
            Console.WriteLine($"[[START_ORCHESTRATOR]]{DateTime.Now:yyyy-MM-dd HH:mm:ss}[[END_TIMESTAMP]]");
        }

        _sessionDirty = true;
        var progressLog = new List<string>();
        List<string> toolCalls = [];
        bool goalComplete = false;
        int stuckCount = 0;
        bool isFirstIteration = true;
        int resultsCountBefore = 0;

        for (int i = 0; i < maxOrchestratorIterations && !goalComplete; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            resultsCountBefore = delegationResults.Count;

            var messages = new List<ChatMessage>();

            if (isFirstIteration)
            {
                messages.Add(new(ChatRole.User,
                    $"""
                    Goal: {goal}
                    
                    Plan how to accomplish this by delegating to your available agents.
                    After all sub-tasks complete, synthesize results and call signal_task_complete.
                    """));
                isFirstIteration = false;
            }
            else
            {
                var continuation = new StringBuilder();

                if (progressLog.Count > 0)
                {
                    continuation.AppendLine("Progress so far:");

                    int totalChars = 0;
                    int startIndex = 0;

                    for (int p = progressLog.Count - 1; p >= 0; p--)
                    {
                        totalChars += progressLog[p].Length + 5;
                        if (totalChars > ExecutionLimits.Current.ProgressLogTotalBudget)
                        {
                            startIndex = p + 1;
                            break;
                        }
                    }

                    if (startIndex > 0)
                        continuation.AppendLine($"  [...{startIndex} earlier steps omitted for brevity]");

                    for (int p = startIndex; p < progressLog.Count; p++)
                        continuation.AppendLine($"  • {progressLog[p]}");

                    continuation.AppendLine();
                }

                if (!continuous)
                {
                    int remaining = maxOrchestratorIterations - i;
                    if (remaining <= 3)
                    {
                        continuation.AppendLine($"⚠ WARNING: Only {remaining} orchestrator iteration(s) remaining. " +
                            "Prioritize completing or gracefully wrapping up the goal now.");
                        continuation.AppendLine();
                    }
                }

                continuation.AppendLine("Determine the next step. " +
                    "Delegate to an agent if there is more work, or call signal_task_complete if the goal is fully achieved.");

                messages.Add(new(ChatRole.User, continuation.ToString()));
            }

            var responseText = new StringBuilder();
            toolCalls.Clear();

            ThinkingIndicator? thinking = null;
            bool startedStreaming = false;
            bool currentlyStreaming = false;
            var orchTurnSw = Stopwatch.StartNew();
            
            try
            {
                MuxConsole.WriteAgentTurnHeader("Orchestrator");
                thinking = MuxConsole.BeginThinking("Orchestrator");
                
                using var orchTurnSpan = OtelTracer.GetSource().StartActivity("orchestrator_turn");
                orchTurnSpan?.SetTag("agent", "Orchestrator");
                orchTurnSpan?.SetTag("iteration", i);
                
                
                await foreach (var update in orchestratorAgent
                    .RunStreamingAsync(messages, orchestratorSession)
                    .WithCancellation(cancellationToken))
                {
                    // Process text first — if this update has text, we need streaming mode.
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        if (!prodMode && !currentlyStreaming)
                        {
                            // Transition: thinking → streaming
                            MuxConsole.BeginStreaming();
                            currentlyStreaming = true;
                            startedStreaming = true;
                        }

                        MuxConsole.WriteStream(update.Text);
                        responseText.Append(update.Text);
                        
                        HookWorker.Enqueue(new HookEvent
                        {
                            Event = "text_chunk",
                            Agent = "Orchestrator",
                            Text = update.Text,
                            Timestamp = DateTimeOffset.UtcNow
                        });
                    }

                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent fc)
                        {
                            HookWorker.Enqueue(new HookEvent
                            {
                                Event = "tool_call",
                                Agent = "Orchestrator",
                                Tool = fc.Name,
                                Timestamp = DateTimeOffset.UtcNow
                            });
                            
                            var toolSpan = OtelTracer.GetSource().StartActivity("tool_call");
                            toolSpan?.SetTag("agent", "Orchestrator");
                            toolSpan?.SetTag("tool", fc.Name);
                            
                            // If we were streaming text and now a tool call arrives,
                            // transition back to thinking mode so the indicator reappears.
                            if (!prodMode && currentlyStreaming)
                            {
                                currentlyStreaming = false;
                                thinking?.Dispose();
                                thinking = MuxConsole.ResumeThinking("Orchestrator");
                                toolCalls.Add(fc.Name);
                                thinking.UpdateStatus(toolCalls);
                            }
                            else
                            {
                                toolCalls.Add(fc.Name);
                                thinking?.UpdateStatus(toolCalls);
                            }

                            if (prodMode)
                                Console.Write($"[[TOOL_CALL]]{fc.Name}[[END_TOOL_CALL]]");
                        }
                        else if (content is FunctionResultContent fr)
                        {
                            HookWorker.Enqueue(new HookEvent
                            {
                                Event = "tool_result",
                                Agent = "Orchestrator",
                                Summary = fr.Result?.ToString(),
                                Timestamp = DateTimeOffset.UtcNow
                            });

                            var resultText = fr.Result?.ToString();
                            Activity.Current?.SetTag("success", true);
                            if (resultText != null)
                                Activity.Current?.SetTag("result", resultText.Length > 4096 ? resultText[..4096] : resultText);
                            Activity.Current?.Stop();

                            OtelMetrics.ToolCalls.Add(1,
                                new KeyValuePair<string, object?>("agent", "Orchestrator"),
                                new KeyValuePair<string, object?>("tool", fr.CallId));
                            
                            if (!prodMode && !currentlyStreaming && thinking != null)
                            {
                                thinking.Dispose();
                                thinking = MuxConsole.BeginThinking("Orchestrator");
                                if (toolCalls.Count > 0)
                                    thinking.UpdateStatus(toolCalls);
                            }

                            if (toolCalls.Any(t => t.Contains("signal_task_complete", StringComparison.OrdinalIgnoreCase)))
                            {
                                thinking?.Dispose();
                                thinking = null;
                                goto streamComplete;
                            }
                        }
                        else if (content is UsageContent usageContent)
                        {
                            _swarmTokens += (uint)(usageContent.Details.TotalTokenCount ?? 0);
                            OtelMetrics.RecordTokens("Orchestrator", OrchestratorModelId,
                                usageContent.Details.InputTokenCount ?? 0,
                                usageContent.Details.OutputTokenCount ?? 0);
                        }
                    }
                }

                streamComplete:;

                if (prodMode)
                {
                    Console.Write("[[END_AGENT_TURN]]");
                }
                else
                {
                    if (currentlyStreaming)
                        MuxConsole.EndStreaming();

                    MuxConsole.WriteAgentTurnFooter();
                }
            }
            catch (OperationCanceledException)
            {
                MuxConsole.WriteWarning("Orchestrator interrupted.");
                throw;
            }
            finally
            {
                // If we began streaming but hit an exception before EndStreaming, ensure we end it.
                if (!prodMode && currentlyStreaming)
                {
                    try { MuxConsole.EndStreaming(); } catch { /* ignore */ }
                }

                thinking?.Dispose();
                
                HookWorker.Enqueue(new HookEvent
                {
                    Event = "turn_end",
                    Agent = "Orchestrator",
                    Summary = responseText.Length > 500 ? responseText.ToString(0, 500) + "..." : responseText.ToString(),
                    Timestamp = DateTimeOffset.UtcNow
                });
                
                orchTurnSw.Stop();
                OtelMetrics.AgentTurns.Add(1, new KeyValuePair<string, object?>("agent", "Orchestrator"));
                OtelMetrics.AgentTurnDuration.Record(orchTurnSw.ElapsedMilliseconds,
                    new KeyValuePair<string, object?>("agent", "Orchestrator"));
                OtelMetrics.OrchestratorIterations.Add(1);
            }

            MuxConsole.WriteLine();
            string response = responseText.ToString();

            for (int r = resultsCountBefore; r < delegationResults.Count; r++)
            {
                var dr = delegationResults[r];
                progressLog.Add($"[{dr.AgentName}] {dr.CompactedResult}");
            }

            if (string.IsNullOrWhiteSpace(response) && toolCalls.Count == 0)
            {
                stuckCount++;
                MuxConsole.WriteWarning($"Orchestrator stuck — empty response ({stuckCount}/{ExecutionLimits.Current.MaxStuckCount})");
                OtelMetrics.AgentStuckCount.Add(1, new KeyValuePair<string, object?>("agent", "Orchestrator"));

                if (stuckCount >= ExecutionLimits.Current.MaxStuckCount)
                {
                    MuxConsole.WriteError("Orchestrator stuck repeatedly — aborting.");
                    break;
                }

                progressLog.Add($"[System] Orchestrator produced no output on iteration {i + 1}. " +
                    "You must either delegate to an agent or call signal_task_complete.");
                continue;
            }

            stuckCount = 0;

            if (toolCalls.Any(t => t.Contains("signal_task_complete", StringComparison.OrdinalIgnoreCase)))
            {
                MuxConsole.WriteSuccess("Orchestrator reports task complete.");
                goalComplete = true;
                break;
            }

            if (toolCalls.Count == 0 && !string.IsNullOrWhiteSpace(response))
            {
                string truncated = response.Length > 200 ? response[..200] + "..." : response;
                progressLog.Add($"[Orchestrator] {truncated}");
            }
        }

        if (!goalComplete)
            MuxConsole.WriteWarning($"Max orchestrator iterations ({maxOrchestratorIterations}) reached.");
    }

    // SubAgent Execution Logic

    private static async Task<(string RawResult, string? Status, string? Summary, string? Artifacts)> RunSubAgentAsync(
        (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def) specialist,
        string subTask,
        int maxIterations,
        CancellationToken cancellationToken,
        bool prodMode = false)
    {
        using var subAgentSpan = OtelTracer.GetSource().StartActivity("agent_session");
        subAgentSpan?.SetTag("agent", specialist.Def.Name);
        subAgentSpan?.SetTag("mode", "sub_agent");
        
        int stuckCount = 0;
        bool isFirstIteration = true;

        string? completionStatus = null;
        string? completionSummary = null;
        string? completionArtifacts = null;

        var subProgress = new List<string>();
        var fullResponseAccumulator = new StringBuilder();

        for (int i = 0; i < maxIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messages = new List<ChatMessage>();

            if (isFirstIteration)
            {
                var taskInstruction = PreambleBuilder.WrapTask(specialist.Def.Name, subTask, App.Config.IsUsingDockerForExec);

                var imageRegex = new Regex(
                    @"(?:\[image:\s*)?((?:[A-Za-z]:\\|\\\\|/)[\w\\\/.:\-\s]+\.(?:png|jpe?g|gif|webp))(?:\s*\])?",
                    RegexOptions.IgnoreCase);

                var imageMatches = imageRegex.Matches(subTask);

                if (imageMatches.Count > 0)
                {
                    var contentParts = new List<AIContent>();

                    string textPart = imageRegex.Replace(subTask, "").Trim();
                    contentParts.Add(new TextContent(
                        PreambleBuilder.WrapTask(specialist.Def.Name, textPart, App.Config.IsUsingDockerForExec)));

                    foreach (Match match in imageMatches)
                    {
                        string imagePath = match.Groups[1].Value.Trim();
                        if (File.Exists(imagePath))
                        {
                            byte[] imageBytes = await File.ReadAllBytesAsync(imagePath);
                            string mediaType = Path.GetExtension(imagePath).ToLower() switch
                            {
                                ".png" => "image/png",
                                ".jpg" => "image/jpeg",
                                ".jpeg" => "image/jpeg",
                                ".gif" => "image/gif",
                                ".webp" => "image/webp",
                                _ => "image/png"
                            };
                            contentParts.Add(new DataContent(imageBytes, mediaType));
                        }
                    }

                    messages.Add(new(ChatRole.User, contentParts));
                }
                else
                {
                    messages.Add(new(ChatRole.User, taskInstruction));
                }

                isFirstIteration = false;
            }
            else
            {
                var continuation = new StringBuilder();
                if (subProgress.Count > 0)
                {
                    continuation.AppendLine("Steps completed so far:");
                    foreach (var step in subProgress.TakeLast(8))
                        continuation.AppendLine($"  • {step}");
                    continuation.AppendLine();
                }

                int remaining = maxIterations - i;
                if (remaining <= 2)
                {
                    continuation.AppendLine($"⚠ WARNING: Only {remaining} iteration(s) remaining. " +
                        "You must complete the task or call signal_task_complete with a partial/failure status now.");
                    continuation.AppendLine();
                }

                continuation.Append("Continue working on the sub-task, or call signal_task_complete if done.");
                messages.Add(new(ChatRole.User, continuation.ToString()));
            }

            var iterResponse = new StringBuilder();
            List<string> iterToolCalls = [];

            ThinkingIndicator? thinking = null;
            bool startedStreaming = false;
            bool currentlyStreaming = false;
            var turnSw = Stopwatch.StartNew();

            try
            {
                MuxConsole.WriteAgentTurnHeader(specialist.Def.Name);
                
                using var turnSpan = OtelTracer.GetSource().StartActivity("agent_turn");
                turnSpan?.SetTag("agent", specialist.Def.Name);
                turnSpan?.SetTag("iteration", i);
                
                thinking = MuxConsole.BeginThinking(specialist.Def.Name);
                
                using var activityTimeout = ActivityTimeout.Start(TimeSpan.FromSeconds(ExecutionLimits.Current.ActivityTimeoutSeconds), cancellationToken);

                await foreach (var update in specialist.Agent
                    .RunStreamingAsync(messages, specialist.Session)
                    .WithCancellation(cancellationToken))
                {

                    activityTimeout.Ping();

                    // Process text first if this update has text, we need streaming mode.
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        if (!prodMode && !currentlyStreaming)
                        {
                            // Transition: thinking -> streaming
                            MuxConsole.BeginStreaming();
                            currentlyStreaming = true;
                            startedStreaming = true;
                        }

                        MuxConsole.WriteStream(update.Text);
                        iterResponse.Append(update.Text);
                        
                        HookWorker.Enqueue(new HookEvent
                        {
                            Event = "text_chunk",
                            Agent = specialist.Def.Name,
                            Text = update.Text,
                            Timestamp = DateTimeOffset.UtcNow
                        });
                    }

                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent fc)
                        {
                            HookWorker.Enqueue(new HookEvent
                            {
                                Event = "tool_call",
                                Agent = specialist.Def.Name,
                                Tool = fc.Name,
                                Timestamp = DateTimeOffset.UtcNow
                            });
                            
                            var toolSpan = OtelTracer.GetSource().StartActivity("tool_call");
                            toolSpan?.SetTag("agent", specialist.Def.Name);
                            toolSpan?.SetTag("tool", fc.Name);
                            

                            // If we were streaming text and now a tool call arrives,
                            // transition back to thinking mode so the indicator reappears.
                            if (!prodMode && currentlyStreaming)
                            {
                                currentlyStreaming = false;
                                thinking?.Dispose();
                                thinking = MuxConsole.ResumeThinking(specialist.Def.Name);
                                iterToolCalls.Add(fc.Name);
                                thinking.UpdateStatus(iterToolCalls);
                            }
                            else
                            {
                                iterToolCalls.Add(fc.Name);
                                thinking?.UpdateStatus(iterToolCalls);
                            }

                            if (prodMode)
                                Console.Write($"[[TOOL_CALL]]{fc.Name}[[END_TOOL_CALL]]");

                            if (fc.Name.Equals("signal_task_complete", StringComparison.OrdinalIgnoreCase)
                                && fc.Arguments is not null)
                            {
                                if (fc.Arguments.TryGetValue("status", out var statusObj))
                                    completionStatus = statusObj?.ToString();
                                if (fc.Arguments.TryGetValue("summary", out var summaryObj))
                                    completionSummary = summaryObj?.ToString();
                                if (fc.Arguments.TryGetValue("artifacts", out var artifactsObj))
                                    completionArtifacts = artifactsObj?.ToString();
                            }

                            if (!fc.Name.Equals("signal_task_complete", StringComparison.OrdinalIgnoreCase))
                                subProgress.Add($"Called {fc.Name}");
                        }
                        else if (content is FunctionResultContent fr)
                        {

                            HookWorker.Enqueue(new HookEvent
                            {
                                Event = "tool_result",
                                Agent = specialist.Def.Name,
                                Summary = fr.Result?.ToString(),
                                Timestamp = DateTimeOffset.UtcNow
                            });
                            
                            var resultText = fr.Result?.ToString();
                            Activity.Current?.SetTag("success", true);
                            if (resultText != null)
                                Activity.Current?.SetTag("result", resultText.Length > 4096 ? resultText[..4096] : resultText);
                            Activity.Current?.Stop();

                            OtelMetrics.ToolCalls.Add(1,
                                new KeyValuePair<string, object?>("agent", specialist.Def.Name),
                                new KeyValuePair<string, object?>("tool", fr.CallId));
                            
                            if (!prodMode && !currentlyStreaming && thinking != null)
                            {
                                thinking.Dispose();
                                thinking = MuxConsole.BeginThinking(specialist.Def.Name);
                                if (iterToolCalls.Count > 0)
                                    thinking.UpdateStatus(iterToolCalls);
                            }
                        }
                        else if (content is UsageContent usageContent)
                        {
                            _swarmTokens += (uint)(usageContent.Details.TotalTokenCount ?? 0);
                            OtelMetrics.RecordTokens(specialist.Def.Name, "sub_agent",
                                usageContent.Details.InputTokenCount ?? 0,
                                usageContent.Details.OutputTokenCount ?? 0);
                        }
                    }
                }

                if (prodMode)
                {
                    Console.Write("[[END_AGENT_TURN]]");
                }
                else
                {
                    if (currentlyStreaming)
                        MuxConsole.EndStreaming();

                    MuxConsole.WriteAgentTurnFooter();
                }
            }
            finally
            {
                // If we began streaming but hit an exception before EndStreaming, ensure we end it.
                if (!prodMode && currentlyStreaming)
                {
                    try { MuxConsole.EndStreaming(); } catch { /* ignore */ }
                }

                thinking?.Dispose();
                
                HookWorker.Enqueue(new HookEvent
                {
                    Event = "turn_end",
                    Agent = specialist.Def.Name,
                    Summary = iterResponse.Length > 500 ? iterResponse.ToString(0, 500) + "..." : iterResponse.ToString(),
                    Timestamp = DateTimeOffset.UtcNow
                });
                
                turnSw.Stop();
                OtelMetrics.AgentTurns.Add(1, new KeyValuePair<string, object?>("agent", specialist.Def.Name));
                OtelMetrics.AgentTurnDuration.Record(turnSw.ElapsedMilliseconds,
                    new KeyValuePair<string, object?>("agent", specialist.Def.Name));
            }

            MuxConsole.WriteLine();
            fullResponseAccumulator.AppendLine(iterResponse.ToString());

            if (string.IsNullOrWhiteSpace(iterResponse.ToString()) && iterToolCalls.Count == 0)
            {
                stuckCount++;
                if (stuckCount >= ExecutionLimits.Current.MaxStuckCount)
                    return (fullResponseAccumulator.ToString(), "partial",
                        $"{specialist.Def.Name} reached max stuck count ({stuckCount}) with no further output",
                        null);

                subProgress.Add($"[System] No output on iteration {i + 1}. You must take an action or call signal_task_complete.");
                continue;
            }

            stuckCount = 0;

            if (iterToolCalls.Any(t => t.Contains("signal_task_complete", StringComparison.OrdinalIgnoreCase)))
            {
                MuxConsole.WriteTaskComplete(specialist.Def.Name, "sub-task");
                MuxConsole.WriteRule();


                string raw = fullResponseAccumulator.ToString();
                return (raw, completionStatus, completionSummary, completionArtifacts);
            }

            if (!string.IsNullOrWhiteSpace(iterResponse.ToString()) && iterToolCalls.Count == 0)
            {
                string note = iterResponse.ToString();
                if (note.Length > 150) note = note[..150] + "...";
                subProgress.Add($"Agent note: {note}");
            }
        }

        MuxConsole.WriteWarning($"{specialist.Def.Name} hit max iterations ({maxIterations})");

        string failRaw = fullResponseAccumulator.ToString();
        if (subProgress.Count > 0)
            failRaw += "\nProgress: " + string.Join("; ", subProgress.TakeLast(5));

        return (failRaw, "partial", $"Hit max iterations ({maxIterations}). Last steps: " +
            string.Join(", ", subProgress.TakeLast(3)), null);
    }
}