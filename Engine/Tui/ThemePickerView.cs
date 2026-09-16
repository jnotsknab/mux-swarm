namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Alt-screen theme picker (/theme with no args in the docked TUI): a navigable preset list on
/// the left and a LIVE PREVIEW pane on the right rendering sample transcript chrome (status
/// badges, agent line, markdown, diff rows, a footer strip) in the highlighted theme's own
/// palette - so a theme can be judged before it is applied. Enter applies, Esc cancels. Pure
/// view: owns no terminal I/O (the TuiDriver runner feeds keys and presents rows), so every
/// behavior is testable headlessly. Same conventions as the model/agent/settings pickers.
/// </summary>
internal sealed class ThemePickerView
{
    internal enum Action { None, Cancel, Apply }

    internal const int MinWidth = 52;
    internal const int MinHeight = 12;

    private readonly IReadOnlyList<Theme> _themes;
    private List<int> _matches;
    private int _index;
    private string _query = "";

    internal ThemePickerView(IReadOnlyList<Theme>? themes = null, string? activeName = null)
    {
        _themes = themes ?? Theme.Presets;
        _matches = Enumerable.Range(0, _themes.Count).ToList();
        // Start on the active theme so Enter with no navigation is a no-op re-apply.
        if (activeName is { Length: > 0 })
        {
            int i = _matches.FindIndex(m => string.Equals(_themes[m].Name, activeName, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _index = i;
        }
    }

    internal string Query => _query;
    internal int MatchCount => _matches.Count;
    internal Theme? Selected => _matches.Count == 0 ? null : _themes[_matches[_index]];

    private void Filter()
    {
        string q = _query.Trim().ToLowerInvariant();
        _matches = Enumerable.Range(0, _themes.Count)
            .Where(i => q.Length == 0 || _themes[i].Name.ToLowerInvariant().Contains(q))
            .ToList();
        _index = 0;
    }

    internal void ClickItem(int matchIndex)
        => _index = Math.Clamp(matchIndex, 0, Math.Max(0, _matches.Count - 1));

    internal void Paste(string text)
    {
        _query += new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (_query.Length > 64) _query = _query[..64];
        Filter();
    }

    internal Action Handle(ConsoleKeyInfo key, int pageSize)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (key.Key == ConsoleKey.Escape || (ctrl && key.Key is ConsoleKey.Q or ConsoleKey.C))
            return Action.Cancel;
        if (ctrl && key.Key == ConsoleKey.U) { _query = ""; Filter(); return Action.None; }
        if (ctrl || key.Modifiers.HasFlag(ConsoleModifiers.Alt)) return Action.None;
        if (key.Key == ConsoleKey.Enter) return Selected is null ? Action.None : Action.Apply;
        int delta = key.Key switch
        {
            ConsoleKey.UpArrow => -1,
            ConsoleKey.DownArrow => 1,
            ConsoleKey.PageUp => -Math.Max(1, pageSize),
            ConsoleKey.PageDown => Math.Max(1, pageSize),
            ConsoleKey.Home => -_matches.Count,
            ConsoleKey.End => _matches.Count,
            _ => 0
        };
        if (delta != 0)
            _index = (int)Math.Clamp((long)_index + delta, 0, Math.Max(0, _matches.Count - 1));
        else if (key.Key == ConsoleKey.Backspace && _query.Length > 0) { _query = _query[..^1]; Filter(); }
        else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) Paste(key.KeyChar.ToString());
        return Action.None;
    }

