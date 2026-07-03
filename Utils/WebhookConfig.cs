using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// One outbound webhook sink (Mux -> external). Configured under swarm.json <c>webhooks[]</c>.
/// Additive + default-empty: with no entries the whole subsystem is inert and every emit path is
/// byte-identical to prior behaviour.
/// </summary>
public class WebhookConfig
{
    /// <summary>Target URL that receives an HTTP POST with a JSON envelope per matched event.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Event-type allowlist (e.g. <c>task_complete</c>, <c>delegation</c>, <c>error</c>,
    /// <c>hook_fired</c>). Matches the <c>type</c> of a structured emit. <c>"*"</c> subscribes to
    /// every event (including high-frequency ones like <c>stream</c> - use with care). Empty = the
    /// sink is disabled.
    /// </summary>
    [JsonPropertyName("events")]
    public List<string> Events { get; set; } = [];

    /// <summary>
    /// Optional HMAC-SHA256 secret. When set, each POST carries
    /// <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> over the raw request body (GitHub-style) so the
    /// receiver can verify authenticity. Null/empty = unsigned.
    /// </summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    /// <summary>Optional static headers added to every request (auth tokens, routing, etc.).</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}
