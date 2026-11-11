namespace MuxSwarm.Engine.Tui;

internal sealed partial class TuiDriver
{
    /// <summary>Run the <c>/keybinds</c> panel on the shared pump. Keys go to the panel RAW (it is capturing chords),
    /// so the user's own bindings never intercept them here. True when the modal ran; <paramref name="saved"/> is
    /// set when the user pressed F4. False when the TUI cannot host it right now (another modal owns the screen).</summary>
    internal bool RunKeybindsPanel(KeybindsPanelView view, out bool saved,
        CancellationToken cancellationToken = default, ConsoleInputPump? input = null)
    {
        saved = false;
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
        var clicks = new MouseClickTracker();
        try
        {
            while (!cancellationToken.IsCancellationRequested && !pump.Disposed && !_shuttingDown)
            {
                int width = Math.Max(1, _term.Width - 1), height = Math.Max(1, _term.Height);
                var rows = view.Render(width, height, hits).Select(TuiMarkup.ToAnsi).ToList();
                lock (MuxConsole.ConsoleLock) presenter.Present(rows);
                if (!pump.TryTake(out var ev, 100)) continue;
                if (width < KeybindsPanelView.MinWidth || height < KeybindsPanelView.MinHeight
                    || _term.Width - 1 != width || _term.Height != height) continue;
                if (ev.Kind == ConsoleInputPump.EventKind.Wheel)
                {
                    if (ev.WheelDir != 0 && !view.Capturing)
                        view.Handle(new ConsoleKeyInfo('\0', ev.WheelDir > 0 ? ConsoleKey.UpArrow : ConsoleKey.DownArrow, false, false, false), 1);
                    continue;
                }
                if (ev.Kind == ConsoleInputPump.EventKind.Mouse)
                {
                    // Click selects a row; double click starts capturing a key for it (the Enter alias).
                    if (clicks.Feed(ev, hits, out var hit, out _, out _, out int clickCount) && hit.Kind == MouseTargetKind.PickerItem)
                    {
                        view.ClickItem(hit.Payload);
                        if (clickCount == 2) view.Handle(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), 1);
                    }
                    continue;
                }
                if (ev.Kind != ConsoleInputPump.EventKind.Key) continue;
                var action = view.Handle(ev.Key, Math.Max(1, height - 6));
                if (action == KeybindsPanelView.Action.Cancel) break;
                if (action == KeybindsPanelView.Action.Apply) { saved = true; break; }
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
