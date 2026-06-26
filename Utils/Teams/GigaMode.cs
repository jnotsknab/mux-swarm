using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.State;

namespace MuxSwarm.Utils.Teams;

/// <summary>
/// Giga mode (v0.12.0 M6). A superset toggle of /ultra: it keeps the deep-reasoning escalation and
/// ADDS dynamic-orchestration capability to the live single agent. While giga is on, the agent is
/// granted tools to spin up an ephemeral team, run a batch of member tasks (each surfaced in the
/// Agent View via the existing ExecuteParallelWorker capture path), and author + run workflow files
/// - i.e. it scripts the runtime's own team/workflow pattern on its own. Toggling giga off removes
/// the tools and restores plan/effort, so the off-giga single-agent path is byte-identical.
///
/// Ephemeral teams created mid-chat live in an in-memory registry (giga:-prefixed) and can be
/// persisted to swarm.json teams[] on request so they survive a restart.
/// </summary>
public static class GigaMode
{
    private static readonly object _gate = new();

    /// <summary>Ephemeral, giga-spawned teams keyed by display name. Reset when giga is toggled off.</summary>
    private static readonly Dictionary<string, TeamConfig> _ephemeral = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The currently-active giga team scope (the last spawn_team), or null. Holds the live
    /// board + mailbox + member runner so the lead-level wrapper tools operate on a real team.</summary>
    private static TeamScope? _activeScope;

    /// <summary>The display name of the active giga team (for status / run_team default), or null.</summary>
    public static string? ActiveTeamName { get; private set; }

    /// <summary>Clear the ephemeral-team registry + tear down any active giga team (called when giga
    /// mode is turned off). Stops the peer/auto runners and clears the board/mailbox providers.</summary>
    public static void Reset()
    {
        lock (_gate) _ephemeral.Clear();
        _activeScope = null;
        ActiveTeamName = null;
        try { TeamController.Clear(); } catch { /* best-effort */ }
    }

    /// <summary>Snapshot of the current ephemeral team names (for UI / status).</summary>
    public static IReadOnlyList<string> EphemeralTeams()
    {
        lock (_gate) return _ephemeral.Keys.ToList();
    }

