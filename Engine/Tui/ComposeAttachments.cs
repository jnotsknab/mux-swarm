using System.Text;

namespace MuxSwarm.Engine.Tui;

/// <summary>Display-only ranges over the real draft. Labels never substitute for model payloads.</summary>
internal sealed class ComposeAttachments
{
    internal sealed record Item(int Id, int Start, int Length, string Title, bool Image);
    private readonly List<Item> _items = new();
    private int _nextId;
    private int _previewId = -1, _previewWidth;
    private List<string> _previewRows = new();
    internal IReadOnlyList<Item> Items => _items;
    internal bool Focused { get; private set; }
    internal bool Expanded { get; private set; }
    internal int Selected { get; private set; }
    internal int Scroll { get; private set; }
    internal static bool IsLarge(string text) => text.Length > 1000 || text.Count(c => c == '\n') >= 10;

    internal void Add(int start, string text, string? imageName = null)
    {
        var title = imageName is null
            ? $"Paste #{++_nextId} · {text.Count(c => c == '\n') + 1} lines · {text.Length:N0} chars"
            : $"Image #{++_nextId} · {imageName}";
        _items.Add(new Item(_nextId, start, text.Length, title, imageName is not null));
        _items.Sort((a, b) => a.Start.CompareTo(b.Start));
        Selected = _items.FindIndex(x => x.Id == _nextId);
        Expanded = false; Scroll = 0;
    }

    internal Item[] Snapshot() => _items.ToArray();
    internal bool HidesCursor(int cursor) => _items.Any(x => cursor > x.Start && cursor <= x.Start + x.Length);
    internal void Restore(IEnumerable<Item> items)
    {
        Clear(); _items.AddRange(items);
        _nextId = Math.Max(_nextId, _items.Select(x => x.Id).DefaultIfEmpty(0).Max());
    }

    internal void Clear()
    {
        _items.Clear(); Focused = Expanded = false; Selected = Scroll = 0;
        _previewId = -1; _previewRows.Clear();
    }

