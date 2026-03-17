using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// Orchestrator-specific configuration
/// </summary>
public class OrchestratorConfig
{
    [JsonPropertyName("promptPath")]
    public string? PromptPath { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("modelOpts")]
    public ModelOpts? ModelOpts { get; set; }

    [JsonPropertyName("mcpServers")]
    public List<string> McpServers { get; set; } = [];

    /// <summary>
    /// Optional: explicit tool name patterns (in addition to mcpServers).
    /// Supports wildcards with * at the end.
    /// </summary>
    [JsonPropertyName("toolPatterns")]
    public List<string> ToolPatterns { get; set; } = [];

}