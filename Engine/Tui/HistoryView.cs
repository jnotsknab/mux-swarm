using System.Text.Json;

namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Alt-screen session HISTORY browser (/history) and the bare-/resume picker. Two modes over one
/// state bag: LIST (fuzzy-searchable session roster, same conventions as the agent/model pickers)
/// and READ (the selected session re-rendered from its persisted statebag into scrollable,
/// searchable rows). Pure view: owns no terminal I/O; the TuiDriver runner feeds keys and
/// presents rendered rows, so every behavior is unit-testable headlessly.
/// </summary>
internal sealed class HistoryView
{
    internal enum Action { None, Cancel, Resume }
    internal enum Mode { List, Read }

    internal const int MinWidth = 24;
    internal const int MinHeight = 8;
    private const int MaxQueryLength = 256;

    private readonly List<(string Id, string Preview)> _sessions;
    private readonly bool _resumePicker;
    private List<int> _matches;
    private int _index;
    private string _query = "";

    // READ mode state.
    private string? _openId;
    private List<string> _lines = new();     // plain text (search corpus)
    private List<string> _markup = new();    // markup twins (render source)
    private int _scroll;                      // top visible row index
    private int _readWidth = -1;
    private List<(string Role, string Text)> _transcript = new();
    // In-view search.
    private bool _searching;
    private string _search = "";
    private readonly List<int> _hits = new();
    private int _hit = -1;

    internal HistoryView(IReadOnlyList<(string Id, string Preview)> sessions, bool resumePicker = false)
    {
        _sessions = sessions.ToList();
        _resumePicker = resumePicker;
        _matches = Enumerable.Range(0, _sessions.Count).ToList();
    }

    internal Mode CurrentMode { get; private set; } = Mode.List;
    internal string Query => _query;
    internal string? SelectedId => _matches.Count == 0 ? null : _sessions[_matches[_index]].Id;
    internal string? OpenId => _openId;
    internal int MatchCount => _matches.Count;
    internal int Scroll => _scroll;
    internal int HitCount => _hits.Count;

    // ---- list mode ------------------------------------------------------------------

    private void Filter()
    {
        string q = _query.Trim().ToLowerInvariant();
        _matches = Enumerable.Range(0, _sessions.Count)
            .Where(i => q.Length == 0
                || _sessions[i].Id.ToLowerInvariant().Contains(q)
                || (_sessions[i].Preview ?? "").ToLowerInvariant().Contains(q)
                || IsSubsequence(q, _sessions[i].Id.ToLowerInvariant()))
            .ToList();
        _index = 0;
    }

    private static bool IsSubsequence(string needle, string hay)
    {
        if (needle.Length < 2) return false;
        int i = 0;
        foreach (char c in hay) if (i < needle.Length && c == needle[i]) i++;
        return i == needle.Length;
    }

    internal void Paste(string text)
    {
        if (CurrentMode == Mode.Read) { if (_searching) AppendSearch(text); return; }
        string value = new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        int room = Math.Max(0, MaxQueryLength - _query.Length);
        _query += value.Length > room ? value[..room] : value;
        Filter();
    }

    internal void ClickItem(int matchIndex)
    { if (CurrentMode == Mode.List) _index = Math.Clamp(matchIndex, 0, Math.Max(0, _matches.Count - 1)); }

    // ---- transcript loading (statebag -> role/text entries) --------------------------