    /// <summary>Maintain ranges after an edit. Editing inside a card unfolds it without losing text.</summary>
    internal void Edited(int start, int removed, int added)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            bool overlaps = removed > 0 ? start < item.Start + item.Length && start + removed > item.Start
                : start > item.Start && start < item.Start + item.Length;
            if (overlaps) _items.RemoveAt(i);
            else if (item.Start >= start + removed) _items[i] = item with { Start = item.Start + added - removed };
        }
        Selected = Math.Clamp(Selected, 0, Math.Max(0, _items.Count - 1));
        if (_items.Count == 0) Focused = Expanded = false;
    }

    internal (int Start, int Length) AtomicRange(int start, int length)
    {
        int end = start + length;
        foreach (var item in _items)
            if (start < item.Start + item.Length && end > item.Start)
            { start = Math.Min(start, item.Start); end = Math.Max(end, item.Start + item.Length); }
        return (start, end - start);
    }

    internal int SnapCursor(int cursor, bool forward)
    {
        var item = _items.FirstOrDefault(x => cursor > x.Start && cursor < x.Start + x.Length);
        return item is null ? cursor : forward ? item.Start + item.Length : item.Start;
    }

    /// <summary>Build a compact safe display and map the raw cursor without rewriting the draft.</summary>
    internal (string Text, int Cursor) Display(string raw, int cursor)
    {
        var result = new StringBuilder(); int pos = 0, mapped = cursor;
        foreach (var item in _items)
        {
            if (item.Start < pos || item.Start + item.Length > raw.Length) continue;
            result.Append(raw, pos, item.Start - pos);
            var label = $"[{item.Title}]";
            if (cursor >= item.Start + item.Length) mapped += label.Length - item.Length;
            else if (cursor > item.Start) mapped = result.Length;
            result.Append(label); pos = item.Start + item.Length;
        }
        result.Append(raw, pos, raw.Length - pos);
        return (SafeText(result.ToString()), Math.Clamp(mapped, 0, result.Length));
    }

    internal bool HandleKey(ConsoleKeyInfo key, Action<int, int> remove)
    {
        if (key.Key == ConsoleKey.F2 && _items.Count > 0)
        { Focused = !Focused; Expanded = false; Scroll = 0; return true; }
        if (!Focused || _items.Count == 0) return false;
        if (key.Key == ConsoleKey.Escape) { if (Expanded) Expanded = false; else Focused = false; return true; }
        if (key.Key == ConsoleKey.Enter) { Expanded = !Expanded; Scroll = 0; return true; }
        if (key.Key is ConsoleKey.Delete or ConsoleKey.Backspace)
        { var item = _items[Selected]; remove(item.Start, item.Length); Expanded = false; Scroll = 0; return true; }
        if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow || (!Expanded && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow))
        {
            int delta = key.Key is ConsoleKey.LeftArrow or ConsoleKey.UpArrow ? -1 : 1;
            Selected = Math.Clamp(Selected + delta, 0, _items.Count - 1); Scroll = 0; return true;
        }
        if (Expanded)
        {
            if (key.Key == ConsoleKey.Home) Scroll = 0;
            else if (key.Key == ConsoleKey.End) Scroll = int.MaxValue;
            else if (key.Key is ConsoleKey.UpArrow or ConsoleKey.PageUp) ScrollBy(key.Key == ConsoleKey.PageUp ? -8 : -1);
            else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.PageDown) ScrollBy(key.Key == ConsoleKey.PageDown ? 8 : 1);
            else { Focused = Expanded = false; return false; }
            return true;
        }
        // Typing resumes the draft; Ctrl+C and other editor commands keep their usual meaning.
        Focused = false;
        return false;
    }

    internal void ScrollBy(int delta) => Scroll = (int)Math.Clamp((long)Scroll + delta, 0, int.MaxValue);

    internal List<string> Render(string raw, int width, int maxRows)
    {
        var rows = new List<string>();
        if (_items.Count == 0 || maxRows < 2) return rows;
        string Row(string text, string color) => $"  [{color}]{Spectre.Console.Markup.Escape(TuiMarkup.TruncatePlain(SafeText(text), Math.Max(1, width - 3)))}[/]";
        var selected = _items[Math.Clamp(Selected, 0, _items.Count - 1)];
        if (Expanded && Focused)
        {
            rows.Add(Row($"╭ {selected.Title} · {Selected + 1}/{_items.Count}", TuiComponents.Accent));
            if (_previewId != selected.Id || _previewWidth != width)
            {
                var body = SafeText(raw.Substring(selected.Start, selected.Length));
                _previewRows = body.Split('\n').SelectMany(l => TuiMarkup.WrapPlain(l, Math.Max(1, width - 5))).ToList();
                _previewId = selected.Id; _previewWidth = width;
            }
            var wrapped = _previewRows;
            int count = Math.Max(1, maxRows - 2);
            Scroll = Math.Clamp(Scroll, 0, Math.Max(0, wrapped.Count - count));
            foreach (var line in wrapped.Skip(Scroll).Take(count)) rows.Add(Row("│ " + line, TuiComponents.Text));
            rows.Add(Row($"╰ {Scroll + 1}–{Math.Min(wrapped.Count, Scroll + count)}/{wrapped.Count} · ↑↓ PgUp/PgDn scroll · ←→ item · Esc close", TuiComponents.Muted));
        }
        else
        {
            int count = Math.Max(1, Math.Min(3, maxRows - 1));
            int offset = Math.Clamp(Selected - count + 1, 0, Math.Max(0, _items.Count - count));
            foreach (var item in _items.Skip(offset).Take(count))
                rows.Add(Row($"{(Focused && item.Id == selected.Id ? '›' : '·')} [{item.Title}]", Focused && item.Id == selected.Id ? TuiComponents.Accent : TuiComponents.Muted));
            rows.Add(Row($"{Selected + 1}/{_items.Count} · F2 {(Focused ? "edit" : "cards")} · {(Focused ? "↑↓ select · Enter preview · Delete detach · Esc edit" : "full content kept for submit")}", TuiComponents.Dim));
        }
        return rows;
    }

    /// <summary>Escape terminal controls at the presentation boundary only; keep original model text.</summary>
    internal static string SafeText(string text) => string.Concat(text.Select(c => c == '\t' ? ' ' : char.IsControl(c) && c != '\n' ? '�' : c));
}
