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
    internal enum Kind { Color, Glyph, Flag, Lines, Choice, Number, Layout }

    /// <summary>One styleable property: its kind, built-in value, (glyphs) allowed width in cells or (numbers)
    /// allowed range, and (choices) the allowed values.</summary>
    internal sealed record Prop(Kind Kind, string Default, int MinCells = 1, int MaxCells = 1, string[]? Choices = null);

    private static Prop C(string d) => new(Kind.Color, d);
    private static Prop G(string d, int max = 1) => new(Kind.Glyph, d, 1, max);
    private static Prop T(string d, int min, int max) => new(Kind.Glyph, d, min, max);   // free text of bounded width
    private static Prop F(bool d) => new(Kind.Flag, d ? "true" : "false");
    private static Prop A() => new(Kind.Lines, "", 0, 80);   // 1-12 text lines, each at most 80 cells; "" = built-in
    private static Prop O(params string[] choices) => new(Kind.Choice, choices[0], Choices: choices);   // first = default
    private static Prop N(int d, int min, int max) => new(Kind.Number, d.ToString(System.Globalization.CultureInfo.InvariantCulture), min, max);

    /// <summary>Every styleable property, keyed <c>region.name</c> (case-insensitive).</summary>
    internal static readonly IReadOnlyDictionary<string, Prop> Props = new Dictionary<string, Prop>(StringComparer.OrdinalIgnoreCase)
    {
        ["notice.okMark"] = G("\u2713", 2), ["notice.warnMark"] = G("!", 2), ["notice.errorMark"] = G("x", 2),
        ["notice.ok"] = C("success"), ["notice.warn"] = C("warning"), ["notice.error"] = C("error"), ["notice.info"] = C("info"),
        ["header.mark"] = G("\u2503", 2), ["header.accent"] = C("accent"), ["header.show"] = F(true),
        ["header.template"] = T("", 0, 80), ["header.frame"] = O("none", "box"),
        ["userMessage.bar"] = G("\u258e"), ["userMessage.barFg"] = C("accent"), ["userMessage.fg"] = C("text"),
        ["userMessage.style"] = O("bar", "bubble"),
        ["agentHeader.mark"] = G("\u25b8", 2), ["agentHeader.rule"] = G("\u2500"), ["agentHeader.fg"] = C("agent"), ["agentHeader.ruleFg"] = C("#3A3A3A"),
        ["answer.lead"] = G("\u25cf"), ["answer.leadFg"] = C("muted"),
        ["reasoning.fg"] = C("grey"), ["reasoning.italic"] = F(true),
        ["toast.mark"] = G("\u25c6"), ["toast.markFg"] = C("accent"), ["toast.fg"] = C("muted"),
        ["divider.glyph"] = G("\u2502"), ["divider.fg"] = C("muted"), ["divider.activeFg"] = C("accent"),
        ["scrollbar.thumb"] = G("\u2588"), ["scrollbar.track"] = G("\u2502"), ["scrollbar.thumbFg"] = C("accent"), ["scrollbar.trackFg"] = C("muted"),
        ["toolCard.dot"] = G("\u25cf"), ["toolCard.ok"] = G("\u2713"), ["toolCard.error"] = G("\u2717"), ["toolCard.result"] = G("\u23bf"), ["toolCard.resultInline"] = G("\u2192", 2),
        ["toolCard.rail"] = G("\u2502"), ["toolCard.pendingFg"] = C("warning"), ["toolCard.okFg"] = C("success"), ["toolCard.errorFg"] = C("error"),
        ["toolCard.style"] = O("bullet", "gutter", "box"), ["toolCard.gutter"] = G("\u250a"), ["toolCard.layout"] = O("merged", "stacked"),
        ["toolCard.boxLeft"] = G("\u2595"), ["toolCard.boxRight"] = G("\u258f"),
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
        ["diff.corner"] = G("\u256d"), ["diff.end"] = G("\u2570"), ["diff.line"] = G("\u2500"),
        ["palette.selected"] = G("\u203a"), ["palette.selectedFg"] = C("accent"), ["palette.itemFg"] = C("text"), ["palette.titleMark"] = G("\u2503"),
        ["palette.titleFg"] = C("accent"), ["palette.up"] = G("\u2191"), ["palette.down"] = G("\u2193"),
        ["modal.title"] = G("\u25b8"), ["modal.titleFg"] = C("accent"), ["modal.selected"] = G("\u203a"), ["modal.selectedFg"] = C("accent"),
        ["modal.checked"] = G("\u25c9"), ["modal.checkedFg"] = C("success"), ["modal.unchecked"] = G("\u25cb"),
        ["panel.rule"] = G("\u2500"), ["panel.ruleFg"] = C("#3A3A3A"), ["panel.tabFg"] = C("muted"), ["panel.tabActiveFg"] = C("accent"),
        ["panel.focusFg"] = C("black"), ["panel.focusBg"] = C("accent"),
        ["panel.header"] = O("plain", "strip", "rule"), ["panel.headerAlign"] = O("left", "center"), ["panel.headerBg"] = C("cardBg"),
        ["panel.selectedStyle"] = O("chevron", "bar", "fill"), ["panel.bar"] = G("\u258c"), ["panel.selectedBg"] = C("cardBg"),
        ["panel.hints"] = O("plain", "strip"), ["panel.hintBg"] = C("cardBg"),
        ["panel.frame"] = O("none", "rounded", "square", "double", "thick", "ascii"), ["panel.frameFg"] = C("border"),
        ["block.rail"] = G("\u2502"), ["block.railFg"] = C("step"), ["block.titleFg"] = C("step"),
        ["splash.show"] = F(true), ["splash.art"] = A(), ["splash.artFg"] = C("banner"), ["splash.accentFg"] = C("success"),
        ["splash.frame"] = O("rounded", "square", "double", "thick", "ascii", "none"), ["splash.borderFg"] = C("muted"),
        ["splash.align"] = O("left", "center"), ["splash.padY"] = N(1, 0, 2),
        ["splash.columns"] = O("all", "logo", "logo,recent", "logo,getting-started", "getting-started,logo", "recent,logo"),
        ["splash.layout"] = new(Kind.Layout, ""), ["splash.width"] = N(0, 0, 100), ["splash.contentAlign"] = O("left", "center"),
        ["splash.divider"] = O("rule", "gap"), ["splash.title"] = T("", 0, 60), ["splash.titleFg"] = C("banner"),
        ["splash.borderTitle"] = T("", 0, 60), ["splash.text"] = A(), ["splash.textFg"] = C("muted"), ["splash.recentCount"] = N(5, 0, 10),
        ["splash.headings"] = O("plain", "rule", "strip", "none"), ["splash.headingFg"] = C("step"), ["splash.headingBg"] = C("cardBg"),
        ["splash.bullet"] = T("", 0, 1), ["splash.bulletFg"] = C("muted"),
        // Layout (shape) properties: where the footer sits relative to the compose box, and blank rows above them.
        ["footer.position"] = O("above", "below", "hidden", "border"),
        ["footer.style"] = O("dot", "pipe", "space", "powerline", "ascii"),
        ["footer.strip"] = F(false), ["footer.bg"] = C("cardBg"), ["footer.fill"] = O("space", "rule"),
        ["compose.gap"] = N(0, 0, 2),
        ["compose.shape"] = O("classic", "rule", "bracket", "box", "bubble", "smooth", "rail", "bare"),
        ["compose.border"] = O("rounded", "square", "double", "thick", "ascii"),
        ["compose.padX"] = N(1, 0, 3), ["compose.padY"] = N(0, 0, 1),
        ["compose.borderFg"] = C("border"), ["compose.rule"] = G("─"),
        ["compose.fill"] = C("inputBg"), ["compose.capLeft"] = G("▐"), ["compose.capRight"] = G("▌"),
        ["compose.rail"] = G("▎"), ["compose.railFg"] = C("accent"),
    };

    /// <summary>The <c>glyphs: "ascii"</c> set: plain-ASCII replacements for every built-in glyph, for terminals
    /// or fonts without box drawing. Applied under an extension's own region keys.</summary>
    internal static readonly IReadOnlyDictionary<string, string> AsciiGlyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["notice.okMark"] = "+", ["header.mark"] = "|", ["userMessage.bar"] = "|", ["agentHeader.mark"] = ">", ["agentHeader.rule"] = "-",
        ["answer.lead"] = "*", ["toast.mark"] = "*", ["divider.glyph"] = "|", ["scrollbar.thumb"] = "#", ["scrollbar.track"] = "|",
        ["toolCard.dot"] = "*", ["toolCard.ok"] = "+", ["toolCard.error"] = "x", ["toolCard.result"] = "-", ["toolCard.resultInline"] = "->", ["toolCard.rail"] = "|",
        ["diff.rail"] = "|", ["diff.corner"] = "+", ["diff.end"] = "+", ["diff.line"] = "-",
        ["delegation.arrow"] = ">", ["delegation.branch"] = "`", ["delegation.mark"] = ">", ["delegation.done"] = "+", ["delegation.failed"] = "x",
        ["footer.separator"] = "|", ["footer.rule"] = "-", ["input.prompt"] = ">", ["input.continuation"] = "|", ["input.rail"] = "|",
        ["palette.selected"] = ">", ["palette.titleMark"] = "|", ["palette.up"] = "^", ["palette.down"] = "v",
        ["modal.title"] = ">", ["modal.selected"] = ">", ["modal.checked"] = "*", ["modal.unchecked"] = "o", ["panel.rule"] = "-", ["panel.bar"] = "|", ["block.rail"] = "|",
    };

    /// <summary>Section names a <c>splash.layout</c> row may place (see the splash docs in DOCS.md).</summary>
    internal static readonly string[] SplashSections =
        ["logo", "art", "title", "version", "message", "hint", "link", "getting-started", "commands", "tips", "recent", "text", "gap"];

    /// <summary>Glyph set names for <c>tui.glyphs</c>.</summary>
    internal static readonly IReadOnlyList<string> GlyphSets = ["unicode", "ascii"];

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

    /// <summary>True when an extension (or a glyph set) overrides <paramref name="key"/>.</summary>
    internal static bool IsSet(string key) => _overrides.ContainsKey(key);

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

    /// <summary>The chosen value for a choice property (one of its <see cref="Prop.Choices"/>, lower case).</summary>
    internal static string Choice(string key) => Raw(key);

    /// <summary>The value of a number property.</summary>
    internal static int Number(string key) => int.Parse(Raw(key), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The text lines for <paramref name="key"/> (plain, unescaped); empty = use the built-in content.</summary>
    internal static string[] Lines(string key) => Raw(key) is { Length: > 0 } v ? v.Split('\n') : [];

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
            case Kind.Lines:
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > 12
                    || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                    return $"'{key}' must be a list of 1-12 strings";
                var lines = value.EnumerateArray().Select(e => e.GetString()!).ToList();
                if (lines.Any(l => l.Any(char.IsControl) || TuiMarkup.Width(l) > prop.MaxCells))
                    return $"'{key}' lines must be at most {prop.MaxCells} terminal cells without control characters";
                normalized = string.Join('\n', lines);
                return null;
            case Kind.Layout:
                return ValidateLayout(key, value, out normalized);
            case Kind.Choice:
                var choice = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim().ToLowerInvariant() : "";
                if (!prop.Choices!.Contains(choice)) return $"'{key}' must be one of {string.Join(", ", prop.Choices!)}";
                normalized = choice;
                return null;
            case Kind.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int n) || n < prop.MinCells || n > prop.MaxCells)
                    return $"'{key}' must be a whole number from {prop.MinCells} to {prop.MaxCells}";
                normalized = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    // splash.layout: 1-8 row strings; a row holds 1-3 cells split by '|', a cell 1-8 section names split by
    // spaces or '+'. Normalized to "a b | c" rows joined by newlines.
    private static string? ValidateLayout(string key, JsonElement value, out string normalized)
    {
        normalized = "";
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > 8
            || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            return $"'{key}' must be a list of 1-8 row strings";
        var rows = new List<string>();
        foreach (var row in value.EnumerateArray())
        {
            var cells = row.GetString()!.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length is 0 or > 3) return $"'{key}' rows must hold 1-3 cells split by |";
            var norm = new List<string>();
            foreach (var cell in cells)
            {
                var ids = cell.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToLowerInvariant()).ToArray();
                if (ids.Length > 8) return $"'{key}' cells hold at most 8 sections";
                if (ids.FirstOrDefault(i => !SplashSections.Contains(i)) is { } bad)
                    return $"'{key}': unknown section '{bad}' (one of {string.Join(", ", SplashSections)})";
                norm.Add(string.Join(' ', ids));
            }
            rows.Add(string.Join(" | ", norm));
        }
        normalized = string.Join('\n', rows);
        return null;
    }
}
