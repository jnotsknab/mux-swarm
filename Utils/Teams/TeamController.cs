using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using MuxSwarm.State;

namespace MuxSwarm.Utils.Teams;

/// <summary>
/// The runtime context for one launched team: the resolved roster, the lead's identity, the
/// (optional) shared TaskBoard, and the set of team tools injected into the lead's session.
/// A TeamScope is built by <see cref="TeamController.Build"/> and handed to
/// <c>SingleAgentOrchestrator.ChatAgentAsync(teamScope: ...)</c>, which runs the lead through
/// the same proven interactive loop as /agent. Off-team (teamScope == null) that loop is
/// byte-identical to today.
/// </summary>
public sealed class TeamScope
{
    public required string DisplayName { get; init; }
    public required Common.AgentDefinition LeadDef { get; init; }
    public required List<string> Members { get; init; }
    public required string Coordination { get; init; }   // "fanout" | "taskboard"
    public TaskBoard? Board { get; init; }                // non-null when Coordination == "taskboard"
    public Mailbox? Mailbox { get; init; }                // inter-agent P2P messaging (M4), always-on per team
    public required IList<AITool> Tools { get; init; }    // team tools appended to the lead's tool list

    /// <summary>M4: per-agent extra-tool factory passed to BuildSpecialists so each MEMBER gets its
    /// own identity-bound send_message/read_inbox. Null when the team has no mailbox.</summary>
    public Func<Common.AgentDefinition, IList<AITool>>? MemberToolFactory { get; init; }
    public required TeamState State { get; init; }

    /// <summary>The board auto-runner for a taskboard team (null otherwise). Started at launch
    /// when the team config sets autoRun, or via the task_autorun tool.</summary>
    public AutoRunner? Runner { get; init; }

    /// <summary>The peer self-claim engine for a taskboard team (null otherwise): per-member loops
    /// that poll the board and claim eligible tasks on their own. Started via the team_peerwork
    /// tool (or /kanban). Distinct from <see cref="Runner"/>, which is a single assignee-keyed loop.</summary>
    public MemberRunner? PeerRunner { get; init; }

    public bool UsesTaskBoard => Board is not null;

    /// <summary>
    /// A concise, static guide injected into the LEAD's system prompt while a team is active
    /// (teamScope != null) so the model has a cohesive overview of HOW to coordinate - not just the
    /// individual tool descriptions. Off-team this is never appended (the prompt stays identical).
    /// </summary>
    public string LeadPreamble()
    {
        var roster = Members.Count > 0 ? string.Join(", ", Members) : "(none)";
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## You are leading a team");
        sb.AppendLine($"Team: {DisplayName}. Members you can delegate to: {roster}.");
        sb.AppendLine("Coordinate by choosing the right tool; do the coordination yourself, don't ask the user to.");
        sb.AppendLine();
        sb.AppendLine("- team_dispatch(assignments[]) - fan INDEPENDENT work to several members at once and");
        sb.AppendLine("  collect their results. Use for parallel work with no ordering between items.");
        if (UsesTaskBoard)
        {
            sb.AppendLine("- task_create(subject, description, blockedBy?, assignee?, startInSeconds?) - put a unit of");
            sb.AppendLine("  work on the shared board. Use blockedBy for DEPENDENT work (a task waits until its");
            sb.AppendLine("  blockers are Done); set assignee to the member who should run it. Unknown dep ids are");
            sb.AppendLine("  rejected, so create blockers first.");
            sb.AppendLine("- task_assign(taskId, member) - claim + run ONE board task now (also reassigns).");
            sb.AppendLine("- team_peerwork(enabled, intervalSeconds?) - turn ON to let members self-claim and drain");
            sb.AppendLine("  the board on their own (each pulls eligible tasks per the team's pickup policy). Prefer");
            sb.AppendLine("  this for a backlog of assigned/dependent tasks instead of assigning each by hand.");
            sb.AppendLine("- task_autorun(enabled, intervalSeconds?) - alternative: one background loop that runs");
            sb.AppendLine("  every assigned, unblocked, timer-elapsed task. task_list / task_info inspect the board.");
            sb.AppendLine("- The user can watch and EDIT the board live with /kanban (add/assign/block/ready/move/peer).");
            sb.AppendLine();
            sb.AppendLine("Typical flow: break the goal into task_create calls (wire dependencies via blockedBy +");
            sb.AppendLine("assignee), then team_peerwork(true) and let the team drain it; summarize results at the end.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("This team uses fan-out coordination (no shared board): use team_dispatch for everything,");
            sb.AppendLine("then synthesize the members' results yourself.");
        }
        sb.AppendLine();
        sb.AppendLine("Mailbox (inter-agent messaging):");
        sb.AppendLine("- send_message(to, type, body) - message a member (or \"all\" to broadcast). type is one of");
        sb.AppendLine("  info | question | answer | handoff | shutdown. Use 'shutdown' to GRACEFULLY stop a member");
        sb.AppendLine("  that is no longer needed (it stops between tasks, never mid-call).");
        sb.AppendLine("- read_inbox() - read replies addressed to you. The user can audit any agent's messages in");
        sb.AppendLine("  the Agent View with 'm'.");
        return sb.ToString();
    }
}

/// <summary>
/// Resolves and launches named teams (swarm.json <c>teams[]</c>). A team is a selection over the
/// existing Agents[] plus a coordination policy; members are spawned as isolated sub-agent
/// sessions via the existing parallel worker path, so they surface live in the Agent View for
/// free. The controller owns everything team-specific - config resolution, install-dir
/// persistence, the TaskBoard, and the lead's team tools - and reuses the single-agent loop for
/// the lead rather than duplicating it.
///
/// M2 supports <c>fanout</c> (independent concurrent tasks) and <c>taskboard</c> (a shared,
/// persisted, dependency-gated, file-locked task graph the lead orchestrates). Mailbox, peer
/// self-claiming, workflows, and giga spawning are later milestones.
/// </summary>
public static class TeamController
{
    /// <summary>The board of the currently-running team, exposed for the Ctrl+T TaskBoard strip.
    /// Null when no taskboard team is active.</summary>
    public static TaskBoard? ActiveBoard { get; private set; }

    /// <summary>The mailbox of the currently-running team (M4), exposed for the Agent View m-log.
    /// Null when no team is active.</summary>
    public static Mailbox? ActiveMailbox { get; private set; }

