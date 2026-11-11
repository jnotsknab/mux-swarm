using System.Text.Json.Serialization;

namespace MuxSwarm.Engine;


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

    /// <summary>Command to run. Without <see cref="Args"/> it is split once at the first space into
    /// executable + argument string (legacy form). With <see cref="Args"/> it is the executable only.</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>Optional explicit argument list passed verbatim (no splitting or re-quoting), so paths
    /// with spaces need no quotes, e.g. <c>"command": "python", "args": ["C:/My Hooks/notify.py"]</c>.
    /// Omitted = legacy split of <see cref="Command"/>.</summary>
    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Args { get; set; }

    [JsonPropertyName("when")]
    public HookClause When { get; set; } = new();

    /// <summary>
    /// Only applies to Blocking mode. Defaults to 30s.
    /// </summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;
}