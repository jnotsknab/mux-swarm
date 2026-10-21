using MuxSwarm.Engine.Extensions;

namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Alt-screen view of one extension panel (<c>ui/open</c>). Lays the element tree out as themed rows:
/// boxes stack (col) or sit side by side (row); buttons, inputs and lists take focus in tree order.
/// Tab / Shift+Tab or Up/Down move focus, Enter presses / submits / selects, a button's hotkey presses it,
/// PgUp/PgDn and the wheel scroll, Esc closes. While an input has focus every printable key types.
/// Pure view: owns no terminal I/O (the TuiDriver runner feeds input and presents rows).
/// </summary>
internal sealed class ExtensionPanelView
{
    internal const int MinWidth = 30, MinHeight = 8;

    /// <summary>What a key or click asked for: nothing, a panel event to send, or close.</summary>
    internal readonly record struct Outcome(bool Close, string? Key = null, string? Type = null, string? Value = null)
    {
        internal static readonly Outcome None = new(false);
        internal bool IsEvent => Type is not null;
    }

    private ExtensionPanels.Panel _panel;
    private List<ExtensionPanels.Node> _focusables = [];
    private int _focus;
    private int _scroll;
    private readonly Dictionary<string, string> _inputs = new(StringComparer.Ordinal);   // key -> current text
    private readonly Dictionary<string, int> _listSel = new(StringComparer.Ordinal);    // key -> selected row

    internal ExtensionPanelView(ExtensionPanels.Panel panel) { _panel = panel; Index(); }

    internal ExtensionPanels.Panel Panel => _panel;
    internal string? FocusedKey => _focusables.Count == 0 ? null : _focusables[_focus].Key;

    /// <summary>Swap in a newer tree (ui/update), keeping focus on the same key and typed input text.</summary>
    internal void Update(ExtensionPanels.Panel panel)
    {
        var key = FocusedKey;
        _panel = panel;
        Index();
        int i = _focusables.FindIndex(n => n.Key == key);
        if (i >= 0) _focus = i;
    }

    private void Index()
    {
        _focusables = [];
        void Walk(ExtensionPanels.Node n)
        {
            if (n.Focusable) _focusables.Add(n);
            if (n.Type == "input" && !_inputs.ContainsKey(n.Key!)) _inputs[n.Key!] = n.Text;
            if (n.Type == "list")
            {
                int sel = n.Selected is null ? -1 : n.ItemList.ToList().FindIndex(it => it.Value == n.Selected);
                if (sel >= 0) _listSel[n.Key!] = sel;
                _listSel[n.Key!] = Math.Clamp(_listSel.GetValueOrDefault(n.Key!), 0, Math.Max(0, n.ItemList.Count - 1));
            }
            foreach (var c in n.Kids) Walk(c);
        }
        Walk(_panel.Root);
        _focus = Math.Clamp(_focus, 0, Math.Max(0, _focusables.Count - 1));
    }

    private ExtensionPanels.Node? Focused => _focusables.Count == 0 ? null : _focusables[_focus];

    /// <summary>Handle a key. <paramref name="page"/> = rows scrolled by PgUp/PgDn.</summary>
    internal Outcome Handle(ConsoleKeyInfo k, int page)
    {
        var f = Focused;
        bool shift = k.Modifiers.HasFlag(ConsoleModifiers.Shift);
        switch (k.Key)
        {
            case ConsoleKey.Escape: return new(true);
            case ConsoleKey.Tab: Move(shift ? -1 : 1); return Outcome.None;
            case ConsoleKey.PageUp: _scroll = Math.Max(0, _scroll - page); return Outcome.None;
            case ConsoleKey.PageDown: _scroll += page; return Outcome.None;
            case ConsoleKey.UpArrow when f?.Type == "list" && _listSel[f.Key!] > 0: _listSel[f.Key!]--; return Outcome.None;
            case ConsoleKey.DownArrow when f?.Type == "list" && _listSel[f.Key!] < f.ItemList.Count - 1: _listSel[f.Key!]++; return Outcome.None;
            case ConsoleKey.UpArrow: Move(-1); return Outcome.None;
            case ConsoleKey.DownArrow: Move(1); return Outcome.None;
            case ConsoleKey.Enter: return Activate();
        }
        if (f?.Type == "input")
        {
            var text = _inputs[f.Key!];
            if (k.Key == ConsoleKey.Backspace) { if (text.Length > 0) _inputs[f.Key!] = text[..^1]; }
            else if (k.Key == ConsoleKey.U && k.Modifiers.HasFlag(ConsoleModifiers.Control)) _inputs[f.Key!] = "";
            else if (k.KeyChar >= ' ' && !char.IsControl(k.KeyChar) && text.Length < 4000) _inputs[f.Key!] = text + k.KeyChar;
            return Outcome.None;
        }
        // A button hotkey (one letter/digit) presses it from anywhere outside an input.
        char c = char.ToLowerInvariant(k.KeyChar);
        int hot = _focusables.FindIndex(n => n.Type == "button" && n.Hotkey == c);
        if (hot >= 0) { _focus = hot; return Activate(); }
        return Outcome.None;
    }

