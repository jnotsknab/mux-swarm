using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// A named team: a selection over the existing Agents[] plus a coordination policy.
/// Purely additive - absent teams[] leaves all existing behavior unchanged. A team is
/// launched with /teams &lt;name&gt; and its members are spawned as isolated sub-agent
/// sessions (RunSubAgentAsync), surfaced live in the Agent View.
/// </summary>
public class TeamConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The lead agent (coordinator). Defaults to "Orchestrator" when unset.</summary>
    [JsonPropertyName("lead")]
    public string? Lead { get; set; }

    /// <summary>Member agent names, resolved against Agents[] in swarm.json.</summary>
    [JsonPropertyName("members")]
    public List<string> Members { get; set; } = [];

    /// <summary>
    /// Coordination policy: "fanout" (independent tasks, no deps), "taskboard" (shared task
    /// graph with file-locked claiming + dependency gating), or "pipeline" (reserved). M2
    /// honors fanout + taskboard; unknown values fall back to fanout.
    /// </summary>
    [JsonPropertyName("coordination")]
    public string Coordination { get; set; } = "fanout";

    /// <summary>Max members running concurrently. Null falls back to /maxp / ExecutionLimits.</summary>
    [JsonPropertyName("maxParallel")]
    public int? MaxParallel { get; set; }

    /// <summary>Agent View density: "auto" (always-on strip) or "minimal". Default "auto".</summary>
    [JsonPropertyName("agentView")]
    public string AgentView { get; set; } = "auto";

    /// <summary>When true (taskboard teams only), the lead starts the background auto-runner at
    /// launch: tasks that are unblocked, unowned, have an assignee, and are past their start
    /// time are claimed + run automatically on a timer. Default false (lead assigns manually).</summary>
    [JsonPropertyName("autoRun")]
    public bool AutoRun { get; set; }

    /// <summary>Auto-runner poll interval in seconds. Default 15. Clamped to a sane floor at runtime.</summary>
    [JsonPropertyName("autoRunIntervalSeconds")]
    public int AutoRunIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// How a member's session is managed across task pickups (taskboard teams):
    /// "persistent" (default) keeps each member's session warm so context carries over between
    /// tasks - bounded by per-member auto-compaction at
    /// <see cref="CompacterConfig.MemberAutoCompactTokenThreshold"/>; "fresh" starts a clean
    /// session for every task (the pre-g12.16 one-shot behavior, no carry-over, no growth).
    /// Unknown values fall back to "persistent".
    /// </summary>
    [JsonPropertyName("memberContext")]
    public string MemberContext { get; set; } = "persistent";

    /// <summary>
    /// How members acquire board tasks when running their own self-claim loops (taskboard teams):
    /// "assigned" (default) - a member only auto-claims tasks whose Assignee is itself (the lead
    /// or /kanban designates who runs what); "open" - any idle member may claim any unassigned,
    /// unblocked, ready task (a self-organizing pool, load-balanced across members). File-locked
    /// claiming makes both race-safe. Unknown values fall back to "assigned".
    /// </summary>
    [JsonPropertyName("pickupPolicy")]
    public string PickupPolicy { get; set; } = "assigned";

    /// <summary>
    /// Whether this team has an inter-agent mailbox (M4). When true (default) the lead AND every
    /// member get the <c>send_message</c> / <c>read_inbox</c> tools, members drain their inbox at
    /// the start of each task, and an idle member wakes to process incoming messages. Set false to
    /// disable inter-agent messaging entirely for the team (no mailbox, no tools, no drain) -
    /// byte-identical to a team with no messaging.
    /// </summary>
    [JsonPropertyName("mailbox")]
    public bool Mailbox { get; set; } = true;
}
