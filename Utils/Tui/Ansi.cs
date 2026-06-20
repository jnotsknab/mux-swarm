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
}