    /// <summary>Look up a configured team by name (case-insensitive).</summary>
    public static TeamConfig? Find(SwarmConfig? config, string name)
        => config?.Teams?.FirstOrDefault(t =>
            t.Name.Equals((name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Build the runtime scope for a configured team: resolve the lead + members against
    /// Agents[], (optionally) open the shared TaskBoard, persist team.json, and assemble the
    /// team tools to inject into the lead's session. Returns null with a written error when the
    /// team or its lead cannot be resolved. <paramref name="ct"/> bounds member dispatch.
    /// </summary>
    public static TeamScope? Build(
        TeamConfig team,
        SwarmConfig config,
        Func<string, IChatClient> chatClientFactory,
        Dictionary<string, string> agentModels,
        CancellationToken ct)
    {
        var allDefs = Common.ParseAgentDefinitions(config);

        // The lead: a named agent if it resolves, else fall back to the configured single agent so
        // the lead always has a valid prompt + tool filter to drive the loop.
        var leadName = string.IsNullOrWhiteSpace(team.Lead) ? "Orchestrator" : team.Lead!.Trim();
        var leadDef = allDefs.FirstOrDefault(d => d.Name.Equals(leadName, StringComparison.OrdinalIgnoreCase))
                      ?? Common.ParseSingleAgentDefinition(config);
        if (leadDef is null)
        {
            MuxConsole.WriteError($"[teams] Cannot resolve a lead for team '{team.Name}' (lead='{leadName}'). " +
                                  "Set a valid 'lead' agent or configure singleAgent in swarm.json.");
            return null;
        }

        // Members: every configured member that resolves to a real agent, excluding the lead.
        var members = new List<string>();
        foreach (var m in team.Members ?? [])
        {
            var name = (m ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (name.Equals(leadDef.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (allDefs.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                if (!members.Contains(name, StringComparer.OrdinalIgnoreCase)) members.Add(name);
            }
            else
            {
                MuxConsole.WriteWarning($"[teams] Member '{name}' is not a defined agent - skipping.");
            }
        }

        if (members.Count == 0)
            MuxConsole.WriteWarning($"[teams] Team '{team.Name}' has no resolvable members; the lead will run solo.");

        var coordination = NormalizeCoordination(team.Coordination);

        var state = new TeamState
        {
            Name = team.Name,
            Description = team.Description,
            Lead = leadDef.Name,
            Members = members,
            Coordination = coordination,
            Status = "active",
            Created = DateTimeOffset.UtcNow,
            LastActive = DateTimeOffset.UtcNow,
        };
        // Preserve original creation time across a resume.
        var prior = TeamState.Load(team.Name);
        if (prior is not null) state.Created = prior.Created;
        state.Save();

        TaskBoard? board = null;
        if (coordination == "taskboard")
        {
            board = TaskBoard.Open(team.Name, TeamState.RootFor(team.Name));
            ActiveBoard = board;
            InstallBoardProvider(board);
        }
        else
        {
            ActiveBoard = null;
        }

        // Mailbox (M4): on by default for a team (works in both fanout and taskboard coordination).
        // The lead AND every member send/read; members drain their inbox into each task brief and
        // wake on actionable messages. team.mailbox=false disables it entirely (no mailbox, no tools).
        Mailbox? mailbox = null;
        if (team.Mailbox)
        {
            mailbox = Mailbox.Open(team.Name, TeamState.RootFor(team.Name));
            ActiveMailbox = mailbox;
            InstallMailboxProvider(mailbox);
        }
        else
        {
            ActiveMailbox = null;
        }

        int maxParallel = team.MaxParallel is { } mp && mp > 0 ? mp : App.MaxDegreeParallelism;
        int maxSubIters = ExecutionLimits.Current.MaxSubAgentIterations;
        if (maxSubIters <= 0) maxSubIters = 8;

        var tools = BuildTeamTools(
            team, leadDef.Name, members, coordination, board, mailbox,
            chatClientFactory, agentModels, maxParallel, maxSubIters, state, ct,
            out var autoRunner, out var peerRunner, out var memberToolFactory);
        ActiveRunner = autoRunner;
        ActivePeerRunner = peerRunner;
        // Capture the roster + context so /taskgraph can lazily spin up a DecomposeDispatcher on
        // demand (it is OFF by default; nothing runs until the user enables it). board may be null
        // for fanout teams - the command checks ActiveBoard before starting.
        ActiveDecomposeDispatcher = null;
        ActiveDecomposeContext = board is not null
            ? (members, chatClientFactory, agentModels, ct)
            : null;

        // Start the auto-runner at launch when the team config opts in (taskboard only).
        if (autoRunner is not null && coordination == "taskboard" && team.AutoRun)
        {
            autoRunner.Start(team.AutoRunIntervalSeconds);
            MuxConsole.WriteInfo($"[teams] Auto-runner started (every {autoRunner.IntervalSeconds}s).");
        }

        return new TeamScope
        {
            DisplayName = team.Name,
            LeadDef = leadDef,
            Members = members,
            Coordination = coordination,
            Board = board,
            Mailbox = mailbox,
            Tools = tools,
            State = state,
            Runner = autoRunner,
            PeerRunner = peerRunner,
            MemberToolFactory = memberToolFactory,
        };
    }

    /// <summary>Clear the active-board reference + UI provider when a team session ends.</summary>
    public static void Clear()
    {
        ActiveBoard = null;
        ActiveMailbox = null;
        MuxConsole.TuiSetTaskBoardProvider(null);
        MuxConsole.TuiSetMessageLogProvider(null);
        try { ActiveRunner?.Stop(); } catch { /* best-effort */ }
        try { ActivePeerRunner?.Stop(); } catch { /* best-effort */ }
        try { ActiveDecomposeDispatcher?.Stop(); } catch { /* best-effort */ }
        ActiveRunner = null;
        ActivePeerRunner = null;
        ActiveDecomposeDispatcher = null;
    }

    /// <summary>The auto-runner of the currently-running team, stopped on Clear().</summary>
    public static AutoRunner? ActiveRunner { get; private set; }

    /// <summary>The peer self-claim engine of the currently-running team, stopped on Clear().</summary>
    public static MemberRunner? ActivePeerRunner { get; private set; }

    /// <summary>The opt-in background task-decompose dispatcher (/taskgraph) of the currently-running
    /// team, stopped + cleared on Clear(). Null when /taskgraph is off (the default).</summary>
    public static DecomposeDispatcher? ActiveDecomposeDispatcher { get; internal set; }

    /// <summary>The roster + cancellation context of the active team, captured so /taskgraph can spin
    /// up its dispatcher on demand without re-plumbing through the command layer.</summary>
    internal static (IReadOnlyList<string> Members, Func<string, IChatClient> Factory,
        Dictionary<string, string> Models, CancellationToken Ct)? ActiveDecomposeContext { get; set; }

    /// <summary>
    /// /taskgraph on|off|status: persist the decompose.enabled flag and, when an active taskboard
    /// team exists, start/stop its background DecomposeDispatcher. Returns a human-readable status.
    /// The dispatcher only acts on goals enqueued via EnqueueDecomposeGoal; one-shot task_decompose
    /// works regardless of this toggle.
    /// </summary>
    public static string ToggleDecompose(string? arg)
    {
        var sub = (arg ?? "status").Trim().ToLowerInvariant();
        var cfg = App.SwarmConfig?.Decompose ?? new DecomposeConfig();

        if (sub == "on" || sub == "off")
        {
            bool on = sub == "on";
            cfg.Enabled = on;
            App.SwarmConfig ??= new SwarmConfig();
            App.SwarmConfig.Decompose = cfg;
            try
            {
                File.WriteAllText(PlatformContext.SwarmPath,
                    System.Text.Json.JsonSerializer.Serialize(App.SwarmConfig,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { return $"[taskgraph] Failed to persist config: {ex.Message}"; }

            if (on)
            {
                if (ActiveBoard is null || ActiveDecomposeContext is null)
                    return "[taskgraph] Enabled (persisted). No active taskboard team yet - the background " +
                           "dispatcher starts when you launch one. One-shot task_decompose is available now.";
                StartDecomposeDispatcher();
                return $"[taskgraph] ON (persisted). Background dispatcher running every " +
                       $"{ActiveDecomposeDispatcher?.IntervalSeconds ?? cfg.PollIntervalSeconds}s; " +
                       "enqueue goals with task_decompose-style intents.";
            }
            else
            {
                try { ActiveDecomposeDispatcher?.Stop(); } catch { /* best-effort */ }
                ActiveDecomposeDispatcher = null;
                return "[taskgraph] OFF (persisted). Dispatcher stopped; one-shot task_decompose still available.";
            }
        }

        // status
        bool enabled = cfg.Enabled;
        bool running = ActiveDecomposeDispatcher?.IsRunning == true;
        return $"[taskgraph] enabled={enabled} running={running} pollInterval={cfg.PollIntervalSeconds}s " +
               $"maxSubtasks={cfg.MaxSubtasks} model={(string.IsNullOrWhiteSpace(cfg.Model) ? "(compaction/orchestrator fallback)" : cfg.Model)}";
    }

    /// <summary>Spin up (or restart) the active team's DecomposeDispatcher using the captured context.</summary>
    private static void StartDecomposeDispatcher()
    {
        if (ActiveBoard is null || ActiveDecomposeContext is null) return;
        var (members, factory, models, ct) = ActiveDecomposeContext.Value;
        var cfg = App.SwarmConfig?.Decompose ?? new DecomposeConfig();
        ActiveDecomposeDispatcher = new DecomposeDispatcher(
            ActiveBoard, members,
            () => ResolveDecomposeClient(factory, models).client,
            () => ResolveDecomposeClient(factory, models).options,
            cfg.MaxSubtasks, ct);
        ActiveDecomposeDispatcher.Start(cfg.PollIntervalSeconds);
    }

    /// <summary>Enqueue a goal for the active background decompose dispatcher (no-op when off).</summary>
    public static void EnqueueDecomposeGoal(string goal)
        => ActiveDecomposeDispatcher?.Enqueue(goal);

    /// <summary>Feed the driver's Ctrl+T TaskBoard strip a point-in-time snapshot of the board
    /// (tally + flattened rows), keeping the TUI layer free of any State/Teams dependency.</summary>
    private static void InstallBoardProvider(TaskBoard board)
    {
        MuxConsole.TuiSetTaskBoardProvider(() =>
        {
            var (total, done, prog, blocked, failed) = board.Tally();
            var rows = board.Snapshot()
                .Select(t => (t.Id, t.Status.ToString(), t.Owner, t.Subject, t.Artifacts.Count))
                .ToList();
            return (total, done, prog, blocked, failed,
                (IReadOnlyList<(string, string, string?, string, int)>)rows);
        });
    }

    /// <summary>Feed the Agent View 'm' message-log: given an agent, format its inbox history into
    /// dim markup rows (M4 Mailbox). Cleared on Clear().</summary>
    private static void InstallMailboxProvider(Mailbox mailbox)
    {
        MuxConsole.TuiSetMessageLogProvider(agent =>
        {
            var history = mailbox.History(agent);
            var rows = new List<string>(history.Count + 1)
            {
                $"[#64B4DC]\u2709 mailbox \u00b7 {Markup(agent)}[/]"
            };
            foreach (var m in history)
            {
                var glyph = m.Type switch
                {
                    MsgType.Shutdown => "\u25a0",
                    MsgType.Question => "?",
                    MsgType.Answer   => "\u2713",
                    MsgType.Handoff  => "\u2192",
                    _                 => "\u00b7",
                };
                var stamp = m.Sent.ToLocalTime().ToString("HH:mm");
                rows.Add($"  [#7A8290]{stamp}[/] {glyph} [#C8CDD4]{Markup(m.From)}[/] [#7A8290]{m.Type}[/]: {Markup(m.Body)}");
            }
            return (IReadOnlyList<string>)rows;
        });
    }

    private static string Markup(string s) => (s ?? string.Empty).Replace("[", "[[").Replace("]", "]]");

    private static string NormalizeCoordination(string? c)
    {
        var v = (c ?? "fanout").Trim().ToLowerInvariant();
        // M2 honors fanout + taskboard; anything else (incl. reserved "pipeline") falls back to fanout.
        return v == "taskboard" ? "taskboard" : "fanout";
    }

    private static IList<AITool> BuildTeamTools(
        TeamConfig team,
        string leadName,
        List<string> members,
        string coordination,
        TaskBoard? board,
        Mailbox? mailbox,
        Func<string, IChatClient> chatClientFactory,
        Dictionary<string, string> agentModels,
        int maxParallel,
        int maxSubIters,
        TeamState state,
        CancellationToken ct,
        out AutoRunner? autoRunner,
        out MemberRunner? peerRunner,
        out Func<Common.AgentDefinition, IList<AITool>>? memberToolFactory)
    {
        autoRunner = null;
        peerRunner = null;
        memberToolFactory = null;
        var delegationResults = new List<ParallelSwarmOrchestrator.DelegationResult>();
        var retryRegistry = new Dictionary<string, ParallelSwarmOrchestrator.RetryState>();
        var semaphore = new SemaphoreSlim(Math.Max(1, maxParallel));
        var memberSet = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);

        // Per-member context: "persistent" (default, warm sessions + auto-compaction) or "fresh"
        // (clean session each task = the pre-g12.16 one-shot behavior). Members get their OWN
        // auto-compact ceiling (compactionAgent.memberAutoCompactTokenThreshold) so a teammate pool
        // stays cheap relative to the lead. The compaction client is resolved the same way the lead
        // resolves its own (compactionAgent model, or the Orchestrator model as a fallback).
        IChatClient? memberCompactionClient = ResolveMemberCompactionClient(chatClientFactory, agentModels);
        ChatOptions? memberCompactionOptions = MultiAgentOrchestrator.SwarmConfig?.CompactionAgent?.ModelOpts?.ToChatOptions();
        int memberThreshold = MultiAgentOrchestrator.SwarmConfig?.CompactionAgent?.MemberAutoCompactTokenThreshold ?? 0;
        var contextManager = new MemberContextManager(
            team.Name, team.MemberContext, memberThreshold, memberCompactionClient, memberCompactionOptions);

        // Run one member on a task through the existing parallel-worker path. This is what makes
        // members appear live in the Agent View (RunSubAgentAsync -> BeginSubAgentCapture). The
        // context manager decides clean-vs-warm session per the team's memberContext policy and
        // auto-compacts a warm session that grows past the member threshold.
        async Task<string> RunMember(string agent, string task)
        {
            await semaphore.WaitAsync(ct);
            try
            {
                state.LastActive = DateTimeOffset.UtcNow;
                // M4: drain this member's inbox INTO the task brief so messages are consumed live
                // (not stranded until the member happens to call read_inbox). Drained messages are
                // marked read; the member can still call read_inbox for anything that arrives mid-task.
                string brief = PrependInbox(mailbox, agent, task);
                return await contextManager.RunAsync(agent, async cleanSession =>
                    await ParallelSwarmOrchestrator.ExecuteParallelWorker(
                        agent, brief, leadName,
                        MultiAgentOrchestrator.Specialists, delegationResults, retryRegistry,
                        chatClientFactory, agentModels, compactionClient: null, compactionChatOptions: null,
                        maxSubAgentIterations: maxSubIters, prodMode: false, ct: ct, cleanSession: cleanSession),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return $"[ERROR {agent}] {ex.Message}"; }
            finally { semaphore.Release(); }
        }

        var tools = new List<AITool>();
        var roster = members.Count > 0 ? string.Join(", ", members) : "(none configured)";

        // team_dispatch: fan a batch of independent member tasks out concurrently (the fanout core).
        tools.Add(AIFunctionFactory.Create(
            method: async (
                [Description("Member assignments to run concurrently. Each has an Agent (a team member name) and a Task.")]
                IEnumerable<TeamAssignment> assignments) =>
            {
                var list = (assignments ?? []).ToList();
                if (list.Count == 0) return "[teams] No assignments provided.";

                var batch = list.Select(async a =>
                {
                    var agent = (a.Agent ?? string.Empty).Trim();
                    if (!memberSet.Contains(agent))
                        return $"[ERROR {agent}] Not a member of this team. Members: {roster}";
                    return await RunMember(agent, a.Task ?? string.Empty);
                });
                var results = await Task.WhenAll(batch);

                var sb = new StringBuilder();
                sb.AppendLine("### TEAM BATCH COMPLETED ###");
                foreach (var r in results) sb.AppendLine(r);
                state.Save();
                return sb.ToString();
            },
            name: "team_dispatch",
            description: "Dispatch one or more tasks to team members concurrently and collect their results. " +
                         $"Members on this team: {roster}. Use for independent work that can run in parallel."));

        // send_message: P2P (or broadcast) message to a teammate's inbox (M4 Mailbox). "shutdown"
        // type gracefully stops a member's self-claim loop between tasks. Lead tools only present
        // when the team has a mailbox (team.mailbox != false).
        if (mailbox is not null)
        {
        tools.Add(AIFunctionFactory.Create(
            method: (
                [Description("Recipient member name, or \"all\" to broadcast to every member.")] string to,
                [Description("Message type: info | question | answer | handoff | shutdown.")] string type,
                [Description("The message body.")] string body) =>
            {
                var dest = (to ?? string.Empty).Trim();
                if (dest.Length == 0) return "[mailbox] No recipient specified.";
                if (!dest.Equals("all", StringComparison.OrdinalIgnoreCase) && dest != "*"
                    && !memberSet.Contains(dest))
                    return $"[mailbox] '{dest}' is not a member of this team. Members: {roster}";
                var mt = (type ?? "info").Trim().ToLowerInvariant() switch
                {
                    "question" => MsgType.Question,
                    "answer"   => MsgType.Answer,
                    "handoff"  => MsgType.Handoff,
                    "shutdown" => MsgType.Shutdown,
                    _          => MsgType.Info,
                };
                int n = mailbox.Send(leadName, dest, mt, body ?? string.Empty, members);
                if (mt == MsgType.Shutdown)
                    return $"[mailbox] Shutdown signalled to {dest} ({n} inbox(es)); it will stop after its current task.";
                return $"[mailbox] Sent {mt} to {dest} ({n} inbox(es)).";
            },
            name: "send_message",
            description: "Send a message to a team member's inbox (or \"all\" to broadcast). Types: info, " +
                         "question, answer, handoff, shutdown. Use 'shutdown' to gracefully stop a member."));

        // read_inbox: drain the lead's own inbox (replies addressed to the lead).
        tools.Add(AIFunctionFactory.Create(
            method: () =>
            {
                var msgs = mailbox.ReadInbox(leadName, drain: true);
                if (msgs.Count == 0) return "[mailbox] No new messages.";
                var sb = new StringBuilder();
                sb.AppendLine($"### INBOX ({msgs.Count} new) ###");
                foreach (var m in msgs)
                    sb.AppendLine($"[{m.Type}] from {m.From}: {m.Body}");
                return sb.ToString();
            },
            name: "read_inbox",
            description: "Read (and clear) new messages addressed to you from teammates."));
        }

        if (coordination == "taskboard" && board is not null)
        {
            // task_create: add a task to the shared board, optionally blocked, optionally pre-assigned
            // to a member and/or scheduled to start after a delay (for the auto-runner).
            tools.Add(AIFunctionFactory.Create(
                method: (
                    [Description("Short task subject/title.")] string subject,
                    [Description("Fuller task description / instructions for the assignee.")] string description,
                    [Description("Optional comma-separated task ids that must finish before this one (e.g. 't1,t2').")] string? blockedBy,
                    [Description("Optional team member to designate as the assignee (required for auto-run to pick it up).")] string? assignee,
                    [Description("Optional delay in seconds before the auto-runner may start this task (a timer/trigger). 0 or omitted = eligible immediately.")] int? startInSeconds,
                    [Description("Optional comma-separated filepaths/refs relevant to this task; handed to whoever claims it.")] string? artifacts) =>
                {
                    var deps = (blockedBy ?? string.Empty)
                        .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var who = string.IsNullOrWhiteSpace(assignee) ? null : assignee!.Trim();
                    if (who is not null && !memberSet.Contains(who))
                        return $"[teams] Assignee '{who}' is not a member. Members: {roster}";
                    // Reject stale/typo'd dependency ids LOUDLY - an unknown dep must never be
                    // silently treated as satisfied (that would bypass dependency gating).
                    var unknownDeps = board.UnknownDeps(deps);
                    if (unknownDeps.Count > 0)
                        return $"[teams] Unknown dependency id(s): {string.Join(", ", unknownDeps)}. " +
                               "Create those tasks first, or omit them. (No task was created.)";
                    DateTimeOffset? startAt = startInSeconds is { } s && s > 0
                        ? DateTimeOffset.UtcNow.AddSeconds(s) : null;
                    var arts = (artifacts ?? string.Empty)
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var t = board.Create(subject, description, deps, who, startAt, arts);
                    return $"Created {t.Id} \"{t.Subject}\" status={t.Status}" +
                           (t.BlockedBy.Count > 0 ? $" blockedBy=[{string.Join(",", t.BlockedBy)}]" : "") +
                           (t.Assignee is not null ? $" assignee={t.Assignee}" : "") +
                           (t.StartAt is not null ? $" startAt={t.StartAt:HH:mm:ss}" : "") +
                           (t.Artifacts.Count > 0 ? $" artifacts={t.Artifacts.Count}" : "");
                },
                name: "task_create",
                description: "Create a task on the shared team board. Tasks can declare dependencies via blockedBy " +
                             "(a blocked task cannot run until its blockers are Done), an assignee (the member who runs " +
                             "it), startInSeconds (a timer before the auto-runner may start it), and artifacts " +
                             "(comma-separated filepaths handed to whoever claims it)."));

            // task_decompose: one light-model call that expands a high-level goal into a blockedBy
            // task graph on this board (subtasks + deps + suggested assignees), instead of hand-
            // authoring many task_create calls. Always available to the lead; the background
            // dispatcher (/taskgraph) is the opt-in periodic form.
            tools.Add(AIFunctionFactory.Create(
                method: async (
                    [Description("High-level goal to break down into a blockedBy task graph on this board.")] string goal) =>
                {
                    var (dclient, dopts) = ResolveDecomposeClient(chatClientFactory, agentModels);
                    int cap = MultiAgentOrchestrator.SwarmConfig?.Decompose?.MaxSubtasks ?? 12;
                    return await TaskDecomposer.DecomposeAsync(board, goal, members, dclient, dopts, cap, ct);
                },
                name: "task_decompose",
                description: "Decompose a high-level goal into a ready blockedBy task graph (subtasks + dependency " +
                             "edges + suggested assignees) on the shared board in one call, instead of hand-authoring " +
                             "many task_create calls."));

            // task_assign: assign/REASSIGN a task to a member, claim it (file-locked), run it, and
            // mark it Done/Failed so dependents auto-unblock. If the task is already owned it is moved
            // to the new member (reassign).
            tools.Add(AIFunctionFactory.Create(
                method: async (
                    [Description("The task id to assign (e.g. 't1').")] string taskId,
                    [Description("The team member to assign/reassign it to.")] string agent) =>
                {
                    var id = (taskId ?? string.Empty).Trim();
                    var who = (agent ?? string.Empty).Trim();
                    if (!memberSet.Contains(who))
                        return $"[teams] '{who}' is not a member. Members: {roster}";

                    // Reassign in place if it is already owned / in-flight by someone else.
                    var existing = board.Get(id);
                    if (existing is null) return $"[teams] no such task '{id}'.";
                    if (existing.Owner is not null && !string.Equals(existing.Owner, who, StringComparison.OrdinalIgnoreCase))
                        board.Reassign(id, who, out _);

                    if (!board.TryClaim(id, who, out var reason))
                        return $"[teams] Cannot assign {id} to {who}: {reason}";

                    return await RunBoardTask(board, id, who, RunMember, state, ct);
                },
                name: "task_assign",
                description: "Assign (or reassign) a board task to a member: atomically claims it, runs the member on " +
                             "it, and marks it Done/Failed - completing a task auto-unblocks its dependents. Reassigning " +
                             "an already-owned task moves it to the new member."));

            // task_unassign: drop a task's owner/claim and return it to Pending/Blocked.
            tools.Add(AIFunctionFactory.Create(
                method: (
                    [Description("The task id to unassign (e.g. 't1').")] string taskId,
                    [Description("Optional: also set/clear the designated assignee. Pass a member name to redirect future auto-run, or 'none' to clear it.")] string? assignee) =>
                {
                    var id = (taskId ?? string.Empty).Trim();
                    bool ok;
                    string reason;
                    if (!string.IsNullOrWhiteSpace(assignee))
                    {
                        var who = assignee!.Trim();
                        var target = who.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : who;
                        if (target is not null && !memberSet.Contains(target))
                            return $"[teams] '{target}' is not a member. Members: {roster}";
                        ok = board.Reassign(id, target, out reason);
                    }
                    else ok = board.Unassign(id, out reason);
                    return ok ? $"[teams] {id} {reason}." : $"[teams] {reason}.";
                },
                name: "task_unassign",
                description: "Clear a task's owner and return it to Pending/Blocked. Optionally pass an assignee to " +
                             "redirect it to a different member, or 'none' to drop the assignee designation."));

            // task_reopen: revert a Done/Failed task to Pending and re-block its dependents.
            tools.Add(AIFunctionFactory.Create(
                method: ([Description("The task id to reopen (e.g. 't1').")] string taskId) =>
                {
                    var id = (taskId ?? string.Empty).Trim();
                    bool ok = board.Reopen(id, out var reason);
                    return ok ? $"[teams] {id} {reason} (dependents re-blocked as needed)." : $"[teams] {reason}.";
                },
                name: "task_reopen",
                description: "Reopen a Done or Failed task: marks it Pending/Blocked again, clears completion, and " +
                             "re-blocks any dependents that had unblocked off it - to redo or correct finished work."));

            // task_clear: remove one task, or the whole board when no id is given.
            tools.Add(AIFunctionFactory.Create(
                method: ([Description("Optional task id to remove (e.g. 't1'). Omit (or pass 'all') to clear the ENTIRE board.")] string? taskId) =>
                {
                    var id = (taskId ?? string.Empty).Trim();
                    if (id.Length == 0 || id.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        int n = board.RemoveAll();
                        return $"[teams] Cleared the board ({n} task(s) removed).";
                    }
                    bool ok = board.Remove(id, out var reason);
                    return ok ? $"[teams] {id} {reason}." : $"[teams] {reason}.";
                },
                name: "task_clear",
                description: "Remove a task from the board by id, or clear the ENTIRE board when no id (or 'all') is " +
                             "given. Removing a task unwires its dependency links and can unblock dependents."));

            // task_info: full detail for one task.
            tools.Add(AIFunctionFactory.Create(
                method: ([Description("The task id to inspect (e.g. 't1').")] string taskId) =>
                {
                    var id = (taskId ?? string.Empty).Trim();
                    var t = board.Get(id);
                    if (t is null) return $"[teams] no such task '{id}'.";
                    var sb = new StringBuilder();
                    sb.AppendLine($"{t.Id}  [{t.Status}]  {t.Subject}");
                    if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine($"  desc: {t.Description}");
                    sb.AppendLine($"  owner: {t.Owner ?? "(unclaimed)"}   assignee: {t.Assignee ?? "(none)"}");
                    if (t.BlockedBy.Count > 0)
                    {
                        // An unknown/stale dep id is NOT "done" - surface it distinctly so a typo'd
                        // dependency is visible rather than silently counted as satisfied.
                        var unknown = board.UnknownDeps(t.BlockedBy);
                        var pending = t.BlockedBy
                            .Where(d => !unknown.Contains(d) && board.Get(d) is { Status: not TeamTaskStatus.Done })
                            .ToList();
                        var notes = new List<string>();
                        if (pending.Count > 0) notes.Add($"waiting on: {string.Join(",", pending)}");
                        if (unknown.Count > 0) notes.Add($"UNKNOWN dep id(s): {string.Join(",", unknown)}");
                        if (notes.Count == 0) notes.Add("all deps done");
                        sb.AppendLine($"  blockedBy: [{string.Join(",", t.BlockedBy)}]  ({string.Join("; ", notes)})");
                    }
                    if (t.Blocks.Count > 0) sb.AppendLine($"  blocks: [{string.Join(",", t.Blocks)}]");
                    if (t.Artifacts.Count > 0) sb.AppendLine($"  artifacts: {string.Join(", ", t.Artifacts)}");
                    sb.AppendLine($"  created: {t.Created:u}" +
                                  (t.StartAt is not null ? $"   startAt: {t.StartAt:u}" : "") +
                                  (t.ClaimedAt is not null ? $"   claimed: {t.ClaimedAt:u}" : "") +
                                  (t.CompletedAt is not null ? $"   completed: {t.CompletedAt:u}" : ""));
                    if (t.Attempts > 0)
                        sb.AppendLine($"  attempts: {t.Attempts}" +
                                      (t.LastHeartbeat is not null ? $"   lastHeartbeat: {t.LastHeartbeat:u}" : ""));
                    return sb.ToString();
                },
                name: "task_info",
                description: "Show full detail for one task: status, owner, assignee, dependencies (and which are still " +
                             "pending), what it blocks, and its timestamps."));

            // task_artifacts: attach/detach/replace the filepaths relevant to a task.
            tools.Add(AIFunctionFactory.Create(
                method: (
                    [Description("The task id to edit (e.g. 't1').")] string taskId,
                    [Description("Optional comma-separated paths to ADD.")] string? add,
                    [Description("Optional comma-separated paths to REMOVE.")] string? remove,
                    [Description("Optional comma-separated paths to REPLACE the whole list with.")] string? set) =>
                {
                    var id = (taskId ?? string.Empty).Trim();
                    string[]? Split(string? s) => string.IsNullOrWhiteSpace(s) ? null
                        : s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (Split(add) is null && Split(remove) is null && Split(set) is null)
                    {
                        var cur = board.Get(id);
                        if (cur is null) return $"[teams] no such task '{id}'.";
                        return cur.Artifacts.Count == 0
                            ? $"{id} has no artifacts."
                            : $"{id} artifacts:\n" + string.Join("\n", cur.Artifacts.Select(a => $"  - {a}"));
                    }
                    if (!board.SetArtifacts(id, Split(add), Split(remove), Split(set), out var reason))
                        return $"[teams] {reason}";
                    var t = board.Get(id);
                    return $"{id} {reason}" + (t is { Artifacts.Count: > 0 }
                        ? "\n" + string.Join("\n", t.Artifacts.Select(a => $"  - {a}")) : "");
                },
                name: "task_artifacts",
                description: "View or edit the filepaths/refs attached to a task. With no add/remove/set, lists current " +
                             "artifacts. Artifacts are handed to whoever claims the task (shown in its brief) and surfaced " +
                             "in task_info and the board strip."));

            // task_list: render the current board for the lead.
            tools.Add(AIFunctionFactory.Create(
                method: () =>
                {
                    var snap = board.Snapshot();
                    if (snap.Count == 0) return "Board is empty.";
                    var sb = new StringBuilder();
                    foreach (var t in snap)
                    {
                        sb.Append(t.Id).Append(' ').Append('[').Append(t.Status).Append(']');
                        if (t.Owner is not null) sb.Append(" @").Append(t.Owner);
                        else if (t.Assignee is not null) sb.Append(" ->").Append(t.Assignee);
                        sb.Append(' ').Append(t.Subject);
                        if (t.BlockedBy.Count > 0) sb.Append("  blockedBy=[").Append(string.Join(",", t.BlockedBy)).Append(']');
                        sb.AppendLine();
                    }
                    return sb.ToString();
                },
                name: "task_list",
                description: "List every task on the shared team board with its status, owner/assignee, and dependencies."));

            // The background auto-runner: drains the board on a timer, claiming + running every task
            // that is unblocked, unowned, assigned, and past its start time (one in-flight per member).
            var runner = new AutoRunner(board, RunMember, state, ct);
            autoRunner = runner;

            tools.Add(AIFunctionFactory.Create(
                method: (
                    [Description("True to start the auto-runner, false to stop it.")] bool enabled,
                    [Description("Optional poll interval in seconds (how often to scan for runnable tasks). Default 15, floor 3.")] int? intervalSeconds) =>
                {
                    if (enabled)
                    {
                        runner.Start(intervalSeconds ?? 15);
                        return $"[teams] Auto-runner ON (every {runner.IntervalSeconds}s). It will claim + run any task " +
                               "that is unblocked, unowned, has an assignee, and is past its start time - one per member.";
                    }
                    runner.Stop();
                    return "[teams] Auto-runner OFF. Tasks now run only when you assign them.";
                },
                name: "task_autorun",
                description: "Toggle the background auto-runner for this team's board. When ON it periodically scans the " +
                             "board and automatically claims + runs eligible tasks (assigned, unblocked, past their start " +
                             "timer) without you assigning each one - draining a whole dependency graph on its own."));

            // The peer self-claim engine: one poll->claim->run->complete loop PER member identity.
            // Each member pulls work it is eligible for under the team's pickupPolicy ("assigned" =
            // only its own assigned tasks; "open" = also any unassigned ready task), claims it
            // file-locked, runs it in its (persistent or fresh) session, completes it, and loops -
            // a self-organizing pool draining the dependency graph on its own.
            bool openPool = NormalizePickupPolicy(team.PickupPolicy) == "open";
            var peer = new MemberRunner(board, members, RunMember, state, openPool, ct, mailbox);
            peerRunner = peer;

            tools.Add(AIFunctionFactory.Create(
                method: (
                    [Description("True to start the peer self-claim engine, false to stop it.")] bool enabled,
                    [Description("Optional poll interval in seconds (how often each idle member scans for claimable work). Default 15, floor 3.")] int? intervalSeconds) =>
                {
                    if (enabled)
                    {
                        peer.Start(intervalSeconds ?? 15);
                        return $"[teams] Peer self-claim ON (every {peer.IntervalSeconds}s, pickup={(openPool ? "open" : "assigned")}). " +
                               "Each member claims + runs eligible board tasks on its own - one in-flight per member.";
                    }
                    peer.Stop();
                    return "[teams] Peer self-claim OFF. Tasks now run only when you assign/auto-run them.";
                },
                name: "team_peerwork",
                description: "Toggle peer self-claiming for this team's board. When ON, every member runs its own loop that " +
                             "claims + runs board tasks it is eligible for (per the team's pickupPolicy) without the lead " +
                             "assigning each one - a self-organizing pool, one task in-flight per member."));
        }

        // M4: identity-bound member mailbox tools. Each member specialist gets a send_message +
        // read_inbox whose "from"/inbox identity is the member itself (vs the lead's, above), built
        // via BuildSpecialists' per-agent extra-tool factory so a member can answer the lead /
        // message a peer. Members only; the lead/Orchestrator already has its own pair.
        // The lead is addressable by BOTH the team-lead alias (team.Lead, e.g. the giga constant
        // "Giga") AND the resolved lead agent's real name (leadDef.Name, e.g. "MuxAgent"). A member
        // runs as its own persona and may address the lead by either, so accept both and NORMALIZE
        // to the single canonical leadName the lead actually drains - otherwise a member->lead reply
        // routed to the unresolved alias lands in an inbox nobody reads (the giga lead-identity gap).
        var leadAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { leadName };
        if (!string.IsNullOrWhiteSpace(team.Lead)) leadAliases.Add(team.Lead!.Trim());
        memberToolFactory = mailbox is null ? null : def =>
        {
            var extra = new List<AITool>();
            if (!memberSet.Contains(def.Name)) return extra;
            var selfName = def.Name;

            extra.Add(AIFunctionFactory.Create(
                method: (
                    [Description("Recipient: a teammate name, the team lead, or \"all\" to broadcast.")] string to,
                    [Description("Message type: info | question | answer | handoff | shutdown.")] string type,
                    [Description("The message body.")] string body) =>
                {
                    var dest = (to ?? string.Empty).Trim();
                    if (dest.Length == 0) return "[mailbox] No recipient specified.";
                    bool bcast = dest.Equals("all", StringComparison.OrdinalIgnoreCase) || dest == "*";
                    // Any lead alias -> the canonical leadName the lead's read_inbox drains.
                    if (leadAliases.Contains(dest)) dest = leadName;
                    if (!bcast && !memberSet.Contains(dest)
                        && !dest.Equals(leadName, StringComparison.OrdinalIgnoreCase))
                        return $"[mailbox] '{dest}' is not the lead or a member. Lead: {leadName}. Members: {roster}";
                    var mt = (type ?? "info").Trim().ToLowerInvariant() switch
                    {
                        "question" => MsgType.Question,
                        "answer"   => MsgType.Answer,
                        "handoff"  => MsgType.Handoff,
                        "shutdown" => MsgType.Shutdown,
                        _          => MsgType.Info,
                    };
                    // Broadcast targets every OTHER member + the lead; a member may not shut peers down.
                    if (mt == MsgType.Shutdown)
                        return "[mailbox] Members cannot signal shutdown; only the lead may stop a member.";
                    var audience = new List<string>(members) { leadName };
                    int n = mailbox.Send(selfName, dest, mt, body ?? string.Empty, audience);
                    return $"[mailbox] Sent {mt} to {dest} ({n} inbox(es)).";
                },
                name: "send_message",
                description: "Send a message to a teammate, the team lead, or \"all\". Use 'question' to ask and " +
                             "'answer'/'handoff' to respond or pass work. Replies reach the recipient's inbox."));

            extra.Add(AIFunctionFactory.Create(
                method: () =>
                {
                    var msgs = mailbox.ReadInbox(selfName, drain: true);
                    if (msgs.Count == 0) return "[mailbox] No new messages.";
                    var sb = new StringBuilder();
                    sb.AppendLine($"### INBOX ({msgs.Count} new) ###");
                    foreach (var m in msgs)
                        sb.AppendLine($"[{m.Type}] from {m.From}: {m.Body}");
                    return sb.ToString();
                },
                name: "read_inbox",
                description: "Read (and clear) new messages addressed to you from the lead or teammates."));

            return extra;
        };

        return tools;
    }

    /// <summary>M4: drain <paramref name="agent"/>'s unread inbox and prepend it to <paramref name="task"/>
    /// so delivered messages are surfaced to the member at the very start of the task turn (rather than
    /// sitting unread until it independently calls read_inbox). Drained messages are marked read. When the
    /// inbox is empty the task is returned unchanged (byte-identical to the no-mailbox path).</summary>
    private static string PrependInbox(Mailbox? mailbox, string agent, string task)
    {
        if (mailbox is null) return task;
        var msgs = mailbox.ReadInbox(agent, drain: true);
        if (msgs.Count == 0) return task;
        var sb = new StringBuilder();
        sb.AppendLine($"### TEAM INBOX ({msgs.Count} new message(s) for you) ###");
        foreach (var m in msgs)
            sb.AppendLine($"- [{m.Type}] from {m.From}: {m.Body}");
        sb.AppendLine("(Address anything that needs a reply with send_message, then proceed.)");
        sb.AppendLine();
        sb.Append(task);
        return sb.ToString();
    }

    /// <summary>Resolve the chat client used to compact MEMBER sessions: the configured compaction
    /// model, or the Orchestrator model as a fallback. Null when neither resolves (callers then do a
    /// bounded hard reset instead of leaking).</summary>
    private static IChatClient? ResolveMemberCompactionClient(
        Func<string, IChatClient> chatClientFactory, Dictionary<string, string> agentModels)
    {
        try
        {
            var model = MultiAgentOrchestrator.SwarmConfig?.CompactionAgent?.Model;
            if (string.IsNullOrWhiteSpace(model))
                model = agentModels.TryGetValue("Compaction", out var cm) ? cm
                      : agentModels.TryGetValue("Orchestrator", out var om) ? om : null;
            return string.IsNullOrWhiteSpace(model) ? null : chatClientFactory(model!);
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolve a LIGHT (client, options) for task_decompose: decompose.model -> compaction model ->
    /// Orchestrator model. Returns (null, null) when none resolve.
    /// </summary>
    private static (IChatClient? client, ChatOptions? options) ResolveDecomposeClient(
        Func<string, IChatClient> chatClientFactory, Dictionary<string, string> agentModels)
    {
        try
        {
            var model = MultiAgentOrchestrator.SwarmConfig?.Decompose?.Model;
            if (string.IsNullOrWhiteSpace(model))
                model = MultiAgentOrchestrator.SwarmConfig?.CompactionAgent?.Model;
            if (string.IsNullOrWhiteSpace(model))
                model = agentModels.TryGetValue("Compaction", out var cm) ? cm
                      : agentModels.TryGetValue("Orchestrator", out var om) ? om : null;
            if (string.IsNullOrWhiteSpace(model)) return (null, null);
            var opts = MultiAgentOrchestrator.SwarmConfig?.CompactionAgent?.ModelOpts?.ToChatOptions();
            return (chatClientFactory(model!), opts);
        }
        catch { return (null, null); }
    }

    /// <summary>Normalize the pickup policy; unknown values fall back to "assigned".</summary>
    private static string NormalizePickupPolicy(string? p)
        => (p ?? "assigned").Trim().ToLowerInvariant() == "open" ? "open" : "assigned";

    /// <summary>Claim-already-done: run a board task to completion and flip its status. Shared by
    /// task_assign and the auto-runner. The caller must have already claimed <paramref name="id"/>
    /// for <paramref name="who"/>.</summary>
    private static async Task<string> RunBoardTask(
        TaskBoard board, string id, string who,
        Func<string, string, Task<string>> runMember, TeamState state, CancellationToken ct)
    {
        string result;
        try
        {
            result = await runMember(who, BuildTaskBrief(board, id));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            board.Unassign(id, out _);   // return it to the pool so it can be picked up again
            throw;
        }

        bool ok = !result.StartsWith("[ERROR", StringComparison.Ordinal);
        board.SetStatus(id, ok ? TeamTaskStatus.Done : TeamTaskStatus.Failed, out _);
        state.Save();
        return $"[{id} -> {who}] {(ok ? "DONE" : "FAILED")}\n{result}";
    }

    private static string BuildTaskBrief(TaskBoard board, string id)
    {
        var t = board.Snapshot().FirstOrDefault(x => x.Id == id);
        if (t is null) return $"Work task {id}.";
        var sb = new StringBuilder();
        sb.AppendLine($"You are assigned team task {t.Id}: {t.Subject}");
        if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine().AppendLine(t.Description);
        if (t.Artifacts.Count > 0)
            sb.AppendLine().AppendLine("Relevant artifacts/paths:")
              .AppendLine(string.Join("\n", t.Artifacts.Select(a => $"  - {a}")));
        return sb.ToString();
    }
}

/// <summary>
/// A background, daemon-like auto-runner for a taskboard team. While running it scans the board
/// on a timer and automatically claims + runs every task that is eligible -- unblocked, unowned,
/// has a designated assignee, and is past its start timer -- bounded to one in-flight task per
/// member so a single member's session is never clobbered. A whole dependency DAG drains on its
/// own: as a blocker completes, its dependents auto-unblock and get picked up on the next scan.
/// Honors the team session's CancellationToken (Esc / quit), and stops cleanly on Stop().
/// Single in-process loop; the board's own lock makes concurrent claims race-safe.
/// </summary>
public sealed class AutoRunner
{
    private readonly TaskBoard _board;
    private readonly Func<string, string, Task<string>> _runMember;
    private readonly TeamState _state;
    private readonly CancellationToken _sessionCt;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly object _gate = new();
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase); // assignees with a task in flight

    /// <summary>Poll interval in seconds (floored at 3). Reflects the last Start() call.</summary>
    public int IntervalSeconds { get; private set; } = 15;

    /// <summary>True while the background loop is active.</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    internal AutoRunner(TaskBoard board, Func<string, string, Task<string>> runMember,
        TeamState state, CancellationToken sessionCt)
    {
        _board = board;
        _runMember = runMember;
        _state = state;
        _sessionCt = sessionCt;
    }

    /// <summary>Start (or restart with a new interval) the background drain loop.</summary>
    public void Start(int intervalSeconds)
    {
        IntervalSeconds = Math.Max(3, intervalSeconds);
        lock (_gate)
        {
            if (IsRunning) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCt);
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    /// <summary>Stop the background loop. Tasks already in flight finish naturally.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { /* already disposed */ }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Reap dead-owner tasks before scanning: a worker that died mid-task (crash, kill,
                // hung stream past the TTL) has its claim reclaimed so the task can run again, or is
                // tripped to Failed once its retry budget is spent (circuit breaker).
                SweepStale();

                // Drain everything currently runnable, then wait a poll interval. Each NextRunnable
                // call excludes assignees that already have an in-flight task (one per member).
                while (!ct.IsCancellationRequested)
                {
                    TeamTask? next;
                    lock (_gate) next = _board.NextRunnable(DateTimeOffset.UtcNow, _busy);
                    if (next is null) break;

                    var who = next.Assignee!;
                    if (!_board.TryClaim(next.Id, who, out _)) continue; // raced; try the next one

                    lock (_gate) _busy.Add(who);
                    // Fire-and-forget the task; the busy-set serializes per member and the board's
                    // lock serializes status writes. We do NOT await here so independent members run
                    // concurrently up to the member set size.
                    _ = RunOneAsync(next.Id, who, ct);
                }

                try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* clean stop */ }
        catch (Exception ex) { MuxConsole.WriteWarning($"[teams] Auto-runner stopped on error: {ex.Message}"); }
    }

    private async Task RunOneAsync(string id, string who, CancellationToken ct)
    {
        try
        {
            string result;
            using var hb = StartHeartbeat(id, who, ct);
            try { result = await _runMember(who, BriefFor(id)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _board.Unassign(id, out _); // return to the pool
                return;
            }
            bool ok = !result.StartsWith("[ERROR", StringComparison.Ordinal);
            _board.SetStatus(id, ok ? TeamTaskStatus.Done : TeamTaskStatus.Failed, out _);
            _state.Save();
        }
        finally
        {
            lock (_gate) _busy.Remove(who);
        }
    }

    /// <summary>Reap stale (dead-owner) tasks using the configured TTL + attempt cap. Best-effort.</summary>
    private void SweepStale()
    {
        try
        {
            var lim = MuxSwarm.Utils.ExecutionLimits.Current;
            var ttl = TimeSpan.FromSeconds(Math.Max(1, lim.TaskClaimTtlSeconds));
            var reaped = _board.ReapStale(ttl, lim.MaxTaskAttempts, DateTimeOffset.UtcNow);
            if (reaped.Count > 0)
                MuxConsole.WriteMuted($"[teams] reaped {reaped.Count} stale task(s): {string.Join(", ", reaped)}");
        }
        catch { /* never let the sweep break the loop */ }
    }

    /// <summary>
    /// Start a background heartbeat that pings the board for an in-flight task every ~TTL/3 seconds
    /// so the reaper sees a live worker. Dispose (end of the run) stops it. Best-effort.
    /// </summary>
    private IDisposable StartHeartbeat(string id, string who, CancellationToken ct)
        => new Heartbeater(_board, id, who, ct);

    private string BriefFor(string id)
    {
        var t = _board.Get(id);
        if (t is null) return $"Work task {id}.";
        var sb = new StringBuilder();
        sb.AppendLine($"You are assigned team task {t.Id}: {t.Subject}");
        if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine().AppendLine(t.Description);
        if (t.Artifacts.Count > 0)
            sb.AppendLine().AppendLine("Relevant artifacts/paths:")
              .AppendLine(string.Join("\n", t.Artifacts.Select(a => $"  - {a}")));
        return sb.ToString();
    }
}

/// <summary>
/// Background heartbeater: while alive, periodically pings TaskBoard.Heartbeat(id, who) so the
/// stale-task reaper can distinguish a live long-running worker from a dead one. Shared by both
/// runner engines. Stops on Dispose or session cancellation. Interval is TTL/3 (floor 5s).
/// </summary>
internal sealed class Heartbeater : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    public Heartbeater(TaskBoard board, string id, string who, CancellationToken sessionCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
        var ct = _cts.Token;
        int ttl = Math.Max(1, MuxSwarm.Utils.ExecutionLimits.Current.TaskClaimTtlSeconds);
        int every = Math.Max(5, ttl / 3);
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(every), ct); }
                    catch (OperationCanceledException) { break; }
                    try { board.Heartbeat(id, who); } catch { /* best-effort */ }
                }
            }
            catch { /* never surface */ }
        }, ct);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}

/// <summary>
/// The peer self-claim engine for a taskboard team: one independent poll-claim-run-complete loop
/// PER member identity (the Claude-Code "teammates pull their own work" model). Each member's loop
/// scans the board for the next task it may claim under the team's pickup policy
/// (assigned-only: its own assigned tasks; open-pool: also any unassigned, ready task), claims it
/// atomically (file-locked, so two members racing an open task can never both win), runs it in that
/// member's session via the shared RunMember path, marks it Done/Failed (auto-unblocking dependents),
/// and loops - one task in-flight per member at a time. The whole dependency DAG drains on its own:
/// as blockers complete, dependents become claimable on the next scan. Honors the team session's
/// CancellationToken; stops cleanly on Stop().
/// </summary>
public sealed class MemberRunner
{
    private readonly TaskBoard _board;
    private readonly List<string> _members;
    private readonly Func<string, string, Task<string>> _runMember;
    private readonly TeamState _state;
    private readonly bool _openPool;
    private readonly CancellationToken _sessionCt;
    private readonly Mailbox? _mailbox;   // M4: cooperative shutdown signalled via the team mailbox

    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _loops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-member poll interval in seconds (floored at 3). Reflects the last Start() call.</summary>
    public int IntervalSeconds { get; private set; } = 15;

    /// <summary>True while at least one member loop is active.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _loops.Values.Any(t => !t.IsCompleted); }
    }

    /// <summary>True when members may also claim unassigned (open-pool) tasks.</summary>
    public bool OpenPool => _openPool;

    internal MemberRunner(TaskBoard board, List<string> members,
        Func<string, string, Task<string>> runMember, TeamState state, bool openPool,
        CancellationToken sessionCt, Mailbox? mailbox = null)
    {
        _board = board;
        _members = members;
        _runMember = runMember;
        _state = state;
        _openPool = openPool;
        _sessionCt = sessionCt;
        _mailbox = mailbox;
    }

    /// <summary>Start (or restart with a new interval) one self-claim loop per member.</summary>
    public void Start(int intervalSeconds)
    {
        IntervalSeconds = Math.Max(3, intervalSeconds);
        lock (_gate)
        {
            if (IsRunning) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCt);
            _loops.Clear();
            foreach (var m in _members)
            {
                var who = m;
                _loops[who] = Task.Run(() => RunMemberLoopAsync(who, _cts.Token));
            }
        }
    }

    /// <summary>Stop every member loop. A task already in flight finishes naturally.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { /* already disposed */ }
        }
    }

