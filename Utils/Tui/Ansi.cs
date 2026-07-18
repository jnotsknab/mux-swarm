namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Minimal ANSI escape-sequence builders for the v0.11.0 live-region TUI renderer
/// (Workstream G, Option B). Pure string helpers - no console I/O - so the renderer
/// stays fully unit-testable headless. These deliberately avoid DECSTBM scroll regions
/// and the alternate screen buffer: the live renderer repaints only a bottom "live
/// region" and commits finished transcript lines into the terminal's native scrollback,
/// the way Ink / Claude Code does (preserving scrollback, never stranding a footer).
/// </summary>
internal static class Ansi
{
    public const char ESC = '\u001b';
    public const string CSI = "\u001b[";

    /// <summary>Erase the entire current line (CSI 2K). Cursor column is unchanged.</summary>
    public const string EraseLine = CSI + "2K";

    /// <summary>Erase from the cursor to the end of the display (CSI 0J).</summary>
    public const string EraseDown = CSI + "0J";

    /// <summary>Move the cursor to column 1 of the current line (CSI G).</summary>
    public const string CursorLeft = CSI + "G";

    /// <summary>Reset all SGR attributes (CSI 0m).</summary>
    public const string Reset = CSI + "0m";

    public const string HideCursor = CSI + "?25l";
    public const string ShowCursor = CSI + "?25h";

    public static string CursorUp(int n) => n <= 0 ? "" : CSI + n + "A";
    public static string CursorDown(int n) => n <= 0 ? "" : CSI + n + "B";

    /// <summary>BEL (used to terminate an OSC string, e.g. OSC 52 clipboard).</summary>
    public const char BEL = '\u0007';

    /// <summary>Enter the alternate screen buffer (CSI ?1049h). The primary buffer + scrollback
    /// are preserved and restored verbatim on <see cref="LeaveAltScreen"/>. Used ONLY by the
    /// brief NAV overlay - normal streaming never enters the alt screen, preserving native
    /// scrollback during agent turns.</summary>
    public const string EnterAltScreen = CSI + "?1049h";

    /// <summary>Leave the alternate screen buffer (CSI ?1049l), restoring the primary buffer.</summary>
    public const string LeaveAltScreen = CSI + "?1049l";

    /// <summary>Clear the entire screen (CSI 2J). Pairs with <see cref="Home"/> for a full repaint.</summary>
    public const string ClearScreen = CSI + "2J";

    /// <summary>Move the cursor to the home position, row 1 col 1 (CSI H).</summary>
    public const string Home = CSI + "H";

    /// <summary>Position the cursor at 1-based (row, col) (CSI row;col H).</summary>
    public static string MoveTo(int row, int col) => $"{CSI}{Math.Max(1, row)};{Math.Max(1, col)}H";

    /// <summary>Enable mouse reporting: click (1000) + drag (1002) + SGR extended coords (1006).
    /// Frame engine only, scoped to the prompt read loop so streamed output never races the
    /// parser. SGR encoding is used because it is unambiguous and supports large terminals.</summary>
    public const string MouseOn = CSI + "?1000h" + CSI + "?1002h" + CSI + "?1006h";

    /// <summary>Disable mouse reporting (reverse order of <see cref="MouseOn"/>).</summary>
    public const string MouseOff = CSI + "?1006l" + CSI + "?1002l" + CSI + "?1000l";

    /// <summary>Reverse-video / invert SGR (CSI 7m) - used to paint the NAV selection highlight.</summary>
    public const string Invert = CSI + "7m";

    /// <summary>Disable terminal auto-wrap, DECAWM off (CSI ?7l). Writing a full-width row then no
    /// longer wraps to the next line - which is what stranded a stray row below the NAV footer.
    /// Re-enabled with <see cref="AutoWrapOn"/> on NAV exit.</summary>
    public const string AutoWrapOff = CSI + "?7l";

    /// <summary>Re-enable terminal auto-wrap, DECAWM on (CSI ?7h).</summary>
    public const string AutoWrapOn = CSI + "?7h";

    /// <summary>Enable bracketed-paste mode, DECSET 2004 (CSI ?2004h). The terminal then wraps any
    /// paste in ESC[200~ ... ESC[201~ so the app can buffer a multi-line paste as one literal block
    /// instead of treating the first embedded newline as a submit. Disabled with <see cref="BracketedPasteOff"/>.</summary>
    public const string BracketedPasteOn = CSI + "?2004h";

    /// <summary>Disable bracketed-paste mode, DECRST 2004 (CSI ?2004l).</summary>
    public const string BracketedPasteOff = CSI + "?2004l";

    /// <summary>
    /// Erase <paramref name="count"/> lines ending at (and including) the current line,
    /// leaving the cursor at column 1 of the topmost erased line. Mirrors the well-proven
    /// log-update / ansi-escapes algorithm: erase current line, move up, repeat; then
    /// snap to column 1. With count == 0 this is a no-op.
    /// </summary>
    public static string EraseLines(int count)
    {
        if (count <= 0) return "";
        var sb = new System.Text.StringBuilder(count * 6);
        for (int i = 0; i < count; i++)
        {
            sb.Append(EraseLine);
            if (i < count - 1) sb.Append(CursorUp(1));
        }
        sb.Append(CursorLeft);
        return sb.ToString();
    }

    /// <summary>Truecolor (24-bit) foreground SGR for the given r,g,b.</summary>
    public static string Fg(byte r, byte g, byte b) => $"{CSI}38;2;{r};{g};{b}m";

    /// <summary>Truecolor (24-bit) background SGR for the given r,g,b.</summary>
    public static string Bg(byte r, byte g, byte b) => $"{CSI}48;2;{r};{g};{b}m";

    public const string Bold = CSI + "1m";
    public const string Dim = CSI + "2m";
    public const string Italic = CSI + "3m";
    public const string Underline = CSI + "4m";

    /// <summary>
    /// Begin Synchronized Update (DEC private mode 2026, CSI ?2026h). The terminal buffers
    /// all output until <see cref="EndSyncOutput"/> and then presents the whole batch in a
    /// single atomic frame, so the user never sees a half-painted live region. This is the
    /// standard fix for spinner/timer flicker (used by Ghostty, WezTerm, Windows Terminal,
    /// kitty, etc.). Terminals that do not support it silently ignore the unknown private
    /// mode, so wrapping a frame is always safe.
    /// </summary>
    public const string BeginSyncOutput = CSI + "?2026h";

    /// <summary>End Synchronized Update (CSI ?2026l): flush the buffered frame atomically.</summary>
    public const string EndSyncOutput = CSI + "?2026l";
}
