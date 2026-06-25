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

    /// <summary>Clear the ephemeral-team registry (called when giga mode is turned off).</summary>
    public static void Reset()
    {
        lock (_gate) _ephemeral.Clear();
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
        sb.AppendLine("  their results. assignments = JSON array of {\"agent\":\"<member>\",\"task\":\"<instruction>\"}.");
        sb.AppendLine("  Each member runs in its own session and appears live in the Agent View.");
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

    /// <summary>Build the giga toolset bound to the current model/agent factories. Members run through
    /// the same ExecuteParallelWorker path used by /teams + delegate_parallel, so they surface live in
    /// the Agent View. Specialists must already be built (the single-agent path builds them when giga
    /// is on). Off-giga this is never called.</summary>
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

        // Run one member task through the shared parallel-worker path (Agent View capture + retries).
        async Task<string> RunOne(string agent, string task)
        {
            var delegationResults = new List<ParallelSwarmOrchestrator.DelegationResult>();
            var retryRegistry = new Dictionary<string, ParallelSwarmOrchestrator.RetryState>();
            return await ParallelSwarmOrchestrator.ExecuteParallelWorker(
                agent, task, "Giga",
                MultiAgentOrchestrator.Specialists, delegationResults, retryRegistry,
                chatClientFactory, agentModels, compactionClient: null, compactionChatOptions: null,
                maxSubAgentIterations: maxSubIters, prodMode: false, ct: ct, cleanSession: false);
        }

        tools.Add(AIFunctionFactory.Create(
            method: (
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
                    Members = mem,
                    Coordination = coord,
                };
                lock (_gate) _ephemeral[disp] = cfg;

                string persistNote = "";
                if (persist)
                {
                    try
                    {
                        var swarm = App.SwarmConfig;
                        if (swarm is not null && !swarm.Teams.Exists(t => string.Equals(t.Name, disp, StringComparison.OrdinalIgnoreCase)))
                        {
                            swarm.Teams.Add(cfg);
                            File.WriteAllText(PlatformContext.SwarmPath,
                                JsonSerializer.Serialize(swarm, new JsonSerializerOptions { WriteIndented = true }));
                            persistNote = " Persisted to swarm.json (run /refresh to make it launchable with /teams).";
                        }
                    }
                    catch (Exception ex) { persistNote = $" (persist failed: {ex.Message})"; }
                }
                return $"[giga] Spawned ephemeral team '{disp}' ({coord}) with members: {string.Join(", ", mem)}." +
                       $" Use run_team to dispatch work.{persistNote}";
            },
            name: "spawn_team",
            description: "Create an ephemeral team (giga:-prefixed) from existing agents that you can then dispatch " +
                         "work to with run_team. Optionally persist it to swarm.json for reuse."));

        tools.Add(AIFunctionFactory.Create(
            method: async (
                [Description("The team name to run (an ephemeral giga: team you spawned, or a configured team).")] string name,
                [Description("JSON array of assignments: [{\"agent\":\"<member>\",\"task\":\"<instruction>\"}, ...]")] string assignments) =>
            {
                var disp = (name ?? string.Empty).Trim();
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

                var batch = list.Select(async a =>
                {
                    var agent = (a.Agent ?? string.Empty).Trim();
                    if (!rosterSet.Contains(agent))
                        return $"[ERROR {agent}] Not a member of '{disp}'. Members: {string.Join(", ", roster)}";
                    return await RunOne(agent, a.Task ?? string.Empty);
                });
                var results = await Task.WhenAll(batch);
                var sb = new StringBuilder();
                sb.AppendLine($"### GIGA TEAM '{disp}' BATCH COMPLETED ###");
                foreach (var r in results) sb.AppendLine(r);
                return sb.ToString();
            },
            name: "run_team",
            description: "Dispatch a batch of tasks to a team's members concurrently and collect their results. " +
                         "Each member runs in its own session and appears live in the Agent View."));

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
