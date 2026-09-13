namespace MuxSwarm.Engine.Tui;

/// <summary>Translate standard CSI/SS3 keyboard sequences to the same keys as native console adapters.</summary>
internal static class VtKeyboard
{
    /// <summary>VT passthrough often has no virtual key; normalize control bytes without changing printable payload.</summary>
    internal static ConsoleKeyInfo Normalize(ConsoleKeyInfo key)
    {
        if (key.Key is not (ConsoleKey.NoName or (ConsoleKey)0)) return key;
        char c = key.KeyChar;
        ConsoleKey code = c switch
        {
            '\r' or '\n' => ConsoleKey.Enter, '\t' => ConsoleKey.Tab,
            '\b' or '\x7f' => ConsoleKey.Backspace, '\x1b' => ConsoleKey.Escape,
            >= '\x01' and <= '\x1a' => (ConsoleKey)((int)ConsoleKey.A + c - 1),
            _ => key.Key
        };
        bool control = c is >= '\x01' and <= '\x1a' && c is not ('\r' or '\n' or '\t' or '\b');
        return new ConsoleKeyInfo(c, code, key.Modifiers.HasFlag(ConsoleModifiers.Shift),
            key.Modifiers.HasFlag(ConsoleModifiers.Alt), control || key.Modifiers.HasFlag(ConsoleModifiers.Control));
    }

    internal static bool TryDecode(string sequence, out ConsoleKeyInfo key)
    {
        key = default;
        if (sequence.Length == 0) return false;
        char final = sequence[^1];
        string[] fields = sequence[..^1].Split(';');
        int Number(int index, int fallback) => index < fields.Length && int.TryParse(fields[index].Split(':')[0], out var n) ? n : fallback;
        int modifiers = Number(1, 1) - 1;
        // Kitty key-release events are not presses; this parser does not request event-type reporting.
        if (fields.Length > 1 && fields[1].EndsWith(":3", StringComparison.Ordinal)) return false;
        ConsoleKey code = final switch
        {
            'A' => ConsoleKey.UpArrow, 'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow, 'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home, 'F' => ConsoleKey.End,
            'P' => ConsoleKey.F1, 'Q' => ConsoleKey.F2, 'R' => ConsoleKey.F3, 'S' => ConsoleKey.F4,
            'Z' => ConsoleKey.Tab, 'M' => ConsoleKey.Enter, _ => ConsoleKey.NoName
        };
        char character = code == ConsoleKey.Tab ? '\t' : code == ConsoleKey.Enter ? '\r' : '\0';
        if (final == 'Z') modifiers |= 1;
        if (final == '~')
        {
            code = Number(0, 0) switch
            {
                1 or 7 => ConsoleKey.Home, 2 => ConsoleKey.Insert, 3 => ConsoleKey.Delete,
                4 or 8 => ConsoleKey.End, 5 => ConsoleKey.PageUp, 6 => ConsoleKey.PageDown,
                11 => ConsoleKey.F1, 12 => ConsoleKey.F2, 13 => ConsoleKey.F3, 14 => ConsoleKey.F4,
                15 => ConsoleKey.F5, 17 => ConsoleKey.F6, 18 => ConsoleKey.F7, 19 => ConsoleKey.F8,
                20 => ConsoleKey.F9, 21 => ConsoleKey.F10, 23 => ConsoleKey.F11, 24 => ConsoleKey.F12,
                _ => ConsoleKey.NoName
            };
        }
        if (final == 'u' || (final == '~' && Number(0, 0) == 27))
        {
            int value = Number(final == 'u' ? 0 : 2, -1);
            if (value is < 0 or > char.MaxValue or >= 57344 and <= 63743) return false;
            character = (char)value;
            code = value switch
            {
                13 or 10 => ConsoleKey.Enter, 9 => ConsoleKey.Tab, 27 => ConsoleKey.Escape,
                127 or 8 => ConsoleKey.Backspace,
                >= 'a' and <= 'z' => (ConsoleKey)(value - 'a' + (int)ConsoleKey.A),
                >= 'A' and <= 'Z' => (ConsoleKey)value,
                >= '0' and <= '9' => (ConsoleKey)value,
                _ => ConsoleKey.NoName
            };
            if (code == ConsoleKey.NoName && char.IsControl(character)) return false;
        }
        if (code == ConsoleKey.NoName && character == '\0') return false;
        key = new ConsoleKeyInfo(character, code, (modifiers & 1) != 0, (modifiers & 2) != 0, (modifiers & 4) != 0);
        return true;
    }
}
