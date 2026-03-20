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

    [JsonPropertyName("singleAgent")]
    public AgentConfig? SingleAgent { get; set; }

    [JsonPropertyName("orchestrator")]
    public OrchestratorConfig? Orchestrator { get; set; }

    [JsonPropertyName("agents")]
    public List<AgentConfig> Agents { get; set; } = [];

    [JsonPropertyName("hooks")]
    public List<HookConfig> Hooks { get; set; } = [];
}