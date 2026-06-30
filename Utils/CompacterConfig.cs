using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// Compaction Agent Configuration
/// </summary>
public class CompacterConfig
{

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("modelOpts")]
    public ModelOpts? ModelOpts { get; set; }

    [JsonPropertyName("autoCompactTokenThreshold")]
    public int AutoCompactTokenThreshold { get; set; }

    /// <summary>
    /// Per-member context ceiling for persistent (warm-session) team members: when a member's
    /// accumulated session is estimated to exceed this many tokens, its history is summarized and
    /// reseeded (the same summarize-then-reseed mechanism the single-agent loop uses for
    /// <see cref="AutoCompactTokenThreshold"/>). Members get their own knob so they can be held
    /// tighter than the lead. 0 (default) falls back to a runtime default; only applies to teams
    /// whose members run with memberContext = "persistent".
    /// </summary>
    [JsonPropertyName("memberAutoCompactTokenThreshold")]
    public int MemberAutoCompactTokenThreshold { get; set; }

}