using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class LlmProviderConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("apiKeyEnvVar")]
    public string? ApiKeyEnvVar { get; set; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("defaultModel")]
    public string? DefaultModel { get; set; }
}