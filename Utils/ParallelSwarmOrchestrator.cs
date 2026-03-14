using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using MuxSwarm.State;

namespace MuxSwarm.Utils;

/// <summary>
/// Parallel Swarm System (The "Classroom" Approach).
/// High-throughput orchestration for independent, non-blocking sub-tasks.
/// </summary>
public static class ParallelSwarmOrchestrator
{
    public static readonly string PromptsDir = PlatformContext.PromptsDirectory;
    public static readonly string SwarmConfPath = PlatformContext.SwarmPath;
    private static readonly string FallbackOrchPromptPath = Path.Combine(PromptsDir, "orchestrator_parallel.md");

    private static readonly object _stateLock = new();
    private static bool _sessionDirty = false;

    private const int ProgressEntryBudget = 1000;
    private const int CrossAgentContextBudget = 2000;
    private const int ProgressLogTotalBudget = 4500;
    private const int DefaultMaxOrchestratorIterations = 15;
    private const int DefaultMaxSubAgentIterations = 8;
    private const int MaxSubTaskRetries = 4;
    private const int MaxStuckCount = 3;

    public record ParallelTaskRequest(
        [Description("Name of the agent to assign (e.g., CodeAgent, WebAgent)")] string AgentName,
        [Description("The specific sub-task or instruction for this agent")] string Task
    );

    private record DelegationResult(
        string AgentName,
        string CompactedResult,
        string? Status,
        string? Summary,
        string? Artifacts
    );

    private record RetryState(int AttemptCount, string? LastFailureReason);

    // ── Helpers mirroring MultiAgentOrchestrator ──────────────────────────

