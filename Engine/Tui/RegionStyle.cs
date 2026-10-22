using System.Reflection;
using System.Text.Json;

namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Per-region TUI styles, the terminal counterpart of the web UI's extension CSS. Each property is a
/// <c>region.name</c> key (e.g. <c>notice.okMark</c>, <c>reasoning.fg</c>) with a built-in default that
/// reproduces the stock look exactly. Extensions override properties through <c>tui.regions</c> in
/// extension.json. Colours are a theme role (<c>accent</c>, <c>muted</c>, ...; resolved at paint time, so
/// they follow <c>/theme</c>) or a literal colour. The override map is swapped whole, never mutated.
/// </summary>
internal static class RegionStyle
{
    /// <summary>What a property holds: a colour, a glyph of bounded terminal width, or an on/off flag.</summary>
    internal enum Kind { Color, Glyph, Flag }

    /// <summary>One styleable property: its kind, built-in value and (glyphs) allowed width in cells.</summary>
    internal sealed record Prop(Kind Kind, string Default, int MinCells = 1, int MaxCells = 1);

    private static Prop C(string d) => new(Kind.Color, d);
    private static Prop G(string d, int max = 1) => new(Kind.Glyph, d, 1, max);
    private static Prop T(string d, int min, int max) => new(Kind.Glyph, d, min, max);   // free text of bounded width
    private static Prop F(bool d) => new(Kind.Flag, d ? "true" : "false");

    /// <summary>Every styleable property, keyed <c>region.name</c> (case-insensitive).</summary>
    internal static readonly IReadOnlyDictionary<string, Prop> Props = new Dictionary<string, Prop>(StringComparer.OrdinalIgnoreCase)
    {
        ["notice.okMark"] = G("\u2713", 2), ["notice.warnMark"] = G("!", 2), ["notice.errorMark"] = G("x", 2),
        ["notice.ok"] = C("success"), ["notice.warn"] = C("warning"), ["notice.error"] = C("error"), ["notice.info"] = C("info"),
        ["header.mark"] = G("\u2503", 2), ["header.accent"] = C("accent"), ["header.show"] = F(true),
        ["userMessage.bar"] = G("\u258e"), ["userMessage.barFg"] = C("accent"), ["userMessage.fg"] = C("text"),
        ["agentHeader.mark"] = G("\u25b8", 2), ["agentHeader.rule"] = G("\u2500"), ["agentHeader.fg"] = C("agent"), ["agentHeader.ruleFg"] = C("#3A3A3A"),
        ["answer.lead"] = G("\u25cf"), ["answer.leadFg"] = C("muted"),
        ["reasoning.fg"] = C("grey"), ["reasoning.italic"] = F(true),
        ["toast.mark"] = G("\u25c6"), ["toast.markFg"] = C("accent"), ["toast.fg"] = C("muted"),
        ["divider.glyph"] = G("\u2502"), ["divider.fg"] = C("muted"), ["divider.activeFg"] = C("accent"),
        ["scrollbar.thumb"] = G("\u2588"), ["scrollbar.track"] = G("\u2502"), ["scrollbar.thumbFg"] = C("accent"), ["scrollbar.trackFg"] = C("muted"),
        ["toolCard.dot"] = G("\u25cf"), ["toolCard.ok"] = G("\u2713"), ["toolCard.error"] = G("\u2717"), ["toolCard.result"] = G("\u23bf"),
        ["toolCard.rail"] = G("\u2502"), ["toolCard.pendingFg"] = C("warning"), ["toolCard.okFg"] = C("success"), ["toolCard.errorFg"] = C("error"),
        ["toolCard.labelFg"] = C("accent"), ["toolCard.railFg"] = C("#3A3A3A"), ["toolCard.bg"] = C("cardBg"),
        ["diff.rail"] = G("\u2502"), ["diff.railFg"] = C("#3A3A3A"), ["diff.addFg"] = C("success"), ["diff.delFg"] = C("error"), ["diff.ctxFg"] = C("#A0A0A0"),
        ["diff.addBg"] = C("diffAddBg"), ["diff.delBg"] = C("diffDelBg"), ["diff.hunkBg"] = C("diffHunkBg"), ["diff.gutterFg"] = C("#5A6675"),
        ["delegation.arrow"] = G("\u2192"), ["delegation.branch"] = G("\u2514"), ["delegation.mark"] = G("\u25b8"), ["delegation.fg"] = C("accent"),
        ["delegation.done"] = G("\u2713"), ["delegation.failed"] = G("\u2717"),
        ["thinking.fg"] = C("#7AA2C0"),
        ["footer.separator"] = G("\u00b7", 3), ["footer.separatorFg"] = C("#5A5A5A"), ["footer.rule"] = G("\u2500"), ["footer.ruleFg"] = C("#3A3A3A"), ["footer.ruleShow"] = F(true),
        ["input.prompt"] = G("\u203a", 2), ["input.promptFg"] = C("accent"), ["input.continuation"] = G("\u2502"), ["input.continuationFg"] = C("#5A5A5A"),
        ["input.placeholder"] = T("type a message, or / for commands\u2026", 0, 60), ["input.placeholderFg"] = C("#5A5A5A"),
        ["input.fg"] = C("text"), ["input.cursorFg"] = C("black"), ["input.cursorBg"] = C("#E0E0E0"), ["input.rail"] = G("\u2502"), ["input.railFg"] = C("accent"),
    };

