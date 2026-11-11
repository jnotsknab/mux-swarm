using System.Text.Json;

namespace MuxSwarm.Engine.Tui;

/// <summary>Stable action ids for rebindable shortcuts; these are the keys of <c>Configs/Keybinds.json</c>.</summary>
internal static class KeyAction
{
    public const string Submit = "prompt.submit";
    public const string Newline = "prompt.newline";
    public const string Complete = "prompt.complete";
    public const string CycleEffort = "prompt.cycleEffort";
    public const string Paste = "prompt.paste";
    public const string UndoAttachment = "prompt.undoAttachment";
    public const string Attachments = "prompt.attachments";
    public const string LineStart = "prompt.lineStart";
    public const string LineEnd = "prompt.lineEnd";
    public const string KillToStart = "prompt.killToStart";
    public const string KillToEnd = "prompt.killToEnd";
    public const string DeleteWord = "prompt.deleteWord";
    public const string Cancel = "prompt.cancel";
    public const string Exit = "prompt.exit";
    public const string Search = "prompt.search";
    public const string Expand = "prompt.expand";
    public const string View = "prompt.view";
    public const string Redraw = "prompt.redraw";
    public const string TaskBoard = "prompt.taskBoard";
    public const string TurnCancel = "turn.cancel";
    public const string TurnExpand = "turn.expand";
    public const string TurnView = "turn.view";
    public const string TurnRedraw = "turn.redraw";
    public const string TurnTaskBoard = "turn.taskBoard";
    public const string TurnSteer = "turn.steer";
}

/// <summary>One key chord: a console key plus the exact Ctrl/Alt/Shift set, e.g. <c>Ctrl+J</c>.</summary>
internal readonly record struct KeyChord(ConsoleKey Key, ConsoleModifiers Modifiers)
{
    private const ConsoleModifiers Mods = ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift;

    /// <summary>True when <paramref name="key"/> is this chord. VT terminals deliver printable letters and digits
    /// without a virtual key, so those also match by character; a bare letter chord ignores Shift.</summary>
    public bool Matches(ConsoleKeyInfo key)
    {
        var mods = key.Modifiers & Mods;
        bool printable = Key is >= ConsoleKey.A and <= ConsoleKey.Z or >= ConsoleKey.D0 and <= ConsoleKey.D9;
        if (printable && Modifiers == 0) mods &= ~ConsoleModifiers.Shift;   // bare "q" also matches Q
        if (mods != Modifiers) return false;
        if (key.Key == Key) return true;
        if (!printable || key.Key is not (ConsoleKey.NoName or (ConsoleKey)0) || (Modifiers & ConsoleModifiers.Control) != 0) return false;
        char glyph = Key >= ConsoleKey.D0 && Key <= ConsoleKey.D9 ? (char)('0' + (Key - ConsoleKey.D0)) : (char)Key;
        return char.ToUpperInvariant(key.KeyChar) == glyph;
    }

    /// <summary>Parse <c>[Ctrl+][Alt+][Shift+]Key</c> (case-insensitive). Keys are <see cref="ConsoleKey"/> names
    /// or the aliases Esc, Enter/Return, Up/Down/Left/Right, Space, Del, Ins, PgUp/PgDn, a single letter or digit.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0)) return false;
        ConsoleModifiers mods = 0;
        foreach (var m in parts[..^1])
        {
            ConsoleModifiers flag = m.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ConsoleModifiers.Control,
                "alt" or "meta" or "option" => ConsoleModifiers.Alt,
                "shift" => ConsoleModifiers.Shift,
                _ => 0
            };
            if (flag == 0 || (mods & flag) != 0) return false;
            mods |= flag;
        }
        if (!TryParseKey(parts[^1], out var key)) return false;
        chord = new KeyChord(key, mods);
        return true;
    }

    private static bool TryParseKey(string name, out ConsoleKey key)
    {
        key = default;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            char c = char.ToUpperInvariant(name[0]);
            key = char.IsAsciiDigit(c) ? ConsoleKey.D0 + (c - '0') : (ConsoleKey)c;
            return true;
        }
        ConsoleKey? alias = name.ToLowerInvariant() switch
        {
            "esc" => ConsoleKey.Escape, "return" => ConsoleKey.Enter, "space" => ConsoleKey.Spacebar,
            "up" => ConsoleKey.UpArrow, "down" => ConsoleKey.DownArrow, "left" => ConsoleKey.LeftArrow, "right" => ConsoleKey.RightArrow,
            "del" => ConsoleKey.Delete, "ins" => ConsoleKey.Insert, "pgup" => ConsoleKey.PageUp, "pgdn" => ConsoleKey.PageDown,
            _ => null
        };
        if (alias is { } a) { key = a; return true; }
        return Enum.TryParse(name, ignoreCase: true, out key) && !int.TryParse(name, out _) && Enum.IsDefined(key);
    }

    /// <summary>Display form, e.g. <c>Ctrl+Shift+J</c>, <c>Esc</c>, <c>Up</c>.</summary>
    public override string ToString()
    {
        string name = Key switch
        {
            ConsoleKey.Escape => "Esc", ConsoleKey.Spacebar => "Space",
            ConsoleKey.UpArrow => "Up", ConsoleKey.DownArrow => "Down", ConsoleKey.LeftArrow => "Left", ConsoleKey.RightArrow => "Right",
            >= ConsoleKey.D0 and <= ConsoleKey.D9 => ((char)('0' + (Key - ConsoleKey.D0))).ToString(),
            _ => Key.ToString()
        };
        return (Modifiers.HasFlag(ConsoleModifiers.Control) ? "Ctrl+" : "")
            + (Modifiers.HasFlag(ConsoleModifiers.Alt) ? "Alt+" : "")
            + (Modifiers.HasFlag(ConsoleModifiers.Shift) ? "Shift+" : "") + name;
    }
}

