namespace MuxSwarm.Engine.Tui;

/// <summary>Pure native settings browser/editor. One draft, no live setters, explicit F4 apply.</summary>
internal sealed class SettingsPickerView
{
    internal enum Action { None, Cancel, Apply }
    internal const int MinWidth = 40;
    internal const int MinHeight = 14;
    private const int MaxValueLength = 65536;
    private readonly TuiConfigCommands.SettingSnapshot[] _settings;
    private readonly LineEditor _search = new();
    private readonly LineEditor _value = new();
    private List<int> _matches;
    private int _index;
    private bool _replaceOnType;
    private bool _dirty;

    /// <summary>Initial key/alias enters its editor; unknown keys start a filtered browser.</summary>
    internal SettingsPickerView(IReadOnlyList<TuiConfigCommands.SettingSnapshot> settings, string? initialKey = null)
    {
        _settings = settings.ToArray();
        _matches = Enumerable.Range(0, _settings.Length).ToList();
        if (!string.IsNullOrWhiteSpace(initialKey))
        {
            int match = Array.FindIndex(_settings, s => s.Name.Equals(initialKey, StringComparison.OrdinalIgnoreCase)
                || s.Aliases.Any(a => a.Equals(initialKey, StringComparison.OrdinalIgnoreCase)));
            if (match >= 0) { _index = match; BeginEdit(); }
            else { _search.SetBuffer(initialKey); Filter(); }
        }
    }
    internal TuiConfigCommands.SettingSnapshot? Selected => _matches.Count == 0 ? null : _settings[_matches[_index]];
    internal bool Editing { get; private set; }
    internal string Query => _search.Buffer;
    internal string Draft => _value.Buffer;
    internal string Message { get; set; } = "One setting per Apply. Browse/edit does not save.";
    internal int MatchCount => _matches.Count;
    internal TuiConfigCommands.SettingSelection? Selection => Selected is { } selected && _dirty && !string.IsNullOrWhiteSpace(Draft)
        ? new(selected.Name, Draft) : null;

