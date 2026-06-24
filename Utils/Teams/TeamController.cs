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
            leadDef.Name, members, coordination, board,
            chatClientFactory, agentModels, maxParallel, maxSubIters, state, ct);

        return new TeamScope
        {
            DisplayName = team.Name,
            LeadDef = leadDef,
            Members = members,
            Coordination = coordination,
            Board = board,
            Tools = tools,
            State = state,
        };
    }

    /// <summary>Clear the active-board reference + UI provider when a team session ends.</summary>
    public static void Clear()
    {
        ActiveBoard = null;
        MuxConsole.TuiSetTaskBoardProvider(null);
    }

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
        string leadName,
        List<string> members,
        string coordination,
        TaskBoard? board,
        Func<string, IChatClient> chatClientFactory,
        Dictionary<string, string> agentModels,
        int maxParallel,
        int maxSubIters,
        TeamState state,
        CancellationToken ct)
    {
        var delegationResults = new List<ParallelSwarmOrchestrator.DelegationResult>();
        var retryRegistry = new Dictionary<string, ParallelSwarmOrchestrator.RetryState>();
        var semaphore = new SemaphoreSlim(Math.Max(1, maxParallel));
        var memberSet = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);

        // Run one member on a task through the existing parallel-worker path. This is what makes
        // members appear live in the Agent View (RunSubAgentAsync -> BeginSubAgentCapture).
        async Task<string> RunMember(string agent, string task)
        {
            await semaphore.WaitAsync(ct);
            try
            {
                state.LastActive = DateTimeOffset.UtcNow;
                return await ParallelSwarmOrchestrator.ExecuteParallelWorker(
                    agent, task, leadName,
                    MultiAgentOrchestrator.Specialists, delegationResults, retryRegistry,
                    chatClientFactory, agentModels, compactionClient: null, compactionChatOptions: null,
                    maxSubAgentIterations: maxSubIters, prodMode: false, ct: ct, cleanSession: true);
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
            // task_create: add a task to the shared board, optionally blocked by other task ids.
            tools.Add(AIFunctionFactory.Create(
                method: (
                    [Description("Short task subject/title.")] string subject,
                    [Description("Fuller task description / instructions for the assignee.")] string description,
                    [Description("Optional comma-separated task ids that must finish before this one (e.g. 't1,t2').")] string? blockedBy) =>
                {
                    var deps = (blockedBy ?? string.Empty)
                        .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var t = board.Create(subject, description, deps);
                    return $"Created {t.Id} \"{t.Subject}\" status={t.Status}" +
                           (t.BlockedBy.Count > 0 ? $" blockedBy=[{string.Join(",", t.BlockedBy)}]" : "");
                },
                name: "task_create",
                description: "Create a task on the shared team board. Tasks can declare dependencies via blockedBy; " +
                             "a blocked task cannot be assigned until its blockers are Done."));

            // task_assign: claim a board task for a member (file-locked) and run it; on success mark
            // Done so dependents auto-unblock. This is the lead-orchestrated taskboard execution path.
            tools.Add(AIFunctionFactory.Create(
                method: async (
                    [Description("The task id to assign (e.g. 't1').")] string taskId,
                    [Description("The team member to assign it to.")] string agent) =>
                {
                    var id = (taskId ?? string.Empty).Trim();
                    var who = (agent ?? string.Empty).Trim();
                    if (!memberSet.Contains(who))
                        return $"[teams] '{who}' is not a member. Members: {roster}";
                    if (!board.TryClaim(id, who, out var reason))
                        return $"[teams] Cannot assign {id} to {who}: {reason}";

                    string result;
                    try
                    {
                        result = await RunMember(who, BuildTaskBrief(board, id));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        board.SetStatus(id, TeamTaskStatus.Pending, out _);
                        throw;
                    }

                    bool ok = !result.StartsWith("[ERROR", StringComparison.Ordinal);
                    board.SetStatus(id, ok ? TeamTaskStatus.Done : TeamTaskStatus.Failed, out _);
                    state.Save();
                    return $"[{id} -> {who}] {(ok ? "DONE" : "FAILED")}\n{result}";
                },
                name: "task_assign",
                description: "Assign a board task to a member: atomically claims it (no two members can claim the " +
                             "same task), runs the member on it, and marks it Done/Failed - completing a task " +
                             "auto-unblocks its dependents."));

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
                        sb.Append(' ').Append(t.Subject);
                        if (t.BlockedBy.Count > 0) sb.Append("  blockedBy=[").Append(string.Join(",", t.BlockedBy)).Append(']');
                        sb.AppendLine();
                    }
                    return sb.ToString();
                },
                name: "task_list",
                description: "List every task on the shared team board with its status, owner, and dependencies."));
        }

        return tools;
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

/// <summary>One member assignment for team_dispatch.</summary>
public record TeamAssignment(
    [property: Description("The team member to run (must be a member of the active team).")] string Agent,
    [property: Description("The task/instruction for this member.")] string Task);
