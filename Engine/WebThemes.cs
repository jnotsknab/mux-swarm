using System.Globalization;

namespace MuxSwarm.Engine;

/// <summary>
/// Maps TUI themes onto the web UI's CSS variables, so <c>GET /api/themes</c> can offer extension
/// themes in the web theme picker. Colors that are not <c>#RRGGBB</c> (named colors, <c>default</c>)
/// fall back to the web UI's default dark palette.
/// </summary>
internal static class WebThemes
{
    // The web UI's "zinc" palette: fallback for roles without a hex color.
    private const string Bg = "#09090b", Card = "#18181b", Input = "#27272a", Line = "#3f3f46", Text = "#fafafa",
        Muted = "#a1a1aa", Blue = "#3b82f6", Green = "#10b981", Amber = "#f59e0b", Red = "#ef4444", Agent = "#8b5cf6", Accent = "#64b4dc";

    /// <summary>One web theme: <paramref name="Id"/> is <c>ext-{name}</c> so it never collides with a built-in web theme.</summary>
    internal sealed record Item(string Id, string Name, string Dot, bool IsLight, IReadOnlyDictionary<string, string> Vars);

    /// <summary>The web form of each theme in <paramref name="themes"/>.</summary>
    internal static List<Item> Build(IEnumerable<Theme> themes) => themes.Select(ToWeb).ToList();

    private static Item ToWeb(Theme t)
    {
        string bg = Hex(t.InputBg, Bg), card = Hex(t.CardBg, Card), line = Hex(t.Border, Line);
        string accent = Hex(t.Accent, Accent), green = Hex(t.Success, Green), blue = Hex(t.MdLink, Blue),
            amber = Hex(t.Warning, Amber), red = Hex(t.Error, Red), agent = Hex(t.Agent, Agent);
        var vars = new Dictionary<string, string>
        {
            ["--bg"] = bg,
            ["--bg-card"] = Rgba(card, 0.6),
            ["--bg-input"] = Rgba(Hex(t.CodeBg, Input), 0.5),
            ["--border"] = Rgba(line, 0.5),
            ["--text"] = Hex(t.Prompt, Text),
            ["--muted"] = Hex(t.Muted, Muted),
            ["--blue"] = blue, ["--green"] = green, ["--amber"] = amber, ["--red"] = red, ["--agent"] = agent, ["--accent"] = accent,
            ["--accent-rgb"] = Rgb(accent), ["--green-rgb"] = Rgb(green), ["--blue-rgb"] = Rgb(blue),
            ["--amber-rgb"] = Rgb(amber), ["--red-rgb"] = Rgb(red), ["--agent-rgb"] = Rgb(agent),
        };
        return new Item("ext-" + t.Name, t.Name, bg, Luminance(bg) > 0.5, vars);
    }

    private static string Hex(string? value, string fallback) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit) ? value.ToLowerInvariant() : fallback;

    private static (int R, int G, int B) Parts(string hex) => (
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));

    private static string Rgb(string hex) { var (r, g, b) = Parts(hex); return $"{r}, {g}, {b}"; }

    private static string Rgba(string hex, double alpha) =>
        $"rgba({Rgb(hex)}, {alpha.ToString(CultureInfo.InvariantCulture)})";

    private static double Luminance(string hex) { var (r, g, b) = Parts(hex); return (0.299 * r + 0.587 * g + 0.114 * b) / 255; }
}
