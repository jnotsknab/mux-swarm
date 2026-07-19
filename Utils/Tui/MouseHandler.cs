namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Semantic mouse event produced by <see cref="MouseHandler"/> from a raw SGR report. The frame
/// engine is the only surface that enables mouse tracking; wheel events drive scrollback today, and
/// Press/Release/Move/Drag are carried for future click-to-interact + drag-select work (Phase 2/3).
/// Row/Col are 1-based terminal cell coordinates as reported by the terminal (SGR x=col, y=row).
/// </summary>
internal readonly record struct MouseEvent(MouseEventKind Kind, int Button, int Row, int Col, int WheelDir);

internal enum MouseEventKind
{
    Wheel,     // WheelDir = +1 (up / back into history) or -1 (down / toward live tail)
    Press,     // button down
    Release,   // button up
    Move,      // motion with no button held (only if the terminal reports it)
    Drag,      // motion with a button held
}

/// <summary>
/// Owns mouse tracking for the frame engine: enabling/disabling SGR reporting, translating a raw
/// parsed report (button;x;y;release) into a semantic <see cref="MouseEvent"/>, and dispatching it to
/// the registered sinks. This is the single seam every mouse feature hangs off - wheel scrollback is
/// wired now; click-to-interact and drag-select register additional sinks later without touching the
/// input-loop byte drain in <see cref="TuiDriver"/> (which owns the shared Console.ReadKey/unget
/// machinery and feeds raw reports here via <see cref="Classify"/> + <see cref="Dispatch"/>).
///
/// Design note: the terminal-facing byte drain deliberately stays in TuiDriver because it must share
/// the driver's unget queue and non-blocking read helpers; MouseHandler owns everything ABOVE that -
/// mode lifecycle, classification, and routing - so the interpretation logic is isolated + testable.
/// </summary>
internal sealed class MouseHandler
{
    private readonly ITuiTerminal _term;
    private bool _enabled;

    // Tracks whether a button is currently held so a motion report classifies as Drag vs Move, and
    // so a future Press+Release on the same cell can be recognized as a click. Button id of the
    // press-in-progress, or -1 when none.
    private int _heldButton = -1;

    public MouseHandler(ITuiTerminal term) => _term = term;

    public bool Enabled => _enabled;

    /// <summary>When false (the <c>wheel</c> preset), press/release/move/drag reports are parsed
    /// and swallowed - only wheel scrollback acts. When true (the <c>buttons</c> preset) they are
    /// dispatched to the sinks below (click-to-interact / drag-select groundwork).</summary>
    public bool ButtonsEnabled { get; set; }

    /// <summary>Future Phase 2/3 sinks (click-to-interact, drag-select). Null = ignored today.</summary>
    public System.Action<MouseEvent>? OnPress { get; set; }
    public System.Action<MouseEvent>? OnRelease { get; set; }
    public System.Action<MouseEvent>? OnDrag { get; set; }

    /// <summary>Enable/disable SGR mouse reporting (idempotent). Writing the mode is best-effort;
    /// terminals that do not support it ignore the private-mode set/reset harmlessly.</summary>
    public void SetEnabled(bool on)
    {
        if (_enabled == on) return;
        _enabled = on;
        try { _term.Write(on ? Ansi.MouseTrackingOn : Ansi.MouseTrackingOff); _term.Flush(); }
        catch { /* handing modes to the terminal must never throw */ }
    }

    /// <summary>Classify a raw parsed report into a semantic event. <paramref name="release"/> is the
    /// SGR final byte (M = press/motion, m = release). Returns null for reports we do not model.</summary>
    public MouseEvent? Classify(int button, int col, int row, bool release)
    {
        int dir = MouseSgrParser.WheelDirection(button);
        if (dir != 0)
        {
            // Wheel reports always use the M final byte; direction is all that matters.
            return new MouseEvent(MouseEventKind.Wheel, button, row, col, dir);
        }

        bool motion = (button & 0x20) != 0;   // SGR motion flag (bit 0x20)
        if (release)
        {
            int b = _heldButton;
            _heldButton = -1;
            return new MouseEvent(MouseEventKind.Release, b >= 0 ? b : button, row, col, 0);
        }
        if (motion)
            return new MouseEvent(_heldButton >= 0 ? MouseEventKind.Drag : MouseEventKind.Move, button, row, col, 0);

        // A plain (non-motion, non-release) report is a button press.
        _heldButton = button & 0x03;
        return new MouseEvent(MouseEventKind.Press, button, row, col, 0);
    }

    /// <summary>Route a classified event to the appropriate sink. Wheel is the only wired path today;
    /// Press/Release/Drag are dispatched to their (currently null) sinks so Phase 2/3 is purely
    /// additive. Returns the wheel direction for a wheel event (so the caller can coalesce bursts),
    /// or 0 otherwise.</summary>
    public int Dispatch(MouseEvent ev)
    {
        switch (ev.Kind)
        {
            case MouseEventKind.Wheel:
                return ev.WheelDir;
            case MouseEventKind.Press:
                if (ButtonsEnabled) OnPress?.Invoke(ev);
                return 0;
            case MouseEventKind.Release:
                if (ButtonsEnabled) OnRelease?.Invoke(ev);
                return 0;
            case MouseEventKind.Drag:
                if (ButtonsEnabled) OnDrag?.Invoke(ev);
                return 0;
            default:
                return 0;
        }
    }
}
