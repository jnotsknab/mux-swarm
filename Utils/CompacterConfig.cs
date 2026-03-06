using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// Compaction Agent Configuration
/// </summary>
public class CompacterConfig
{

    [JsonPropertyName("model")]
    public string? Model { get; set; }
    
    [JsonPropertyName("autoCompactTokenThreshold")]
    public int AutoCompactTokenThreshold { get; set; }
    
}