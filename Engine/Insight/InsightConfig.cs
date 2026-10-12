using System.Text.Json.Serialization;

namespace MuxSwarm.Engine.Insight;

/// <summary>
/// <c>insight</c> in config.json: how much the lead agent is told about its own context window.
/// Set live with <c>/insight [off|basic|full|max]</c> (persisted here).
/// </summary>
public class InsightConfig
{
    /// <summary>
    /// <c>off</c> (default) | <c>basic</c> (one context line on each model call) | <c>full</c> (basic plus
    /// growth, tool share and compaction count) | <c>max</c> (full plus the <c>context_map</c> /
    /// <c>context_drop</c> tools). Unknown values are treated as <c>off</c>.
    /// </summary>
    [JsonPropertyName("level")]
    public string Level { get; set; } = "off";
}

/// <summary>Insight detail levels, ordered: each level includes everything below it.</summary>
internal enum InsightLevel { Off, Basic, Full, Max }

/// <summary>Parsing and naming for <see cref="InsightLevel"/>.</summary>
internal static class InsightLevels
{
    /// <summary>All level names, lowest first.</summary>
    internal static readonly string[] Names = ["off", "basic", "full", "max"];

    /// <summary>Parse a level name (case-insensitive). Returns false for anything else.</summary>
    internal static bool TryParse(string? value, out InsightLevel level)
    {
        int i = Array.FindIndex(Names, n => n.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
        level = i < 0 ? InsightLevel.Off : (InsightLevel)i;
        return i >= 0;
    }

    /// <summary>The configured level from <see cref="App.Config"/>; unknown values are Off.</summary>
    internal static InsightLevel Current =>
        TryParse(App.Config?.Insight?.Level, out var level) ? level : InsightLevel.Off;

    /// <summary>Lower-case name of <paramref name="level"/>.</summary>
    internal static string Name(InsightLevel level) => Names[(int)level];
}
