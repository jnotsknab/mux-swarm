using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class DaemonConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("triggers")]
    public List<DaemonTrigger> Triggers { get; set; } = [];
}