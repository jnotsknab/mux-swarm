using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class ProviderConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("apiKeyEnvVar")]
    public string? ApiKeyEnvVar { get; set; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }
}