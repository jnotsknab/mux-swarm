using MuxSwarm.Engine.Extensions;

namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Alt-screen <c>/extensions</c> panel: the installed extensions on the left (state badges), the
/// highlighted one's details on the right (contributions, process, conflicts, problems), and an
/// action bar. Keys and mouse map to the same <see cref="Action"/>s: Space or a double click
/// toggles, t trusts, n creates, e edits, o opens the folder, r reloads, / searches, Esc closes.
/// Pure view: owns no terminal I/O (the TuiDriver runner feeds input and presents rows), so every
/// behavior is testable headlessly. Same conventions as the theme/model/settings pickers.
/// </summary>
internal sealed class ExtensionsPanelView
{
    internal enum Action { None, Close, Toggle, Trust, New, Edit, OpenFolder, Reload }

    internal const int MinWidth = 60;
    internal const int MinHeight = 14;

    // Action-bar buttons, in order; the hit payload is the index here.
    private static readonly (string Label, Action Action)[] Buttons =
    [
        ("Space toggle", Action.Toggle), ("t trust", Action.Trust), ("n new", Action.New), ("e edit", Action.Edit),
        ("o folder", Action.OpenFolder), ("r reload", Action.Reload), ("Esc close", Action.Close),
    ];

    private IReadOnlyList<ExtensionCatalog.Item> _items;
    private IReadOnlyList<string> _other;
    private List<int> _matches = [];
    private int _index;
    private string _query = "";
    private bool _searching;
    private int _detailScroll;

    internal ExtensionsPanelView(IReadOnlyList<ExtensionCatalog.Item> items, IReadOnlyList<string>? other = null, string? select = null)
    {
        _items = items;
        _other = other ?? [];
        Filter();
        if (select is not null) Select(select);
    }

    /// <summary>A status line shown above the action bar (result of the last action).</summary>
    internal string Message { get; set; } = "";

    internal string Query => _query;
    internal bool Searching => _searching;
    internal int MatchCount => _matches.Count;
    internal ExtensionCatalog.Item? Selected => _matches.Count == 0 ? null : _items[_matches[_index]];

    /// <summary>Swap in fresh items after an action, keeping the selection on <paramref name="select"/> when present.</summary>
    internal void Update(IReadOnlyList<ExtensionCatalog.Item> items, IReadOnlyList<string> other, string? select)
    {
        _items = items;
        _other = other;
        Filter();
        if (select is not null) Select(select);
    }

    private void Select(string name)
    {
        int i = _matches.FindIndex(m => _items[m].Name == name);
        if (i >= 0) _index = i;
    }

    private void Filter()
    {
        string q = _query.Trim().ToLowerInvariant();
        _matches = Enumerable.Range(0, _items.Count)
            .Where(i => q.Length == 0 || _items[i].Name.Contains(q) || (_items[i].Description ?? "").ToLowerInvariant().Contains(q))
            .ToList();
        _index = Math.Clamp(_index, 0, Math.Max(0, _matches.Count - 1));
        _detailScroll = 0;
    }

    /// <summary>Select a clicked list row (region payload = match index).</summary>
    internal void ClickItem(int matchIndex)
    {
        int i = Math.Clamp(matchIndex, 0, Math.Max(0, _matches.Count - 1));
        if (i != _index) _detailScroll = 0;
        _index = i;
    }

    /// <summary>The action of a clicked action-bar button (region payload = button index).</summary>
    internal static Action ClickButton(int index) => index >= 0 && index < Buttons.Length ? Buttons[index].Action : Action.None;

    /// <summary>Scroll the details pane (mouse wheel over it).</summary>
    internal void ScrollDetail(int delta) => _detailScroll = Math.Max(0, _detailScroll + delta);

    internal void Paste(string text)
    {
        _searching = true;
        _query += new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (_query.Length > 64) _query = _query[..64];
        Filter();
    }

