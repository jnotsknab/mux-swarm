namespace MuxSwarm.Engine.Tui;

/// <summary>
/// The <c>/keybinds</c> panel: every rebindable action with the keys in effect. Enter (or A) captures the next
/// chord pressed and adds it, Delete removes the last added chord, R resets to the default, U unbinds; F4 saves
/// to Keybinds.json, Esc discards. Pure view (no terminal I/O), so every behavior is testable headlessly.
/// </summary>
internal sealed class KeybindsPanelView
{
    internal enum Action { None, Cancel, Apply }

    internal const int MinWidth = 50;
    internal const int MinHeight = 10;

    private readonly List<KeyBind> _rows;
    private readonly Dictionary<string, List<KeyChord>> _edits = new();
    private int _index;
    private bool _capturing;

    internal KeybindsPanelView(IReadOnlyDictionary<string, KeyBind>? current = null)
    {
        var map = current ?? KeyBinds.Current;
        _rows = KeyBinds.Defaults.Select(d => map.TryGetValue(d.Action, out var b) ? b : d).ToList();
    }

    internal bool Capturing => _capturing;
    internal int Index => _index;
    internal KeyBind Selected => _rows[_index];
    internal string? Message { get; private set; }

    /// <summary>Chords in effect for <paramref name="action"/> including unsaved edits.</summary>
    internal IReadOnlyList<KeyChord> Chords(string action)
        => _edits.TryGetValue(action, out var e) ? e : _rows.First(r => r.Action == action).Combination;

    /// <summary>True when there are unsaved edits that differ from the bindings the panel opened with.</summary>
    internal bool Dirty => _edits.Any(e => !e.Value.SequenceEqual(_rows.First(r => r.Action == e.Key).Combination));

    internal void ClickItem(int row) { _capturing = false; _index = Math.Clamp(row, 0, _rows.Count - 1); }

    private void Set(string action, List<KeyChord> chords) => _edits[action] = chords;

    /// <summary>Actions in the same context (prompt/turn) that also use <paramref name="chord"/>.</summary>
    internal List<string> Conflicts(string action, KeyChord chord)
    {
        string ctx = _rows.First(r => r.Action == action).Context;
        return _rows.Where(r => r.Action != action && r.Context == ctx && Chords(r.Action).Contains(chord))
            .Select(r => r.Action).ToList();
    }