    /// <summary>Paste into the focused input (one line).</summary>
    internal void Paste(string text)
    {
        if (Focused is { Type: "input" } f)
            _inputs[f.Key!] = (_inputs[f.Key!] + text.Replace("\r", "").Replace('\n', ' ')) is var s && s.Length > 4000 ? s[..4000] : s;
    }

    /// <summary>Wheel / programmatic scroll by <paramref name="rows"/> (negative = up).</summary>
    internal void Scroll(int rows) => _scroll = Math.Max(0, _scroll + rows);

    /// <summary>A click on control <paramref name="index"/> (hit payload); a list row click passes <paramref name="row"/>.</summary>
    internal Outcome Click(int index, int row = -1)
    {
        if (index < 0 || index >= _focusables.Count) return Outcome.None;
        _focus = index;
        var f = _focusables[index];
        if (f.Type == "list" && row >= 0 && row < f.ItemList.Count) { _listSel[f.Key!] = row; return Activate(); }
        return f.Type == "button" ? Activate() : Outcome.None;
    }

    private void Move(int d)
    {
        if (_focusables.Count == 0) { _scroll = Math.Max(0, _scroll + d); return; }
        _focus = (_focus + d + _focusables.Count) % _focusables.Count;
    }

    private Outcome Activate()
    {
        var f = Focused;
        if (f is null) return Outcome.None;
        switch (f.Type)
        {
            case "button": return new(false, f.Key, "press");
            case "input":
            {
                var v = _inputs[f.Key!];
                _inputs[f.Key!] = "";   // like a chat box: the extension decides what to show next
                return new(false, f.Key, "submit", v);
            }
            default:
                return f.ItemList.Count == 0 ? Outcome.None : new(false, f.Key, "select", f.ItemList[_listSel[f.Key!]].Value);
        }
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s);

    private static string Role(string? color) => color switch
    {
        "muted" => TuiComponents.Muted, "accent" => TuiComponents.Accent, "success" => TuiComponents.Ok,
        "warning" => TuiComponents.Warn, "error" => TuiComponents.Err, "info" => Theme.Active.Info, _ => TuiComponents.Text,
    };

