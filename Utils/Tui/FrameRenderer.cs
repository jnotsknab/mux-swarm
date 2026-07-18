using System.Text;

namespace MuxSwarm.Utils.Tui;

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
    private int _lastCols = -1;        // terminal width at last present; a change forces full invalidation
    private bool _entered;             // true once the alternate screen has been entered

    public FrameRenderer(ITuiTerminal term) => _term = term;

    /// <summary>
    /// Present a full viewport frame. <paramref name="rows"/> must already be rendered to ANSI and
    /// SHOULD be exactly the viewport height (the driver pads/clamps). The first present enters the
    /// alternate screen; a width change or an <see cref="Invalidate"/> forces a full clear+redraw,
    /// otherwise only changed rows are rewritten. The whole frame is wrapped in one Synchronized
    /// Output envelope and emitted as a single write+flush so the user never sees a half-paint.
    /// </summary>
    public void Present(IReadOnlyList<string> rows)
    {
        var sb = new StringBuilder(4096);
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
            || _lastCols != _term.Width;

        sb.Append(Ansi.BeginSyncOutput);
        if (full)
        {
            // First paint, geometry change, or forced invalidation: re-assert terminal modes
            // (auto-wrap off so a full-width row can never soft-wrap and strand a line; cursor
            // hidden - the input caret is a synthetic block cell in the composed rows), clear the
            // whole screen, and draw every row absolutely.
            sb.Append(Ansi.AutoWrapOff);
            sb.Append(Ansi.HideCursor);
            sb.Append(Ansi.ClearScreen);
            sb.Append(Ansi.Home);
            for (int i = 0; i < rows.Count; i++)
                sb.Append(Ansi.MoveTo(i + 1, 1)).Append(rows[i]);
        }
        else
        {
            // Steady state: rewrite only the rows whose ANSI changed. Erase-line before the write
            // fully overwrites the prior row (any width) with no residue. A no-change frame emits
            // zero row writes (spinner-idle stays O(changed rows), not O(viewport)).
            var last = _lastRows!;
            for (int i = 0; i < rows.Count; i++)
                if (!string.Equals(rows[i], last[i], StringComparison.Ordinal))
                    sb.Append(Ansi.MoveTo(i + 1, 1)).Append(Ansi.EraseLine).Append(rows[i]);
        }
        sb.Append(Ansi.EndSyncOutput);

        _lastRows = rows is List<string> l ? l : new List<string>(rows);
        _lastCols = _term.Width;
        _term.Write(sb.ToString());
        _term.Flush();
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
}
