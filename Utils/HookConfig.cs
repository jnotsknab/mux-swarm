using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;


[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HookMode
{
    Async,
    Blocking
}

public class HookConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public HookMode Mode { get; set; } = HookMode.Async;

    [JsonPropertyName("persistent")]
    public bool Persistent { get; set; } = false;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("when")]
    public List<HookClause> When { get; set; } = [];

    /// <summary>
    /// Only applies to Blocking mode. Defaults to 30s.
    /// </summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;
}