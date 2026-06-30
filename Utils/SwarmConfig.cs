using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// Configuration model for swarm.json - defines sub-agents and their tool access
/// </summary>
public class SwarmConfig
{

    [JsonPropertyName("executionLimits")]
    public ExecutionLimits ExecutionLimits { get; set; } = new();

    [JsonPropertyName("compactionAgent")]
    public CompacterConfig? CompactionAgent { get; set; }

    [JsonPropertyName("reflectionAgent")]
    public ReflectionConfig? ReflectionAgent { get; set; }

    /// <summary>
    /// Top-level convenience alias for <c>reflectionAgent.mode</c> ("standard" | "deep"): lets the
    /// user flip deep memory with one key. When set it overrides the nested mode at load time (see
    /// <see cref="ResolveReflection"/>). Null = use the nested config (or default standard).
    /// </summary>
    [JsonPropertyName("memoryMode")]
    public string? MemoryMode { get; set; }

    /// <summary>
    /// Resolve the effective reflection config, folding the top-level <see cref="MemoryMode"/>
    /// alias onto the nested <see cref="ReflectionAgent"/> block. Never returns null. The result is
    /// always-safe: when neither is set, mode is "standard" and the subsystem is inert.
    /// </summary>
    public ReflectionConfig ResolveReflection()
    {
        var cfg = ReflectionAgent ?? new ReflectionConfig();
        if (!string.IsNullOrWhiteSpace(MemoryMode))
            cfg.Mode = MemoryMode.Trim();
        return cfg;
    }

    [JsonPropertyName("decompose")]
    public DecomposeConfig? Decompose { get; set; }

    [JsonPropertyName("visionAgent")]
    public VisionConfig? VisionAgent { get; set; }

    [JsonPropertyName("singleAgent")]
    public AgentConfig? SingleAgent { get; set; }

    [JsonPropertyName("orchestrator")]
    public OrchestratorConfig? Orchestrator { get; set; }

    [JsonPropertyName("agents")]
    public List<AgentConfig> Agents { get; set; } = [];

    [JsonPropertyName("teams")]
    public List<TeamConfig> Teams { get; set; } = [];

    [JsonPropertyName("hooks")]
    public List<HookConfig> Hooks { get; set; } = [];
}