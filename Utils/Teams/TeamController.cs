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
    public required IList<AITool> Tools { get; init; }    // team tools appended to the lead's tool list
    public required TeamState State { get; init; }

    /// <summary>The board auto-runner for a taskboard team (null otherwise). Started at launch
    /// when the team config sets autoRun, or via the task_autorun tool.</summary>
    public AutoRunner? Runner { get; init; }

    /// <summary>The peer self-claim engine for a taskboard team (null otherwise): per-member loops
    /// that poll the board and claim eligible tasks on their own. Started via the team_peerwork
    /// tool (or /kanban). Distinct from <see cref="Runner"/>, which is a single assignee-keyed loop.</summary>
    public MemberRunner? PeerRunner { get; init; }

    public bool UsesTaskBoard => Board is not null;
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

        int maxParallel = team.MaxParallel is { } mp && mp > 0 ? mp : App.MaxDegreeParallelism;
        int maxSubIters = ExecutionLimits.Current.MaxSubAgentIterations;
        if (maxSubIters <= 0) maxSubIters = 8;

        var tools = BuildTeamTools(
            team, leadDef.Name, members, coordination, board,
            chatClientFactory, agentModels, maxParallel, maxSubIters, state, ct,
            out var autoRunner, out var peerRunner);
        ActiveRunner = autoRunner;
        ActivePeerRunner = peerRunner;

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
            Tools = tools,
            State = state,
            Runner = autoRunner,
            PeerRunner = peerRunner,
        };
    }

    /// <summary>Clear the active-board reference + UI provider when a team session ends.</summary>
    public static void Clear()
    {
        ActiveBoard = null;
        MuxConsole.TuiSetTaskBoardProvider(null);
        try { ActiveRunner?.Stop(); } catch { /* best-effort */ }
        try { ActivePeerRunner?.Stop(); } catch { /* best-effort */ }
        ActiveRunner = null;
        ActivePeerRunner = null;
    }

    /// <summary>The auto-runner of the currently-running team, stopped on Clear().</summary>
    public static AutoRunner? ActiveRunner { get; private set; }

    /// <summary>The peer self-claim engine of the currently-running team, stopped on Clear().</summary>
    public static MemberRunner? ActivePeerRunner { get; private set; }

    /// <summary>Feed the driver's Ctrl+T TaskBoard strip a point-in-time snapshot of the board
    /// (tally + flattened rows), keeping the TUI layer free of any State/Teams dependency.</summary>
    private static void InstallBoardProvider(TaskBoard board)
    {
        MuxConsole.TuiSetTaskBoardProvider(() =>
        {
            var (total, done, prog, blocked, failed) = board.Tally();
            var rows = board.Snapshot()
                .Select(t => (t.Id, t.Status.ToString(), t.Owner, t.Subject))
                .ToList();
            return (total, done, prog, blocked, failed,
                (IReadOnlyList<(string, string, string?, string)>)rows);
        });
    }

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
        Func<string, IChatClient> chatClientFactory,
        Dictionary<string, string> agentModels,
        int maxParallel,
        int maxSubIters,
        TeamState state,
        CancellationToken ct,
        out AutoRunner? autoRunner,
        out MemberRunner? peerRunner)
    {
        autoRunner = null;
        peerRunner = null;
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
                return await contextManager.RunAsync(agent, async cleanSession =>
                    await ParallelSwarmOrchestrator.ExecuteParallelWorker(
                        agent, task, leadName,
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
                    [Description("Optional delay in seconds before the auto-runner may start this task (a timer/trigger). 0 or omitted = eligible immediately.")] int? startInSeconds) =>
                {
                    var deps = (blockedBy ?? string.Empty)
                        .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var who = string.IsNullOrWhiteSpace(assignee) ? null : assignee!.Trim();
                    if (who is not null && !memberSet.Contains(who))
                        return $"[teams] Assignee '{who}' is not a member. Members: {roster}";
                    DateTimeOffset? startAt = startInSeconds is { } s && s > 0
                        ? DateTimeOffset.UtcNow.AddSeconds(s) : null;
                    var t = board.Create(subject, description, deps, who, startAt);
                    return $"Created {t.Id} \"{t.Subject}\" status={t.Status}" +
                           (t.BlockedBy.Count > 0 ? $" blockedBy=[{string.Join(",", t.BlockedBy)}]" : "") +
                           (t.Assignee is not null ? $" assignee={t.Assignee}" : "") +
                           (t.StartAt is not null ? $" startAt={t.StartAt:HH:mm:ss}" : "");
                },
                name: "task_create",
                description: "Create a task on the shared team board. Tasks can declare dependencies via blockedBy " +
                             "(a blocked task cannot run until its blockers are Done), an assignee (the member who runs " +
                             "it), and startInSeconds (a timer before the auto-runner may start it)."));

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
                        var pending = t.BlockedBy.Where(d => board.Get(d) is { Status: not TeamTaskStatus.Done }).ToList();
                        sb.AppendLine($"  blockedBy: [{string.Join(",", t.BlockedBy)}]" +
                                      (pending.Count > 0 ? $"  (waiting on: {string.Join(",", pending)})" : "  (all deps done)"));
                    }
                    if (t.Blocks.Count > 0) sb.AppendLine($"  blocks: [{string.Join(",", t.Blocks)}]");
                    sb.AppendLine($"  created: {t.Created:u}" +
                                  (t.StartAt is not null ? $"   startAt: {t.StartAt:u}" : "") +
                                  (t.ClaimedAt is not null ? $"   claimed: {t.ClaimedAt:u}" : "") +
                                  (t.CompletedAt is not null ? $"   completed: {t.CompletedAt:u}" : ""));
                    return sb.ToString();
                },
                name: "task_info",
                description: "Show full detail for one task: status, owner, assignee, dependencies (and which are still " +
                             "pending), what it blocks, and its timestamps."));

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
            var peer = new MemberRunner(board, members, RunMember, state, openPool, ct);
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

        return tools;
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

    private string BriefFor(string id)
    {
        var t = _board.Get(id);
        if (t is null) return $"Work task {id}.";
        var sb = new StringBuilder();
        sb.AppendLine($"You are assigned team task {t.Id}: {t.Subject}");
        if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine().AppendLine(t.Description);
        return sb.ToString();
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
        CancellationToken sessionCt)
    {
        _board = board;
        _members = members;
        _runMember = runMember;
        _state = state;
        _openPool = openPool;
        _sessionCt = sessionCt;
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
                bool didWork = false;

                // Drain everything this member can claim right now, one task at a time.
                while (!ct.IsCancellationRequested)
                {
                    var next = _board.NextClaimableFor(who, _openPool, DateTimeOffset.UtcNow);
                    if (next is null) break;

                    if (!_board.TryClaim(next.Id, who, out _)) continue; // raced another member; try the next
                    didWork = true;

                    string result;
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

                // Nothing claimable this pass -> wait a poll interval (shorter if we just worked, so a
                // freshly-unblocked dependent is picked up promptly).
                try { await Task.Delay(TimeSpan.FromSeconds(didWork ? 1 : IntervalSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* clean stop */ }
        catch (Exception ex) { MuxConsole.WriteWarning($"[teams] Peer loop for {who} stopped on error: {ex.Message}"); }
    }

    private string BriefFor(string id)
    {
        var t = _board.Get(id);
        if (t is null) return $"Work task {id}.";
        var sb = new StringBuilder();
        sb.AppendLine($"You are assigned team task {t.Id}: {t.Subject}");
        if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine().AppendLine(t.Description);
        return sb.ToString();
    }
}

/// <summary>One member assignment for team_dispatch.</summary>
public record TeamAssignment(
    [property: Description("The team member to run (must be a member of the active team).")] string Agent,
    [property: Description("The task/instruction for this member.")] string Task);
