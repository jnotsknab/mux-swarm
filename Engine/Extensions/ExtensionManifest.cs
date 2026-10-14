using System.Text.Json.Serialization;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// The <c>extension.json</c> manifest at the root of an extension folder. Everything an
/// extension contributes is declared here, so Mux can list and validate it without running code.
/// </summary>
internal sealed class ExtensionManifest
{
    /// <summary>
    /// Unique id: lowercase letters, digits and hyphens, 1-32 chars, starting with a letter or
    /// digit. Defaults to the folder name when omitted.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Free-form version string shown by <c>/extensions</c>.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>One-line description shown by <c>/extensions</c>.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// TUI color themes. Each entry needs a <c>name</c>, may name a built-in <c>base</c> preset
    /// (default <c>default</c>), and overrides any theme color role by its camelCase key
    /// (<c>accent</c>, <c>success</c>, <c>cardBg</c>, ...). Values are Spectre color tokens:
    /// <c>#RRGGBB</c>, a named color, or <c>default</c>.
    /// </summary>
    [JsonPropertyName("themes")]
    public List<Dictionary<string, string>> Themes { get; set; } = [];

    /// <summary>
    /// Thinking-indicator spinner frames, replacing the built-in ones: 1-64 frames, each 1-8 terminal
    /// cells (any text: multi-character, emoji, ZWJ sequences). Shorthand for <c>thinking.frames</c>.
    /// </summary>
    [JsonPropertyName("spinner")]
    public List<string>? Spinner { get; set; }

    /// <summary>Words cycled by the thinking indicator while the agent reasons ("Thinking", "Pondering", ...). Replaces the built-in list.</summary>
    [JsonPropertyName("quips")]
    public List<string>? Quips { get; set; }

    /// <summary>
    /// Tool-call action labels keyed by verb stem (the first word of a tool name after any server
    /// prefix, e.g. <c>read</c>, <c>search</c>). Merged over the built-ins; the extension wins per key.
    /// </summary>
    [JsonPropertyName("toolLabels")]
    public Dictionary<string, string>? ToolLabels { get; set; }

    /// <summary>Startup splash lines. Replaces the whole built-in pool (quotes, facts, tips, taglines).</summary>
    [JsonPropertyName("splash")]
    public List<string>? Splash { get; set; }

    /// <summary>The whole thinking-indicator animation (frames, speed, quips). Null = built-in.</summary>
    [JsonPropertyName("thinking")]
    public ThinkingManifest? Thinking { get; set; }
}

/// <summary>
/// The <c>thinking</c> block: full control of the agent's thinking indicator. Every key is optional;
/// <c>frames</c> and <c>quips</c> take precedence over the top-level <c>spinner</c> / <c>quips</c>.
/// </summary>
internal sealed class ThinkingManifest
{
    /// <summary>Spinner frames: 1-64, each 1-8 terminal cells. Narrower frames are padded to the widest.</summary>
    [JsonPropertyName("frames")]
    public List<string>? Frames { get; set; }

    /// <summary>Milliseconds per frame, 40-1000 (default 80).</summary>
    [JsonPropertyName("intervalMs")]
    public int? IntervalMs { get; set; }

    /// <summary>Words cycled while the agent reasons (1-200, max 80 chars each).</summary>
    [JsonPropertyName("quips")]
    public List<string>? Quips { get; set; }

    /// <summary>Milliseconds each quip shows, 500-60000 (default 2000).</summary>
    [JsonPropertyName("quipIntervalMs")]
    public int? QuipIntervalMs { get; set; }

    /// <summary>Shuffle the quips per user (default true); false cycles them in order.</summary>
    [JsonPropertyName("shuffle")]
    public bool? Shuffle { get; set; }

    /// <summary>Text after the quip (default <c>...</c>, max 8 chars, may be empty).</summary>
    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }
}
