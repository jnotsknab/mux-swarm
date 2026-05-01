using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// Default Vision Model Configuration
/// </summary>
public class VisionConfig
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("modelOpts")]
    public ModelOpts? ModelOpts { get; set; }
}