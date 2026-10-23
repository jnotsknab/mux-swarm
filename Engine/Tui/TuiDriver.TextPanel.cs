namespace MuxSwarm.Engine.Tui;

internal sealed partial class TuiDriver
{
    /// <summary>Own a <see cref="TextPanelView"/> on the shared input pump until the user closes it.
    /// False when the view cannot open (another modal owns input, or no pump).</summary>
    internal bool RunTextPanel(TextPanelView view, CancellationToken cancellationToken = default, ConsoleInputPump? input = null)
    {
        var pump = input ?? ConsoleInputPump.Current;
        if (pump is null || pump.Disposed) return false;
        var presenter = _engineFrame ? _frame : new FrameRenderer(_term) { BracketedPaste = _bracketedPaste };
        bool previousPrompt, previousModal;
        lock (MuxConsole.ConsoleLock)
        {
            if (_suspended || _shuttingDown || _navActive || _promptModalActive || _agentViewActive
                || _jobViewActive || _workflowViewActive || ConsoleInputPump.PromptActive || ConsoleInputPump.ModalActive)
                return false;
            previousPrompt = ConsoleInputPump.PromptActive;
            previousModal = ConsoleInputPump.ModalActive;
            _navActive = true;
            ConsoleInputPump.PromptActive = true;
            ConsoleInputPump.ModalActive = true;
            _modalPresenting = true;
            ApplyMouseMode();
        }
        var hits = new MouseHitMap();
        try
        {
            while (!cancellationToken.IsCancellationRequested && !pump.Disposed && !_shuttingDown)
            {
                int outerW = Math.Max(1, _term.Width - 1), outerH = Math.Max(1, _term.Height);
                var (width, height) = ViewFrame.Inner(outerW, outerH);
                ViewFrame.Prepare(hits, outerW, outerH);
                var rows = ViewFrame.Wrap(view.Render(width, height, hits), outerW, outerH).Select(TuiMarkup.ToAnsi).ToList();
                lock (MuxConsole.ConsoleLock) presenter.Present(rows);
                if (!pump.TryTake(out var ev, 100)) continue;
                int page = TextPanelView.PageSize(height);
                if (ev.Kind == ConsoleInputPump.EventKind.Wheel) { if (ev.WheelDir != 0) view.Wheel(ev.WheelDir, page); continue; }
                if (ev.Kind != ConsoleInputPump.EventKind.Key) continue;
                if (!view.Handle(ev.Key, page)) break;
            }
            return true;
        }
        finally
        {
            lock (MuxConsole.ConsoleLock)
            {
                _modalPresenting = false;
                ApplyMouseMode();
                try { if (!_engineFrame) presenter.Leave(); }
                finally
                {
                    _frame.Invalidate();
                    _navActive = false;
                    ConsoleInputPump.ModalActive = previousModal;
                    ConsoleInputPump.PromptActive = previousPrompt;
                }
                Repaint();
            }
        }
    }
}
