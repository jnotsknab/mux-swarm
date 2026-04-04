using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class ExecutionLimits
{
    public static ExecutionLimits Current { get; set; } = new();

    [JsonPropertyName("progressEntryBudget")]
    public int ProgressEntryBudget { get; set; } = 1000;

    [JsonPropertyName("crossAgentContextBudget")]
    public int CrossAgentContextBudget { get; set; } = 2000;

    [JsonPropertyName("progressLogTotalBudget")]
    public int ProgressLogTotalBudget { get; set; } = 4500;

    [JsonPropertyName("maxOrchestratorIterations")]
    public int MaxOrchestratorIterations { get; set; } = 15;

    [JsonPropertyName("maxSubAgentIterations")]
    public int MaxSubAgentIterations { get; set; } = 8;

    [JsonPropertyName("maxSubTaskRetries")]
    public int MaxSubTaskRetries { get; set; } = 4;

    [JsonPropertyName("maxStuckCount")]
    public int MaxStuckCount { get; set; } = 3;

    [JsonPropertyName("compactionCharBudget")]
    public int CompactionCharBudget { get; set; } = 6000;
    
    [JsonPropertyName("contextInjection")]
    public string ContextInjection { get; set; } = "full";
    
    [JsonPropertyName("compactionMaxMessageChars")]
    public int CompactionMaxMessageChars { get; set; } = 2500;

    [JsonPropertyName("activityTimeoutSeconds")]
    public int ActivityTimeoutSeconds { get; set; } = 180;

}