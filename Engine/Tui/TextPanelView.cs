namespace MuxSwarm.Engine.Tui;

/// <summary>
/// A read-only, scrollable, searchable full-screen view of command output (/help, /status, /tools ...).
/// Rows are already-themed markup; the chrome (header, hint rows, frame) follows the <c>panel.*</c> styles.
/// </summary>
internal sealed class TextPanelView
{
    internal const int MinWidth = 24;
    internal const int MinHeight = 8;

    private readonly string _title;
    private readonly IReadOnlyList<string> _source;
    private List<string> _markup = new(), _plain = new();
    private int _width = -1, _scroll;
    private bool _searching;
    private string _search = "";
    private readonly List<int> _hits = new();
    private int _hit = -1;

    internal TextPanelView(string title, IReadOnlyList<string> markupLines)
    {
        _title = title;
        _source = markupLines;
    }

    /// <summary>Wrapped body rows at the last rendered width (tests).</summary>
    internal int RowCount => _markup.Count;

    private void Layout(int width)
    {
        if (width == _width) return;
        _width = width;
        _markup = new(); _plain = new();
        foreach (var line in _source)
            foreach (var row in TuiMarkup.WrapMarkup(line, Math.Max(8, width - 2)))
            {
                _markup.Add(" " + row);
                _plain.Add(TuiMarkup.Plain(row));
            }
        RunSearch();
    }

    private void RunSearch()
    {
        _hits.Clear(); _hit = -1;
        string s = _search.Trim();
        if (s.Length == 0) return;
        for (int i = 0; i < _plain.Count; i++)
            if (_plain[i].Contains(s, StringComparison.OrdinalIgnoreCase)) _hits.Add(i);
        if (_hits.Count > 0) { _hit = 0; _scroll = Math.Max(0, _hits[0] - 3); }
    }

    private void NextHit(int dir)
    {
        if (_hits.Count == 0) return;
        _hit = ((_hit + dir) % _hits.Count + _hits.Count) % _hits.Count;
        _scroll = Math.Max(0, _hits[_hit] - 3);
    }

    /// <summary>Handle one key; false = close the view.</summary>
    internal bool Handle(ConsoleKeyInfo key, int pageSize)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (ctrl && key.Key is ConsoleKey.Q or ConsoleKey.C) return false;
        if (_searching)
        {
            if (key.Key == ConsoleKey.Escape) { _searching = false; _search = ""; RunSearch(); }
            else if (key.Key == ConsoleKey.Enter) _searching = false;
            else if (key.Key == ConsoleKey.Backspace) { if (_search.Length > 0) { _search = _search[..^1]; RunSearch(); } }
            else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) { _search += key.KeyChar; RunSearch(); }
            return true;
        }
        int max = Math.Max(0, _markup.Count - Math.Max(1, pageSize));
        switch (key.Key)
        {
            case ConsoleKey.Escape: return false;
            case ConsoleKey.UpArrow: _scroll = Math.Max(0, _scroll - 1); return true;
            case ConsoleKey.DownArrow: _scroll = Math.Min(max, _scroll + 1); return true;
            case ConsoleKey.PageUp: _scroll = Math.Max(0, _scroll - pageSize); return true;
            case ConsoleKey.PageDown: case ConsoleKey.Spacebar: _scroll = Math.Min(max, _scroll + pageSize); return true;
            case ConsoleKey.Home: _scroll = 0; return true;
            case ConsoleKey.End: _scroll = max; return true;
        }
        switch (key.KeyChar)
        {
            case 'q': return false;
            case 'j': _scroll = Math.Min(max, _scroll + 1); break;
            case 'k': _scroll = Math.Max(0, _scroll - 1); break;
            case 'g': _scroll = 0; break;
            case 'G': _scroll = max; break;
            case '/': _searching = true; _search = ""; RunSearch(); break;
            case 'n': NextHit(+1); break;
            case 'N': NextHit(-1); break;
        }
        return true;
    }

    internal void Wheel(int dir, int pageSize)
    {
        int max = Math.Max(0, _markup.Count - Math.Max(1, pageSize));
        _scroll = Math.Clamp(_scroll + (dir > 0 ? -3 : 3), 0, max);
    }

    /// <summary>Rows that fit the body (header, search line, rule above; position + hint below).</summary>
    internal static int PageSize(int height) => Math.Max(1, height - 5);

    internal List<string> Render(int width, int height, MouseHitMap? hits = null)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        hits?.Begin(width, height);
        string Clip(string s) => TuiMarkup.TruncateMarkup(s, width, "");
        if (width < MinWidth || height < MinHeight)
            return new[] { "Esc: close", $"Resize to {MinWidth}x{MinHeight} to read this." }
                .Concat(Enumerable.Repeat("", height)).Take(height).Select(Clip).ToList();
        Layout(width);
        string Esc(string s) => Spectre.Console.Markup.Escape(s);
        var rows = new List<string>
        {
            TuiComponents.PanelHeader($"[{TuiComponents.Accent} bold] {Esc(_title)}[/]", width),
            $"[{TuiComponents.Muted}] {(_searching ? $"/{Esc(_search)}\u258f" : _hits.Count > 0 ? $"match {_hit + 1}/{_hits.Count} \u00b7 n/N next/prev" : "/ search")}[/]",
            TuiComponents.FullRule(width),
        };
        int room = PageSize(height);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _markup.Count - room));
        for (int i = _scroll; i < Math.Min(_markup.Count, _scroll + room); i++)
            rows.Add(_hit >= 0 && i == _hits[_hit] ? $"[invert] {Esc(_plain[i])}[/]" : _markup[i]);
        while (rows.Count < height - 2) rows.Add("");
        rows.Add($"[{TuiComponents.Muted}] {Math.Min(_markup.Count, _scroll + room)}/{_markup.Count} rows[/]");
        rows.Add(TuiComponents.PanelHint($"[{TuiComponents.Accent}] j/k \u2191\u2193 PgUp/PgDn g/G scroll \u00b7 / search \u00b7 Esc/q close[/]", width));
        return rows.Select(Clip).ToList();
    }
}
