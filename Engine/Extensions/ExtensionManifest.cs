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

    /// <summary>TUI footer layout. Null = leave the built-in footer alone.</summary>
    [JsonPropertyName("footer")]
    public FooterManifest? Footer { get; set; }
}

/// <summary>The <c>footer</c> block of an extension manifest.</summary>
internal sealed class FooterManifest
{
    /// <summary>
    /// Segment ids in braces, in display order, e.g. <c>"{mode} {context} {model}"</c>. Only the
    /// placeholders matter: text between them is ignored and chips keep the standard separator.
    /// Segments not named are hidden, except <c>share</c>, which always shows.
    /// </summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }
}
