using System.Text;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Output sink + terminal metrics the live renderer writes through. Abstracted so the
/// renderer can be driven by an in-memory fake in unit tests (no real console needed).
/// </summary>
internal interface ITuiTerminal
{
    int Width { get; }
    int Height { get; }
    void Write(string s);
    void Flush();
}

/// <summary>
/// The console-backed terminal. Width/Height fall back to sane defaults when the buffer
/// size is unavailable (redirected output, which the TUI never runs under anyway).
/// </summary>
internal sealed class ConsoleTuiTerminal : ITuiTerminal
{
    public int Width
    {
        get { try { return Math.Max(1, Console.BufferWidth); } catch { return 80; } }
    }
    public int Height
    {
        get { try { return Math.Max(1, Console.WindowHeight); } catch { return 24; } }
    }
    public void Write(string s) => Console.Out.Write(s);
    public void Flush() { try { Console.Out.Flush(); } catch { /* ignore */ } }
}

/// <summary>
/// The log-update / live-region renderer that gives the v0.11.0 TUI its Claude-Code feel
/// WITHOUT a DECSTBM scroll region or the alternate screen buffer. A bottom "live region"
/// (streaming message, input box, pinned footer) is repainted in place each frame by
/// erasing the previously-painted physical rows and rewriting them; finished transcript
/// content is "committed" into the terminal's native scrollback ABOVE the live region so
/// normal scrollback keeps working and nothing is ever stranded.
///
/// All wrapping/width math goes through <see cref="TuiMarkup"/>; the class only ever talks
/// to an <see cref="ITuiTerminal"/>, so the full repaint algorithm is unit-testable.
///
/// Not thread-safe by itself - callers serialize through MuxConsole.ConsoleLock.
/// </summary>
internal sealed class LiveRegion
{
    private readonly ITuiTerminal _term;
    private List<string> _live = new();   // current live-region lines (markup, pre-wrap)
    private int _paintedRows;             // physical rows the live region occupies on screen
    private bool _cursorHidden;

    public LiveRegion(ITuiTerminal term) => _term = term;

    /// <summary>The markup lines currently pinned in the live region (pre-wrap).</summary>
    public IReadOnlyList<string> CurrentLive => _live;

    /// <summary>Physical rows the live region currently occupies (post-wrap). Test hook.</summary>
    public int PaintedRows => _paintedRows;

    private int Cols => Math.Max(1, _term.Width);

    /// <summary>Expand markup lines into wrapped, ANSI-rendered physical rows.</summary>
    private List<string> RenderPhysicalRows(IReadOnlyList<string> markupLines)
    {
        int cols = Cols;
        var rows = new List<string>();
        foreach (var ml in markupLines)
        {
            // Wrap on plain width, then re-apply styling per wrapped slice by re-parsing.
            // Simpler + robust: render whole line to ANSI, but wrap by plain text. Because
            // styles reset at each span boundary in ToAnsi, wrapping the MARKUP keeps tags
            // balanced only if we wrap plain and re-style. We wrap the plain text and apply
            // the line's leading style is lost; to keep it correct we wrap per-span instead.
            foreach (var wrapped in WrapMarkupLine(ml, cols))
                rows.Add(wrapped);
        }
        return rows;
    }

    /// <summary>
    /// Wrap a single markup line to <paramref name="cols"/> display columns, preserving
    /// styling, and return each wrapped slice already rendered to ANSI. Works at the span
    /// level so color/attribute tags never get split mid-sequence.
    /// </summary>
    internal static List<string> WrapMarkupLine(string markup, int cols)
    {
        var spans = TuiMarkup.Parse(markup);
        var outRows = new List<string>();
        var cur = new StringBuilder();
        int curW = 0;

        void NewRow()
        {
            if (curW > 0 || cur.Length > 0) { cur.Append(Ansi.Reset); }
            outRows.Add(cur.ToString());
            cur.Clear(); curW = 0;
        }

        foreach (var span in spans)
        {
            // Split span text into wrapped chunks honoring embedded newlines.
            foreach (var sub in (span.Text ?? "").Replace("\r\n", "\n").Split('\n').Select((t, i) => (t, i)))
            {
                if (sub.i > 0) NewRow(); // explicit newline inside a span
                string sgr = span.Style.ToAnsi();
                foreach (var piece in WrapPieces(sub.t, cols))
                {
                    int pw = TuiMarkup.Width(piece.Text);
                    if (curW + pw > cols && curW > 0) NewRow();
                    if (sgr.Length > 0) cur.Append(sgr);
                    cur.Append(piece.Text);
                    if (sgr.Length > 0) cur.Append(Ansi.Reset);
                    curW += pw;
                    if (piece.ForceBreak) NewRow();
                }
            }
        }
        if (cur.Length > 0 || outRows.Count == 0) { if (cur.Length > 0) cur.Append(Ansi.Reset); outRows.Add(cur.ToString()); }
        return outRows;
    }