    private async Task RunMemberLoopAsync(string who, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // M4 graceful shutdown: the lead sent this member a Shutdown message. Stop the
                // self-claim loop BETWEEN tasks (never mid-call) so in-flight work finishes cleanly.
                if (_mailbox is not null && _mailbox.IsShutdownRequested(who))
                {
                    MuxConsole.WriteInfo($"[teams] {who} received shutdown - stopping its peer loop.");
                    return;
                }

                bool didWork = false;

                // Reap dead-owner tasks (TTL + retry breaker) before this member scans for work.
                SweepStale();

                // Drain everything this member can claim right now, one task at a time. Each task's
                // brief is prepended with any unread inbox messages (handled in RunMember), so a
                // working member naturally consumes its mailbox.
                while (!ct.IsCancellationRequested)
                {
                    var next = _board.NextClaimableFor(who, _openPool, DateTimeOffset.UtcNow);
                    if (next is null) break;

                    if (!_board.TryClaim(next.Id, who, out _)) continue; // raced another member; try the next
                    didWork = true;

                    string result;
                    using var hb = new Heartbeater(_board, next.Id, who, ct);
                    try { result = await _runMember(who, BriefFor(next.Id)); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        _board.Unassign(next.Id, out _); // return it to the pool
                        return;
                    }
                    bool ok = !result.StartsWith("[ERROR", StringComparison.Ordinal);
                    _board.SetStatus(next.Id, ok ? TeamTaskStatus.Done : TeamTaskStatus.Failed, out _);
                    _state.Save();
                }

                // M4 wake: an idle member with NO claimable task but an actionable unread message
                // (a question or handoff addressed to it) runs a short turn to read + respond, so a
                // peer's message is acted on promptly instead of sitting until the next board task.
                if (!didWork && _mailbox is not null && _mailbox.HasActionableUnread(who))
                {
                    try
                    {
                        didWork = true;
                        await _runMember(who,
                            "You have unread team messages (a question or handoff from a teammate). " +
                            "Read them, take any action they require, and reply with send_message. " +
                            "If nothing is actionable, simply acknowledge and finish.");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { MuxConsole.WriteMuted($"[teams] {who} inbox turn error: {ex.Message}"); }
                }

                // Nothing claimable this pass -> wait a poll interval (shorter if we just worked, so a
                // freshly-unblocked dependent or a fresh message is picked up promptly).
                try { await Task.Delay(TimeSpan.FromSeconds(didWork ? 1 : IntervalSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* clean stop */ }
        catch (Exception ex) { MuxConsole.WriteWarning($"[teams] Peer loop for {who} stopped on error: {ex.Message}"); }
    }

    /// <summary>Reap stale (dead-owner) tasks using the configured TTL + attempt cap. Best-effort.</summary>
    private void SweepStale()
    {
        try
        {
            var lim = MuxSwarm.Utils.ExecutionLimits.Current;
            var ttl = TimeSpan.FromSeconds(Math.Max(1, lim.TaskClaimTtlSeconds));
            var reaped = _board.ReapStale(ttl, lim.MaxTaskAttempts, DateTimeOffset.UtcNow);
            if (reaped.Count > 0)
                MuxConsole.WriteMuted($"[teams] reaped {reaped.Count} stale task(s): {string.Join(", ", reaped)}");
        }
        catch { /* never let the sweep break the loop */ }
    }

    private string BriefFor(string id)
    {
        var t = _board.Get(id);
        if (t is null) return $"Work task {id}.";
        var sb = new StringBuilder();
        sb.AppendLine($"You are assigned team task {t.Id}: {t.Subject}");
        if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine().AppendLine(t.Description);
        if (t.Artifacts.Count > 0)
            sb.AppendLine().AppendLine("Relevant artifacts/paths:")
              .AppendLine(string.Join("\n", t.Artifacts.Select(a => $"  - {a}")));
        return sb.ToString();
    }
}

/// <summary>One member assignment for team_dispatch.</summary>
public record TeamAssignment(
    [property: Description("The team member to run (must be a member of the active team).")] string Agent,
    [property: Description("The task/instruction for this member.")] string Task);
