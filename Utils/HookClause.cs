using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class HookClause
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    [JsonPropertyName("tool")]
    public string? Tool { get; set; }
}