    /// <summary>Region names that have at least one property.</summary>
    internal static IEnumerable<string> Regions => Props.Keys.Select(k => k[..k.IndexOf('.')]).Distinct();

    // Theme colour roles by camelCase name, plus "text" (= prompt, the body text colour).
    private static readonly Dictionary<string, Func<Theme, string>> Roles = BuildRoles();

    private static Dictionary<string, Func<Theme, string>> BuildRoles()
    {
        var roles = typeof(Theme).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.Name != nameof(Theme.Name))
            .ToDictionary(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..],
                p => p.GetMethod!.CreateDelegate<Func<Theme, string>>(), StringComparer.OrdinalIgnoreCase);
        roles["text"] = t => t.Prompt;
        return roles;
    }

    /// <summary>Theme role names a colour property may reference.</summary>
    internal static IEnumerable<string> RoleNames => Roles.Keys.Order(StringComparer.Ordinal);

    private static volatile IReadOnlyDictionary<string, string> _overrides = new Dictionary<string, string>();
    private static int _version;

    /// <summary>Bumped on every <see cref="Set"/>, so derived caches (clipboard chrome pattern) can rebuild.</summary>
    internal static int Version => Volatile.Read(ref _version);

    /// <summary>Use <paramref name="overrides"/> (validated <c>region.name</c> -&gt; value; null = built-ins).</summary>
    internal static void Set(IReadOnlyDictionary<string, string>? overrides)
    {
        _overrides = overrides is null ? new Dictionary<string, string>() : new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Overridden glyphs that lead a transcript row (bars and marks), for clipboard and hang-indent rules.</summary>
    internal static IEnumerable<string> LeadGlyphs(params string[] keys) =>
        keys.Where(_overrides.ContainsKey).Select(Raw);

    private static string Raw(string key) =>
        _overrides.TryGetValue(key, out var v) ? v : Props.TryGetValue(key, out var p) ? p.Default
            : throw new KeyNotFoundException($"unknown TUI style property '{key}'");

    /// <summary>The colour token for <paramref name="key"/>, with theme roles resolved against the active theme.</summary>
    internal static string Color(string key)
    {
        var v = Raw(key);
        return Roles.TryGetValue(v, out var role) ? role(Theme.Active) : v;
    }

    /// <summary>The glyph for <paramref name="key"/>, escaped for markup.</summary>
    internal static string Glyph(string key) => Spectre.Console.Markup.Escape(Raw(key));

    /// <summary>The glyph for <paramref name="key"/> as plain text (no escaping), for width and marker checks.</summary>
    internal static string PlainGlyph(string key) => Raw(key);

    /// <summary>The flag value for <paramref name="key"/>.</summary>
    internal static bool Flag(string key) => Raw(key) == "true";

    /// <summary>
    /// True when <paramref name="value"/> is a colour BOTH renderers draw: <c>#RRGGBB</c>, <c>default</c>,
    /// or a name the TUI markup resolver knows that Spectre also parses (<c>grey</c>, <c>white</c>, ...).
    /// </summary>
    internal static bool IsColor(string value) =>
        value.Length is > 0 and <= 32
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '#' || c == '_')
        && TuiMarkup.IsColorToken(value)
        && Spectre.Console.Style.TryParse(value, out _);

    /// <summary>
    /// Validate one <c>region.name</c> value from JSON. Returns null and the normalized value when valid,
    /// else the reason (naming the key) and an empty value.
    /// </summary>
    internal static string? Validate(string key, JsonElement value, out string normalized)
    {
        normalized = "";
        if (!Props.TryGetValue(key, out var prop))
        {
            var region = key.Contains('.') ? key[..key.IndexOf('.')] : key;
            return Regions.Contains(region, StringComparer.OrdinalIgnoreCase)
                ? $"'{key}' is not a property of region '{region}'"
                : $"unknown region '{region}' (one of {string.Join(", ", Regions)})";
        }
        switch (prop.Kind)
        {
            case Kind.Flag:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return $"'{key}' must be true or false";
                normalized = value.ValueKind == JsonValueKind.True ? "true" : "false";
                return null;
            case Kind.Color:
                var c = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
                if (Roles.ContainsKey(c)) { normalized = char.ToLowerInvariant(c[0]) + c[1..]; return null; }
                if (IsColor(c)) { normalized = c; return null; }
                return $"'{key}' must be a theme role ({string.Join(" ", RoleNames)}), #RRGGBB, default, or a basic colour name";
            default:
                var g = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
                int maxLen = Math.Max(16, prop.MaxCells * 2);
                if (g is null || (g.Length == 0 && prop.MinCells > 0) || g.Length > maxLen || g.Any(char.IsControl))
                    return $"'{key}' must be a string of at most {maxLen} characters without control characters";
                int w = TuiMarkup.Width(g);
                if (w < prop.MinCells || w > prop.MaxCells)
                    return $"'{key}' must be {(prop.MinCells == prop.MaxCells ? $"{prop.MinCells}" : $"{prop.MinCells}-{prop.MaxCells}")} terminal cell{(prop.MaxCells == 1 ? "" : "s")} wide (got {w})";
                normalized = g;
                return null;
        }
    }
}
