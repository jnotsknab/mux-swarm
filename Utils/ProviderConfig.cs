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

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Optional auth type tag. Null/absent/"apikey" => the normal OpenAI-compatible path (endpoint + key).
    /// Retained for backward compatibility with existing configs; subscription providers now route through
    /// the local CLIProxyAPI sidecar as ordinary OpenAI-compatible endpoints (see CliProxyManager).
    /// </summary>
    [JsonPropertyName("authType")]
    public string? AuthType { get; set; }
}