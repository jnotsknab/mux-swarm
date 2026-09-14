using System.Text;

namespace MuxSwarm.Engine.Tui;

/// <summary>
/// v0.12.4 experimental full-frame renderer (console.renderEngine = "frame"). A single render
/// owner that takes complete viewport ownership on the ALTERNATE screen and paints a retained
/// semantic frame every present: the driver composes a list of already-ANSI physical rows exactly
/// <c>height</c> tall (visible transcript tail re-wrapped at the CURRENT width + the live
/// stream/tool/footer/input rows pinned at the bottom), and this class diffs them against the last
/// frame and rewrites ONLY the rows that changed, addressed absolutely with <c>CSI row;col H</c>.
///
/// This is the durable fix for the hybrid-model resize artifacts: because the whole viewport is
/// rebuilt from retained state at the live width every frame, a terminal reflow can never strand an
/// old physical row - there is no cursor-relative footer anchor to lose. One synchronized,
/// buffered write + one flush per frame (DEC mode 2026) presents each frame atomically.
///
/// It generalizes the proven NAV painter (TuiDriver.EnterNavMode) into a reusable owner. Not
/// thread-safe by itself - the driver serializes every call through MuxConsole.ConsoleLock, the
/// same invariant the inline <see cref="LiveRegion"/> relies on.
/// </summary>
internal sealed class FrameRenderer
{
    private readonly ITuiTerminal _term;
    private List<string>? _lastRows;   // last-presented physical rows (ANSI); null forces a full redraw
    private List<string>? _lastGutter; // chrome is independent of text width and row wrapping
    private int _lastCols = -1;        // terminal width at last present; a change forces full invalidation
    private bool _entered;             // true once the alternate screen has been entered

    /// <summary>Configured input-framing mode, reasserted whenever this renderer takes screen ownership.</summary>
    internal bool BracketedPaste { get; set; } = true;

    public FrameRenderer(ITuiTerminal term) => _term = term;

    /// <summary>
    /// Present a full viewport frame. <paramref name="rows"/> must already be rendered to ANSI and
    /// SHOULD be exactly the viewport height (the driver pads/clamps). The first present enters the
    /// alternate screen; a width change or an <see cref="Invalidate"/> forces a full clear+redraw,
    /// otherwise only changed rows are rewritten. The whole frame is wrapped in one Synchronized
    /// Output envelope and emitted as a single write+flush so the user never sees a half-paint.
    /// Optional <paramref name="rightGutter"/> cells are addressed at the physical last column,
    /// independently of the content width estimate, and must have the same height as the rows.
    /// </summary>
    public void Present(IReadOnlyList<string> rows, IReadOnlyList<string>? rightGutter = null)
    {
        int columns = Math.Max(1, _term.Width);
        if (rightGutter is not null && rightGutter.Count != rows.Count)
            throw new ArgumentException("Gutter and content must have identical height.", nameof(rightGutter));
        var sb = new StringBuilder(4096);
        // Open the DEC synchronized-output envelope BEFORE the alt-screen switch. On resume from a
        // Spectre prompt, Leave() reset _entered, so this Present re-enters the alt screen. If the
        // EnterAltScreen (CSI ?1049h) were emitted OUTSIDE the envelope, the terminal could flip to
        // a blank alt buffer immediately and show it for one frame before the synchronized clear +
        // full repaint lands - the visible "flicker returning from input". Wrapping the switch,
        // clear, and full paint in one ?2026 envelope reveals only the completed frame atomically.
        sb.Append(Ansi.BeginSyncOutput);
        if (!_entered)
        {
            // Take the alternate screen. The primary buffer + native scrollback are preserved and
            // restored verbatim on Leave(), so exiting the TUI hands the terminal back untouched.
            sb.Append(Ansi.EnterAltScreen);
            _entered = true;
            _lastRows = null;   // nothing on the alt screen yet - force a full paint
        }

        bool full = _lastRows is null
            || _lastRows.Count != rows.Count
            || _lastCols != columns;

        if (full)
        {
            // First paint, geometry change, or forced invalidation: re-assert terminal modes
            // (auto-wrap off so a full-width row can never soft-wrap and strand a line; cursor
            // hidden - the input caret is a synthetic block cell in the composed rows), clear the
            // whole screen, and draw every row absolutely.
            sb.Append(BracketedPaste ? Ansi.BracketedPasteOn : Ansi.BracketedPasteOff);
            sb.Append(Ansi.AutoWrapOff);
            sb.Append(Ansi.HideCursor);
            sb.Append(Ansi.ClearScreen);
            sb.Append(Ansi.Home);
            for (int i = 0; i < rows.Count; i++)
            {
                WriteRowInto(sb, i, rows[i], columns, hasGutter: rightGutter is not null);
                WriteGutterInto(sb, i, columns, rightGutter);
            }
        }
        else
        {
            // Steady state: rewrite only changed rows, then clear their suffix and repaint chrome.
            // A shaded band is never erased before replacement (BCE §4). A
            // no-change frame emits zero row writes (spinner-idle stays O(changed rows), not O(viewport)).
            var last = _lastRows!;
            for (int i = 0; i < rows.Count; i++)
            {
                bool changed = !string.Equals(rows[i], last[i], StringComparison.Ordinal);
                if (changed) WriteRowInto(sb, i, rows[i], columns, hasGutter: rightGutter is not null);
                string cell = rightGutter?[i] ?? " ";
                string previous = _lastGutter?[i] ?? " ";
                // A row rewrite clears its suffix, including the chrome. Reapply the cell even if
                // its semantic state is unchanged. Removing a gutter explicitly blanks old cells.
                if ((changed && rightGutter is not null) || cell != previous)
                    sb.Append(Ansi.MoveTo(i + 1, columns)).Append(Ansi.Reset).Append(cell).Append(Ansi.Reset);
            }
        }
        sb.Append(Ansi.EndSyncOutput);

        try
        {
            _term.Write(sb.ToString());
            _term.Flush();
            // Snapshot only successfully presented content. Caller-owned lists may be reused.
            _lastRows = new List<string>(rows);
            _lastGutter = rightGutter is null ? null : new List<string>(rightGutter);
            _lastCols = columns;
        }
        catch
        {
            Invalidate(); // A partial write is not a valid diff baseline.
            throw;
        }
    }