    internal Action Handle(ConsoleKeyInfo key, int pageSize)
    {
        key = WithLetterKey(key);
        if (_capturing)
        {
            _capturing = false;
            if (key.Key == ConsoleKey.Escape && key.Modifiers == 0) { Message = "Capture cancelled."; return Action.None; }
            if (key.Key is ConsoleKey.NoName or (ConsoleKey)0 or ConsoleKey.LeftWindows or ConsoleKey.RightWindows or ConsoleKey.Applications)
            { Message = "That key cannot be bound."; return Action.None; }
            var chord = new KeyChord(key.Key, key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift));
            var list = Chords(Selected.Action).ToList();
            if (list.Contains(chord)) { Message = $"{chord} is already bound here."; return Action.None; }
            list.Add(chord);
            Set(Selected.Action, list);
            var clash = Conflicts(Selected.Action, chord);
            Message = clash.Count == 0 ? $"Added {chord}." : $"Added {chord}; also bound to {string.Join(", ", clash)} (first match wins).";
            return Action.None;
        }

        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (key.Key == ConsoleKey.Escape || (ctrl && key.Key is ConsoleKey.Q or ConsoleKey.C)) return Action.Cancel;
        if (key.Key == ConsoleKey.F4) return Action.Apply;
        if (ctrl || key.Modifiers.HasFlag(ConsoleModifiers.Alt)) return Action.None;
        int delta = key.Key switch
        {
            ConsoleKey.UpArrow => -1, ConsoleKey.DownArrow => 1,
            ConsoleKey.PageUp => -Math.Max(1, pageSize), ConsoleKey.PageDown => Math.Max(1, pageSize),
            ConsoleKey.Home => -_rows.Count, ConsoleKey.End => _rows.Count, _ => 0
        };
        if (delta != 0) { _index = (int)Math.Clamp((long)_index + delta, 0, _rows.Count - 1); Message = null; return Action.None; }
        switch (key.Key)
        {
            case ConsoleKey.Enter or ConsoleKey.A:
                _capturing = true; Message = $"Press the new key for {Selected.Action} (Esc cancels)."; break;
            case ConsoleKey.Delete or ConsoleKey.Backspace:
            {
                var list = Chords(Selected.Action).ToList();
                if (list.Count == 0) { Message = "Nothing to remove."; break; }
                var removed = list[^1]; list.RemoveAt(list.Count - 1);
                Set(Selected.Action, list); Message = $"Removed {removed}."; break;
            }
            case ConsoleKey.R:
                Set(Selected.Action, KeyBinds.Defaults.First(d => d.Action == Selected.Action).Combination.ToList());
                Message = "Reset to default."; break;
            case ConsoleKey.U:
                Set(Selected.Action, []); Message = "Unbound."; break;
        }
        return Action.None;
    }

    /// <summary>VT input delivers printable letters and digits with no virtual key; give them their key code so the
    /// letter commands (A/R/U) and chord capture see them like native console keys.</summary>
    private static ConsoleKeyInfo WithLetterKey(ConsoleKeyInfo key)
    {
        if (key.Key is not (ConsoleKey.NoName or (ConsoleKey)0)) return key;
        char c = char.ToUpperInvariant(key.KeyChar);
        ConsoleKey code = c is >= 'A' and <= 'Z' ? (ConsoleKey)c : c is >= '0' and <= '9' ? ConsoleKey.D0 + (c - '0') : key.Key;
        if (code == key.Key) return key;
        var m = key.Modifiers;
        return new ConsoleKeyInfo(key.KeyChar, code, m.HasFlag(ConsoleModifiers.Shift), m.HasFlag(ConsoleModifiers.Alt), m.HasFlag(ConsoleModifiers.Control));
    }

    /// <summary>The complete set of user overrides after the edits: every action whose chords differ from the
    /// built-in default (so the saved file stays minimal and a reset removes the entry).</summary>
    internal Dictionary<string, string[]> Overrides()
    {
        var result = new Dictionary<string, string[]>();
        foreach (var def in KeyBinds.Defaults)
        {
            var chords = Chords(def.Action);
            if (!chords.SequenceEqual(def.Combination)) result[def.Action] = chords.Select(c => c.ToString()).ToArray();
        }
        return result;
    }

    // ---- render ----------------------------------------------------------------------

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s);

    internal List<string> Render(int width, int height, MouseHitMap? hits = null)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        hits?.Begin(width, height);
        string Clip(string s) => TuiMarkup.TruncateMarkup(s, width, "");
        if (width < MinWidth || height < MinHeight)
            return new[] { "Esc: close", $"Resize to {MinWidth}x{MinHeight} for the keybinds panel." }
                .Concat(Enumerable.Repeat("", height)).Take(height).Select(Clip).ToList();

        var rows = new List<string>
        {
            $"[{TuiComponents.Accent} bold] KEYBINDS[/] [{TuiComponents.Muted}]{Esc(KeyBinds.FilePath)}[/]",
            TuiComponents.FullRule(width),
        };
        int room = height - 5;
        int start = Math.Clamp(_index - room / 2, 0, Math.Max(0, _rows.Count - room));
        int actionW = Math.Min(24, _rows.Max(r => r.Action.Length) + 1);
        // Keys get the room they need (the newline row lists 4+ chords); descriptions take the rest.
        int keysW = Math.Clamp(_rows.Max(r => string.Join(" / ", Chords(r.Action)).Length) + 1, 16, Math.Max(16, width / 2));
        for (int r = 0; r < room; r++)
        {
            int i = start + r;
            if (i >= _rows.Count) { rows.Add(""); continue; }
            var b = _rows[i];
            bool cur = i == _index;
            var chords = Chords(b.Action);
            string keys = chords.Count == 0 ? "(unbound)" : string.Join(" / ", chords);
            if (cur && _capturing) keys = "press a key\u2026";
            bool changed = !chords.SequenceEqual(KeyBinds.Defaults.First(d => d.Action == b.Action).Combination);
            hits?.Add(new HitRegion(rows.Count + 1, 1, 1, width, MouseTargetKind.PickerItem, i));
            string marker = cur ? $"[{TuiComponents.Accent}]\u203a[/]" : " ";
            string name = cur ? $"[{TuiComponents.Text} bold]{Esc(b.Action.PadRight(actionW))}[/]" : $"[{TuiComponents.Muted}]{Esc(b.Action.PadRight(actionW))}[/]";
            string keyCell = $"[{(changed ? TuiComponents.Ok : TuiComponents.Text)}]{Esc(TuiMarkup.TruncatePlain(keys, keysW).PadRight(keysW))}[/]";
            rows.Add($"{marker} {name} {keyCell} [{TuiComponents.Muted}]{Esc(b.Description)}[/]");
        }
        rows.Add(Message is { } m ? $"[{TuiComponents.Accent}] {Esc(m)}[/]" : "");
        rows.Add($"[{TuiComponents.Accent}] Enter/A add key \u00b7 Del remove \u00b7 R reset \u00b7 U unbind \u00b7 F4 save[/]");
        rows.Add($"[{TuiComponents.Muted}] \u2191\u2193 move \u00b7 Esc discard{(Dirty ? " \u00b7 unsaved changes" : "")}[/]");
        return rows.Select(Clip).ToList();
    }
}