    /// <summary>The giga-mode system-prompt branch: the command/capability reference fed to the agent
    /// so it knows HOW to drive the team + workflow engine. Appended only while giga is active.</summary>
    public static string Preamble()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Giga Mode \u2014 Dynamic Orchestration (you can build your own team)");
        sb.AppendLine("You are in Giga mode: maximum reasoning PLUS the ability to orchestrate other agents");
        sb.AppendLine("on your own, mid-conversation, without the user wiring anything. Use it when a task is");
        sb.AppendLine("large enough to benefit from parallel specialists or a multi-phase workflow.");
        sb.AppendLine();
        sb.AppendLine("### Your orchestration tools");
        sb.AppendLine("- spawn_team(name, members, coordination, persist): create an ephemeral team (a");
        sb.AppendLine("  selection over existing agents). members = comma-separated agent names; coordination");
        sb.AppendLine("  = 'fanout' (independent parallel tasks) or 'taskboard' (dependency-gated board).");
        sb.AppendLine("  persist=true also writes it to swarm.json teams[] so it survives a restart. The team");
        sb.AppendLine("  is prefixed 'giga:' so it is clearly distinguished from user-configured teams.");
        sb.AppendLine("- run_team(name, assignments): run a batch of member tasks concurrently and collect");
        sb.AppendLine("  their results (bounded by maxParallel). assignments = JSON array of");
        sb.AppendLine("  {\"agent\":\"<member>\",\"task\":\"<instruction>\"}. Each member runs in its own session,");
        sb.AppendLine("  appears live in the Agent View, and has its own mailbox.");
        sb.AppendLine("- A spawned team is a REAL team: you also get the lead tools send_message / read_inbox");
        sb.AppendLine("  (message members + read their replies) and, for a 'taskboard' team, task_create /");
        sb.AppendLine("  task_assign / task_list / team_peerwork (build a dependency board the members drain");
        sb.AppendLine("  themselves). Members can message you back - drain with read_inbox.");
        sb.AppendLine("- write_workflow(name, steps): author a reusable workflow FILE. steps = JSON array of");
        sb.AppendLine("  strings, each 'AgentName: instruction' (or just 'instruction' to route to the lead).");
        sb.AppendLine("  Saved as <name>.workflow.json under the Teams directory.");
        sb.AppendLine("- run_workflow(path): execute a workflow file phase-by-phase in order, routing each step");
        sb.AppendLine("  to its agent and feeding prior step results forward as context.");
        sb.AppendLine("- list_workflows(): list saved workflow files you can run.");
        sb.AppendLine();
        sb.AppendLine("### How to use it");
        sb.AppendLine("1. Decide whether the task is genuinely parallelizable or multi-phase. If a single agent");
        sb.AppendLine("   suffices, just do it - do NOT spin up a team for trivial work.");
        sb.AppendLine("2. For parallel work: spawn_team then run_team with one assignment per independent piece.");
        sb.AppendLine("3. For a repeatable multi-phase process: write_workflow then run_workflow.");
        sb.AppendLine("4. Synthesize the members' results yourself and report back to the user. You remain the");
        sb.AppendLine("   single voice in this conversation; the team works under you.");
        sb.AppendLine("5. Persist a team (persist=true) only when the user will want to reuse it later.");
        return sb.ToString();
    }

    /// <summary>Build the giga toolset bound to the current model/agent factories. Giga is a true
    /// superset of a team lead: spawn_team materializes a REAL team (live board + mailbox + Agent-View
    /// providers + member specialists equipped with their own mailbox tools), and the lead-level
    /// wrappers (send_message/read_inbox/task_*/team_peerwork) operate on that active team. Members run
    /// through the shared ExecuteParallelWorker path, so they surface live in the Agent View. Off-giga
    /// this is never called.</summary>
    public static IList<AITool> BuildTools(
        Func<string, IChatClient> chatClientFactory,
        Dictionary<string, string> agentModels,
        CancellationToken ct)
    {
        var tools = new List<AITool>();
        var allDefs = Common.ParseAgentDefinitions(App.SwarmConfig);
        var validAgents = new HashSet<string>(allDefs.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        int maxSubIters = ExecutionLimits.Current.MaxSubAgentIterations;
        if (maxSubIters <= 0) maxSubIters = 8;
        const string LeadName = "Giga";

        // Run one member task through the shared parallel-worker path (Agent View capture + retries).
        async Task<string> RunOne(string agent, string task)
        {
            var delegationResults = new List<ParallelSwarmOrchestrator.DelegationResult>();
            var retryRegistry = new Dictionary<string, ParallelSwarmOrchestrator.RetryState>();
            return await ParallelSwarmOrchestrator.ExecuteParallelWorker(
                agent, task, LeadName,
                MultiAgentOrchestrator.Specialists, delegationResults, retryRegistry,
                chatClientFactory, agentModels, compactionClient: null, compactionChatOptions: null,
                maxSubAgentIterations: maxSubIters, prodMode: false, ct: ct, cleanSession: false);
        }

        // --- spawn_team: materialize a REAL team (board + mailbox + providers), not just a registry row.
        tools.Add(AIFunctionFactory.Create(
            method: async (
                [Description("Short name for the ephemeral team.")] string name,
                [Description("Comma-separated member agent names (must be defined agents).")] string members,
                [Description("Coordination: 'fanout' (independent parallel) or 'taskboard' (dependency board). Default fanout.")] string? coordination,
                [Description("When true, also persist this team to swarm.json teams[] so it survives a restart.")] bool persist) =>
            {
                var disp = "giga:" + (name ?? "team").Trim();
                var mem = (members ?? string.Empty)
                    .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(m => m.Trim())
                    .Where(m => validAgents.Contains(m))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (mem.Count == 0)
                    return $"[giga] No valid members. Defined agents: {string.Join(", ", validAgents)}";
                var coord = (coordination ?? "fanout").Trim().ToLowerInvariant() == "taskboard" ? "taskboard" : "fanout";
                var cfg = new TeamConfig
                {
                    Name = disp,
                    Description = "Ephemeral team spawned in giga mode.",
                    Lead = LeadName,
                    Members = mem,
                    Coordination = coord,
                };
                lock (_gate) _ephemeral[disp] = cfg;

                // Build the REAL team: this lights up ActiveBoard / ActiveMailbox + the Agent-View
                // board/m-log providers, and returns the per-member mailbox-tool factory.
                var scope = TeamController.Build(cfg, App.SwarmConfig!, chatClientFactory, agentModels, ct);
                if (scope is null)
                    return $"[giga] Failed to materialize team '{disp}'.";
                // Rebuild the member specialists so each gets its identity-bound send_message/read_inbox
                // (same path the /teams lead uses). Off-team specialists are unaffected by absence.
                await MultiAgentOrchestrator.BuildSpecialists(agentModels, chatClientFactory,
                    (App.McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
                    scope.MemberToolFactory);
                _activeScope = scope;
                ActiveTeamName = disp;

                string persistNote = "";
                if (persist)
                {
                    try
                    {
                        var swarm = App.SwarmConfig;
                        if (swarm is not null && !swarm.Teams.Exists(x => string.Equals(x.Name, disp, StringComparison.OrdinalIgnoreCase)))
                        {
                            swarm.Teams.Add(cfg);
                            File.WriteAllText(PlatformContext.SwarmPath,
                                JsonSerializer.Serialize(swarm, new JsonSerializerOptions { WriteIndented = true }));
                            persistNote = " Persisted to swarm.json (also launchable later with /teams after /refresh).";
                        }
                    }
                    catch (Exception ex) { persistNote = $" (persist failed: {ex.Message})"; }
                }
                return $"[giga] Team '{disp}' is live ({coord}) with members: {string.Join(", ", mem)}. " +
                       $"Mailbox + board are active; dispatch with run_team, or use task_* / team_peerwork " +
                       $"to drive the board.{persistNote}";
            },
            name: "spawn_team",
            description: "Create + activate an ephemeral team (giga:-prefixed) from existing agents, with a live " +
                         "mailbox and (for taskboard) a task board. Dispatch work with run_team or the task_* tools. " +
                         "Optionally persist it to swarm.json for reuse."));

        // --- run_team: dispatch a batch concurrently, bounded by the active team's maxParallel.
        tools.Add(AIFunctionFactory.Create(
            method: async (
                [Description("The team name to run (an ephemeral giga: team you spawned, or a configured team). Omit to use the active giga team.")] string? name,
                [Description("JSON array of assignments: [{\"agent\":\"<member>\",\"task\":\"<instruction>\"}, ...]")] string assignments) =>
            {
                var disp = string.IsNullOrWhiteSpace(name) ? (ActiveTeamName ?? "") : name!.Trim();
                List<string> roster;
                lock (_gate)
                {
                    if (_ephemeral.TryGetValue(disp, out var ecfg)) roster = ecfg.Members;
                    else
                    {
                        var cfg = TeamController.Find(App.SwarmConfig, disp);
                        if (cfg is null) return $"[giga] No team named '{disp}'. Spawn one first with spawn_team.";
                        roster = cfg.Members;
                    }
                }
                var rosterSet = new HashSet<string>(roster, StringComparer.OrdinalIgnoreCase);

                List<GigaAssignment>? list;
                try { list = JsonSerializer.Deserialize<List<GigaAssignment>>(assignments ?? "[]",
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                catch (Exception ex) { return $"[giga] Could not parse assignments JSON: {ex.Message}"; }
                if (list is null || list.Count == 0) return "[giga] No assignments provided.";

                // Bound concurrency so a giant batch does not blow past rate limits all at once.
                int maxPar = App.MaxDegreeParallelism > 0 ? App.MaxDegreeParallelism : 4;
                using var gate = new SemaphoreSlim(maxPar);
                var batch = list.Select(async a =>
                {
                    var agent = (a.Agent ?? string.Empty).Trim();
                    if (!rosterSet.Contains(agent))
                        return $"[ERROR {agent}] Not a member of '{disp}'. Members: {string.Join(", ", roster)}";
                    await gate.WaitAsync(ct);
                    try { return await RunOne(agent, a.Task ?? string.Empty); }
                    finally { gate.Release(); }
                });
                var results = await Task.WhenAll(batch);
                var sb = new StringBuilder();
                sb.AppendLine($"### GIGA TEAM '{disp}' BATCH COMPLETED ###");
                foreach (var r in results) sb.AppendLine(r);
                return sb.ToString();
            },
            name: "run_team",
            description: "Dispatch a batch of tasks to the active (or named) team's members concurrently and collect " +
                         "their results, bounded by maxParallel. Each member runs in its own session and appears live in the Agent View."));

        // --- Lead-level mailbox + board wrappers (operate on the ACTIVE giga team).
        tools.Add(AIFunctionFactory.Create(
            method: (
                [Description("Recipient member name, or \"all\" to broadcast.")] string to,
                [Description("Message type: info | question | answer | handoff | shutdown.")] string type,
                [Description("The message body.")] string body) =>
            {
                var box = TeamController.ActiveMailbox;
                if (box is null || _activeScope is null) return "[giga] No active team. Spawn one with spawn_team first.";
                var mt = (type ?? "info").Trim().ToLowerInvariant() switch
                {
                    "question" => MsgType.Question, "answer" => MsgType.Answer,
                    "handoff" => MsgType.Handoff, "shutdown" => MsgType.Shutdown, _ => MsgType.Info,
                };
                int n = box.Send(LeadName, (to ?? string.Empty).Trim(), mt, body ?? string.Empty, _activeScope.Members);
                return mt == MsgType.Shutdown
                    ? $"[giga] Shutdown signalled to {to} ({n} inbox(es))."
                    : $"[giga] Sent {mt} to {to} ({n} inbox(es)).";
            },
            name: "send_message",
            description: "Message a member of the active giga team (or \"all\"). Types: info, question, answer, handoff, shutdown."));

        tools.Add(AIFunctionFactory.Create(
            method: () =>
            {
                var box = TeamController.ActiveMailbox;
                if (box is null) return "[giga] No active team.";
                var msgs = box.ReadInbox(LeadName, drain: true);
                if (msgs.Count == 0) return "[giga] No new messages.";
                var sb = new StringBuilder($"### INBOX ({msgs.Count} new) ###");
                sb.AppendLine();
                foreach (var m in msgs) sb.AppendLine($"[{m.Type}] from {m.From}: {m.Body}");
                return sb.ToString();
            },
            name: "read_inbox",
            description: "Read (and clear) replies from members of the active giga team."));

        tools.Add(AIFunctionFactory.Create(
            method: (
                [Description("Short task subject/title.")] string subject,
                [Description("Fuller task description / instructions for the assignee.")] string description,
                [Description("Optional team member to designate as the assignee.")] string? assignee,
                [Description("Optional comma-separated task ids that must finish first.")] string? blockedBy) =>
            {
                var board = TeamController.ActiveBoard;
                if (board is null) return "[giga] The active team has no task board (spawn a 'taskboard' team).";
                var deps = (blockedBy ?? string.Empty).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var unknown = board.UnknownDeps(deps);
                if (unknown.Count > 0) return $"[giga] Unknown dependency id(s): {string.Join(", ", unknown)}.";
                var who = string.IsNullOrWhiteSpace(assignee) ? null : assignee!.Trim();
                var t2 = board.Create(subject, description, deps, who, null);
                return $"Created {t2.Id} \"{t2.Subject}\" status={t2.Status}" + (who is not null ? $" assignee={who}" : "");
            },
            name: "task_create",
            description: "Create a task on the active giga team's board (taskboard teams). Optionally assign it and/or declare blockedBy deps."));

        tools.Add(AIFunctionFactory.Create(
            method: (
                [Description("Task id to assign.")] string id,
                [Description("Member to assign it to.")] string assignee) =>
            {
                var board = TeamController.ActiveBoard;
                if (board is null) return "[giga] The active team has no task board.";
                return board.Reassign(id, (assignee ?? string.Empty).Trim(), out var reason)
                    ? $"[giga] {id} assigned to {assignee}. Turn on team_peerwork (or use run_team) to run it."
                    : $"[giga] {reason}";
            },
            name: "task_assign",
            description: "Assign (or reassign) a board task to a member of the active giga team."));

        tools.Add(AIFunctionFactory.Create(
            method: () =>
            {
                var board = TeamController.ActiveBoard;
                if (board is null) return "[giga] The active team has no task board.";
                var rows = board.Snapshot();
                if (rows.Count == 0) return "[giga] Board is empty.";
                var sb = new StringBuilder();
                foreach (var t2 in rows)
                    sb.AppendLine($"{t2.Id} [{t2.Status}] {(t2.Owner ?? t2.Assignee ?? "-")}: {t2.Subject}");
                return sb.ToString();
            },
            name: "task_list",
            description: "List every task on the active giga team's board with status + owner/assignee."));

        tools.Add(AIFunctionFactory.Create(
            method: (
                [Description("True to start the peer self-claim engine, false to stop it.")] bool enabled,
                [Description("Optional poll interval seconds (default 15, floor 3).")] int? intervalSeconds) =>
            {
                var peer = TeamController.ActivePeerRunner;
                if (peer is null) return "[giga] The active team has no peer engine (spawn a 'taskboard' team).";
                if (enabled) { peer.Start(intervalSeconds ?? 15); return $"[giga] Peer self-claim ON (every {peer.IntervalSeconds}s). Members run their own assigned board tasks."; }
                peer.Stop();
                return "[giga] Peer self-claim OFF.";
            },
            name: "team_peerwork",
            description: "Toggle peer self-claiming on the active giga team's board: each member runs its own assigned, unblocked tasks."));

        // --- Workflow authoring + execution.
        tools.Add(AIFunctionFactory.Create(
            method: (
                [Description("Workflow name (file saved as <name>.workflow.json).")] string name,
                [Description("JSON array of step strings, each 'AgentName: instruction' or just 'instruction'.")] string steps) =>
            {
                List<string>? stepList;
                try { stepList = JsonSerializer.Deserialize<List<string>>(steps ?? "[]"); }
                catch (Exception ex) { return $"[giga] Could not parse steps JSON: {ex.Message}"; }
                if (stepList is null || stepList.Count == 0) return "[giga] No steps provided.";

                var wf = new Workflow { Name = name ?? "workflow", Steps = stepList };
                var safe = name ?? "workflow";
                foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                Directory.CreateDirectory(PlatformContext.TeamsDirectory);
                var path = Path.Combine(PlatformContext.TeamsDirectory, $"{safe}.workflow.json");
                File.WriteAllText(path, JsonSerializer.Serialize(wf, new JsonSerializerOptions { WriteIndented = true }));
                return $"[giga] Wrote workflow '{wf.Name}' ({wf.Steps.Count} steps) to {path}. Run it with run_workflow.";
            },
            name: "write_workflow",
            description: "Author a reusable workflow file (a DAG of phase steps). Each step is 'AgentName: instruction' " +
                         "(routed to that agent) or just 'instruction' (routed to the Orchestrator)."));

        tools.Add(AIFunctionFactory.Create(
            method: async (
                [Description("Path to a .workflow.json file (absolute, or a name under the Teams directory).")] string path) =>
            {
                var p = (path ?? string.Empty).Trim().Trim('"', '\'');
                if (!File.Exists(p))
                {
                    var alt = Path.Combine(PlatformContext.TeamsDirectory,
                        p.EndsWith(".workflow.json", StringComparison.OrdinalIgnoreCase) ? p : $"{p}.workflow.json");
                    if (File.Exists(alt)) p = alt;
                    else return $"[giga] Workflow file not found: {path}";
                }
                Workflow wf;
                try { wf = JsonSerializer.Deserialize<Workflow>(File.ReadAllText(p),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Workflow(); }
                catch (Exception ex) { return $"[giga] Could not parse workflow: {ex.Message}"; }
                if (wf.Steps.Count == 0) return "[giga] Workflow has no steps.";

                var sb = new StringBuilder();
                sb.AppendLine($"### WORKFLOW '{wf.Name}' ({wf.Steps.Count} steps) ###");
                string priorContext = "";
                for (int i = 0; i < wf.Steps.Count; i++)
                {
                    var raw = wf.Steps[i] ?? string.Empty;
                    string agent = "Orchestrator", instruction = raw;
                    int colon = raw.IndexOf(':');
                    if (colon > 0)
                    {
                        var cand = raw.Substring(0, colon).Trim();
                        if (validAgents.Contains(cand)) { agent = cand; instruction = raw.Substring(colon + 1).Trim(); }
                    }
                    var task = string.IsNullOrEmpty(priorContext)
                        ? instruction
                        : $"Prior phase results:\r\n{priorContext}\r\n\r\nNow: {instruction}";
                    var result = await RunOne(agent, task);
                    priorContext = result;
                    sb.AppendLine($"-- Step {i + 1} [{agent}]: {instruction}");
                    sb.AppendLine(result);
                }
                return sb.ToString();
            },
            name: "run_workflow",
            description: "Execute a workflow file phase-by-phase in order, routing each step to its agent and feeding " +
                         "each step's result forward as context to the next."));

        tools.Add(AIFunctionFactory.Create(
            method: () =>
            {
                if (!Directory.Exists(PlatformContext.TeamsDirectory)) return "[giga] No workflows saved.";
                var files = Directory.EnumerateFiles(PlatformContext.TeamsDirectory, "*.workflow.json")
                    .Select(Path.GetFileName).ToList();
                if (files.Count == 0) return "[giga] No workflows saved.";
                return "Saved workflows:\r\n" + string.Join("\r\n", files.Select(f => $"- {f}"));
            },
            name: "list_workflows",
            description: "List the saved workflow files you can run with run_workflow."));

        return tools;
    }

    /// <summary>One assignment for run_team (giga ephemeral dispatch).</summary>
    public sealed class GigaAssignment
    {
        public string? Agent { get; set; }
        public string? Task { get; set; }
    }
}
