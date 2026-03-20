using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class HookEvent
{
    [JsonPropertyName("event")] 
    public string Event { get; set; } = string.Empty;
    
    [JsonPropertyName("agent")]
    public string? Agent { get; set; }
    
    [JsonPropertyName("tool")]
    public string? Tool { get; set; }
    
    [JsonPropertyName("args")]
    public string? Args { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
    
    [JsonPropertyName("goalId")]
    public string? GoalId { get; set; }
    
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }
    
}