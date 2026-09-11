using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>Portable reasoning settings plus explicit OpenAI-compatible max/custom effort values.</summary>
public class ReasoningConfig
{
    /// <summary>none, low, med/medium, high, xhigh/extra_high, max, or custom &lt;raw-value&gt;.
    /// Max/custom are sent literally and are not silently downgraded on rejection.</summary>
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }

    /// <summary>Requested reasoning visibility: none, summary, or full; support depends on the provider.</summary>
    [JsonPropertyName("output")]
    public string? Output { get; set; }
}