    // ---- render ----------------------------------------------------------------------

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s);

    /// <summary>Sample preview lines rendered in <paramref name="t"/>'s own palette (not the
    /// active theme), so the pane shows exactly what the theme will look like when applied.</summary>
    internal static List<string> PreviewLines(Theme t, int width)
    {
        string bg(string fg, string back) => back == "default" ? fg : $"{fg} on {back}";
        var rows = new List<string>
        {
            $"[{t.Banner} bold] {Esc(t.Name)}[/]",
            "",
            $"[{t.Accent}]\u276f[/] [{t.Prompt}]build the parser and run the tests[/]",
            $"[{t.Agent}]CodeAgent[/][{t.Muted}] \u00b7 thinking\u2026[/]",
            $"[{t.Success}]\u2713 Build succeeded[/][{t.Muted}] \u00b7 1848 tests[/]",
            $"[{t.Warning}]\u26a0 2 warnings[/][{t.Muted}] \u00b7 [/][{t.Error}]\u2717 1 error fixed[/]",
            "",
            $"[{t.MdHeading} bold]## Sample heading[/]",
            $"[{t.Prompt}]Body text with [/][{bg(t.MdCode, t.InlineCodeBg)} ] inline_code [/][{t.Prompt}] and a [/][{t.MdLink} underline]link[/][{t.Prompt}].[/]",
            $"[{t.MdQuote} italic]\u258e quoted note[/]",
            $"[{bg(t.Success, t.DiffAddBg)}]+ added diff line[/]",
            $"[{bg(t.Error, t.DiffDelBg)}]- removed diff line[/]",
            "",
            $"[{bg(t.Muted, t.CardBg)} ] card [/] [{bg(t.Prompt, t.InputBg)} ] input band [/] [{t.Step}]\u25cf step[/] [{t.Info}]info[/]",
        };
        return rows;
    }

    internal List<string> Render(int width, int height, MouseHitMap? hits = null)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        hits?.Begin(width, height);
        string Clip(string s) => TuiMarkup.TruncateMarkup(s, width, "");
        if (width < MinWidth || height < MinHeight)
            return new[] { "Esc: close", $"Resize to {MinWidth}x{MinHeight} for the theme picker." }
                .Concat(Enumerable.Repeat("", height)).Take(height).Select(Clip).ToList();

        var rows = new List<string>
        {
            $"[{TuiComponents.Accent} bold] THEMES[/] [{TuiComponents.Muted}]({_matches.Count}/{_themes.Count})[/]",
            $"[{TuiComponents.Accent}] Search: {Esc(_query)}\u258f[/]",
            TuiComponents.FullRule(width),
        };

        // Two columns: list (left, fixed) + preview (right, rest). The preview renders in the
        // HIGHLIGHTED theme's palette.
        int listW = Math.Min(26, Math.Max(16, width / 3));
        int listRoom = height - 6;
        int start = Math.Clamp(_index - listRoom / 2, 0, Math.Max(0, _matches.Count - listRoom));
        var preview = Selected is { } sel ? PreviewLines(sel, width - listW - 3) : new List<string>();
        for (int r = 0; r < listRoom; r++)
        {
            int mi = start + r;
            string left;
            if (mi < _matches.Count)
            {
                var th = _themes[_matches[mi]];
                bool cur = mi == _index;
                bool active = string.Equals(th.Name, Theme.Active.Name, StringComparison.OrdinalIgnoreCase);
                hits?.Add(new HitRegion(rows.Count + 1, 1, 1, listW, MouseTargetKind.PickerItem, mi));
                string marker = cur ? $"[{TuiComponents.Accent}]\u203a[/]" : " ";
                string dot = $"[{th.Accent}]\u25cf[/]";
                string name = cur
                    ? $"[{TuiComponents.Text} bold]{Esc(th.Name)}[/]"
                    : $"[{TuiComponents.Muted}]{Esc(th.Name)}[/]";
                left = $"{marker} {dot} {name}{(active ? $" [{TuiComponents.Ok}]\u2713[/]" : "")}";
            }
            else if (mi == _matches.Count && _matches.Count == 0)
                left = $"[{TuiComponents.Muted}] No matches. Ctrl+U clears.[/]";
            else
                left = "";
            string right = r < preview.Count ? preview[r] : "";
            // Pad the left column to a fixed plain-width so the preview column aligns.
            int pad = Math.Max(0, listW - TuiMarkup.MarkupWidth(left));
            rows.Add(left + new string(' ', pad) + $"[{TuiComponents.Muted}]\u2502[/] " + right);
        }

        while (rows.Count < height - 2) rows.Add("");
        rows.Add($"[{TuiComponents.Accent}] Enter apply \u00b7 \u2191\u2193/PgUp/PgDn navigate \u00b7 type to search[/]");
        rows.Add($"[{TuiComponents.Muted}] Ctrl+U clear \u00b7 Esc/Ctrl+Q cancel[/]");
        return rows.Select(Clip).ToList();
    }
}
