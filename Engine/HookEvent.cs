using System.Text.Json.Serialization;

namespace MuxSwarm.Engine;

public class HookEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    [JsonPropertyName("tool")]
    public string? Tool { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("args")]
    public string? Args { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("goalId")]
    public string? GoalId { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// The agent that started the flow this event came from (the delegating lead for a sub-agent's
    /// events), stamped from the ambient context; null for the lead itself and process-level events.
    /// </summary>
    [JsonPropertyName("parentAgent")]
    public string? ParentAgent { get; set; }

    /// <summary>Delegation or background-job id of the flow this event came from, when inside one.</summary>
    [JsonPropertyName("flowId")]
    public string? FlowId { get; set; }

    /// <summary>
    /// Event-specific structured fields (ids, names, counts, durations; never secrets). Absent when
    /// unset; each event's keys are listed in DOCS.md <c>## Event Hooks</c>.
    /// </summary>
    [JsonPropertyName("data")]
    public Dictionary<string, object?>? Data { get; set; }
}