using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// Deep-memory reflection agent configuration (sibling of <see cref="CompacterConfig"/>).
///
/// When <see cref="Mode"/> is "deep", a background <c>ReflectionGatherer</c> observes the live
/// session (user messages, agent responses, tool successes/failures, decisions) and distills
/// durable reflections to <c>Context/Reflections/</c>; a <c>ReflectionInjector</c> then selects a
/// small, budgeted block scored by recency * importance * relevance and injects it into agent
/// preambles so working agents never spend a turn querying memory.
///
/// Default mode "standard" makes the whole subsystem inert (byte-identical to a build without it).
/// The store is filesystem-first: <c>Context/Reflections/</c> is always primary; the
/// knowledge-graph + ChromaDB MCP servers are OPTIONAL accelerators. When they are absent or
/// failing, deep mode STAYS ACTIVE and degrades to lexical/recency scoring over the filesystem
/// reflections (no warning, no drop to standard).
/// </summary>
public class ReflectionConfig
{
    /// <summary>"standard" (default, = today, inert) | "deep" (gatherer + injector active).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "standard";

    /// <summary>Model id for the gatherer's distillation call. Null falls back to the
    /// orchestrator / compaction model (a cheap model is recommended).</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Optional ChatOptions (maxOutputTokens etc.) for the gatherer call.</summary>
    [JsonPropertyName("modelOpts")]
    public ModelOpts? ModelOpts { get; set; }

    /// <summary>Hard cap (tokens, approximate) on the injected reflection block. Truncated, never
    /// overflowed. Configurable.</summary>
    [JsonPropertyName("injectTokenBudget")]
    public int InjectTokenBudget { get; set; } = 1500;

    /// <summary>Background poll/check cadence in seconds. On each wake the gatherer SKIPS the LLM
    /// call entirely when no activity has occurred since the last reflection. Configurable.</summary>
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 90;

    /// <summary>Minimum relevance score (0..1) for a reflection to be injected (anti-noise).</summary>
    [JsonPropertyName("relevanceFloor")]
    public double RelevanceFloor { get; set; } = 0.35;

    /// <summary>"lead" (lead + orchestrator only, default) | "all" (also sub-agents).</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "lead";

    /// <summary>Hard cap on retained reflections in the single-file store; oldest beyond this are
    /// pruned on append. Default 1000.</summary>
    [JsonPropertyName("maxReflections")]
    public int MaxReflections { get; set; } = 1000;

    /// <summary>How many recent conversation messages the Pass-1 distill call observes. Default 30.</summary>
    [JsonPropertyName("historyWindow")]
    public int HistoryWindow { get; set; } = 30;

    /// <summary>Max dig requests the Pass-2 read-only investigator chases per gatherer tick. Default 2.</summary>
    [JsonPropertyName("maxDigsPerTick")]
    public int MaxDigsPerTick { get; set; } = 2;

    /// <summary>Pass-2 dig: max files the read-only grep scans before stopping. Default 4000.</summary>
    [JsonPropertyName("digMaxFilesScanned")]
    public int DigMaxFilesScanned { get; set; } = 4000;

    /// <summary>Pass-2 dig: max grep matches returned. Default 40.</summary>
    [JsonPropertyName("digMaxMatches")]
    public int DigMaxMatches { get; set; } = 40;

    /// <summary>Pass-2 dig: max chars returned from a single file read. Default 8000.</summary>
    [JsonPropertyName("digMaxReadChars")]
    public int DigMaxReadChars { get; set; } = 8000;

    /// <summary>True when deep mode is requested.</summary>
    [JsonIgnore]
    public bool IsDeep =>
        string.Equals(Mode?.Trim(), "deep", StringComparison.OrdinalIgnoreCase);
}
