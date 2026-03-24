using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class ReasoningConfig
{
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }
    
    [JsonPropertyName("output")]
    public string? Output { get; set; }
}