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
    public int ActivityTimeoutSeconds { get; set; } = 1200;


    /// <summary>
    /// Max model-&gt;tool round-trips the function-invocation middleware will run within a single
    /// turn before it stops looping and returns. A long autonomous tool chain that hits this looks
    /// like the agent "just stopped" mid-task. Default is high so a normal run never trips it; a
    /// value &lt;= 0 means unlimited (the real ceiling then comes from the orchestrator iteration
    /// cap + the activity timeout, so a genuine runaway still surfaces).
    /// </summary>
    [JsonPropertyName("maxToolIterationsPerTurn")]
    public int MaxToolIterationsPerTurn { get; set; } = 1000;

    /// <summary>
    /// How many times a single turn may transparently continue itself when the model's response
    /// ended with finish_reason "length" (output/reasoning token cap hit mid-generation) rather
    /// than a real stop. Each auto-continue re-invokes on the SAME session so the model resumes
    /// exactly where it was cut off. 0 disables auto-continue (revert to manual "continue"). When
    /// the budget is exhausted mid-generation a muted hint is shown.
    /// </summary>
    [JsonPropertyName("maxAutoContinuesPerTurn")]
    public int MaxAutoContinuesPerTurn { get; set; } = 3;

}