/// <summary>A rebindable shortcut: its action id, where it applies, the built-in default chords, and the
/// chords in effect (<see cref="Combination"/>, from <c>Configs/Keybinds.json</c> or the defaults).</summary>
internal sealed class KeyBind
{
    public KeyBind(string action, string context, string description, params string[] defaults)
    {
        Action = action; Context = context; Description = description; Defaults = defaults;
        Combination = defaults.Select(d => KeyChord.TryParse(d, out var c) ? c : throw new ArgumentException(d)).ToArray();
    }

    private KeyBind(KeyBind source, IReadOnlyList<KeyChord> combination)
    {
        Action = source.Action; Context = source.Context; Description = source.Description; Defaults = source.Defaults;
        Combination = combination;
    }

    /// <summary>Action id (the JSON key), e.g. <c>prompt.newline</c>.</summary>
    public string Action { get; }
    /// <summary>prompt (input line) or turn (while an agent turn runs).</summary>
    public string Context { get; }
    public string Description { get; }
    public IReadOnlyList<string> Defaults { get; }
    /// <summary>Chords that trigger the action. Empty = unbound.</summary>
    public IReadOnlyList<KeyChord> Combination { get; }

    internal KeyBind With(IReadOnlyList<KeyChord> combination) => new(this, combination);

    public bool Matches(ConsoleKeyInfo key)
    {
        foreach (var chord in Combination) if (chord.Matches(key)) return true;
        return false;
    }

    /// <summary>Chords for display, e.g. <c>Ctrl+J / Ctrl+Enter</c>; "(unbound)" when empty.</summary>
    public string Label => Combination.Count == 0 ? "(unbound)" : string.Join(" / ", Combination);
}

/// <summary>
/// Shortcut registry. Key handlers ask <see cref="Matches"/> instead of comparing keys themselves. The map
/// starts as the built-in defaults; <see cref="Load"/> applies <c>Keybinds.json</c> from the config directory.
/// The file lists only the actions to change: <c>{"prompt.newline": ["Ctrl+J", "Alt+N"]}</c>.
/// </summary>
internal static class KeyBinds
{
    /// <summary>File name inside the directory that holds Config.json.</summary>
    public const string FileName = "Keybinds.json";

    /// <summary>Built-in defaults, in display order.</summary>
    public static readonly KeyBind[] Defaults =
    [
        new(KeyAction.Submit, "prompt", "Submit the current message", "Enter"),
        new(KeyAction.Newline, "prompt", "Insert a newline without submitting (VT terminals send Ctrl+Enter as Ctrl+J)",
            "Ctrl+J", "Ctrl+Enter", "Shift+Enter", "Alt+Enter"),
        new(KeyAction.Complete, "prompt", "Accept the top autocomplete", "Tab"),
        new(KeyAction.CycleEffort, "prompt", "Cycle reasoning effort", "Shift+Tab"),
        new(KeyAction.Paste, "prompt", "Paste clipboard text or screenshot", "Ctrl+V", "Alt+V"),
        new(KeyAction.UndoAttachment, "prompt", "Undo the last attachment insertion/removal", "Ctrl+Z"),
        new(KeyAction.Attachments, "prompt", "Focus paste cards", "F2"),
        new(KeyAction.LineStart, "prompt", "Move cursor to start of line", "Ctrl+A"),
        new(KeyAction.LineEnd, "prompt", "Move cursor to end of line", "Ctrl+E"),
        new(KeyAction.KillToStart, "prompt", "Delete to start of line", "Ctrl+U"),
        new(KeyAction.KillToEnd, "prompt", "Delete to end of line", "Ctrl+K"),
        new(KeyAction.DeleteWord, "prompt", "Delete the previous word", "Ctrl+W"),
        new(KeyAction.Cancel, "prompt", "Cancel the current input line", "Ctrl+C"),
        new(KeyAction.Exit, "prompt", "Exit on an empty line", "Ctrl+D"),
        new(KeyAction.Search, "prompt", "Reverse-search command history", "Ctrl+R"),
        new(KeyAction.Expand, "prompt", "Expand the latest tool result / live panel (else end of line)", "Ctrl+E"),
        new(KeyAction.View, "prompt", "Open the transcript/expand view", "Ctrl+G"),
        new(KeyAction.Redraw, "prompt", "Clear and redraw the screen", "Ctrl+L"),
        new(KeyAction.TaskBoard, "prompt", "Toggle the team TaskBoard strip", "Ctrl+T"),
        new(KeyAction.TurnCancel, "turn", "Cancel the current turn (bare q also cancels)", "Esc", "Ctrl+Q"),
        new(KeyAction.TurnExpand, "turn", "Expand the latest large tool result without cancelling", "Ctrl+E"),
        new(KeyAction.TurnView, "turn", "Open the transcript/expand view without cancelling", "Ctrl+G"),
        new(KeyAction.TurnRedraw, "turn", "Clear and redraw the screen", "Ctrl+L"),
        new(KeyAction.TurnTaskBoard, "turn", "Toggle the team TaskBoard strip", "Ctrl+T"),
        new(KeyAction.TurnSteer, "turn", "Open the steer compose box", "Ctrl+N"),
    ];

