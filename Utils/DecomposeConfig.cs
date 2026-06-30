using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// Configuration for M-B task auto-decomposition (<c>task_decompose</c> tool + <c>/taskgraph</c>
/// background dispatcher). OFF by default: when <see cref="Enabled"/> is false the dispatcher never
/// starts and zero LLM calls are made; the one-shot <c>task_decompose</c> tool is always available
/// to the lead/giga regardless of this flag.
/// </summary>
public class DecomposeConfig
{
    /// <summary>When true, <c>/taskgraph on</c> may run the background tick dispatcher that
    /// periodically expands the active board from pending goals. Default false (no standing tax).</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Optional model id for the single decomposition call. Empty -> falls back to the
    /// compaction model, then the Orchestrator model. Keep this a LIGHT model (it only emits a small
    /// JSON task graph).</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Background dispatcher poll interval in seconds (floored at 10). Only used while the
    /// dispatcher is running.</summary>
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Hard ceiling on subtasks emitted per decomposition call (keeps a runaway model from
    /// flooding the board).</summary>
    [JsonPropertyName("maxSubtasks")]
    public int MaxSubtasks { get; set; } = 12;
}