    // Rendered rows of a node at width w, plus (row offset, focusable index, height, list row base) hit records.
    private List<string> Layout(ExtensionPanels.Node n, int w, List<(int Row, int Col, int Width, int Index, int ItemRow)> hits, int col)
    {
        w = Math.Max(1, w);
        var rows = new List<string>();
        int fi = n.Focusable ? _focusables.IndexOf(n) : -1;
        bool focused = ShowFocus && fi >= 0 && fi == _focus;
        switch (n.Type)
        {
            case "text":
            {
                string style = Role(n.Color) + (n.Bold ? " bold" : "") + (n.Dim ? " dim" : "");
                foreach (var line in n.Text.Replace("\r\n", "\n").Split('\n'))
                    foreach (var part in TuiMarkup.WrapPlain(line, w)) rows.Add($"[{style}]{Esc(part)}[/]");
                break;
            }
            case "markdown":
                foreach (var line in n.Text.Replace("\r\n", "\n").Split('\n'))
                    rows.AddRange(TuiMarkup.WrapMarkup(TuiMarkdown.ToMarkup(line.TrimEnd()), w));
                break;
            case "code":
                foreach (var line in n.Text.Replace("\r\n", "\n").Split('\n'))
                    foreach (var part in TuiMarkup.WrapPlain(line.Replace("\t", "    "), w)) rows.Add($"[{Theme.Active.MdCode}]{Esc(part)}[/]");
                break;
            case "progress":
            {
                int bar = Math.Max(4, Math.Min(40, w - TuiMarkup.Width(n.Text) - 7));
                int full = (int)Math.Round(n.Value * bar);
                rows.Add($"[{TuiComponents.Accent}]{new string('\u2588', full)}[/][{TuiComponents.Muted}]{new string('\u2591', bar - full)} {(int)Math.Round(n.Value * 100),3}%[/] {Esc(n.Text)}");
                break;
            }
            case "button":
            {
                string hk = n.Hotkey is { } h ? $"{h}: " : "";
                string label = TuiMarkup.TruncatePlain(hk + n.Text, Math.Max(1, w - 4));
                rows.Add(focused ? $"[black on {TuiComponents.Accent}] {Esc(label)} [/]" : $"[{TuiComponents.Accent}][[ {Esc(label)} ]][/]");
                hits.Add((0, col, TuiMarkup.Width(label) + 4, fi, -1));
                break;
            }
            case "input":
            {
                string text = _inputs.GetValueOrDefault(n.Key!, "");
                string shown = text.Length == 0 && !focused ? n.Placeholder ?? "" : text;
                int room = Math.Max(1, w - 4);
                string fit = TuiMarkup.Width(shown) > room ? "\u2026" + shown[^Math.Min(shown.Length, room - 1)..] : shown;
                string body = text.Length == 0 && !focused ? $"[{TuiComponents.Muted}]{Esc(fit)}[/]" : Esc(fit);
                rows.Add($"[{(focused ? TuiComponents.Accent : TuiComponents.Muted)}]\u203a[/] {body}{(focused ? "\u258f" : "")}");
                hits.Add((0, col, w, fi, -1));
                break;
            }
            case "list":
            {
                int sel = _listSel.GetValueOrDefault(n.Key!);
                if (n.ItemList.Count == 0) rows.Add($"[{TuiComponents.Muted}](empty)[/]");
                for (int i = 0; i < n.ItemList.Count; i++)
                {
                    var it = n.ItemList[i];
                    bool cur = i == sel;
                    string marker = cur && focused ? $"[{TuiComponents.Accent}]\u203a[/]" : cur ? $"[{TuiComponents.Muted}]\u203a[/]" : " ";
                    string label = cur ? $"[{TuiComponents.Text} bold]{Esc(it.Label)}[/]" : $"[{TuiComponents.Text}]{Esc(it.Label)}[/]";
                    string detail = it.Detail is { Length: > 0 } d ? $"  [{TuiComponents.Muted}]{Esc(d)}[/]" : "";
                    hits.Add((rows.Count, col, w, fi, i));
                    rows.Add(TuiMarkup.TruncateMarkup($"{marker} {label}{detail}", w, "\u2026"));
                }
                break;
            }
            default: // box
            {
                bool border = n.Border != "none";
                int inner = border ? Math.Max(1, w - 4) : w;
                int innerCol = border ? col + 2 : col;
                var body = new List<string>();
                if (n.Dir == "row" && n.Kids.Count > 0)
                {
                    int gaps = n.Gap * (n.Kids.Count - 1);
                    int each = Math.Max(1, (inner - gaps) / n.Kids.Count);
                    var cols = new List<List<string>>();
                    int x = innerCol;
                    foreach (var kid in n.Kids)
                    {
                        var kidHits = new List<(int, int, int, int, int)>();
                        cols.Add(Layout(kid, each, kidHits, x));
                        hits.AddRange(kidHits.Select(h => (h.Item1 + (border ? 1 : 0), h.Item2, h.Item3, h.Item4, h.Item5)));
                        x += each + n.Gap;
                    }
                    int height = cols.Max(c => c.Count);
                    for (int r = 0; r < height; r++)
                        body.Add(string.Join(new string(' ', n.Gap), cols.Select(c =>
                        {
                            string cell = r < c.Count ? TuiMarkup.TruncateMarkup(c[r], each, "") : "";
                            return cell + new string(' ', Math.Max(0, each - TuiMarkup.MarkupWidth(cell)));
                        })));
                }
                else
                {
                    foreach (var kid in n.Kids)
                    {
                        if (body.Count > 0) for (int g = 0; g < n.Gap; g++) body.Add("");
                        var kidHits = new List<(int, int, int, int, int)>();
                        var kidRows = Layout(kid, inner, kidHits, innerCol);
                        int baseRow = body.Count + (border ? 1 : 0);
                        hits.AddRange(kidHits.Select(h => (h.Item1 + baseRow, h.Item2, h.Item3, h.Item4, h.Item5)));
                        body.AddRange(kidRows);
                    }
                }
                if (!border) { rows.AddRange(body); break; }
                var (tl, tr, bl, br) = n.Border == "round" ? ('\u256d', '\u256e', '\u2570', '\u256f') : ('\u250c', '\u2510', '\u2514', '\u2518');
                string m = TuiComponents.Muted;
                rows.Add($"[{m}]{tl}{new string('\u2500', Math.Max(0, w - 2))}{tr}[/]");
                foreach (var b in body)
                {
                    string cell = TuiMarkup.TruncateMarkup(b, inner, "");
                    rows.Add($"[{m}]\u2502[/] {cell}{new string(' ', Math.Max(0, inner - TuiMarkup.MarkupWidth(cell)))} [{m}]\u2502[/]");
                }
                rows.Add($"[{m}]{bl}{new string('\u2500', Math.Max(0, w - 2))}{br}[/]");
                break;
            }
        }
        return rows;
    }