    private static IList<AITool> GetOrchestratorFilteredToolsFromConfig(IList<AITool> mcpTools)
    {
        if (!File.Exists(SwarmConfPath))
            return new List<AITool>();

        try
        {
            var json = File.ReadAllText(SwarmConfPath);
            var config = JsonSerializer.Deserialize<SwarmConfig>(json);
            var orch = config?.Orchestrator;
            if (orch == null) return new List<AITool>();

            return Common.ApplyToolFilter(
                tools: mcpTools,
                mcpServers: orch.McpServers,
                toolPatterns: orch.ToolPatterns,
                includeAllWhenEmpty: false
            );
        }
        catch { return new List<AITool>(); }
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

    private static string InjectRetryHint(string task, int attemptNumber, string? lastFailureReason)
    {
        var hint = new StringBuilder();
        hint.AppendLine();
        hint.AppendLine("---");
        hint.AppendLine($"[RETRY ATTEMPT {attemptNumber}/{MaxSubTaskRetries}]");

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
        return task + hint;
    }

    private static async Task<string> EnrichTaskWithCrossAgentContext(
        string originalTask,
        string targetAgentName,
        List<DelegationResult> priorResults,
        IChatClient? compactionClient,
        ChatOptions? compactionChatOptions = null)
    {
        List<DelegationResult> snapshot;
        lock (_stateLock)
        {
            if (priorResults.Count == 0) return originalTask;
            snapshot = priorResults
                .Where(r => !r.AgentName.Equals(targetAgentName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (snapshot.Count == 0) return originalTask;

        var context = new StringBuilder();
        context.AppendLine(originalTask);
        context.AppendLine();
        context.AppendLine("--- Context from prior agents ---");

        int budgetRemaining = CrossAgentContextBudget;

        foreach (var result in snapshot)
        {
            if (budgetRemaining <= 0) break;

            string entry = result.CompactedResult;
            if (entry.Length > budgetRemaining)
            {
                entry = await ResultCompactor.CompactAsync(
                    entry, result.Status, result.Summary, result.Artifacts,
                    charBudget: budgetRemaining,
                    chatClient: compactionClient,
                    chatOptions: compactionChatOptions);
            }

            context.AppendLine($"[{result.AgentName}]: {entry}");
            budgetRemaining -= entry.Length + result.AgentName.Length + 5;
        }

        context.AppendLine("--- End context ---");
        return context.ToString();
    }

    // ── Core Orchestration ───────────────────────────────────────────────

    public static async Task RunAsync(
        Func<string, IChatClient> chatClientFactory,
        IList<AITool> mcpTools,
        Dictionary<string, string> agentModels,
        int maxDegreeOfParallelism = 4,
        int maxOrchestratorIterations = DefaultMaxOrchestratorIterations,
        int maxSubAgentIterations = DefaultMaxSubAgentIterations,
        bool prodMode = false,
        string? incomingGoal = null,
        bool continuous = false,
        string? goalId = null,
        uint minDelaySeconds = 300,
        uint persistIntervalSeconds = 60,
        uint sessionRetention = 10,
        CancellationToken cancellationToken = default)
    {
        // ── 1. Load config & definitions ─────────────────────────────────

        var agentDefs = Common.GetAgentDefinitions(SwarmConfPath);

        SwarmConfig? swarmConfig = null;
        try { swarmConfig = JsonSerializer.Deserialize<SwarmConfig>(File.ReadAllText(SwarmConfPath)); }
        catch { /* defaults */ }

        string orchestratorPromptPath = GetOrchestratorPromptPath();
        SkillLoader.LoadSkills();

        var specialists = new Dictionary<string, (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def)>();
        var delegationResults = new List<DelegationResult>();
        var retryRegistry = new Dictionary<string, RetryState>();
        var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);
        _sessionDirty = false;

        // ── 2. Compaction client ─────────────────────────────────────────

        IChatClient? compactionClient = null;
        try
        {
            var compactionModel = agentModels.GetValueOrDefault("Compaction", agentModels["Orchestrator"]);
            compactionClient = chatClientFactory(compactionModel);
        }
        catch { /* falls back to extractive only */ }

        ChatOptions? compactionChatOptions = swarmConfig?.CompactionAgent?.ModelOpts?.ToChatOptions();

        // ── 3. Shared tools ──────────────────────────────────────────────

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

        // Sub-agent delegation tool (for agents with CanDelegate)
        var subAgentDelegateTool = AIFunctionFactory.Create(
            method: async (
                [Description("Name of the specialist agent to delegate to. Cannot delegate to Orchestrator.")] string agentName,
                [Description("The specific sub-task or instruction for the specialist agent")] string task
            ) =>
            {
                if (agentName == "Orchestrator")
                    return "[ERROR] Sub-agents cannot delegate back to the Orchestrator.";

                if (!specialists.TryGetValue(agentName, out var specialist))
                {
                    var available = string.Join(", ", specialists.Keys.Where(k => k != "Orchestrator"));
                    return $"[ERROR] Unknown agent '{agentName}'. Available agents: {available}";
                }

                return await ExecuteParallelWorker(
                    agentName, task, "SubAgent",
                    specialists, delegationResults, retryRegistry,
                    chatClientFactory, agentModels, compactionClient, compactionChatOptions,
                    maxSubAgentIterations, prodMode, ct: cancellationToken);
            },
            name: "delegate_to_agent",
            description: "Delegate a sub-task to a specialist agent by name. Cannot delegate to the Orchestrator."
        );

        // ── 4. Build specialist agents ───────────────────────────────────

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

                    prompt += "\n\n(NOTICE) You have sub-agent delegation enabled via the delegate_to_agent tool. Available agents:\n"
                              + string.Join("\n", roster);

                    prompt += """

                              ## Memory Policy (MANDATORY)

                              **At the START of your task:** If relevant prior context has not already been provided to you, delegate to MemoryAgent to search for related decisions, research, or artifacts before beginning work. Skip this if context was already passed in your task instructions.

                              **At the END of your task:** It is recommended you delegate to MemoryAgent to persist your findings before calling signal_task_complete. Pass it:
                              - Task name and outcome (success/failure)
                              - Key findings or decisions made
                              - Artifact paths written to NAS (if any)
                              - A summary of what you did and which agents helped

                              """;
                }

                IList<AITool> filteredTools = def.ToolFilter(mcpTools);

                if (filteredTools.Count == 0)
                    MuxConsole.WriteWarning($"{def.Name} matched 0 tools. Check mcpServers in swarm.json or ToolFilter.");
                else
                    MuxConsole.WriteSuccess($"{def.Name} has {filteredTools.Count} tools available");

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
                    description: "List all available skills with their descriptions."
                );

                var readSkillTool = AIFunctionFactory.Create(
                    method: (
                        [Description("Name of the skill to load.")] string skillName
                    ) =>
                    {
                        var content = SkillLoader.ReadSkill(skillName);
                        if (content != null) return content;

                        var available = SkillLoader.GetSkillMetadata(def.Name);
                        var listing = string.Join("\n", available.Select(s => $"- {s.Name}: {s.Description}"));
                        return $"Skill '{skillName}' not found. Available:\n{listing}";
                    },
                    name: "read_skill",
                    description: "Read the full instructions for a skill by name."
                );

                var agentTools = def.CanDelegate
                    ? (IList<AITool>)[taskCompleteTool, listSkillsTool, readSkillTool, LocalAiFunctions.SleepTool, analyzeImageTool, subAgentDelegateTool, .. filteredTools]
                    : (IList<AITool>)[taskCompleteTool, listSkillsTool, readSkillTool, LocalAiFunctions.SleepTool, analyzeImageTool, .. filteredTools];

                if (def.CanDelegate)
                    MuxConsole.WriteMuted($"{def.Name} has sub-agent delegation enabled");

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

        // ── 5. The Parallel "Assignment" Tool ────────────────────────────

        var delegateParallelTool = AIFunctionFactory.Create(
            method: async (
                [Description("A list of agent assignments to run simultaneously")]
                IEnumerable<ParallelTaskRequest> assignments
            ) =>
            {
                var assignmentList = assignments.ToList();
                MuxConsole.WriteInfo($"[CLASSROOM] Dispatching {assignmentList.Count} tasks concurrently...");

                var taskBatch = assignmentList.Select(async req =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        return await ExecuteParallelWorker(
                            req.AgentName, req.Task, "Orchestrator",
                            specialists, delegationResults, retryRegistry,
                            chatClientFactory, agentModels, compactionClient, compactionChatOptions,
                            maxSubAgentIterations, prodMode, ct: cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(taskBatch);

                var synthesis = new StringBuilder();
                synthesis.AppendLine("### PARALLEL BATCH COMPLETED ###");
                foreach (var res in results) synthesis.AppendLine(res);
                return synthesis.ToString();
            },
            name: "delegate_parallel",
            description: "Executes multiple sub-tasks simultaneously. Use this for independent tasks like " +
                         "researching different topics or auditing multiple files. Each assignment specifies " +
                         "an AgentName and a Task string."
        );

        // ── 6. Build the orchestrator ────────────────────────────────────

        string orchestratorPrompt = await Common.LoadPromptAsync(orchestratorPromptPath);

        string agentRoster = string.Join("\n", agentDefs.Select(d =>
            $"  - {d.Name}: {d.Description}"));
        orchestratorPrompt += $"\n\nAvailable agents:\n{agentRoster}";

        orchestratorPrompt += """


                              ## Parallel Dispatch
                              You have access to `delegate_parallel` which dispatches multiple agent tasks concurrently.
                              Use this when sub-tasks are independent and can run simultaneously.
                              Group related but independent work into a single batch call for maximum throughput.
                              After the batch returns, review all results and either dispatch another batch or
                              call signal_task_complete.
                              """;

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

        // ── Load or create continuous state ──────────────────────────────
        CurrentStateMetadata? state = null;
        if (continuous)
        {
            goalId ??= Guid.NewGuid().ToString("N")[..8];
            state = ContinuousStateManager.Load(goalId, PlatformContext.SessionsDirectory)
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

        string sessionContext = SessionSummarizer.BuildRollingContext(PlatformContext.SessionsDirectory);
        if (!string.IsNullOrEmpty(sessionContext))
            orchestratorPrompt += $"\n\n{sessionContext}";

        string orchestratorModelId = agentModels["Orchestrator"];
        IChatClient orchestratorClient = chatClientFactory(orchestratorModelId);

        var orchestratorFilteredTools = GetOrchestratorFilteredToolsFromConfig(mcpTools);

        if (orchestratorFilteredTools.Count == 0)
            MuxConsole.WriteMuted("Orchestrator has 0 MCP tools. Using built-in tools only.");
        else
            MuxConsole.WriteSuccess($"Orchestrator has {orchestratorFilteredTools.Count} MCP tools available");

        var orchestratorTools = (IList<AITool>)[
            delegateParallelTool,
            taskCompleteTool,
            LocalAiFunctions.ListSkillsTool,
            LocalAiFunctions.ReadSkillTool,
            LocalAiFunctions.SleepTool,
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
        }

        var orchestratorAgent = orchestratorClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "Orchestrator",
            ChatOptions = orchChatOptions
        });

        var orchestratorSession = await orchestratorAgent.CreateSessionAsync();

        // ── Periodic session persister ────────────────────────────────────
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

        // ── 7. Goal entry ────────────────────────────────────────────────

        bool goalFromArgs = !string.IsNullOrEmpty(incomingGoal);

        if (!prodMode && !goalFromArgs)
            MuxConsole.WriteBody("Parallel Swarm ready. Enter a goal (or /qm to quit):");

        if (!prodMode)
            MuxConsole.WriteMuted("Press [Escape] during execution to cancel the current goal.");

        MuxConsole.WriteLine();
        MuxConsole.WriteRule();

        // ── 8. Main loop ─────────────────────────────────────────────────

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
                string? input = StdinCancelMonitor.Instance?.ReadLine() 
                                ?? Console.ReadLine();

                if (string.IsNullOrEmpty(input) ||
                    input.Trim().Equals("/qm", StringComparison.OrdinalIgnoreCase) ||
                    input.Trim().Equals("/qc", StringComparison.OrdinalIgnoreCase))
                {
                    MuxConsole.WriteSuccess("Exited from Parallel Swarm interface successfully!");
                    break;
                }

                goal = File.Exists(input) ? File.ReadAllText(input) : input;
            }

            // Fresh sessions per goal
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
            currentIterationSessionDir = Path.Combine(PlatformContext.SessionsDirectory, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));

            if (state != null)
            {
                state.Status = "running";
                ContinuousStateManager.WriteAtomic(goalId!, state, PlatformContext.SessionsDirectory);
            }

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
                    delegationResults: delegationResults,
                    maxOrchestratorIterations: continuous ? int.MaxValue : maxOrchestratorIterations,
                    cancellationToken: goalCts.Token,
                    prodMode: prodMode,
                    continuous: continuous);
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
                escapeListener?.Dispose();
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
                _sessionDirty = false;
            }

            if (continuous && state != null)
            {
                state.Iteration++;
                state.LastCompletedAt = DateTime.Now;
                state.NextWakeAt = DateTime.Now.AddSeconds(state.MinDelaySeconds);
                state.Status = wasInterrupted ? "interrupted" : "sleeping";
                ContinuousStateManager.WriteAtomic(goalId!, state, PlatformContext.SessionsDirectory);
                Common.PruneOldSessions(PlatformContext.SessionsDirectory, sessionRetention);
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

        // ── Graceful shutdown ─────────────────────────────────────────────
        if (continuous && state != null)
        {
            if (cancellationToken.IsCancellationRequested)
                ContinuousStateManager.MarkStopped(goalId!, state, PlatformContext.SessionsDirectory);
            else
                ContinuousStateManager.Clear(goalId!, PlatformContext.SessionsDirectory);
        }
    }

    // ── Goal Execution (Orchestrator Loop) ───────────────────────────────

    private static async Task RunOrchestratedGoalAsync(
        string goal,
        AIAgent orchestratorAgent,
        AgentSession orchestratorSession,
        List<DelegationResult> delegationResults,
        int maxOrchestratorIterations,
        CancellationToken cancellationToken,
        bool prodMode = false,
        bool continuous = false)
    {
        if (!prodMode)
            MuxConsole.WriteLine();
        else
            Console.WriteLine($"[[START_ORCHESTRATOR]]{DateTime.Now:yyyy-MM-dd HH:mm:ss}[[END_TIMESTAMP]]");

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

            lock (_stateLock)
            {
                resultsCountBefore = delegationResults.Count;
            }

            var messages = new List<ChatMessage>();

            if (isFirstIteration)
            {
                messages.Add(new(ChatRole.User,
                    $"""
                    Goal: {goal}

                    Plan how to accomplish this by dispatching parallel batches to your available agents
                    via delegate_parallel. Group independent sub-tasks into a single batch for concurrency.
                    After all work completes, synthesize results and call signal_task_complete.
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
                        if (totalChars > ProgressLogTotalBudget)
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
                    "Dispatch another parallel batch if there is more work, or call signal_task_complete if done.");

                messages.Add(new(ChatRole.User, continuation.ToString()));
            }

            var responseText = new StringBuilder();
            toolCalls.Clear();

            ThinkingIndicator? thinking = null;
            bool currentlyStreaming = false;

            try
            {
                if (prodMode)
                {
                    Console.Write("[[START_AGENT_TURN]]Orchestrator[[END_AGENT_NAME]]");
                }
                else
                {
                    MuxConsole.WriteAgentTurnHeader("Orchestrator");
                    thinking = MuxConsole.BeginThinking("Orchestrator");
                }

                await foreach (var update in orchestratorAgent
                    .RunStreamingAsync(messages, orchestratorSession)
                    .WithCancellation(cancellationToken))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        if (!prodMode && !currentlyStreaming)
                        {
                            MuxConsole.BeginStreaming();
                            currentlyStreaming = true;
                        }

                        MuxConsole.WriteStream(update.Text);
                        responseText.Append(update.Text);
                    }

                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent fc)
                        {
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
                            if (!prodMode && !currentlyStreaming && thinking != null)
                            {
                                thinking.Dispose();
                                thinking = MuxConsole.BeginThinking("Orchestrator");
                                if (toolCalls.Count > 0)
                                    thinking.UpdateStatus(toolCalls);
                            }

                            if (prodMode)
                            {
                                string resultText = fr.Result?.ToString() ?? "";
                                Console.Write($"[[TOOL_RESULT]]{resultText}[[END_TOOL_RESULT]]");
                            }

                            if (toolCalls.Any(t => t.Contains("signal_task_complete", StringComparison.OrdinalIgnoreCase)))
                                goto streamComplete;
                        }
                    }
                }

                streamComplete:;

                if (prodMode)
                    Console.Write("[[END_AGENT_TURN]]");
                else
                {
                    if (currentlyStreaming) MuxConsole.EndStreaming();
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
                if (!prodMode && currentlyStreaming)
                {
                    try { MuxConsole.EndStreaming(); } catch { /* ignore */ }
                }
                thinking?.Dispose();
            }

            MuxConsole.WriteLine();
            string response = responseText.ToString();

            lock (_stateLock)
            {
                for (int r = resultsCountBefore; r < delegationResults.Count; r++)
                {
                    var dr = delegationResults[r];
                    progressLog.Add($"[{dr.AgentName}] {dr.CompactedResult}");
                }
            }

            if (string.IsNullOrWhiteSpace(response) && toolCalls.Count == 0)
            {
                stuckCount++;
                MuxConsole.WriteWarning($"Orchestrator stuck — empty response ({stuckCount}/{MaxStuckCount})");

                if (stuckCount >= MaxStuckCount)
                {
                    MuxConsole.WriteError("Orchestrator stuck repeatedly — aborting.");
                    break;
                }

                progressLog.Add($"[System] Orchestrator produced no output on iteration {i + 1}. " +
                    "You must either dispatch a parallel batch or call signal_task_complete.");
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

    // ── Parallel Worker (per-agent execution) ────────────────────────────

    private static async Task<string> ExecuteParallelWorker(
        string agentName,
        string task,
        string callerName,
        Dictionary<string, (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def)> specialists,
        List<DelegationResult> delegationResults,
        Dictionary<string, RetryState> retryRegistry,
        Func<string, IChatClient> chatClientFactory,
        Dictionary<string, string> agentModels,
        IChatClient? compactionClient,
        ChatOptions? compactionChatOptions,
        int maxSubAgentIterations,
        bool prodMode,
        CancellationToken ct)
    {
        if (!specialists.TryGetValue(agentName, out var specialist))
        {
            var available = string.Join(", ", specialists.Keys.Where(k => k != "Orchestrator"));
            return $"[ERROR] Unknown agent '{agentName}'. Available agents: {available}";
        }

        if (agentName == callerName)
            return $"[ERROR] Agent '{callerName}' cannot delegate to itself.";

        string retryKey = $"{agentName}:{Math.Abs(task.GetHashCode())}";
        RetryState? retryState;
        int attemptNumber;

        lock (_stateLock)
        {
            retryRegistry.TryGetValue(retryKey, out retryState);
            attemptNumber = (retryState?.AttemptCount ?? 0) + 1;
        }

        MuxConsole.WriteDelegation(callerName, agentName, $"[Parallel] {task}");

        if (attemptNumber > 1)
            MuxConsole.WriteWarning($"RETRY {attemptNumber}/{MaxSubTaskRetries} — Prior failure: {retryState?.LastFailureReason ?? "unknown"}");

        // Enrich with cross-agent context (thread-safe snapshot inside)
        string enrichedTask = await EnrichTaskWithCrossAgentContext(
            task, agentName, delegationResults, compactionClient, compactionChatOptions);

        if (attemptNumber > 1 && retryState != null)
            enrichedTask = InjectRetryHint(enrichedTask, attemptNumber, retryState.LastFailureReason);

        // Run the sub-agent
        var (rawResult, status, summary, artifacts) = await RunSubAgentAsync(
            specialist, enrichedTask, maxSubAgentIterations, ct, prodMode: prodMode);

        bool succeeded = status == "success";

        lock (_stateLock)
        {
            if (!succeeded)
            {
                retryRegistry[retryKey] = new RetryState(
                    AttemptCount: attemptNumber,
                    LastFailureReason: summary ?? "No summary provided");

                if (attemptNumber >= MaxSubTaskRetries)
                    MuxConsole.WriteError($"{agentName} failed {MaxSubTaskRetries} times on this sub-task.");
            }
            else
            {
                retryRegistry.Remove(retryKey);
            }
        }

        // Compact the result
        string compacted = await ResultCompactor.CompactAsync(
            rawResult,
            completionStatus: status,
            completionSummary: summary,
            completionArtifacts: artifacts,
            charBudget: ProgressEntryBudget,
            chatClient: compactionClient,
            chatOptions: compactionChatOptions);

        lock (_stateLock)
        {
            delegationResults.Add(new DelegationResult(agentName, compacted, status, summary, artifacts));
        }

        if (prodMode)
            compacted = $"[[START_AGENT_TURN]]{agentName}[[END_AGENT_NAME]]{compacted}[[END_AGENT_TURN]]";

        if (!succeeded && attemptNumber >= MaxSubTaskRetries)
        {
            compacted += $"\n[RETRY_EXHAUSTED] {agentName} failed {MaxSubTaskRetries} attempts. " +
                         $"Last reason: {retryState?.LastFailureReason ?? summary}. " +
                         "Consider a different approach or agent, or surface this to the user.";
        }

        return string.IsNullOrWhiteSpace(compacted)
            ? $"[{agentName} completed but returned no output]"
            : compacted;
    }

    // ── SubAgent Execution (mirrors MultiAgentOrchestrator.RunSubAgentAsync) ─

    private static async Task<(string RawResult, string? Status, string? Summary, string? Artifacts)> RunSubAgentAsync(
        (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def) specialist,
        string subTask,
        int maxIterations,
        CancellationToken cancellationToken,
        bool prodMode = false)
    {
        if (prodMode)
            Console.WriteLine($"[[START_SUBAGENT]]{specialist.Def.Name}[[END_AGENT_NAME]]{subTask}[[END_TASK]]");

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
                            byte[] imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
                            string mediaType = Path.GetExtension(imagePath).ToLower() switch
                            {
                                ".png" => "image/png",
                                ".jpg" or ".jpeg" => "image/jpeg",
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
            bool currentlyStreaming = false;

            try
            {
                if (prodMode)
                {
                    Console.Write("[[START_AGENT_TURN]]" + specialist.Def.Name + "[[END_AGENT_NAME]]");
                }
                else
                {
                    MuxConsole.WriteAgentTurnHeader(specialist.Def.Name);
                    thinking = MuxConsole.BeginThinking(specialist.Def.Name);
                }

                using var activityTimeout = ActivityTimeout.Start(TimeSpan.FromMinutes(3), cancellationToken);

                await foreach (var update in specialist.Agent
                    .RunStreamingAsync(messages, specialist.Session)
                    .WithCancellation(cancellationToken))
                {
                    activityTimeout.Ping();

                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        if (!prodMode && !currentlyStreaming)
                        {
                            MuxConsole.BeginStreaming();
                            currentlyStreaming = true;
                        }

                        MuxConsole.WriteStream(update.Text);
                        iterResponse.Append(update.Text);
                    }

                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent fc)
                        {
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
                            if (!prodMode && !currentlyStreaming && thinking != null)
                            {
                                thinking.Dispose();
                                thinking = MuxConsole.BeginThinking(specialist.Def.Name);
                                if (iterToolCalls.Count > 0)
                                    thinking.UpdateStatus(iterToolCalls);
                            }

                            if (prodMode)
                            {
                                string resultText = fr.Result?.ToString() ?? "";
                                Console.Write($"[[TOOL_RESULT]]{resultText}[[END_TOOL_RESULT]]");
                            }
                        }
                    }
                }

                if (prodMode)
                    Console.Write("[[END_AGENT_TURN]]");
                else
                {
                    if (currentlyStreaming) MuxConsole.EndStreaming();
                    MuxConsole.WriteAgentTurnFooter();
                }
            }
            finally
            {
                if (!prodMode && currentlyStreaming)
                {
                    try { MuxConsole.EndStreaming(); } catch { /* ignore */ }
                }
                thinking?.Dispose();
            }

            MuxConsole.WriteLine();
            fullResponseAccumulator.AppendLine(iterResponse.ToString());

            if (string.IsNullOrWhiteSpace(iterResponse.ToString()) && iterToolCalls.Count == 0)
            {
                stuckCount++;
                if (stuckCount >= MaxStuckCount)
                    return ($"[{specialist.Def.Name} stuck after {stuckCount} empty responses]", "failure",
                            "Agent produced no output repeatedly", null);

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