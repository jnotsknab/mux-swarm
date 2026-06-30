using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// A named TUI color theme: the palette of role -> color used by <see cref="MuxConsole"/> for all
/// rendered chrome (banner/accent, status step, success/warning/error, info/muted, prompt text, the
/// agent name color) and for rendered-markdown styling. Colors are Spectre.Console markup color
/// tokens (a "#RRGGBB" hex, a named console color like "grey"/"white", or "default" to inherit the
/// terminal's own foreground).
///
/// Themes are selected with <c>/theme &lt;name&gt;</c> (or during <c>/setup</c>) and persisted to
/// <c>config.json</c> under <c>console.theme</c>. The DEFAULT theme reproduces the exact pre-theme
/// hardcoded palette, so an absent / unset theme is byte-identical to prior behaviour.
/// </summary>
public sealed record Theme(
    string Name,
    string Step,
    string Success,
    string Warning,
    string Error,
    string Info,
    string Muted,
    string Accent,
    string Prompt,
    string Banner,
    string Agent,
    string MdHeading,
    string MdCode,
    string MdLink,
    string MdQuote)
{
    // Backing field for Active. Left null at static init so it does NOT depend on the declaration
    // order of the Default property (a "= Default" initializer here would capture Default's value
    // BEFORE Default's own initializer ran -> null). Active resolves to Default until Set/Apply.
    private static Theme? _active;

    /// <summary>
    /// The active theme. Defaults to <see cref="Default"/>; swapped at startup from config and at
    /// runtime by <c>/theme</c>. Never null.
    /// </summary>
    public static Theme Active => _active ?? Default;

    /// <summary>The original hardcoded palette (cyan accent on a neutral grey scale).</summary>
    public static Theme Default { get; } = new(
        Name: "default",
        Step: "#64B4DC", Success: "#78C88C", Warning: "#D4A054", Error: "#D46C6C",
        Info: "#909090", Muted: "#787878", Accent: "#64B4DC", Prompt: "#B0B0B0",
        Banner: "#64B4DC", Agent: "#8FB8D4",
        MdHeading: "#64B4DC", MdCode: "#C8A05A", MdLink: "#6CA0DC", MdQuote: "#909090");

    /// <summary>High-contrast cool palette tuned for dark terminals.</summary>
    public static Theme Dark { get; } = new(
        Name: "dark",
        Step: "#7AC0FF", Success: "#7EE787", Warning: "#E3B341", Error: "#FF7B72",
        Info: "#B0B6BE", Muted: "#8B949E", Accent: "#7AC0FF", Prompt: "#E6EDF3",
        Banner: "#7AC0FF", Agent: "#A5D6FF",
        MdHeading: "#7AC0FF", MdCode: "#E3B341", MdLink: "#79C0FF", MdQuote: "#8B949E");

    /// <summary>Darker, saturated palette for bright/light terminal backgrounds.</summary>
    public static Theme Light { get; } = new(
        Name: "light",
        Step: "#1A6FB0", Success: "#1E7E34", Warning: "#B8860B", Error: "#C0392B",
        Info: "#555555", Muted: "#777777", Accent: "#1A6FB0", Prompt: "#222222",
        Banner: "#1A6FB0", Agent: "#2C5F8A",
        MdHeading: "#1A6FB0", MdCode: "#9A6A00", MdLink: "#0A66C2", MdQuote: "#555555");

    /// <summary>No-color / accessibility theme: everything inherits the terminal foreground
    /// ("default"); structure is conveyed by layout + prefixes, not hue. Only success/warning/error
    /// keep minimal named tints so semantic signals remain distinguishable.</summary>
    public static Theme Mono { get; } = new(
        Name: "mono",
        Step: "default", Success: "default", Warning: "default", Error: "default",
        Info: "default", Muted: "grey", Accent: "default", Prompt: "default",
        Banner: "default", Agent: "default",
        MdHeading: "default", MdCode: "default", MdLink: "default", MdQuote: "grey");

    /// <summary>Solarized (Ethan Schoonover) accent set.</summary>
    public static Theme Solarized { get; } = new(
        Name: "solarized",
        Step: "#268BD2", Success: "#859900", Warning: "#B58900", Error: "#DC322F",
        Info: "#93A1A1", Muted: "#657B83", Accent: "#2AA198", Prompt: "#EEE8D5",
        Banner: "#268BD2", Agent: "#6C71C4",
        MdHeading: "#268BD2", MdCode: "#B58900", MdLink: "#2AA198", MdQuote: "#657B83");

    /// <summary>Dracula palette.</summary>
    public static Theme Dracula { get; } = new(
        Name: "dracula",
        Step: "#8BE9FD", Success: "#50FA7B", Warning: "#F1FA8C", Error: "#FF5555",
        Info: "#BD93F9", Muted: "#6272A4", Accent: "#BD93F9", Prompt: "#F8F8F2",
        Banner: "#FF79C6", Agent: "#8BE9FD",
        MdHeading: "#BD93F9", MdCode: "#F1FA8C", MdLink: "#8BE9FD", MdQuote: "#6272A4");

    /// <summary>Gruvbox (warm, retro) palette.</summary>
    public static Theme Gruvbox { get; } = new(
        Name: "gruvbox",
        Step: "#83A598", Success: "#B8BB26", Warning: "#FABD2F", Error: "#FB4934",
        Info: "#A89984", Muted: "#928374", Accent: "#FE8019", Prompt: "#EBDBB2",
        Banner: "#FE8019", Agent: "#83A598",
        MdHeading: "#FABD2F", MdCode: "#B8BB26", MdLink: "#83A598", MdQuote: "#928374");

    /// <summary>All built-in presets, in display order (default first).</summary>
    public static readonly IReadOnlyList<Theme> Presets =
        new[] { Default, Dark, Light, Mono, Solarized, Dracula, Gruvbox };

    /// <summary>The preset names, for help text / validation.</summary>
    public static IReadOnlyList<string> Names => Presets.Select(t => t.Name).ToList();

    /// <summary>Look up a preset by case-insensitive name. Null when unknown.</summary>
    public static Theme? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        foreach (var t in Presets)
            if (string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    /// <summary>
    /// Set the active theme by name (no-op + false when unknown, leaving the current theme intact).
    /// Does NOT persist; the caller writes config.json when the change is durable.
    /// </summary>
    public static bool Apply(string? name)
    {
        if (Find(name) is not { } t) return false;
        _active = t;
        return true;
    }

    /// <summary>Set the active theme instance directly (startup wiring from config).</summary>
    public static void Set(Theme theme) => _active = theme ?? Default;
}