    /// <summary>
    /// Discard the cached frame so the next <see cref="Present"/> does a full clear+redraw and
    /// re-asserts terminal modes. Used after another surface (e.g. the NAV overlay) has drawn over
    /// the alternate screen, or on a manual redraw (Ctrl+L).
    /// </summary>
    public void Invalidate() => _lastRows = null;

    /// <summary>
    /// Leave the alternate screen and hand the terminal back cleanly: restore auto-wrap, show the
    /// cursor, and pop back to the primary buffer (scrollback intact). Idempotent and
    /// exception-safe so it is safe on suspend / process exit. The next <see cref="Present"/>
    /// re-enters the alternate screen from a clean slate.
    /// </summary>
    public void Leave()
    {
        if (!_entered) return;
        _entered = false;
        _lastRows = null;
        _lastCols = -1;
        try
        {
            _term.Write(Ansi.AutoWrapOn + Ansi.ShowCursor + Ansi.LeaveAltScreen);
            _term.Flush();
        }
        catch { /* handing the terminal back must never throw */ }
    }

    // Visible (display) column width of an already-ANSI row: strip CSI sequences, then measure runes.
    private static readonly System.Text.RegularExpressions.Regex AnsiCsi =
        new("\u001b\\[[0-9;?]*[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int VisibleWidth(string ansiRow) =>
        TuiMarkup.Width(AnsiCsi.Replace(ansiRow, string.Empty));

    // Paint before clearing: never erase a shaded band ahead of its replacement (BCE flash).
    // EL uses the REAL cursor position, not our approximate Unicode width. Padding alone can leave
    // the final cells untouched when a terminal renders a text symbol narrower than our estimator.
    private static void WriteRowInto(StringBuilder sb, int rowIndex, string rowAnsi, int columns, bool hasGutter)
    {
        sb.Append(Ansi.MoveTo(rowIndex + 1, 1)).Append(Ansi.Reset).Append(rowAnsi).Append(Ansi.Reset);
        // With a chrome gutter the suffix clear is UNCONDITIONAL: the estimator can call a row
        // full-width while the terminal rendered it narrower (the U+23BA class), and skipping the
        // clear strands stale cells between the real content end and the rail - the scrollbar-region
        // artifact. The gutter pass repaints the final column right after, so nothing is lost even
        // when the cursor sits on it. Gutter-less presents (modal pickers, content pre-clamped below
        // full width) keep the estimator guard so a genuinely full-width row never erases its last cell.
        if (hasGutter || VisibleWidth(rowAnsi) < columns) sb.Append("\u001b[0K");
    }

    private static void WriteGutterInto(StringBuilder sb, int row, int columns, IReadOnlyList<string>? gutter)
    {
        if (gutter is not null)
            sb.Append(Ansi.MoveTo(row + 1, columns)).Append(Ansi.Reset).Append(gutter[row]).Append(Ansi.Reset);
    }
}