    /// <summary>When false, no control is drawn as focused (a docked panel the keyboard is not in).</summary>
    internal bool ShowFocus { get; set; } = true;

    /// <summary>
    /// The scrolled body (no header/footer) in at most <paramref name="room"/> rows of <paramref name="width"/>,
    /// keeping the focused control in view. <paramref name="hits"/> gets (row 0-based, col 1-based, width,
    /// payload) per visible control; decode the payload with <see cref="DecodeHit"/>.
    /// </summary>
    internal List<string> RenderBody(int width, int room, List<(int Row, int Col, int Width, int Payload)> hits)
    {
        room = Math.Max(1, room);
        var bodyHits = new List<(int Row, int Col, int Width, int Index, int ItemRow)>();
        var body = Layout(_panel.Root, Math.Max(1, width - 2), bodyHits, 2).Select(r => " " + r).ToList();
        var fh = bodyHits.Where(h => h.Index == _focus).Select(h => h.Row).DefaultIfEmpty(-1);
        int focusTop = fh.Min(), focusBottom = fh.Max();
        if (ShowFocus && _focusables.Count > 0 && focusTop >= 0)
        {
            var f = Focused!;
            if (f.Type == "list") focusTop = focusBottom = bodyHits.First(h => h.Index == _focus && h.ItemRow == _listSel[f.Key!]).Row;
            if (focusTop < _scroll) _scroll = focusTop;
            if (focusBottom >= _scroll + room) _scroll = focusBottom - room + 1;
        }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, body.Count - room));
        BodyRows = body.Count;
        foreach (var h in bodyHits)
        {
            int r = h.Row - _scroll;
            if (r >= 0 && r < room) hits.Add((r, h.Col, h.Width, h.Index * 4096 + (h.ItemRow + 1)));
        }
        return body.Skip(_scroll).Take(room).ToList();
    }

    /// <summary>Total body rows at the last <see cref="RenderBody"/> (for the scroll position label).</summary>
    internal int BodyRows { get; private set; }

    /// <summary>First visible body row (0-based) at the last render.</summary>
    internal int ScrollTop => _scroll;

    /// <summary>Render the panel at <paramref name="width"/> x <paramref name="height"/>, registering click targets.</summary>
    internal List<string> Render(int width, int height, MouseHitMap? hits = null)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        hits?.Begin(width, height);
        string Clip(string s) => TuiMarkup.TruncateMarkup(s, width, "");
        if (width < MinWidth || height < MinHeight)
            return new[] { "Esc: close", $"Resize to {MinWidth}x{MinHeight} for this panel." }
                .Concat(Enumerable.Repeat("", height)).Take(height).Select(Clip).ToList();

        var rows = new List<string>
        {
            $"[{TuiComponents.Accent} bold] {Esc(_panel.Title.ToUpperInvariant())}[/] [{TuiComponents.Muted}]{Esc(_panel.Extension)}[/]",
            TuiComponents.FullRule(width),
        };
        int headerRows = rows.Count, room = height - headerRows - 2;
        var bodyHits = new List<(int Row, int Col, int Width, int Payload)>();
        rows.AddRange(RenderBody(width, room, bodyHits));
        foreach (var h in bodyHits)
            hits?.Add(new HitRegion(headerRows + h.Row + 1, h.Col, 1, h.Width, MouseTargetKind.PromptModalOption, h.Payload));
        while (rows.Count < height - 2) rows.Add("");
        rows.Add(TuiComponents.FullRule(width));
        string more = BodyRows > room ? $" \u00b7 {_scroll + 1}-{Math.Min(BodyRows, _scroll + room)}/{BodyRows}" : "";
        rows.Add($"[{TuiComponents.Muted}] Tab/\u2191\u2193 focus \u00b7 Enter activate \u00b7 PgUp/PgDn scroll \u00b7 Esc close{more}[/]");
        return rows.Take(height).Select(Clip).ToList();
    }

    /// <summary>Decode a hit payload into (control index, list row or -1).</summary>
    internal static (int Index, int Row) DecodeHit(int payload) => (payload / 4096, payload % 4096 - 1);
}
