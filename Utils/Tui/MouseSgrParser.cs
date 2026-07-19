namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Pure parsing for SGR mouse reports (DECSET ?1006). A full report is
/// <c>ESC [ &lt; b ; x ; y (M|m)</c>: <c>M</c> = press/motion, <c>m</c> = release. Only the body
/// <c>b;x;y</c> (button;col;row) is parsed here; the driver consumes the ESC[&lt; prefix and the
/// M/m terminator synchronously (see TuiDriver.TryConsumeMouseReport), so no stateful cross-read
/// machinery is needed - which is what made the earlier char-fed parser leak fragments during fast
/// wheel bursts (it lived in the main input loop and torn reports slipped past its per-char gate).
///
/// SGR wheel buttons: 64 = wheel up, 65 = wheel down (bits above the low 2 button bits carry
/// modifiers/motion; we mask to the low 7 and compare the wheel codes).
/// </summary>
internal static class MouseSgrParser
{
    public const int WheelUp = 64;
    public const int WheelDown = 65;

    /// <summary>Parse a report BODY of the form <c>b;x;y</c> (the text between <c>ESC[&lt;</c> and the
    /// <c>M</c>/<c>m</c> terminator). Returns false for any malformed body. Coordinates are ignored by
    /// the caller for wheel handling but parsed for completeness/validation.</summary>
    public static bool TryParseBody(string body, out int button, out int x, out int y)
    {
        button = x = y = 0;
        if (string.IsNullOrEmpty(body)) return false;

        // Exactly three ';'-separated non-negative integers.
        int p0 = body.IndexOf(';');
        if (p0 <= 0) return false;
        int p1 = body.IndexOf(';', p0 + 1);
        if (p1 <= p0 + 1) return false;
        if (body.IndexOf(';', p1 + 1) >= 0) return false;   // a 4th field -> malformed

        return TryInt(body, 0, p0, out button)
            && TryInt(body, p0 + 1, p1, out x)
            && TryInt(body, p1 + 1, body.Length, out y);
    }

    /// <summary>Wheel direction of a parsed button code: +1 = up, -1 = down, 0 = not a wheel event.
    /// SGR encodes wheel events with bit 0x40 set (buttons 64..67); the modifier bits (shift 0x04,
    /// meta 0x08, ctrl 0x10) may also be set, so we test the wheel flag and read the low two bits for
    /// the axis/direction rather than comparing the whole code: low 0 = up, 1 = down (2/3 = the
    /// horizontal wheel, ignored).</summary>
    public static int WheelDirection(int button)
    {
        if ((button & 0x40) == 0) return 0;   // not a wheel event (0x40 = wheel flag)
        int low = button & 0x03;              // 0=up, 1=down, 2=left, 3=right
        if (low == 0) return +1;
        if (low == 1) return -1;
        return 0;                             // horizontal wheel - not used for vertical scrollback
    }

    private static bool TryInt(string s, int start, int end, out int value)
    {
        value = 0;
        if (end <= start) return false;
        long acc = 0;
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9') return false;
            acc = (acc * 10) + (c - '0');
            if (acc > 1_000_000) acc = 1_000_000;   // clamp; coordinates never realistically exceed this
        }
        value = (int)acc;
        return true;
    }
}
