using System.Text.Json.Serialization;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// The <c>extensions</c> block in config.json. Extensions are folders holding an
/// <c>extension.json</c> manifest that customize Mux (themes today; more contribution types
/// later). Off by default: when <see cref="Enabled"/> is false nothing is scanned and Mux
/// behaves exactly as it does without the block. Toggle with <c>/set extensions.enabled on</c>
/// and apply with <c>/refresh</c>.
/// </summary>
public class ExtensionsConfig
{
    /// <summary>Master gate. False (default) = no extension is loaded.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Extra directories to scan, in addition to <c>&lt;install&gt;/Extensions</c>. Each direct
    /// child folder containing an <c>extension.json</c> is one extension. Relative paths resolve
    /// against the install directory.
    /// </summary>
    [JsonPropertyName("dirs")]
    public List<string> Dirs { get; set; } = [];
}
