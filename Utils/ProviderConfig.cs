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
    /// Optional native subscription-OAuth auth type. Null/absent/"apikey" => the normal OpenAI-compatible
    /// path (endpoint + key), byte-identical to before. "oauth-claude" => the engine talks DIRECTLY to
    /// Anthropic with the captured OAuth bearer (see AnthropicOAuthChatClientFactory), no endpoint/key
    /// needed. (Codex "oauth-codex" is a later milestone.)
    /// </summary>
    [JsonPropertyName("authType")]
    public string? AuthType { get; set; }
}