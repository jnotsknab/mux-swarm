using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MuxSwarm.Utils;

/// <summary>
/// Outbound webhook dispatcher (Mux -> external). Subscribes to the structured event stream at the
/// <see cref="MuxConsole"/> emit chokepoint and POSTs a signed JSON envelope to every configured
/// <see cref="WebhookConfig"/> whose event allowlist matches.
///
/// Design notes:
/// - <b>Inert by default.</b> With no configured sinks <see cref="IsActive"/> is false and
///   <see cref="Notify"/> returns immediately, so the hot emit path is byte-identical to before.
/// - <b>Fire-and-forget.</b> Delivery runs on a background task with bounded retry+backoff; a slow
///   or dead receiver never blocks the agent turn or the console writer.
/// - <b>Signed.</b> When a sink has a secret, each POST carries GitHub-style
///   <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> HMAC over the raw request body.
/// </summary>
public static class WebhookSink
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static List<WebhookConfig> _sinks = [];

    /// <summary>Max delivery attempts per event (1 initial + retries).</summary>
    private const int MaxAttempts = 3;

    /// <summary>True when at least one sink with a URL and a non-empty allowlist is configured.</summary>
    public static bool IsActive { get; private set; }

    /// <summary>
    /// Install the configured outbound sinks. Filters out entries missing a URL or an event
    /// allowlist so <see cref="IsActive"/> reflects real, usable sinks only. Safe to call once at
    /// startup; passing an empty/absent list leaves the subsystem inert.
    /// </summary>
    public static void Start(List<WebhookConfig>? sinks)
    {
        _sinks = (sinks ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Url) && s.Events.Count > 0)
            .ToList();
        IsActive = _sinks.Count > 0;
        if (IsActive)
            MuxConsole.WriteInfo($"[Webhooks] {_sinks.Count} outbound sink(s) armed.");
    }

    /// <summary>
    /// Notify all matching sinks of a structured event. Called from the <see cref="MuxConsole"/>
    /// emit chokepoint. Non-blocking and never throws: matching sinks are dispatched on background
    /// tasks. No-op when the subsystem is inert.
    /// </summary>
    public static void Notify(string type, IReadOnlyDictionary<string, object?>? fields)
    {
        if (!IsActive) return;

        List<WebhookConfig>? matched = null;
        foreach (var sink in _sinks)
        {
            if (sink.Events.Contains("*") ||
                sink.Events.Any(e => string.Equals(e, type, StringComparison.OrdinalIgnoreCase)))
            {
                (matched ??= []).Add(sink);
            }
        }
        if (matched is null) return;

        // Snapshot the fields now (the caller's dictionary may be reused/mutated after we return).
        var envelope = new Dictionary<string, object?>
        {
            ["event"] = type,
            ["timestamp"] = DateTimeOffset.UtcNow,
        };
        if (fields is not null)
            foreach (var kvp in fields)
                envelope[kvp.Key] = kvp.Value;

        string body;
        try { body = JsonSerializer.Serialize(envelope, JsonOpts); }
        catch { return; } // non-serializable payload: drop rather than crash the emit path

        foreach (var sink in matched)
            _ = Task.Run(() => DeliverAsync(sink, body));
    }

    private static async Task DeliverAsync(WebhookConfig sink, string body)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, sink.Url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrEmpty(sink.Secret))
                    req.Headers.TryAddWithoutValidation("X-Hub-Signature-256", Sign(body, sink.Secret));

                if (sink.Headers is not null)
                    foreach (var (k, v) in sink.Headers)
                        req.Headers.TryAddWithoutValidation(k, v);

                using var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                    return;

                // 4xx (except 408/429) is a client error: retrying won't help.
                var code = (int)resp.StatusCode;
                if (code is >= 400 and < 500 && code != 408 && code != 429)
                {
                    MuxConsole.WriteMuted($"[Webhooks] {sink.Url} rejected event ({code}); not retrying.");
                    return;
                }
            }
            catch
            {
                // network error / timeout: fall through to backoff + retry
            }

            if (attempt < MaxAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)));
        }
        MuxConsole.WriteMuted($"[Webhooks] {sink.Url} delivery failed after {MaxAttempts} attempts.");
    }

    /// <summary>GitHub-style signature: <c>sha256=&lt;lowercase-hex HMAC of the raw body&gt;</c>.</summary>
    private static string Sign(string body, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(body);
        var hash = HMACSHA256.HashData(key, data);
        return "sha256=" + Convert.ToHexStringLower(hash);
    }
}