    private static readonly IReadOnlyDictionary<string, KeyBind> DefaultMap = Defaults.ToDictionary(b => b.Action);
    private static volatile IReadOnlyDictionary<string, KeyBind> _current = DefaultMap;

    /// <summary>Bindings in effect (defaults until <see cref="Load"/> applies a file).</summary>
    public static IReadOnlyDictionary<string, KeyBind> Current => _current;

    /// <summary>Path of the keybinds file: next to the active Config.json (follows <c>--cfg</c>).</summary>
    public static string FilePath =>
        Path.Combine(Path.GetDirectoryName(PlatformContext.ConfigPath) ?? PlatformContext.ConfigDirectory, FileName);

    /// <summary>True when the in-effect binding for <paramref name="action"/> matches <paramref name="key"/>.</summary>
    public static bool Matches(string action, ConsoleKeyInfo key) => Matches(_current, action, key);

    internal static bool Matches(IReadOnlyDictionary<string, KeyBind> map, string action, ConsoleKeyInfo key)
        => map.TryGetValue(action, out var bind) && bind.Matches(key);

    /// <summary>Display label for <paramref name="action"/>'s chords.</summary>
    public static string Label(string action) => _current.TryGetValue(action, out var b) ? b.Label : "";

    /// <summary>Re-read <see cref="FilePath"/> and make it current. A missing file = defaults. Returns warnings.</summary>
    public static List<string> Load()
    {
        var warnings = new List<string>();
        _current = Read(FilePath, warnings);
        return warnings;
    }

    /// <summary>Read a keybinds file into a map without touching <see cref="Current"/>.</summary>
    internal static IReadOnlyDictionary<string, KeyBind> Read(string path, List<string> warnings)
    {
        if (!File.Exists(path)) return DefaultMap;
        try { return Parse(File.ReadAllText(path), warnings); }
        catch (IOException ex) { warnings.Add($"{FileName}: {ex.Message}; using defaults."); return DefaultMap; }
    }

    /// <summary>Apply a JSON object of <c>action: chord | [chords]</c> over the defaults. Bad JSON keeps every
    /// default; a bad chord is skipped; an empty array unbinds; keys starting with <c>$</c> or <c>_</c> are ignored.</summary>
    internal static IReadOnlyDictionary<string, KeyBind> Parse(string json, List<string> warnings)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { warnings.Add($"{FileName} is not valid JSON ({ex.Message}); using defaults."); return DefaultMap; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            { warnings.Add($"{FileName} must be a JSON object; using defaults."); return DefaultMap; }
            var map = new Dictionary<string, KeyBind>(DefaultMap);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith('$') || prop.Name.StartsWith('_')) continue;
                if (!DefaultMap.TryGetValue(prop.Name, out var bind)) { warnings.Add($"{FileName}: unknown action '{prop.Name}'."); continue; }
                string?[] texts = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => [prop.Value.GetString()],
                    JsonValueKind.Array => prop.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToArray(),
                    _ => [null]
                };
                var chords = new List<KeyChord>();
                foreach (var t in texts)
                {
                    if (KeyChord.TryParse(t, out var c)) { if (!chords.Contains(c)) chords.Add(c); }
                    else warnings.Add($"{FileName}: '{prop.Name}' has an invalid key '{t}'.");
                }
                if (chords.Count == 0 && texts.Length > 0) { warnings.Add($"{FileName}: '{prop.Name}' keeps its defaults."); continue; }
                map[prop.Name] = bind.With(chords);
            }
            return map;
        }
    }

    /// <summary>The full default file (every action with its default chords), for <c>/keybinds init</c>.</summary>
    public static string DefaultJson() => JsonSerializer.Serialize(
        Defaults.ToDictionary(b => b.Action, b => b.Defaults),
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