    /// <summary>
    /// Extract a readable transcript from a persisted session statebag: user/assistant text plus
    /// compact tool-call/result markers. Defensive against schema drift - unknown content kinds
    /// are skipped, oversized tool results clipped. Static + pure for testability.
    /// </summary>
    internal static List<(string Role, string Text)> ExtractTranscript(JsonElement sessionRoot)
    {
        var outp = new List<(string, string)>();
        if (!Common.TryGetSessionMessages(sessionRoot, out var messages)) return outp;
        foreach (var msg in messages.EnumerateArray())
        {
            string role = msg.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
            if (role.Length == 0) continue;
            var sb = new System.Text.StringBuilder();
            if (msg.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in contents.EnumerateArray())
                {
                    string kind = c.TryGetProperty("$type", out var t) ? t.GetString() ?? "" : "";
                    if (kind == "text" && c.TryGetProperty("text", out var txt))
                        sb.AppendLine((txt.GetString() ?? "").TrimEnd());
                    else if (kind.Contains("functionCall", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                        sb.AppendLine($"[tool call: {name}]");
                    }
                    else if (kind.Contains("functionResult", StringComparison.OrdinalIgnoreCase))
                    {
                        string body = c.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String
                            ? res.GetString() ?? "" : "";
                        body = body.Replace("\r\n", "\n");
                        if (body.Length > 2000) body = body[..2000] + " [...]";
                        sb.AppendLine(body.Length > 0 ? $"[tool result] {body.TrimEnd()}" : "[tool result]");
                    }
                }
            }
            string text = sb.ToString().TrimEnd();
            if (text.Length > 0) outp.Add((role, text));
        }
        return outp;
    }

    /// <summary>Open a session in READ mode from an already-parsed statebag root.</summary>
    internal void OpenTranscript(string id, JsonElement sessionRoot)
    {
        _openId = id;
        _transcript = ExtractTranscript(sessionRoot);
        _readWidth = -1;   // force lay-out at the next Render
        _scroll = 0;
        _searching = false; _search = ""; _hits.Clear(); _hit = -1;
        CurrentMode = Mode.Read;
    }

