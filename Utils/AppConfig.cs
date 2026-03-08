using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class AppConfig
{
    [JsonPropertyName("setupCompleted")]
    public bool SetupCompleted { get; set; } = false;

    [JsonPropertyName("isUsingDockerForExec")]
    public bool IsUsingDockerForExec { get; set; } = false;

    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();

    [JsonPropertyName("llmProviders")]
    public LlmProvidersConfig LlmProviders { get; set; } = new();

    [JsonPropertyName("filesystem")]
    public FilesystemConfig Filesystem { get; set; } = new();
}
