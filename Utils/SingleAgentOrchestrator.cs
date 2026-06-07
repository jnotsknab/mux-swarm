using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using MuxSwarm.State;
using System.Diagnostics;

namespace MuxSwarm.Utils;

public static class SingleAgentOrchestrator
{

    public static Common.AgentDefinition? AgentDef = null;

    private static readonly Dictionary<string, string> Models = Common.LoadAgentModels();
    //uncached tokens
    private static uint _cachedTokens;
    //total tokens in session (cached + uncached)
    private static uint _sessionTokens;

    private static bool _pendingCompaction;

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
        bool shouldPlan = false,
        string? systemPromptOverride = null,
        Func<string, IChatClient>? chatClientFactory = null,
        int? autoCompactTokenThreshold = 80_000,
        bool persistSession = true,
        string? incomingGoal = null,
        bool continuous = false,
        string? goalId = null,
        uint minDelaySeconds = 0,
        uint persistIntervalSeconds = 0,
        uint sessionRetention = 0,
        JsonElement? resumedSession = null,
        string? resumedSessionDir = null,
        bool allowSubAgents = false,
        bool allowParallelSubAgents = false)
    {
        MuxConsole.WriteBanner(persistSession ? "AGENTIC CHAT INTERFACE" : "STATELESS AGENTIC CHAT INTERFACE");
        MuxConsole.WriteMuted("Type /qc to exit, /compact to compress context. Press [Esc] to cancel the current turn.");

        var singleAgentDef = GetCurrSingleAgentDef();
        var delegationResults = new List<MultiAgentOrchestrator.DelegationResult>();
        var pDelegationResults = new List<ParallelSwarmOrchestrator.DelegationResult>();
        var retryRegistry = new Dictionary<string, ParallelSwarmOrchestrator.RetryState>();
        var semaphore = new SemaphoreSlim(App.MaxDegreeParallelism);

        if (singleAgentDef != null && singleAgentDef.CanDelegate && !allowSubAgents && !allowParallelSubAgents)
            MuxConsole.WriteWarning($"[AGENT] {singleAgentDef.Name} is configured with delegation capabilities, run /subagents (/sub) or /parasubagents (/psub) to enable delegation in single agent mode.");

        var resolvedModelId = "";
        using var sessionSpan = OtelTracer.GetSource().StartActivity("agent_session");

        try
        {
            var swarmJson = File.ReadAllText(MultiAgentOrchestrator.SwarmConfPath);
            var swarm = JsonSerializer.Deserialize<SwarmConfig>(swarmJson);

            var match = singleAgentDef != null
                ? swarm?.Agents?.FirstOrDefault(a =>
                    a.Name != null && a.Name.Equals(singleAgentDef.Name, StringComparison.OrdinalIgnoreCase))
                : null;

            HookWorker.Enqueue(new HookEvent
            {
                Event = "session_start",
                Agent = singleAgentDef?.Name,
                Summary = persistSession ? "agent" : "stateless",
                Text = incomingGoal,
                Timestamp = DateTimeOffset.UtcNow
            });

            resolvedModelId = match?.Model ?? swarm?.SingleAgent?.Model ?? "";

            sessionSpan?.SetTag("agent", singleAgentDef?.Name);
            sessionSpan?.SetTag("mode", persistSession ? "agent" : "stateless");
            sessionSpan?.SetTag("model", resolvedModelId);
            sessionSpan?.SetTag("goal_id", goalId);
            OtelMetrics.SessionsStarted.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef?.Name));

        }
        catch { /* fall through */ }

        IList<AITool> allTools = (mcpTools ?? Array.Empty<McpClientTool>()).Cast<AITool>().ToList();
        IList<AITool> filteredTools = singleAgentDef?.ToolFilter(allTools) ?? allTools;

        if (filteredTools.Count == 0)
            MuxConsole.WriteWarning($"{singleAgentDef?.Name ?? "Agent"} Matched 0 tools. Check mcpServers in swarm.json singleAgent block.");
        else
            MuxConsole.WriteSuccess($"{singleAgentDef?.Name ?? "Agent"} has {filteredTools.Count} tools available");

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
            initialGoal = MuxConsole.ReadInput(cancellationToken) ?? "";

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (IsQuitCommand(initialGoal))
        {
            MuxConsole.WriteSuccess("Exited from Chat interface successfully!");
            return;
        }

        HookWorker.Enqueue(new HookEvent
        {
            Event = "user_input",
            Agent = singleAgentDef?.Name,
            Text = initialGoal,
            Timestamp = DateTimeOffset.UtcNow
        });

        OtelMetrics.RecordAgentMessage(singleAgentDef?.Name ?? string.Empty, "user", initialGoal);
        OtelMetrics.GoalsReceived.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef?.Name ?? string.Empty));

        if (string.IsNullOrEmpty(singleAgentDef?.SystemPromptPath))
        {
            MuxConsole.WriteError("[AGENT] singleAgent.promptPath not set in swarm.json.");
            return;
        }

        var systemPrompt = string.IsNullOrEmpty(systemPromptOverride) ? await Common.LoadPromptAsync(singleAgentDef.SystemPromptPath) : systemPromptOverride;

        var preamble = PreambleBuilder.Build(
            singleAgentDef.Name,
            App.Config.IsUsingDockerForExec,
            continuous,
            shouldPlan);

        systemPrompt = preamble + "\n\n" + systemPrompt;

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

        var swarmVision = App.SwarmConfig?.VisionAgent;
        var visionModelId = swarmVision?.Model;

        var analyzeImageTool = chatClientFactory != null && !string.IsNullOrEmpty(visionModelId)
            ? LocalAiFunctions.CreateAnalyzeImageTool(chatClientFactory, visionModelId)
            : null;

        ChatOptions? compactChatOpts = MultiAgentOrchestrator.SwarmConfig?.CompactionAgent?.ModelOpts?.ToChatOptions();

        async Task<string> ExecuteDelegation(
            string agentName,
            string task,
            string callerName,
            bool restrictToSpecialists,
            bool parallel = false)
        {
            if (restrictToSpecialists && agentName == "Orchestrator")
                return "[ERROR] Sub-agents cannot delegate back to the Orchestrator.";

            if (!MultiAgentOrchestrator.Specialists.TryGetValue(agentName, out var specialist))
            {
                var available = restrictToSpecialists
                    ? string.Join(", ", MultiAgentOrchestrator.Specialists.Keys.Where(k => k != "Orchestrator"))
                    : string.Join(", ", MultiAgentOrchestrator.Specialists.Keys);
                return $"[ERROR] Unknown agent '{agentName}'. Available agents: {available}";
            }

            if (agentName == callerName)
                return $"[ERROR] Agent '{callerName}' cannot delegate to itself.";


            using var delegationSpan = OtelTracer.GetSource().StartActivity("delegation");
            delegationSpan?.SetTag("from", callerName);
            delegationSpan?.SetTag("to", agentName);
            var delegationSw = Stopwatch.StartNew();

            MuxConsole.WriteDelegation(callerName, agentName, task);


            int attempts = 0;
            var (rawResult, status, summary, artifacts) = await MultiAgentOrchestrator.RunSubAgentAsync(
                specialist, task, ExecutionLimits.Current.MaxSubTaskRetries, cancellationToken, prodMode: false, cleanSession: true);

            bool succeeded = status == "success";
            attempts += 1;

            if (!succeeded)
            {
                if (attempts >= ExecutionLimits.Current.MaxSubTaskRetries)
                    MuxConsole.WriteError($"{agentName} failed {ExecutionLimits.Current.MaxSubTaskRetries} times on this sub-task.");
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
                    chatClient: MultiAgentOrchestrator.CompactionClient,
                    chatOptions: compactChatOpts);
            });

            delegationResults.Add(new MultiAgentOrchestrator.DelegationResult(agentName, compacted, status, summary, artifacts));

            if (!succeeded && attempts >= ExecutionLimits.Current.MaxSubTaskRetries)
            {
                compacted += $"\n[RETRY_EXHAUSTED] {agentName} failed {ExecutionLimits.Current.MaxSubTaskRetries} attempts. " +
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

        var subAgentDelegateTool = AIFunctionFactory.Create(
            method: async (
                [Description("Name of the specialist agent to delegate to. Cannot delegate to Orchestrator.")] string agentName,
                [Description("The specific sub-task or instruction for the specialist agent")] string task
            ) =>
            {
                var specialists = MultiAgentOrchestrator.Specialists;
                if (agentName == "Orchestrator")
                    return "[ERROR] Sub-agents cannot delegate back to the Orchestrator.";

                if (!specialists.TryGetValue(agentName, out var specialist))
                {
                    var available = string.Join(", ", specialists.Keys.Where(k => k != "Orchestrator"));
                    return $"[ERROR] Unknown agent '{agentName}'. Available agents: {available}";
                }

                return await ExecuteDelegation(agentName, task, singleAgentDef.Name, restrictToSpecialists: true);
            },
            name: "delegate_to_agent_lite",
            description: "Delegate a sub-task to an agent by name. " +
                         "Use when a task would be better handled by another agent based on their specialization, or when offloading would improve efficiency. " +
                         "Cannot delegate to the Orchestrator. Note: Synchronous Version - Tip: Execute once with random string if no agent names are known to return err output with available agents"
        );

        var delegateParallelTool = AIFunctionFactory.Create(
            method: async (
                [Description("A list of agent assignments to run simultaneously")]
                IEnumerable<ParallelSwarmOrchestrator.ParallelTaskRequest> assignments
            ) =>
            {
                if (chatClientFactory == null)
                    return "[Error] Cannot create chat client for agent, chat client is null";

                var specialists = MultiAgentOrchestrator.Specialists;
                var assignmentList = assignments.ToList();
                MuxConsole.WriteInfo($"[CLASSROOM] Dispatching {assignmentList.Count} tasks concurrently...");

                var taskBatch = assignmentList.Select(async req =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        return await ParallelSwarmOrchestrator.ExecuteParallelWorker(
                            req.AgentName, req.Task, singleAgentDef.Name,
                            specialists, pDelegationResults, retryRegistry,
                            chatClientFactory, Models, compactionClient, compactionChatOptions,
                            maxIterations, false, ct: cancellationToken, cleanSession: true);
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
                         "an AgentName and a Task string. Tip: Execute once with random string if no agent names are known to return err output with available agents"
        );

        var singleAgentTools = (IList<AITool>)
        [
            listSkillsTool, readSkillTool, LocalAiFunctions.SleepTool, LocalAiFunctions.MuxRefreshTool,
            .. (analyzeImageTool != null ? new[] { analyzeImageTool } : Array.Empty<AITool>()),
            .. filteredTools
        ];

        if (shouldPlan || systemPromptOverride != null) singleAgentTools.Add(LocalAiFunctions.AskUserTool);

        if (allowSubAgents)
        {
            singleAgentTools.Add(subAgentDelegateTool);
            await MultiAgentOrchestrator.BuildSpecialists(Models, modelId => App.CreateChatClient(modelId),
                (App.McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList());
        }

        if (allowParallelSubAgents)
        {
            singleAgentTools.Add(delegateParallelTool);
            await MultiAgentOrchestrator.BuildSpecialists(Models, modelId => App.CreateChatClient(modelId),
                (App.McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList());
        }

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
                agentChatOptions.Reasoning = modelChatOpts.Reasoning;
                agentChatOptions.AdditionalProperties = modelChatOpts.AdditionalProperties;
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

        var conversationHistory = resumedSession.HasValue
            ? Common.ExtractMessagesFromSession(resumedSession.Value)
            : new List<ChatMessage>();

        if (resumedSession.HasValue)
            MuxConsole.WriteSuccess($" Extracted {conversationHistory.Count} messages from resumed session");

        _pendingCompaction = false;
        async Task<bool> TryCompactAsync()
        {
            using var compactSpan = OtelTracer.GetSource().StartActivity("compaction");
            compactSpan?.SetTag("agent", singleAgentDef?.Name);
            var compactSw = Stopwatch.StartNew();

            var cc = ResolveCompactionClient();
            if (cc == null)
            {
                MuxConsole.WriteWarning("No compaction model configured. Set compactionAgent in swarm.json.");
                return false;
            }

            uint beforeTokens = _sessionTokens > 0
                ? _sessionTokens
                : (uint)Common.EstimateTokenCount(conversationHistory);
            ChatMessage? compactedMsg = null;

            await MuxConsole.WithSpinnerAsync("Compacting conversation history", async () =>
            {
                compactedMsg = await ResultCompactor.CompactConversationAsync(
                    conversationHistory, cc, chatOptions: compactionChatOptions);

                session = await agent.CreateSessionAsync();
                conversationHistory.Clear();

                conversationHistory.Add(new ChatMessage(ChatRole.User, compactedMsg.Text));

                conversationHistory.Add(new ChatMessage(ChatRole.Assistant,
                    "Context restored. Ready to continue."));
            });

            compactSw.Stop();
            OtelMetrics.CompactionRuns.Add(1);
            OtelMetrics.CompactionDuration.Record(compactSw.ElapsedMilliseconds);
            if (beforeTokens > 0)
                OtelMetrics.CompactionRatio.Record((double)_sessionTokens / beforeTokens);

            _pendingCompaction = true;
            _sessionTokens = (uint)Common.EstimateTokenCount(conversationHistory);
            MuxConsole.WriteSuccess($"Compacted: {beforeTokens:N0} -> {_sessionTokens:N0} tokens");
            return _pendingCompaction;
        }

        //Offer option to compact
        if (resumedSession.HasValue)
        {
            int estimatedResumeTokens = Common.EstimateTokenCount(resumedSession.Value);
            if (estimatedResumeTokens > autoCompactTokenThreshold)
            {
                MuxConsole.WriteWarning($"Resumed session is large (~{estimatedResumeTokens:N0} tokens, threshold: {autoCompactTokenThreshold:N0}).");
                _sessionTokens = (uint)estimatedResumeTokens;

                if (MuxConsole.Confirm("Compact before continuing?"))
                    await TryCompactAsync();
            }
        }

        var lastPersistTime = DateTime.UtcNow;
        (string userMsg, string partialResponse)? lastInterruptedContext = null;
        do
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (_sessionTokens > autoCompactTokenThreshold)
            {
                MuxConsole.WriteInfo($"Context approaching limit (~{_sessionTokens:N0} tokens). Auto-compacting...");
                await TryCompactAsync();
            }

            List<ChatMessage> messages;
            if (lastInterruptedContext is { } ctx)
            {
                messages = [
                    new(ChatRole.User, ctx.userMsg),
                    new(ChatRole.Assistant, ctx.partialResponse + "\n\n[response interrupted by user]"),
                    new(ChatRole.User, currentGoal)
                ];
                lastInterruptedContext = null;
            }
            else
            {
                messages = [new(ChatRole.User, currentGoal)];
            }

            conversationHistory.Add(new ChatMessage(ChatRole.User, currentGoal));

            int stuckCount = 0;

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var escapeListener = EscapeKeyListener.Start(turnCts, cancellationToken);
            StdinCancelMonitor.Instance?.SetActiveTurnCts(turnCts);

            bool wasInterrupted = false;
            StringBuilder responseText = new();
            try
            {
                for (int i = 0; i < maxIterations; i++)
                {
                    turnCts.Token.ThrowIfCancellationRequested();

                    MuxConsole.WriteAgentTurnHeader(singleAgentDef.Name);

                    using var turnSpan = OtelTracer.GetSource().StartActivity("agent_turn");
                    turnSpan?.SetTag("agent", singleAgentDef.Name);
                    turnSpan?.SetTag("model", resolvedModelId);
                    turnSpan?.SetTag("iteration", i);
                    var turnSw = Stopwatch.StartNew();

                    ThinkingIndicator? thinking = null;
                    bool startedStreaming = false;
                    bool currentlyStreaming = false;

                    try
                    {
                        thinking = MuxConsole.BeginThinking(singleAgentDef.Name);

                        var calledTools = new List<string>();
                        string? lastToolName = null;

                        using var activityTimeout = ActivityTimeout.Start(TimeSpan.FromSeconds(ExecutionLimits.Current.ActivityTimeoutSeconds), turnCts.Token);

                        if (_pendingCompaction)
                        {
                            messages.InsertRange(0, new[]
                            {
                                conversationHistory[0],  // already has the header
                                conversationHistory[1]   // Context restored. Ready to continue.
                            });
                            _pendingCompaction = false;
                        }

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

                                HookWorker.Enqueue(new HookEvent
                                {
                                    Event = "text_chunk",
                                    Agent = singleAgentDef.Name,
                                    Text = update.Text,
                                    Timestamp = DateTimeOffset.UtcNow
                                });
                            }

                            foreach (AIContent content in update.Contents)
                            {
                                if (content is TextReasoningContent reasoningContent)
                                {
                                    if (string.IsNullOrEmpty(reasoningContent.Text))
                                        continue;

                                    if (!currentlyStreaming)
                                    {
                                        thinking?.Dispose();
                                        thinking = null;
                                        MuxConsole.BeginStreaming();
                                        currentlyStreaming = true;
                                        startedStreaming = true;
                                    }

                                    MuxConsole.WriteStream(reasoningContent.Text, muted: true);

                                    HookWorker.Enqueue(new HookEvent
                                    {
                                        Event = "thinking_chunk",
                                        Agent = singleAgentDef.Name,
                                        Text = reasoningContent.Text,
                                        Timestamp = DateTimeOffset.UtcNow
                                    });

                                    continue;
                                }

                                if (content is FunctionCallContent functionCall)
                                {
                                    lastToolName = functionCall.Name;

                                    HookWorker.Enqueue(new HookEvent
                                    {
                                        Event = "tool_call",
                                        Agent = singleAgentDef.Name,
                                        Tool = functionCall.Name,
                                        Timestamp = DateTimeOffset.UtcNow
                                    });

                                    var toolSpan = OtelTracer.GetSource().StartActivity("tool_call");
                                    toolSpan?.SetTag("agent", singleAgentDef.Name);
                                    toolSpan?.SetTag("tool", functionCall.Name);
                                    toolSpan?.SetTag("args", functionCall.Arguments?.ToString()?[..Math.Min(functionCall.Arguments?.ToString()?.Length ?? 0, 4096)]);

                                    if (currentlyStreaming)
                                    {
                                        currentlyStreaming = false;
                                        thinking?.Dispose();
                                        thinking = MuxConsole.ResumeThinking(singleAgentDef.Name);
                                        calledTools.Add(functionCall.Name);
                                        thinking.UpdateStatus(calledTools);
                                    }
                                    else
                                    {
                                        calledTools.Add(functionCall.Name);
                                        thinking?.UpdateStatus(calledTools);
                                    }
                                }
                                else if (content is FunctionResultContent functionResult)
                                {
                                    var resultText = functionResult.Result?.ToString();
                                    Activity.Current?.SetTag("success", true);
                                    if (resultText != null)
                                        Activity.Current?.SetTag("result", resultText.Length > 4096 ? resultText[..4096] : resultText);
                                    Activity.Current?.Stop();

                                    HookWorker.Enqueue(new HookEvent
                                    {
                                        Event = "tool_result",
                                        Agent = singleAgentDef.Name,
                                        Summary = functionResult.Result?.ToString(),
                                        Timestamp = DateTimeOffset.UtcNow
                                    });

                                    if (resultText != null)
                                        MuxConsole.WriteToolResult(singleAgentDef.Name, lastToolName ?? "unknown", resultText);

                                    if (!currentlyStreaming && thinking != null)
                                    {
                                        thinking.Dispose();
                                        thinking = MuxConsole.BeginThinking(singleAgentDef.Name);
                                        if (calledTools.Count > 0)
                                            thinking.UpdateStatus(calledTools);
                                    }
                                }
                                else if (content is UsageContent usageContent)
                                {
                                    var details = usageContent.Details;
                                    _sessionTokens = (uint)(details.TotalTokenCount ?? 0);
                                    _cachedTokens = (uint)(details.CachedInputTokenCount ?? 0);
                                    OtelMetrics.RecordTokens(
                                        singleAgentDef.Name, resolvedModelId, details.InputTokenCount ?? 0, details.OutputTokenCount ?? 0, details.CachedInputTokenCount, details.ReasoningTokenCount, details.TotalTokenCount
                                        );
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

                        StdinCancelMonitor.Instance?.ClearActiveTurnCts();
                        thinking?.Dispose();

                        HookWorker.Enqueue(new HookEvent
                        {
                            Event = "turn_end",
                            Agent = singleAgentDef.Name,
                            Summary = responseText.Length > 500 ? responseText.ToString(0, 500) + "..." : responseText.ToString(),
                            Timestamp = DateTimeOffset.UtcNow
                        });

                        turnSw.Stop();
                        OtelMetrics.AgentTurns.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));
                        OtelMetrics.AgentTurnDuration.Record(turnSw.ElapsedMilliseconds,
                            new KeyValuePair<string, object?>("agent", singleAgentDef.Name));

                        // Only Fires In Verbose Path
                        OtelMetrics.RecordAgentMessage(singleAgentDef.Name, "assistant", responseText.ToString());
                    }

                    MuxConsole.WriteAgentTurnFooter();

                    string response = responseText.ToString();

                    if (!string.IsNullOrWhiteSpace(response))
                        conversationHistory.Add(new ChatMessage(ChatRole.Assistant, response));

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        stuckCount++;
                        MuxConsole.WriteWarning($"Empty response ({stuckCount}/3)");
                        OtelMetrics.AgentStuckCount.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));
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

                string partial = responseText.ToString();
                if (!string.IsNullOrWhiteSpace(partial))
                {
                    conversationHistory.Add(new ChatMessage(ChatRole.Assistant, partial + "\n\n[interrupted by user]"));
                    lastInterruptedContext = (currentGoal, partial);
                }
                else
                {
                    lastInterruptedContext = (currentGoal, "[no response — interrupted before agent replied]");
                }

                MuxConsole.WriteLine();
                MuxConsole.WriteWarning("Turn cancelled by user (Esc key pressed).");
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

            MuxConsole.WriteMuted($"Total Tokens: {_sessionTokens:N0} - Cached Tokens: {_cachedTokens:N0}");

            //Cleanly Exit as goal was passed through cli args and continuous flag was not passed
            if (incomingGoal != null && !continuous)
                break;

            if (continuous && wasInterrupted)
            {
                MuxConsole.WriteSuccess("Continuous execution stopped by user.");
                break;
            }

            if (continuous)
            {
                if (minDelaySeconds > 0)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    using var delayEsc = EscapeKeyListener.Start(delayCts, cancellationToken);

                    try
                    {
                        await MuxConsole.WithSpinnerAsync($"Next iteration in {minDelaySeconds}s, press [ESC] to cancel", async () =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(minDelaySeconds), delayCts.Token);
                        });
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        MuxConsole.WriteSuccess("Continuous execution stopped by user.");
                        break;
                    }
                }

                currentGoal = "Continue working on the task. If complete, summarize your results.";
                continue;
            }

            MuxConsole.WriteRule();
            MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");

            cancellationToken.ThrowIfCancellationRequested();
            string? nextInput = MuxConsole.ReadInput(cancellationToken);
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
                nextInput = MuxConsole.ReadInput(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (IsQuitCommand(nextInput))
                {
                    MuxConsole.WriteSuccess("Exited from Chat interface successfully!");
                    break;
                }
            }

            currentGoal = nextInput!;

            HookWorker.Enqueue(new HookEvent
            {
                Event = "user_input",
                Agent = singleAgentDef.Name,
                Text = currentGoal,
                Timestamp = DateTimeOffset.UtcNow
            });

            OtelMetrics.RecordAgentMessage(singleAgentDef.Name, "user", currentGoal);
            OtelMetrics.GoalsReceived.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));

        } while (!Environment.HasShutdownStarted);

        if (sessionRetention > 0)
            Common.PruneOldSessions(PlatformContext.SessionsDirectory, sessionRetention);

        HookWorker.Enqueue(new HookEvent
        {
            Event = "session_end",
            Agent = singleAgentDef.Name,
            Summary = cancellationToken.IsCancellationRequested ? "interrupted" : "complete",
            Timestamp = DateTimeOffset.UtcNow
        });

        sessionSpan?.SetTag("outcome", cancellationToken.IsCancellationRequested ? "interrupted" : "complete");
        sessionSpan?.SetTag("final_tokens", _sessionTokens);

        if (cancellationToken.IsCancellationRequested)
            OtelMetrics.SessionsFailed.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));
        else
            OtelMetrics.SessionsCompleted.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));
    }
}