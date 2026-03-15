using System.Text.Json.Serialization;

namespace MuxSwarm.State;

public class Workflow
{   
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("steps")]
    public List<string> Steps { get; set; } = new();
    
}