using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class AppConfig
{
    [JsonPropertyName("setupCompleted")]
    public bool SetupCompleted { get; set; } = false;

    [JsonPropertyName("isUsingDockerForExec")]
    public bool IsUsingDockerForExec { get; set; } = false;
    
    [JsonPropertyName("serveAddress")]
    public string ServeAddress { get; set; } = "0.0.0.0";

    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();

    [JsonPropertyName("llmProviders")]
    public List<ProviderConfig> LlmProviders { get; set; } = [];

    [JsonPropertyName("filesystem")]
    public FilesystemConfig Filesystem { get; set; } = new();

    [JsonPropertyName("userInfo")]
    public UserInfoConfig UserInfo { get; set; } = new();
    
    [JsonPropertyName("telemetry")]
    public TelemetryConfig Telemetry { get; set; } = new();

    [JsonPropertyName("daemon")] 
    public DaemonConfig? Daemon { get; set; } = new();


}
