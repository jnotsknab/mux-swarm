using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class DaemonTrigger
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>"watch", "cron", "status", "bridge", or "webhook"</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";


    /// <summary>Glob path for file watch triggers (supports {file} substitution in goal).</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Raw command for process based daemon triggers</summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>Standard 5-field cron expression (minute hour day month weekday).</summary>
    [JsonPropertyName("schedule")]
    public string? Schedule { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("args")]
    public string? Args { get; set; }

    /// <summary>
    /// What to check. Prefixes:
    ///   "http://..." or "https://..." -- HTTP HEAD request
    ///   "process:name"               -- check process by name
    ///   "tcp:host:port"              -- TCP connect check
    /// </summary>
    [JsonPropertyName("check")]
    public string? Check { get; set; }

    /// <summary>If true, attempt to restart the checked resource on failure.</summary>
    [JsonPropertyName("restart")]
    public bool Restart { get; set; }


    /// <summary>Goal text for watch/cron triggers. Supports {file}, {timestamp}, {id}.</summary>
    [JsonPropertyName("goal")]
    public string? Goal { get; set; }

    /// <summary>Orchestration mode: "agent", "swarm", "pswarm".</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "agent";

    /// <summary>Minimum seconds between firings (watch debounce, status poll interval).</summary>
    [JsonPropertyName("interval")]
    public uint Interval { get; set; } = 30;

    /// <summary>Alias for interval on watch triggers for readability.</summary>
    [JsonPropertyName("cooldown")]
    public uint Cooldown { get; set; }

    /// <summary>Optional agent name override for single-agent mode goals.</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    /// <summary>Max consecutive status check failures before alerting (0 = alert every time).</summary>
    [JsonPropertyName("failThreshold")]
    public int FailThreshold { get; set; } = 3;

    /// <summary>
    /// HMAC-SHA256 shared secret for <c>webhook</c> triggers. When set, an inbound
    /// <c>POST /api/hook/{id}</c> must carry a valid <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>
    /// over the raw request body (GitHub-style) or it is rejected 401. This is the webhook's auth;
    /// unsigned hooks fall back to the runtime bearer gate. Null/empty = no HMAC.
    /// </summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    /// <summary>
    /// Max bytes of the inbound webhook body forwarded into the goal template (prompt-injection
    /// surface: the body is untrusted). Bodies over the cap are truncated. Default 8 KiB.
    /// </summary>
    [JsonPropertyName("payloadLimit")]
    public int PayloadLimit { get; set; } = 8192;

    /// <summary>Effective interval: Cooldown if set, otherwise Interval.</summary>
    [JsonIgnore]
    public uint EffectiveInterval => Cooldown > 0 ? Cooldown : Interval;
}