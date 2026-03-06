namespace MuxSwarm.State;
using System.Text.Json.Serialization;

public class CurrentStateMetadata(
    string goalId,
    string goal,
    uint iteration,
    DateTime lastCompletedAt,
    DateTime nextWakeAt,
    string status,
    uint minDelaySeconds)
{
    [JsonPropertyName("GoalId")] public string GoalId { get; set; } = goalId;
    [JsonPropertyName("Goal")] public string Goal { get; set; } = goal;
    [JsonPropertyName("Iteration")] public uint Iteration { get; set; } = iteration;
    [JsonPropertyName("LastCompletedAt")] public DateTime LastCompletedAt { get; set; } = lastCompletedAt;
    [JsonPropertyName("NextWakeAt")] public DateTime NextWakeAt { get; set; } = nextWakeAt;
    [JsonPropertyName("Status")] public string Status { get; set; } = status;
    [JsonPropertyName("MinDelaySeconds")] public uint MinDelaySeconds { get; set; } = minDelaySeconds;
}