    private static string Clean(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    private static int Score(string name, string term)
    {
        int hit = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (hit >= 0) return hit;
        int pos = 0;
        foreach (char c in name) if (pos < term.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(term[pos])) pos++;
        return pos == term.Length ? 1000 + name.Length : -1;
    }
    private void Filter()
    {
        var terms = Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _matches = Enumerable.Range(0, _settings.Length).Select(i =>
        {
            var setting = _settings[i]; int sum = 0;
            foreach (var term in terms)
            {
                int best = new[] { setting.Name }.Concat(setting.Aliases).Select(n => Score(n, term)).Where(n => n >= 0).DefaultIfEmpty(-1).Min();
                if (best < 0 && setting.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) best = 2000;
                if (best < 0) return (Index: i, Score: -1);
                sum += best;
            }
            return (Index: i, Score: setting.Name.Equals(Query, StringComparison.OrdinalIgnoreCase) ? -2 : sum);
        }).Where(x => x.Score != -1).OrderBy(x => x.Score).ThenBy(x => x.Index).Select(x => x.Index).ToList();
        _index = 0;
    }
    private void BeginEdit()
    {
        if (Selected is not { } selected) return;
        Editing = true; _dirty = false; _replaceOnType = true;
        _value.SetBuffer(selected.Sensitive || selected.Current is "(null)" or "(unavailable)" ? "" : selected.Current);
        Message = "F4 applies this draft; Enter/Tab returns to search and discards it. Esc cancels.";
    }
    private string[] Choices => Selected is { } s && s.ValueHint.Contains('|') && !s.ValueHint.Contains('<')
        ? s.ValueHint.Split('|') : Array.Empty<string>();
    private void Cycle(int delta)
    {
        var choices = Choices;
        if (choices.Length == 0) return;
        string current = Draft.Equals("true", StringComparison.OrdinalIgnoreCase) ? "on"
            : Draft.Equals("false", StringComparison.OrdinalIgnoreCase) ? "off" : Draft;
        int index = Array.FindIndex(choices, c => c.Equals(current, StringComparison.OrdinalIgnoreCase));
        _value.SetBuffer(choices[(index + delta + choices.Length) % choices.Length]);
        _dirty = true; _replaceOnType = true;
    }

    /// <summary>Pastes are inert input; never Apply. Reject oversize edits instead of silently truncating.</summary>
    internal void Paste(string text)
    {
        text = Clean(text);
        var target = Editing ? _value : _search;
        int max = Editing ? MaxValueLength : 256;
        if ((_replaceOnType && Editing ? 0 : target.Buffer.Length) + text.Length > max)
        { Message = $"Input is too long for this view (limit {max:N0} characters). No text was inserted."; return; }
        if (Editing && _replaceOnType) target.SetBuffer("");
        _replaceOnType = false;
        target.InsertText(text);
        if (Editing) _dirty = true; else Filter();
    }

    /// <summary>Editing/search actions never mutate configuration; F4 returns an explicit apply intent.</summary>
    internal Action Handle(ConsoleKeyInfo key, int pageSize)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control), alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        if (key.Key == ConsoleKey.Escape || (ctrl && key.Key is ConsoleKey.Q or ConsoleKey.C)) return Action.Cancel;
        if (key.Key == ConsoleKey.F4 && !ctrl && !alt)
        {
            if (Editing && Selection is not null) return Action.Apply;
            Message = "Select a setting and enter a new value before F4 Apply."; return Action.None;
        }
        if (alt) return Action.None;
        if (key.Key is ConsoleKey.Tab or ConsoleKey.Enter && !ctrl)
        {
            if (Editing) { Editing = false; _value.Reset(); _dirty = false; _replaceOnType = false; Message = "Draft discarded. Search or choose another setting."; }
            else BeginEdit();
            return Action.None;
        }
        if (ctrl && key.Key == ConsoleKey.U)
        {
            if (Editing) { _value.Reset(); _dirty = true; _replaceOnType = false; }
            else { _search.Reset(); Filter(); }
            return Action.None;
        }
        if (!Editing)
        {
            if (ctrl) return Action.None;
            int delta = key.Key switch
            {
                ConsoleKey.UpArrow => -1, ConsoleKey.DownArrow => 1,
                ConsoleKey.PageUp => -Math.Max(1, pageSize), ConsoleKey.PageDown => Math.Max(1, pageSize),
                ConsoleKey.Home => -_matches.Count, ConsoleKey.End => _matches.Count, _ => 0
            };
            if (delta != 0) _index = (int)Math.Clamp((long)_index + delta, 0, Math.Max(0, _matches.Count - 1));
            else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) Paste(key.KeyChar.ToString());
            else { EditKey(_search, key); Filter(); }
            return Action.None;
        }
        if (!ctrl && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        { Cycle(key.Key == ConsoleKey.DownArrow ? 1 : -1); return Action.None; }
        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) && !ctrl) { Paste(key.KeyChar.ToString()); return Action.None; }
        if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.Backspace or ConsoleKey.Delete)
        {
            _replaceOnType = false; string before = Draft;
            EditKey(_value, key); if (before != Draft) _dirty = true;
        }
        return Action.None;
    }

    private static void EditKey(LineEditor editor, ConsoleKeyInfo key)
    {
        string text = editor.Buffer;
        int cursor = editor.Cursor;
        var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        int previous = boundaries.LastOrDefault(n => n < cursor);
        int next = boundaries.FirstOrDefault(n => n > cursor, text.Length);
        if (!key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            if (key.Key == ConsoleKey.LeftArrow) { editor.SetBuffer(text, previous); return; }
            if (key.Key == ConsoleKey.RightArrow) { editor.SetBuffer(text, next); return; }
            if (key.Key == ConsoleKey.Backspace && cursor > 0)
            { editor.SetBuffer(text.Remove(previous, cursor - previous), previous); return; }
            if (key.Key == ConsoleKey.Delete && cursor < text.Length)
            { editor.SetBuffer(text.Remove(cursor, next - cursor), cursor); return; }
        }
        editor.Feed(key);
    }

    private static string VisibleEditor(LineEditor editor, int columns)
    {
        string text = Clean(editor.Buffer);
        int cursor = Math.Clamp(editor.Cursor, 0, text.Length);
        string before = text[..cursor], after = text[cursor..];
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(before);
        int start = before.Length, used = 0, end = before.Length;
        // Walk from the caret backward once: long config strings must not cause quadratic renders.
        for (int i = elements.Length - 1; i >= 0; i--)
        {
            int next = elements[i];
            int cells = TuiMarkup.Width(before[next..end]);
            if (used + cells > Math.Max(1, columns - 3)) break;
            start = next; used += cells; end = next;
        }
        return (start > 0 ? "…" : "") + before[start..] + "▏" + after;
    }

    /// <summary>Style-compatible bounded list and value pane; values never appear in searchable metadata.</summary>
    internal List<string> Render(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        string Esc(string text) => Spectre.Console.Markup.Escape(Clean(text));
        string Clip(string text) => TuiMarkup.TruncateMarkup(text, width, "");
        if (width < MinWidth || height < MinHeight)
            return new[] { "Esc: cancel settings", $"Resize to {MinWidth} columns / {MinHeight} rows to edit." }
                .Concat(Enumerable.Repeat("", height)).Take(height).Select(Clip).ToList();
        var rows = new List<string>
        {
            $"[{TuiComponents.Accent} bold] SETTINGS · /set[/]",
            $"[{(Editing ? TuiComponents.Muted : TuiComponents.Accent)}] Search: {Esc(Editing ? Query : VisibleEditor(_search, Math.Max(1, width - 11)))}[/]",
            TuiComponents.FullRule(width)
        };
        int room = height - 11;
        int start = Math.Clamp(_index - room / 2, 0, Math.Max(0, _matches.Count - room));
        for (int i = start; i < Math.Min(_matches.Count, start + room); i++)
        {
            var item = _settings[_matches[i]];
            rows.Add($"[{(i == _index ? TuiComponents.Accent : TuiComponents.Muted)}]{(i == _index ? "›" : " ")} {Esc(item.Name)}[/]");
        }
        if (_matches.Count == 0) rows.Add($"[{TuiComponents.Muted}] No matches. Ctrl+U clears search.[/]");
        while (rows.Count < height - 8) rows.Add("");
        var selected = Selected;
        rows.Add(TuiComponents.FullRule(width));
        rows.Add($"[{TuiComponents.Text}] {Esc(selected?.Description ?? "Choose a setting to edit.")}[/]");
        rows.Add($"[{TuiComponents.Muted}] Current: {Esc(selected?.Sensitive == true ? "(masked)" : selected?.Current ?? "-")} · Type: {Esc(selected?.ValueHint ?? "-")}[/]");
        string draft = selected?.Sensitive == true ? (_dirty ? "(masked draft)▏" : "(enter replacement)▏")
            : VisibleEditor(_value, Math.Max(1, width - 10));
        rows.Add($"[{(Editing ? TuiComponents.Accent : TuiComponents.Muted)}] Draft: {Esc(Editing ? draft : "Enter to edit")}[/]");
        rows.Add($"[{TuiComponents.Warn}] {Esc(Message)}[/]");
        rows.Add($"[{TuiComponents.Muted}] {_matches.Count}/{_settings.Length} settings · {(Editing ? "↑↓ cycle choices; type replaces; arrows edit" : "↑↓/PgUp/PgDn/Home/End select")}[/]");
        rows.Add($"[{TuiComponents.Accent}] F4 Apply · Enter/Tab {(Editing ? "discard draft, search" : "edit selected")}[/]");
        rows.Add($"[{TuiComponents.Muted}] Ctrl+U clear · Esc/Ctrl+Q cancel · No automatic /refresh[/]");
        return rows.Select(Clip).ToList();
    }
}