    internal Action Handle(ConsoleKeyInfo key, int pageSize)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (ctrl && key.Key is ConsoleKey.Q or ConsoleKey.C) return Action.Close;
        if (key.Key == ConsoleKey.Escape)
        {
            if (!_searching) return Action.Close;
            _searching = false;   // first Esc leaves search, keeping the filter
            return Action.None;
        }
        if (ctrl && key.Key == ConsoleKey.U) { _query = ""; Filter(); return Action.None; }
        if (ctrl || key.Modifiers.HasFlag(ConsoleModifiers.Alt)) return Action.None;
        int delta = key.Key switch
        {
            ConsoleKey.UpArrow => -1,
            ConsoleKey.DownArrow => 1,
            ConsoleKey.PageUp => -Math.Max(1, pageSize),
            ConsoleKey.PageDown => Math.Max(1, pageSize),
            ConsoleKey.Home => -_matches.Count,
            ConsoleKey.End => _matches.Count,
            _ => 0,
        };
        if (delta != 0)
        {
            int next = (int)Math.Clamp((long)_index + delta, 0, Math.Max(0, _matches.Count - 1));
            if (next != _index) _detailScroll = 0;
            _index = next;
            return Action.None;
        }
        if (key.Key == ConsoleKey.Enter) { _searching = false; return Selected is null ? Action.None : Action.Toggle; }
        if (_searching)
        {
            if (key.Key == ConsoleKey.Backspace) { if (_query.Length > 0) { _query = _query[..^1]; Filter(); } }
            else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) Paste(key.KeyChar.ToString());
            return Action.None;
        }
        return key.KeyChar switch
        {
            ' ' => Selected is null ? Action.None : Action.Toggle,
            't' or 'T' => Selected is { HasProcess: true } ? Action.Trust : Action.None,
            'n' or 'N' => Action.New,
            'e' or 'E' => Selected is null ? Action.None : Action.Edit,
            'o' or 'O' => Selected is null ? Action.None : Action.OpenFolder,
            'r' or 'R' => Action.Reload,
            '/' => Search(),
            _ => Action.None,
        };
    }

    private Action Search() { _searching = true; return Action.None; }

    // ---- render ----------------------------------------------------------------------

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s);

    /// <summary>Badges for a list row: enabled dot, process state, conflict and problem marks.</summary>
    internal static string Badges(ExtensionCatalog.Item it)
    {
        string dot = it.Enabled ? $"[{TuiComponents.Ok}]\u25cf[/]" : $"[{TuiComponents.Muted}]\u25cb[/]";
        string proc = !it.HasProcess ? " "
            : it.Process.StartsWith("running", StringComparison.Ordinal) ? $"[{TuiComponents.Ok}]\u25b6[/]"
            : $"[{TuiComponents.Warn}]\u25b6[/]";
        string conflict = it.Conflicts.Count > 0 ? $" [{TuiComponents.Warn}]\u21c4[/]" : "";
        string problems = it.Problems.Count > 0 ? $" [{TuiComponents.Err}]!{it.Problems.Count}[/]" : "";
        return $"{dot} {proc}{conflict}{problems}";
    }

    /// <summary>Plain-ish detail lines for <paramref name="it"/> (markup), before wrapping.</summary>
    internal static List<string> DetailLines(ExtensionCatalog.Item it)
    {
        var rows = new List<string>
        {
            $"[{TuiComponents.Text} bold]{Esc(it.Name)}[/][{TuiComponents.Muted}]{Esc(string.IsNullOrWhiteSpace(it.Version) ? "" : " " + it.Version)}  " +
            $"{(it.Enabled ? "enabled" : "disabled")}[/]",
        };
        if (!string.IsNullOrWhiteSpace(it.Description)) rows.Add($"[{TuiComponents.Muted}]{Esc(it.Description)}[/]");
        rows.Add($"[{TuiComponents.Muted}]{Esc(it.Dir)}[/]");
        rows.Add("");
        if (!it.Enabled) rows.Add($"[{TuiComponents.Muted}]Disabled: contributes nothing. Space enables it.[/]");
        foreach (var c in it.Contributes) rows.Add($"[{TuiComponents.Text}]{Esc(c)}[/]");
        if (it.HasProcess)
            rows.Add($"[{TuiComponents.Text}]process: {Esc(it.Process)}[/]" + (it.Trusted ? "" : $"[{TuiComponents.Muted}] (t trusts it)[/]"));
        if (it.Conflicts.Count > 0)
        {
            rows.Add("");
            rows.Add($"[{TuiComponents.Warn} bold]Conflicts[/]");
            foreach (var c in it.Conflicts)
                rows.Add(c.Winner == it.Name
                    ? $"[{TuiComponents.Warn}]\u21c4 {Esc(c.What)}: overrides {Esc(c.Loser)}[/]"
                    : $"[{TuiComponents.Warn}]\u21c4 {Esc(c.What)}: overridden by {Esc(c.Winner)} (disable {Esc(c.Winner)} to use this one)[/]");
        }
        if (it.Problems.Count > 0)
        {
            rows.Add("");
            rows.Add($"[{TuiComponents.Err} bold]Problems[/]");
            foreach (var p in it.Problems) rows.Add($"[{TuiComponents.Err}]! {Esc(p)}[/]");
        }
        if (it.Stderr.Count > 0)
        {
            rows.Add("");
            rows.Add($"[{TuiComponents.Muted} bold]stderr[/]");
            foreach (var l in it.Stderr) rows.Add($"[{TuiComponents.Muted}]{Esc(l)}[/]");
        }
        return rows;
    }

    internal List<string> Render(int width, int height, MouseHitMap? hits = null)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        hits?.Begin(width, height);
        string Clip(string s) => TuiMarkup.TruncateMarkup(s, width, "");
        if (width < MinWidth || height < MinHeight)
            return new[] { "Esc: close", $"Resize to {MinWidth}x{MinHeight} for the extensions panel." }
                .Concat(Enumerable.Repeat("", height)).Take(height).Select(Clip).ToList();

        int enabled = _items.Count(i => i.Enabled), conflicts = _items.Sum(i => i.Conflicts.Count(c => c.Winner == i.Name));
        string search = _searching || _query.Length > 0
            ? $"[{(_searching ? TuiComponents.Accent : TuiComponents.Muted)}] Search: {Esc(_query)}{(_searching ? "\u258f" : "")}[/]"
            : $"[{TuiComponents.Muted}] / to search[/]";
        var rows = new List<string>
        {
            $"[{TuiComponents.Accent} bold] EXTENSIONS[/] [{TuiComponents.Muted}]({enabled}/{_items.Count} enabled" +
            (conflicts > 0 ? $", {conflicts} conflict{(conflicts == 1 ? "" : "s")}" : "") + ")[/]",
            search,
            TuiComponents.FullRule(width),
        };
        hits?.Add(new HitRegion(2, 1, 1, width, MouseTargetKind.PickerSearchBox, 0));

        int listW = Math.Min(30, Math.Max(20, width / 3));
        int detailW = Math.Max(10, width - listW - 3);
        int room = height - rows.Count - 4;   // footer: rule, other/message, actions, hint
        int start = Math.Clamp(_index - room / 2, 0, Math.Max(0, _matches.Count - room));
        var detail = Selected is { } sel ? DetailLines(sel).SelectMany(l => l.Length == 0 ? [""] : TuiMarkup.WrapMarkup(l, detailW)).ToList() : [];
        _detailScroll = Math.Clamp(_detailScroll, 0, Math.Max(0, detail.Count - room));
        for (int r = 0; r < room; r++)
        {
            int mi = start + r;
            string left;
            if (mi < _matches.Count)
            {
                var it = _items[_matches[mi]];
                bool cur = mi == _index;
                hits?.Add(new HitRegion(rows.Count + 1, 1, 1, listW, MouseTargetKind.PickerItem, mi));
                string marker = cur ? $"[{TuiComponents.Accent}]\u203a[/]" : " ";
                string name = cur ? $"[{TuiComponents.Text} bold]{Esc(it.Name)}[/]"
                    : it.Enabled ? $"[{TuiComponents.Text}]{Esc(it.Name)}[/]" : $"[{TuiComponents.Muted}]{Esc(it.Name)}[/]";
                left = TuiMarkup.TruncateMarkup($"{marker} {Badges(it)} {name}", listW, "\u2026");
            }
            else if (r == 0 && _matches.Count == 0)
                left = $"[{TuiComponents.Muted}] {(_items.Count == 0 ? "None installed. n creates one." : "No matches. Ctrl+U clears.")}[/]";
            else left = "";
            int pad = Math.Max(0, listW - TuiMarkup.MarkupWidth(left));
            int di = _detailScroll + r;
            rows.Add(left + new string(' ', pad) + $" [{TuiComponents.Muted}]\u2502[/] " + (di < detail.Count ? detail[di] : ""));
        }
        hits?.Add(new HitRegion(4, listW + 2, room, width - listW - 1, MouseTargetKind.ExpandedPanel, 0));

        rows.Add(TuiComponents.FullRule(width));
        string status = Message.Length > 0 ? $"[{TuiComponents.Accent}] {Esc(Message)}[/]"
            : _other.Count > 0 ? $"[{TuiComponents.Warn}] ! {Esc(_other[0])}{(_other.Count > 1 ? $" (+{_other.Count - 1} more)" : "")}[/]"
            : "";
        rows.Add(status);
        // Action bar: each button is a click target; disabled ones are dimmed and unclickable.
        var bar = new System.Text.StringBuilder(" ");
        int col = 2;
        for (int b = 0; b < Buttons.Length; b++)
        {
            var (label, action) = Buttons[b];
            bool on = action switch
            {
                Action.Toggle or Action.Edit or Action.OpenFolder => Selected is not null,
                Action.Trust => Selected is { HasProcess: true },
                _ => true,
            };
            string text = action == Action.Trust && Selected is { Trusted: true } ? "t untrust" : action == Action.Toggle && Selected is { Enabled: false } ? "Space enable" : label;
            int w = TuiMarkup.Width(text) + 2;
            if (on) hits?.Add(new HitRegion(rows.Count + 1, col, 1, w, MouseTargetKind.PromptModalOption, b));
            bar.Append(on ? $"[{TuiComponents.Accent}][[{Esc(text)}]][/]" : $"[{TuiComponents.Muted}][[{Esc(text)}]][/]").Append(' ');
            col += w + 1;
        }
        rows.Add(bar.ToString());
        rows.Add($"[{TuiComponents.Muted}] \u2191\u2193 select \u00b7 Enter/double-click toggle \u00b7 wheel scrolls \u00b7 \u25cf on \u25cb off \u25b6 process \u21c4 conflict ! problems[/]");
        while (rows.Count < height) rows.Insert(rows.Count - 4, "");
        return rows.Take(height).Select(Clip).ToList();
    }
}
