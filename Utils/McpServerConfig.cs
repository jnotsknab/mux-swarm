using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class McpServerConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "stdio"; // "stdio" or "http"

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string?>? Env { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}