    private readonly record struct Piece(string Text, bool ForceBreak);

    /// <summary>
    /// Break a plain run into pieces that each fit within <paramref name="cols"/>. Pieces
    /// that exactly fill a row are flagged ForceBreak so the caller starts a new row.
    /// </summary>
    private static IEnumerable<Piece> WrapPieces(string text, int cols)
    {
        if (string.IsNullOrEmpty(text)) { yield return new Piece("", false); yield break; }
        var lines = TuiMarkup.WrapPlain(text, cols);
        for (int i = 0; i < lines.Count; i++)
            yield return new Piece(lines[i], i < lines.Count - 1);
    }

    /// <summary>Hide the cursor for the duration of live painting (idempotent).</summary>
    public void HideCursor()
    {
        if (_cursorHidden) return;
        _term.Write(Ansi.HideCursor);
        _cursorHidden = true;
    }

    /// <summary>Show the cursor (idempotent). Always called on teardown.</summary>
    public void ShowCursor()
    {
        if (!_cursorHidden) return;
        _term.Write(Ansi.ShowCursor);
        _cursorHidden = false;
    }

    /// <summary>Erase the painted live region from the screen, leaving the cursor at its top.</summary>
    private void EraseLiveRegion()
    {
        if (_paintedRows <= 0) return;
        // Cursor is just below the last painted row (on a fresh line). Move up onto the
        // last row, then erase upward.
        _term.Write(Ansi.CursorUp(_paintedRows));
        _term.Write(Ansi.EraseDown);
        _paintedRows = 0;
    }

    /// <summary>Paint the current live lines, recording how many physical rows they took.</summary>
    private void PaintLiveRegion()
    {
        var rows = RenderPhysicalRows(_live);
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            sb.Append(Ansi.EraseLine);
            sb.Append(rows[i]);
            sb.Append('\n'); // trailing newline keeps the cursor on a fresh line below
        }
        _term.Write(sb.ToString());
        _paintedRows = rows.Count;
    }

    /// <summary>
    /// Replace the live region contents and repaint in place. Markup lines are pre-wrap;
    /// wrapping to the current width happens here. Pass an empty list to clear it.
    /// </summary>
    public void SetLive(IReadOnlyList<string> markupLines)
    {
        HideCursor();
        EraseLiveRegion();
        _live = markupLines is List<string> l ? new List<string>(l) : markupLines.ToList();
        PaintLiveRegion();
        _term.Flush();
    }

    /// <summary>
    /// Commit permanent transcript lines into native scrollback ABOVE the live region:
    /// erase the live region, write the committed lines (which scroll naturally), then
    /// repaint the live region beneath them. This is how streamed/finished output becomes
    /// part of normal terminal history while the footer stays pinned at the bottom.
    /// </summary>
    public void CommitAbove(IReadOnlyList<string> markupLines)
        => CommitAbove(markupLines, null);

    /// <summary>
    /// Commit lines above the region and repaint the live region. When <paramref name="newLive"/>
    /// is supplied it replaces the live-region contents before repainting, so callers can
    /// commit + refresh the footer/input atomically (avoids repainting a stale frame).
    /// </summary>
    public void CommitAbove(IReadOnlyList<string> markupLines, IReadOnlyList<string>? newLive)
    {
        HideCursor();
        EraseLiveRegion();
        var sb = new StringBuilder();
        foreach (var ml in markupLines)
            foreach (var row in RenderPhysicalRows(new[] { ml }))
            {
                sb.Append(Ansi.EraseLine);
                sb.Append(row);
                sb.Append('\n');
            }
        _term.Write(sb.ToString());
        if (newLive is not null)
            _live = newLive is List<string> l ? new List<string>(l) : newLive.ToList();
        PaintLiveRegion();
        _term.Flush();
    }

    /// <summary>Convenience: commit a single markup line above the live region.</summary>
    public void CommitLine(string markupLine) => CommitAbove(new[] { markupLine });

    /// <summary>
    /// Erase the live region, drop its contents, and show the cursor. Call before handing
    /// the terminal back (blocking ReadLine, mode switch, exit) so nothing is stranded.
    /// </summary>
    public void Clear()
    {
        EraseLiveRegion();
        _live = new List<string>();
        ShowCursor();
        _term.Flush();
    }
}
