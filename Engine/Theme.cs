namespace MuxSwarm.Engine;

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
    string MdQuote,
    // Background shades (v0.12.1): the elevated card/panel fill, the compose-field band, the
    // diff line bands, and the fenced/inline code fills. Optional with defaults = the original
    // hardcoded dark shades, so a preset that omits them (and any external caller) is unchanged.
    string CardBg = "#1C2530",
    string InputBg = "#12161C",
    string DiffAddBg = "#16261C",
    string DiffDelBg = "#2A1A1C",
    string DiffHunkBg = "#16202C",
    string CodeBg = "#1E1E1E",
    string InlineCodeBg = "#2A2A2A")
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
        MdHeading: "#7AC0FF", MdCode: "#E3B341", MdLink: "#79C0FF", MdQuote: "#8B949E",
        CardBg: "#161B22", InputBg: "#0D1117", DiffAddBg: "#12261C", DiffDelBg: "#2D1213",
        DiffHunkBg: "#161F2C", CodeBg: "#161B22", InlineCodeBg: "#21262D");

    /// <summary>Darker, saturated palette for bright/light terminal backgrounds.</summary>
    public static Theme Light { get; } = new(
        Name: "light",
        Step: "#1A6FB0", Success: "#1E7E34", Warning: "#B8860B", Error: "#C0392B",
        Info: "#555555", Muted: "#777777", Accent: "#1A6FB0", Prompt: "#222222",
        Banner: "#1A6FB0", Agent: "#2C5F8A",
        MdHeading: "#1A6FB0", MdCode: "#9A6A00", MdLink: "#0A66C2", MdQuote: "#555555",
        CardBg: "#EAEEF2", InputBg: "#F2F4F7", DiffAddBg: "#E1F3E4", DiffDelBg: "#FBE4E4",
        DiffHunkBg: "#E4ECF5", CodeBg: "#ECECEC", InlineCodeBg: "#E2E2E2");

    /// <summary>No-color / accessibility theme: everything inherits the terminal foreground
    /// ("default"); structure is conveyed by layout + prefixes, not hue. Only success/warning/error
    /// keep minimal named tints so semantic signals remain distinguishable.</summary>
    public static Theme Mono { get; } = new(
        Name: "mono",
        Step: "default", Success: "default", Warning: "default", Error: "default",
        Info: "default", Muted: "grey", Accent: "default", Prompt: "default",
        Banner: "default", Agent: "default",
        MdHeading: "default", MdCode: "default", MdLink: "default", MdQuote: "grey",
        CardBg: "default", InputBg: "default", DiffAddBg: "default", DiffDelBg: "default",
        DiffHunkBg: "default", CodeBg: "default", InlineCodeBg: "default");

    /// <summary>Solarized (Ethan Schoonover) accent set.</summary>
    public static Theme Solarized { get; } = new(
        Name: "solarized",
        Step: "#268BD2", Success: "#859900", Warning: "#B58900", Error: "#DC322F",
        Info: "#93A1A1", Muted: "#657B83", Accent: "#2AA198", Prompt: "#EEE8D5",
        Banner: "#268BD2", Agent: "#6C71C4",
        MdHeading: "#268BD2", MdCode: "#B58900", MdLink: "#2AA198", MdQuote: "#657B83",
        CardBg: "#073642", InputBg: "#002B36", DiffAddBg: "#0B3A2E", DiffDelBg: "#3A1E22",
        DiffHunkBg: "#073642", CodeBg: "#002B36", InlineCodeBg: "#073642");

    /// <summary>Dracula palette.</summary>
    public static Theme Dracula { get; } = new(
        Name: "dracula",
        Step: "#8BE9FD", Success: "#50FA7B", Warning: "#F1FA8C", Error: "#FF5555",
        Info: "#BD93F9", Muted: "#6272A4", Accent: "#BD93F9", Prompt: "#F8F8F2",
        Banner: "#FF79C6", Agent: "#8BE9FD",
        MdHeading: "#BD93F9", MdCode: "#F1FA8C", MdLink: "#8BE9FD", MdQuote: "#6272A4",
        CardBg: "#282A36", InputBg: "#21222C", DiffAddBg: "#1E3A2A", DiffDelBg: "#3A2130",
        DiffHunkBg: "#2B2E3B", CodeBg: "#21222C", InlineCodeBg: "#343746");

    /// <summary>Gruvbox (warm, retro) palette.</summary>
    public static Theme Gruvbox { get; } = new(
        Name: "gruvbox",
        Step: "#83A598", Success: "#B8BB26", Warning: "#FABD2F", Error: "#FB4934",
        Info: "#A89984", Muted: "#928374", Accent: "#FE8019", Prompt: "#EBDBB2",
        Banner: "#FE8019", Agent: "#83A598",
        MdHeading: "#FABD2F", MdCode: "#B8BB26", MdLink: "#83A598", MdQuote: "#928374",
        CardBg: "#3C3836", InputBg: "#282828", DiffAddBg: "#323D1F", DiffDelBg: "#442A25",
        DiffHunkBg: "#33302B", CodeBg: "#282828", InlineCodeBg: "#3C3836");


    /// <summary>Catppuccin Mocha (flagship dark) - canonical palette v1.8.0.</summary>
    public static Theme CatppuccinMocha { get; } = new(
        Name: "catppuccin-mocha",
        Step: "#CBA6F7", Success: "#A6E3A1", Warning: "#F9E2AF", Error: "#F38BA8",
        Info: "#BAC2DE", Muted: "#6C7086", Accent: "#CBA6F7", Prompt: "#CDD6F4",
        Banner: "#CBA6F7", Agent: "#89B4FA",
        MdHeading: "#CBA6F7", MdCode: "#FAB387", MdLink: "#89DCEB", MdQuote: "#A6ADC8",
        CardBg: "#313244", InputBg: "#181825", DiffAddBg: "#28382E", DiffDelBg: "#3B2733",
        DiffHunkBg: "#2A2B3C", CodeBg: "#181825", InlineCodeBg: "#313244");

    /// <summary>Catppuccin Macchiato (dark).</summary>
    public static Theme CatppuccinMacchiato { get; } = new(
        Name: "catppuccin-macchiato",
        Step: "#C6A0F6", Success: "#A6DA95", Warning: "#EED49F", Error: "#ED8796",
        Info: "#B8C0E0", Muted: "#6E738D", Accent: "#C6A0F6", Prompt: "#CAD3F5",
        Banner: "#C6A0F6", Agent: "#8AADF4",
        MdHeading: "#C6A0F6", MdCode: "#F5A97F", MdLink: "#91D7E3", MdQuote: "#A5ADCB",
        CardBg: "#363A4F", InputBg: "#1E2030", DiffAddBg: "#2C3B32", DiffDelBg: "#3E2B36",
        DiffHunkBg: "#2E3145", CodeBg: "#1E2030", InlineCodeBg: "#363A4F");

    /// <summary>Catppuccin Latte (light).</summary>
    public static Theme CatppuccinLatte { get; } = new(
        Name: "catppuccin-latte",
        Step: "#8839EF", Success: "#40A02B", Warning: "#DF8E1D", Error: "#D20F39",
        Info: "#5C5F77", Muted: "#8C8FA1", Accent: "#8839EF", Prompt: "#4C4F69",
        Banner: "#8839EF", Agent: "#1E66F5",
        MdHeading: "#8839EF", MdCode: "#FE640B", MdLink: "#04A5E5", MdQuote: "#6C6F85",
        CardBg: "#E6E9EF", InputBg: "#EFF1F5", DiffAddBg: "#DDEBD8", DiffDelBg: "#F5D8DE",
        DiffHunkBg: "#DEE4F0", CodeBg: "#E6E9EF", InlineCodeBg: "#CCD0DA");

    /// <summary>Nord (arctic, bluish) - nord0-15.</summary>
    public static Theme Nord { get; } = new(
        Name: "nord",
        Step: "#88C0D0", Success: "#A3BE8C", Warning: "#EBCB8B", Error: "#BF616A",
        Info: "#81A1C1", Muted: "#4C566A", Accent: "#88C0D0", Prompt: "#D8DEE9",
        Banner: "#88C0D0", Agent: "#81A1C1",
        MdHeading: "#88C0D0", MdCode: "#D08770", MdLink: "#8FBCBB", MdQuote: "#4C566A",
        CardBg: "#3B4252", InputBg: "#2E3440", DiffAddBg: "#37414A", DiffDelBg: "#443C48",
        DiffHunkBg: "#394356", CodeBg: "#2E3440", InlineCodeBg: "#434C5E");

    /// <summary>Tokyo Night (night variant).</summary>
    public static Theme TokyoNight { get; } = new(
        Name: "tokyo-night",
        Step: "#7AA2F7", Success: "#9ECE6A", Warning: "#E0AF68", Error: "#F7768E",
        Info: "#A9B1D6", Muted: "#565F89", Accent: "#BB9AF7", Prompt: "#C0CAF5",
        Banner: "#BB9AF7", Agent: "#7DCFFF",
        MdHeading: "#BB9AF7", MdCode: "#FF9E64", MdLink: "#7DCFFF", MdQuote: "#565F89",
        CardBg: "#292E42", InputBg: "#16161E", DiffAddBg: "#20303B", DiffDelBg: "#37222C",
        DiffHunkBg: "#283457", CodeBg: "#16161E", InlineCodeBg: "#292E42");

    /// <summary>Tokyo Night Storm (lighter bg tier, same accents).</summary>
    public static Theme TokyoNightStorm { get; } = new(
        Name: "tokyo-storm",
        Step: "#7AA2F7", Success: "#9ECE6A", Warning: "#E0AF68", Error: "#F7768E",
        Info: "#A9B1D6", Muted: "#565F89", Accent: "#BB9AF7", Prompt: "#C0CAF5",
        Banner: "#BB9AF7", Agent: "#7DCFFF",
        MdHeading: "#BB9AF7", MdCode: "#FF9E64", MdLink: "#7DCFFF", MdQuote: "#565F89",
        CardBg: "#292E42", InputBg: "#1F2335", DiffAddBg: "#243B33", DiffDelBg: "#3B2735",
        DiffHunkBg: "#2E3C64", CodeBg: "#1F2335", InlineCodeBg: "#292E42");

    /// <summary>Rose Pine (main).</summary>
    public static Theme RosePine { get; } = new(
        Name: "rose-pine",
        Step: "#EBBCBA", Success: "#95B1AC", Warning: "#F6C177", Error: "#EB6F92",
        Info: "#908CAA", Muted: "#6E6A86", Accent: "#EBBCBA", Prompt: "#E0DEF4",
        Banner: "#EBBCBA", Agent: "#9CCFD8",
        MdHeading: "#C4A7E7", MdCode: "#F6C177", MdLink: "#9CCFD8", MdQuote: "#908CAA",
        CardBg: "#1F1D2E", InputBg: "#191724", DiffAddBg: "#233029", DiffDelBg: "#35222E",
        DiffHunkBg: "#26233A", CodeBg: "#191724", InlineCodeBg: "#26233A");

    /// <summary>Kanagawa (wave) - Hokusai-inspired inks.</summary>
    public static Theme Kanagawa { get; } = new(
        Name: "kanagawa",
        Step: "#7E9CD8", Success: "#98BB6C", Warning: "#E6C384", Error: "#E46876",
        Info: "#C8C093", Muted: "#727169", Accent: "#FFA066", Prompt: "#DCD7BA",
        Banner: "#FFA066", Agent: "#7FB4CA",
        MdHeading: "#957FB8", MdCode: "#C0A36E", MdLink: "#7AA89F", MdQuote: "#727169",
        CardBg: "#2A2A37", InputBg: "#16161D", DiffAddBg: "#2B3328", DiffDelBg: "#43242B",
        DiffHunkBg: "#223249", CodeBg: "#16161D", InlineCodeBg: "#2A2A37");

    /// <summary>Everforest (dark medium) - comfy greens.</summary>
    public static Theme Everforest { get; } = new(
        Name: "everforest",
        Step: "#A7C080", Success: "#A7C080", Warning: "#DBBC7F", Error: "#E67E80",
        Info: "#9DA9A0", Muted: "#859289", Accent: "#A7C080", Prompt: "#D3C6AA",
        Banner: "#A7C080", Agent: "#7FBBB3",
        MdHeading: "#A7C080", MdCode: "#E69875", MdLink: "#83C092", MdQuote: "#859289",
        CardBg: "#3D484D", InputBg: "#2D353B", DiffAddBg: "#425047", DiffDelBg: "#514045",
        DiffHunkBg: "#3A515D", CodeBg: "#2D353B", InlineCodeBg: "#475258");

    /// <summary>One Dark (Atom).</summary>
    public static Theme OneDark { get; } = new(
        Name: "one-dark",
        Step: "#61AFEF", Success: "#98C379", Warning: "#E5C07B", Error: "#E06C75",
        Info: "#828997", Muted: "#5C6370", Accent: "#61AFEF", Prompt: "#ABB2BF",
        Banner: "#61AFEF", Agent: "#56B6C2",
        MdHeading: "#C678DD", MdCode: "#D19A66", MdLink: "#61AFEF", MdQuote: "#5C6370",
        CardBg: "#21252B", InputBg: "#282C34", DiffAddBg: "#2B3328", DiffDelBg: "#3B282C",
        DiffHunkBg: "#2C313C", CodeBg: "#21252B", InlineCodeBg: "#2C313C");

    /// <summary>Monokai (classic).</summary>
    public static Theme Monokai { get; } = new(
        Name: "monokai",
        Step: "#66D9EF", Success: "#A6E22E", Warning: "#E6DB74", Error: "#F92672",
        Info: "#90908A", Muted: "#75715E", Accent: "#F92672", Prompt: "#F8F8F2",
        Banner: "#F92672", Agent: "#66D9EF",
        MdHeading: "#F92672", MdCode: "#E6DB74", MdLink: "#66D9EF", MdQuote: "#75715E",
        CardBg: "#1E1F1C", InputBg: "#272822", DiffAddBg: "#2E3B22", DiffDelBg: "#3D2228",
        DiffHunkBg: "#34352F", CodeBg: "#1E1F1C", InlineCodeBg: "#3E3D32");

    /// <summary>Ayu Mirage (soft dark).</summary>
    public static Theme AyuMirage { get; } = new(
        Name: "ayu-mirage",
        Step: "#FFCC66", Success: "#87D96C", Warning: "#FFD173", Error: "#F28779",
        Info: "#707A8C", Muted: "#707A8C", Accent: "#FFCC66", Prompt: "#CCCAC2",
        Banner: "#FFCC66", Agent: "#73D0FF",
        MdHeading: "#FFCC66", MdCode: "#FFAD66", MdLink: "#5CCFE6", MdQuote: "#707A8C",
        CardBg: "#282E3B", InputBg: "#242936", DiffAddBg: "#273A2B", DiffDelBg: "#3D2A30",
        DiffHunkBg: "#1F2430", CodeBg: "#1F2430", InlineCodeBg: "#282E3B");

    /// <summary>SynthWave '84 (Robb Owen) - neon retrowave.</summary>
    public static Theme Synthwave { get; } = new(
        Name: "synthwave",
        Step: "#36F9F6", Success: "#72F1B8", Warning: "#FEDE5D", Error: "#FE4450",
        Info: "#B6B1B1", Muted: "#848BBD", Accent: "#FF7EDB", Prompt: "#FFFFFF",
        Banner: "#FF7EDB", Agent: "#36F9F6",
        MdHeading: "#FF7EDB", MdCode: "#FF8B39", MdLink: "#36F9F6", MdQuote: "#848BBD",
        CardBg: "#241B2F", InputBg: "#262335", DiffAddBg: "#1E3A32", DiffDelBg: "#3D2130",
        DiffHunkBg: "#2A2139", CodeBg: "#241B2F", InlineCodeBg: "#2A2139");

    /// <summary>Rose Pine Dawn (light).</summary>
    public static Theme RosePineDawn { get; } = new(
        Name: "rose-dawn",
        Step: "#D7827E", Success: "#6D8F89", Warning: "#EA9D34", Error: "#B4637A",
        Info: "#797593", Muted: "#9893A5", Accent: "#D7827E", Prompt: "#464261",
        Banner: "#D7827E", Agent: "#56949F",
        MdHeading: "#907AA9", MdCode: "#EA9D34", MdLink: "#56949F", MdQuote: "#797593",
        CardBg: "#FFFAF3", InputBg: "#FAF4ED", DiffAddBg: "#E4EDE7", DiffDelBg: "#F6E0E4",
        DiffHunkBg: "#F2E9E1", CodeBg: "#FFFAF3", InlineCodeBg: "#F2E9E1");

    /// <summary>All built-in presets, in display order (default first).</summary>
    public static readonly IReadOnlyList<Theme> Presets =
        new[] { Default, Dark, Light, Mono, Solarized, Dracula, Gruvbox,
            CatppuccinMocha, CatppuccinMacchiato, CatppuccinLatte, Nord,
            TokyoNight, TokyoNightStorm, RosePine, RosePineDawn, Kanagawa,
            Everforest, OneDark, Monokai, AyuMirage, Synthwave };

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
