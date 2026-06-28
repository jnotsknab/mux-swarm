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
    private List<string> _lastRows = new(); // last-painted physical rows (ANSI), for diffing
    private bool _cursorHidden;
    private int _lastWidth = -1;          // terminal width at last paint; resize invalidates geometry
    // Per-line wrap/render cache (markup -> ANSI physical rows) scoped to one width. Most rows
    // (footer, context meter, input box) are unchanged across spinner ticks, so caching the
    // wrap+ANSI result avoids re-parsing every markup line on every repaint. Cleared on resize.
    private readonly Dictionary<string, List<string>> _rowCache = new(StringComparer.Ordinal);
    private int _rowCacheWidth = -1;
    // Terminal auto-wrap (DECAWM) state. The live region writes hard, pre-wrapped rows with
    // auto-wrap OFF so the emulator can never SOFT-wrap + reflow them on a resize - reflow is
    // what drifted the on-screen row count away from _paintedRows and stranded old frames
    // (the resize-artifact bug). Committed transcript lines are written with auto-wrap ON so
    // normal scrollback still wraps. Toggled idempotently via WrapEscape.
    private bool _autoWrapDisabled;

    public LiveRegion(ITuiTerminal term) => _term = term;

    /// <summary>The markup lines currently pinned in the live region (pre-wrap).</summary>
    public IReadOnlyList<string> CurrentLive => _live;

    /// <summary>Physical rows the live region currently occupies (post-wrap). Test hook.</summary>
    public int PaintedRows => _paintedRows;

    private int Cols => Math.Max(1, _term.Width);

    /// <summary>Visible window height in rows (>=1). Bounds the live region so its in-place
    /// repaint never exceeds the viewport.</summary>
    private int Rows => Math.Max(1, _term.Height);

    /// <summary>
    /// Bound a rendered live frame to the visible window so the cursor-relative erase/repaint
    /// can never overrun the top of the viewport. The live region is repainted with relative
    /// cursor moves (<c>CSI nA</c>), which CLAMP at the top edge and never scroll - so if the
    /// frame is taller than the window the up-move stops short, the erase under-clears, and the
    /// next CommitAbove pushes the un-erased rows permanently into scrollback (the streaming
    /// "artifacts left in buffer" bug). Keeping the frame within the window (minus one headroom
    /// row for the trailing newline) makes <c>_paintedRows</c> always reachable. We retain the
    /// LAST rows because the footer + input + freshest streaming tail live at the bottom and
    /// must stay pinned; earlier rows of an over-long in-progress line reappear via scrollback
    /// once committed.
    /// </summary>
    private List<string> ClampRows(List<string> rows)
    {
        int max = Math.Max(1, Rows - 1);
        if (rows.Count <= max) return rows;
        return rows.GetRange(rows.Count - max, max);
    }

    /// <summary>Expand markup lines into wrapped, ANSI-rendered physical rows.</summary>
    private List<string> RenderPhysicalRows(IReadOnlyList<string> markupLines)
    {
        int cols = Cols;
        if (cols != _rowCacheWidth) { _rowCache.Clear(); _rowCacheWidth = cols; }
        var rows = new List<string>();
        foreach (var ml in markupLines)
        {
            // Cache the wrap+ANSI render per markup line at the current width. WrapMarkupLine is
            // pure for a given (markup, cols), so unchanged lines (footer/meter/input) are served
            // from the cache and never re-parsed on a spinner tick.
            if (!_rowCache.TryGetValue(ml, out var wrappedRows))
            {
                wrappedRows = WrapMarkupLine(ml, cols);
                // Bound the cache: the streaming tail line mutates every token, so distinct keys
                // would otherwise grow without limit over a long turn. The live region only holds a
                // handful of lines per frame, so a small cap keeps the stable rows (footer/meter/
                // input) hot while discarding stale streaming-tail entries.
                if (_rowCache.Count >= 256) _rowCache.Clear();
                _rowCache[ml] = wrappedRows;
            }
            rows.AddRange(wrappedRows);
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

        // Hang-indent: when a logical line is visually indented (leading spaces, or a lead-dot
        // "* " marker that puts its text at col 2), every WRAPPED continuation row must re-apply
        // that indent - otherwise long agent-prose lines soft-wrap back to col 0 and break the
        // aligned column (the flush-left "off-indentation" bug). The hang width is the leading
        // plain-text whitespace of the line, or 2 when the line starts with the U+25CF lead dot.
        string hangIndent = ComputeHangIndent(spans);
        // Reserve room for the hang indent so a continuation row (hangIndent + wrapped text) never
        // EXCEEDS the terminal width. Without this, pieces wrapped to the full `cols` plus the 2-col
        // hang prefix sum to cols+2 and the terminal soft-wraps the overflow back to col 0 - the
        // residual flush-left wrap bug. Wrapping text to (cols - hang) keeps every row within `cols`.
        int textWidth = hangIndent.Length > 0 ? Math.Max(1, cols - hangIndent.Length) : cols;

        void NewRow()
        {
            if (curW > 0 || cur.Length > 0) { cur.Append(Ansi.Reset); }
            outRows.Add(cur.ToString());
            cur.Clear(); curW = 0;
            if (hangIndent.Length > 0) { cur.Append(hangIndent); curW = hangIndent.Length; }
        }

        foreach (var span in spans)
        {
            // Split span text into wrapped chunks honoring embedded newlines.
            foreach (var sub in (span.Text ?? "").Replace("\r\n", "\n").Split('\n').Select((t, i) => (t, i)))
            {
                if (sub.i > 0) NewRow(); // explicit newline inside a span
                string sgr = span.Style.ToAnsi();
                foreach (var piece in WrapPieces(sub.t, textWidth))
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
    /// <summary>
    /// Leading indent to re-apply on each wrapped continuation row, derived from the logical line's
    /// own leading whitespace - or 2 columns when the line opens with the U+25CF lead dot ("* text",
    /// dot at col 0, text at col 2). Returns "" for flush-left lines (continuation stays at col 0).
    /// Capped so a deeply-indented line can never consume the whole width.
    /// </summary>
    private static string ComputeHangIndent(IReadOnlyList<Span> spans)
    {
        if (spans.Count == 0) return "";
        // Reconstruct the line's plain text (the dot is a styled span on its own, so it would be
        // missed by only inspecting spans[0]) to find the column where the BODY text begins. That
        // column is the hang indent every wrapped continuation row re-applies so the block stays in
        // one aligned column. Lead-dot lines ("  * body", dot at col 2) put body at col 4; a plainly
        // indented line uses its own leading whitespace.
        string plain = string.Concat(spans.Select(s => s.Text ?? ""));
        int i = 0;
        while (i < plain.Length && plain[i] == ' ') i++;          // leading whitespace
        if (i < plain.Length && plain[i] == '\u25cf')            // the lead dot ...
        {
            i++;
            while (i < plain.Length && plain[i] == ' ') i++;      // ... and the space(s) after it
        }
        if (i == 0) return "";
        return new string(' ', Math.Min(i, 8));
    }

    private static IEnumerable<Piece> WrapPieces(string text, int cols)
    {
        if (string.IsNullOrEmpty(text)) { yield return new Piece("", false); yield break; }
        var lines = TuiMarkup.WrapPlain(text, cols);
        for (int i = 0; i < lines.Count; i++)
            yield return new Piece(lines[i], i < lines.Count - 1);
    }

    /// <summary>Escape to put auto-wrap into the requested state, or empty if already there.
    /// Idempotent; tracks <see cref="_autoWrapDisabled"/>.</summary>
    private string WrapEscape(bool disabled)
    {
        if (disabled == _autoWrapDisabled) return "";
        _autoWrapDisabled = disabled;
        return disabled ? Ansi.AutoWrapOff : Ansi.AutoWrapOn;
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

    /// <summary>Erase the painted live region from the screen, leaving the cursor at its top.
    /// Relies on live rows having been written with auto-wrap OFF (see <see cref="WrapEscape"/>),
    /// which prevents the emulator from reflowing them on resize - so <c>_paintedRows</c> stays
    /// the true on-screen row count and the erase is exact.</summary>
    private void EraseLiveRegion()
    {
        if (_paintedRows <= 0) return;
        // Live rows were emitted with auto-wrap OFF, so the terminal cannot have soft-wrapped or
        // reflowed them - _paintedRows is still exactly the number of physical rows on screen,
        // even after a resize. Move up over them and erase down.
        _term.Write(Ansi.CursorLeft);
        _term.Write(Ansi.CursorUp(_paintedRows));
        _term.Write(Ansi.EraseDown);
        _paintedRows = 0;
        _lastRows = new List<string>();
    }

    /// <summary>Paint the current live lines, recording how many physical rows they took.</summary>
    private void PaintLiveRegion() => PaintLiveRegion(ClampRows(RenderPhysicalRows(_live)));

    /// <summary>Paint the supplied physical rows, recording how many rows they took.
    /// Overload lets callers pass rows they already rendered to avoid a second wrap pass.</summary>
    private void PaintLiveRegion(List<string> rows)
    {
        rows = ClampRows(rows);
        var sb = new StringBuilder();
        sb.Append(WrapEscape(disabled: true)); // hard rows; never let the terminal reflow them
        for (int i = 0; i < rows.Count; i++)
        {
            sb.Append(Ansi.EraseLine);
            sb.Append(rows[i]);
            sb.Append('\n'); // trailing newline keeps the cursor on a fresh line below
        }
        _term.Write(sb.ToString());
        _paintedRows = rows.Count;
        _lastRows = rows;
        _lastWidth = Cols;
    }

    /// <summary>
    /// Replace the live region contents and repaint in place. Markup lines are pre-wrap;
    /// wrapping to the current width happens here. Pass an empty list to clear it.
    /// </summary>
    public void SetLive(IReadOnlyList<string> markupLines)
    {
        HideCursor();
        _live = markupLines is List<string> l ? new List<string>(l) : markupLines.ToList();

        // Diff-based repaint: render the new physical rows and compare against what is
        // currently on screen. When the row COUNT is unchanged we only rewrite the rows that
        // actually differ (seeking the cursor to each and erasing just that line), which
        // eliminates the full-region teardown that made the footer flicker every spinner
        // tick. When the row count changes we fall back to a full erase+repaint.
        var newRows = ClampRows(RenderPhysicalRows(_live));

        // On a width change the cached _lastRows/_paintedRows describe the OLD geometry, so the
        // in-place diff path would seek to wrong rows and leave trails. Force a full erase+repaint
        // (EraseLiveRegion re-measures the reflowed old content) and skip the fast path.
        bool widthChanged = _lastWidth >= 0 && _lastWidth != Cols;

        if (!widthChanged && _paintedRows > 0 && newRows.Count == _lastRows.Count)
        {
            // Find which rows changed.
            var changed = new List<int>();
            for (int i = 0; i < newRows.Count; i++)
                if (!string.Equals(newRows[i], _lastRows[i], StringComparison.Ordinal))
                    changed.Add(i);

            if (changed.Count == 0) { _term.Flush(); return; } // nothing to do - no paint, no flicker

            // Cursor is on a fresh line just BELOW the last painted row. Row index r counts
            // from the top of the region; distance from the cursor up to row r is
            // (_paintedRows - r). Rewrite each changed row in place.
            var sb = new StringBuilder();
            // Synchronized Output: batch the whole in-place rewrite into ONE atomic frame so
            // the spinner/timer tick never shows a half-erased row (the flicker source).
            sb.Append(Ansi.BeginSyncOutput);
            sb.Append(WrapEscape(disabled: true)); // keep rows un-reflowable during in-place rewrite
            int cursorOffset = 0; // current cursor distance above the home (below-last) line
            foreach (int r in changed)
            {
                int up = _paintedRows - r;             // lines to move up from home to row r
                int delta = up - cursorOffset;
                if (delta > 0) sb.Append(Ansi.CursorUp(delta));
                else if (delta < 0) sb.Append(Ansi.CursorDown(-delta));
                cursorOffset = up;
                sb.Append(Ansi.CursorLeft);
                sb.Append(Ansi.EraseLine);
                sb.Append(newRows[r]);
            }
            // Return the cursor to the home line (below the last row).
            if (cursorOffset > 0) sb.Append(Ansi.CursorDown(cursorOffset));
            sb.Append(Ansi.CursorLeft);
            sb.Append(Ansi.EndSyncOutput);   // present the batched frame atomically
            _term.Write(sb.ToString());
            _lastRows = newRows;
            _term.Flush();
            return;
        }

        // Row count changed, width changed, or first paint: full erase + repaint. Reuse the
        // rows we already rendered above instead of wrapping the frame a second time. The
        // erase+repaint is wrapped in Synchronized Output so it presents as one atomic frame.
        _term.Write(Ansi.BeginSyncOutput);
        EraseLiveRegion();
        PaintLiveRegion(newRows);
        _term.Write(Ansi.EndSyncOutput);
        _term.Flush();
    }

    /// <summary>
    /// Re-lay-out the live region cleanly by clearing the whole viewport and repainting, used
    /// after a terminal RESIZE or on a manual redraw (Ctrl+L). On a width change the emulator
    /// reflows its buffer (conhost and Windows Terminal both re-wrap already-emitted rows
    /// regardless of hard newlines / DECAWM), which drifts the cursor anchor away from the cached
    /// <c>_paintedRows</c> - so any erase relative to that anchor can strand frames. A full
    /// clear is the only approach that is guaranteed artifact-free across emulators; the committed
    /// transcript scrolls up into native scrollback, the standard full-redraw behaviour.
    /// </summary>
    public void ForceRepaint()
    {
        HideCursor();
        // Discard cached geometry: after a reflow neither _paintedRows nor _lastRows nor the wrap
        // cache describe what is actually on screen any more.
        _paintedRows = 0;
        _lastRows = new List<string>();
        _rowCache.Clear();
        _rowCacheWidth = -1;
        _autoWrapDisabled = false; // force WrapEscape to re-emit the off sequence on repaint
        _term.Write(Ansi.BeginSyncOutput + Ansi.ClearScreen + Ansi.Home);
        PaintLiveRegion(ClampRows(RenderPhysicalRows(_live)));
        _term.Write(Ansi.EndSyncOutput);
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
        sb.Append(WrapEscape(disabled: false)); // committed scrollback lines wrap normally
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
        _term.Write(WrapEscape(disabled: false)); // restore auto-wrap before handing the terminal back
        ShowCursor();
        _term.Flush();
    }
}
