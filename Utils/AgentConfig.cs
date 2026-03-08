using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// Individual agent configuration
/// </summary>
public class AgentConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("promptPath")]
    public string? PromptPath { get; set; }

    [JsonPropertyName("canDelegate")]
    public bool CanDelegate { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// List of MCP server names this agent should have access to.
    /// Tools will be filtered to only include those from specified servers.
    /// </summary>
    [JsonPropertyName("mcpServers")]
    public List<string> McpServers { get; set; } = [];

    /// <summary>
    /// Optional: explicit tool name patterns (in addition to mcpServers).
    /// Supports wildcards with * at the end.
    /// </summary>
    [JsonPropertyName("toolPatterns")]
    public List<string> ToolPatterns { get; set; } = [];

    /// <summary>
    /// Optional: explicit skill name patterns.
    /// Supports wildcards with * at the end.
    /// </summary>
    [JsonPropertyName("skillPatterns")]
    public List<string> SkillPatterns { get; set; } = [];
}