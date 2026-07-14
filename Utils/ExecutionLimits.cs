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

    /// <summary>
    /// How sub-agent / cross-agent results are compacted when they exceed the budget but are not
    /// huge. Three modes:
    ///   "auto"       - (default) run the LLM summarizer AND append signal-scored extracted
    ///                  references, so the lead gets a dense summary plus the concrete
    ///                  paths/errors/identifiers the summary may have dropped. Uses the existing
    ///                  cross-agent budget.
    ///   "llm"        - LLM summary with extracted references as supplemental.
    ///   "extractive" - NEVER call the LLM. Always use the improved extractive algorithm only
    ///                  (the money-saving mode).
    /// Unknown/empty falls back to "auto".
    /// </summary>
    [JsonPropertyName("subAgentSummaryMode")]
    public string SubAgentSummaryMode { get; set; } = "auto";

    /// <summary>
    /// How many days spilled sub-agent raw outputs (the size-tiered delegation retention dir under
    /// &lt;sandbox&gt;/delegations or %LOCALAPPDATA%/Mux-Swarm/delegations) are kept before a startup
    /// prune deletes them. 0 disables pruning. The retention dir holds the FULL raw output a lead
    /// reads on demand via read_delegation; everything else in the tiering engine scales off the
    /// existing progress budgets, so this is the only new knob.
    /// </summary>
    [JsonPropertyName("delegationRetentionDays")]
    public int DelegationRetentionDays { get; set; } = 30;

    /// <summary>
    /// Deadman's-switch window (seconds) for a single streaming response: a turn is cancelled if no
    /// stream chunk arrives within this span (reset on every chunk). This value is ALSO reused as the
    /// OpenAI client HTTP NetworkTimeout, so it bounds how long a single request may stall before the
    /// connection is torn down. It is NOT an idle timeout between turns -- sitting at the prompt or
    /// between requests never trips it. Default 3600 (1h) so long tool-running turns, system_sleep
    /// gaps inside a turn, and slow providers are tolerated; a genuinely hung stream still surfaces
    /// eventually. Lower it if you want hung streams to fail fast.
    /// </summary>
    [JsonPropertyName("activityTimeoutSeconds")]
    public int ActivityTimeoutSeconds { get; set; } = 3600;


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

    /// <summary>
    /// Claim time-to-live (seconds) for a running team task. When a task has been InProgress with no
    /// heartbeat (or, absent heartbeats, no claim) newer than this, the stale-task reaper treats its
    /// owner as dead and requeues the task (or trips it to Failed once <see cref="MaxTaskAttempts"/>
    /// is exhausted). Default 900 (15 min). The runner loop heartbeats well inside this window.
    /// </summary>
    [JsonPropertyName("taskClaimTtlSeconds")]
    public int TaskClaimTtlSeconds { get; set; } = 900;

    /// <summary>
    /// Maximum times a single team task may be (re)claimed/run before the bounded-retry circuit
    /// breaker marks it terminal Failed instead of requeuing it again - so a task that kills its
    /// worker every time can't respawn forever. Default 3. A value &lt;= 0 disables the breaker
    /// (tasks requeue indefinitely on staleness).
    /// </summary>
    [JsonPropertyName("maxTaskAttempts")]
    public int MaxTaskAttempts { get; set; } = 3;

    /// <summary>
    /// When true, a single-agent turn is auto-compacted MID-TURN as soon as an authoritative
    /// UsageContent checkpoint reports the running session token total has crossed the
    /// compaction threshold - rather than waiting for the next user input. TryCompactAsync
    /// summarizes the conversation, spins up a fresh session from the summary, and the current
    /// turn continues on the compacted context (the live message list is wiped down to the
    /// reseeded summary; the system prompt lives on the agent and is unaffected). Default false
    /// preserves the legacy behavior of only compacting between turns.
    /// </summary>
    [JsonPropertyName("midTurnCompaction")]
    public bool MidTurnCompaction { get; set; } = false;

}