    private void LayoutRead(int width)
    {
        if (width == _readWidth) return;
        _readWidth = width;
        _lines = new(); _markup = new();
        int w = Math.Max(8, width - 2);
        foreach (var (role, text) in _transcript)
        {
            bool user = role.Equals("user", StringComparison.OrdinalIgnoreCase);
            // Turn header row.
            _lines.Add("");
            _markup.Add("");
            string label = user ? "user" : role.ToLowerInvariant();
            _lines.Add($"── {label} ──");
            _markup.Add($"[{(user ? TuiComponents.Accent : TuiComponents.Agent)}]── {label} ──[/]");
            // Table-aware layout: TuiMarkdown is strictly per-line and cannot align columns, so
            // contiguous GFM table rows are buffered and rendered through the production TuiTable
            // path (same as the live renderer's FlushTableBuffer) instead of leaking raw pipes.
            var tableRun = new List<string>();
            void FlushTableRun()
            {
                if (tableRun.Count == 0) return;
                foreach (var trow in TuiTable.Render(tableRun, w))
                {
                    _lines.Add(TuiMarkup.Plain(trow));
                    _markup.Add("  " + trow);
                }
                tableRun.Clear();
            }
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (!user && (TuiTable.IsTableRow(raw) || (tableRun.Count > 0 && TuiTable.IsSeparatorRow(raw))))
                {
                    tableRun.Add(raw);
                    continue;
                }
                FlushTableRun();
                foreach (var wrapped in TuiMarkup.WrapPlain(raw, w))
                {
                    _lines.Add(wrapped);
                    bool toolMarker = wrapped.StartsWith("[tool ", StringComparison.Ordinal);
                    _markup.Add(toolMarker
                        ? $"  [{TuiComponents.Dim}]{Spectre.Console.Markup.Escape(wrapped)}[/]"
                        : user
                            ? $"  [{TuiComponents.Text}]{Spectre.Console.Markup.Escape(wrapped)}[/]"
                            : "  " + TuiMarkdown.ToMarkup(wrapped));
                }
            }
            FlushTableRun();
        }
        RunSearch();   // re-anchor hits at the new layout
    }

    // ---- search ----------------------------------------------------------------------

    private void AppendSearch(string text)
    {
        _search += new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        RunSearch();
    }

    private void RunSearch()
    {
        _hits.Clear(); _hit = -1;
        if (_search.Trim().Length == 0) return;
        string s = _search.Trim().ToLowerInvariant();
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].ToLowerInvariant().Contains(s)) _hits.Add(i);
        if (_hits.Count > 0) { _hit = 0; _scroll = Math.Max(0, _hits[0] - 3); }
    }

    private void NextHit(int dir)
    {
        if (_hits.Count == 0) return;
        _hit = ((_hit + dir) % _hits.Count + _hits.Count) % _hits.Count;
        _scroll = Math.Max(0, _hits[_hit] - 3);
    }

    // ---- input -----------------------------------------------------------------------

    internal (Action Action, string? OpenRequest) Handle(ConsoleKeyInfo key, int pageSize)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (ctrl && key.Key is ConsoleKey.Q or ConsoleKey.C) return (Action.Cancel, null);

        if (CurrentMode == Mode.Read)
        {
            if (_searching)
            {
                if (key.Key == ConsoleKey.Escape) { _searching = false; _search = ""; _hits.Clear(); _hit = -1; return (Action.None, null); }
                if (key.Key == ConsoleKey.Enter) { _searching = false; return (Action.None, null); }
                if (key.Key == ConsoleKey.Backspace)
                { if (_search.Length > 0) { _search = _search[..^1]; RunSearch(); } return (Action.None, null); }
                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) AppendSearch(key.KeyChar.ToString());
                return (Action.None, null);
            }
            if (key.Key == ConsoleKey.Escape) { CurrentMode = Mode.List; return (Action.None, null); }
            char ch = key.KeyChar;
            int max = Math.Max(0, _lines.Count - Math.Max(1, pageSize));
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: _scroll = Math.Max(0, _scroll - 1); return (Action.None, null);
                case ConsoleKey.DownArrow: _scroll = Math.Min(max, _scroll + 1); return (Action.None, null);
                case ConsoleKey.PageUp: _scroll = Math.Max(0, _scroll - pageSize); return (Action.None, null);
                case ConsoleKey.PageDown: _scroll = Math.Min(max, _scroll + pageSize); return (Action.None, null);
                case ConsoleKey.Home: _scroll = 0; return (Action.None, null);
                case ConsoleKey.End: _scroll = max; return (Action.None, null);
            }
            switch (ch)
            {
                case 'j': _scroll = Math.Min(max, _scroll + 1); break;
                case 'k': _scroll = Math.Max(0, _scroll - 1); break;
                case 'g': _scroll = 0; break;
                case 'G': _scroll = max; break;
                case '/': _searching = true; _search = ""; _hits.Clear(); _hit = -1; break;
                case 'n': NextHit(+1); break;
                case 'N': NextHit(-1); break;
                case 'q': CurrentMode = Mode.List; break;
            }
            return (Action.None, null);
        }

        // LIST mode.
        if (key.Key == ConsoleKey.Escape) return (Action.Cancel, null);
        if (ctrl && key.Key == ConsoleKey.U) { _query = ""; Filter(); return (Action.None, null); }
        if (ctrl || key.Modifiers.HasFlag(ConsoleModifiers.Alt)) return (Action.None, null);
        if (key.Key == ConsoleKey.Enter && SelectedId is { } sel)
            return _resumePicker ? (Action.Resume, null) : (Action.None, sel);
        // v previews (opens READ) in resume-picker mode; plain typing must still reach the query,
        // so v only acts when the picker offers it AND the query is empty (v as first char).
        if (_resumePicker && key.KeyChar == 'v' && _query.Length == 0 && SelectedId is { } pv)
            return (Action.None, pv);
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
        if (delta != 0) _index = (int)Math.Clamp((long)_index + delta, 0, Math.Max(0, _matches.Count - 1));
        else if (key.Key == ConsoleKey.Backspace && _query.Length > 0) { _query = _query[..^1]; Filter(); }
        else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) Paste(key.KeyChar.ToString());
        return (Action.None, null);
    }

    internal void Wheel(int dir, int pageSize)
    {
        if (CurrentMode == Mode.Read)
        {
            int max = Math.Max(0, _lines.Count - Math.Max(1, pageSize));
            _scroll = Math.Clamp(_scroll + (dir > 0 ? -3 : 3), 0, max);
        }
        else if (_matches.Count > 0)
            _index = Math.Clamp(_index + (dir > 0 ? -1 : 1), 0, _matches.Count - 1);
    }

    // ---- render ----------------------------------------------------------------------

    internal List<string> Render(int width, int height, MouseHitMap? hits = null)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        hits?.Begin(width, height);
        string Clip(string s) => TuiMarkup.TruncateMarkup(s, width, "");
        if (width < MinWidth || height < MinHeight)
            return new[] { "Esc: close", $"Resize to {MinWidth}x{MinHeight} to browse history." }
                .Concat(Enumerable.Repeat("", height)).Take(height).Select(Clip).ToList();

        var rows = new List<string>();
        if (CurrentMode == Mode.Read)
        {
            LayoutRead(width);
            string title = _resumePicker ? "SESSION PREVIEW" : "SESSION HISTORY";
            rows.Add($"[{TuiComponents.Accent} bold] {title}[/] [{TuiComponents.Muted}]{Spectre.Console.Markup.Escape(_openId ?? "")}[/]");
            string searchLabel = _searching
                ? $"/{Spectre.Console.Markup.Escape(_search)}▏"
                : _hits.Count > 0 ? $"match {_hit + 1}/{_hits.Count} · n/N next/prev" : "/ search";
            rows.Add($"[{TuiComponents.Muted}] {searchLabel}[/]");
            rows.Add(TuiComponents.FullRule(width));
            int room = height - 5;
            int max = Math.Max(0, _lines.Count - room);
            if (_scroll > max) _scroll = max;
            for (int i = _scroll; i < Math.Min(_lines.Count, _scroll + room); i++)
            {
                bool isHit = _hit >= 0 && _hits.Count > 0 && i == _hits[_hit];
                // Hit row renders from the PLAIN twin (escaped) so [invert] wraps one clean run.
                rows.Add(isHit
                    ? $"[invert]  {Spectre.Console.Markup.Escape(_lines[i])}[/]"
                    : _markup[i]);
            }
            while (rows.Count < height - 2) rows.Add("");
            rows.Add($"[{TuiComponents.Muted}] {Math.Min(_lines.Count, _scroll + room)}/{_lines.Count} rows[/]");
            rows.Add($"[{TuiComponents.Accent}] j/k ↑↓ PgUp/PgDn g/G scroll · / search · Esc back · Ctrl+Q close[/]");
            return rows.Select(Clip).ToList();
        }

        string caption = _resumePicker ? "RESUME SESSION" : "SESSION HISTORY";
        rows.Add($"[{TuiComponents.Accent} bold] {caption}[/] [{TuiComponents.Muted}]({_matches.Count}/{_sessions.Count})[/]");
        rows.Add($"[{TuiComponents.Accent}] Search: {Spectre.Console.Markup.Escape(_query)}▏[/]");
        rows.Add(TuiComponents.FullRule(width));
        int listRoom = height - 6;
        int start = Math.Clamp(_index - listRoom / 2, 0, Math.Max(0, _matches.Count - listRoom));
        if (_matches.Count == 0)
            rows.Add($"[{TuiComponents.Muted}] {(_sessions.Count == 0 ? "No saved sessions." : "No matches. Backspace or Ctrl+U clears the filter.")}[/]");
        for (int i = start; i < Math.Min(_matches.Count, start + listRoom); i++)
        {
            var (id, preview) = _sessions[_matches[i]];
            string one = TuiMarkup.TruncatePlain(
                System.Text.RegularExpressions.Regex.Replace(preview ?? "", @"\s+", " ").Trim(),
                Math.Max(8, width - id.Length - 6));
            hits?.Add(new HitRegion(rows.Count + 1, 1, 1, width, MouseTargetKind.PickerItem, i));
            rows.Add(i == _index
                ? $"[{TuiComponents.Accent}]›[/] [{TuiComponents.Text}]{Spectre.Console.Markup.Escape(id)}[/]  [{TuiComponents.Text}]{Spectre.Console.Markup.Escape(one)}[/]"
                : $"  [{TuiComponents.Agent}]{Spectre.Console.Markup.Escape(id)}[/]  [{TuiComponents.Muted}]{Spectre.Console.Markup.Escape(one)}[/]");
        }
        while (rows.Count < height - 2) rows.Add("");
        rows.Add($"[{TuiComponents.Accent}] {(_resumePicker ? "Enter resume · v preview" : "Enter open")} · ↑↓/PgUp/PgDn navigate[/]");
        rows.Add($"[{TuiComponents.Muted}] Type to search · Ctrl+U clear · Esc/Ctrl+Q close[/]");
        return rows.Select(Clip).ToList();
    }
}
