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
}
