namespace MuxSwarm.Engine.Tui;

internal sealed partial class TuiDriver
{
    /// <summary>Browse agents on the shared input pump in either renderer. Returns handled/cancel distinctly
    /// from unavailable; only explicit Enter returns a definition, with no config or session mutation.</summary>
    internal bool RunAgentPicker(AgentPickerView view, out Common.AgentDefinition? selected,
        CancellationToken cancellationToken = default, ConsoleInputPump? input = null)
    {
        selected = null;
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
            ApplyMouseMode();   // inline: scope mouse tracking to the alt-screen modal
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
                if (ev.Kind == ConsoleInputPump.EventKind.Key &&
                    (ev.Key.Key == ConsoleKey.Escape || (ev.Key.Modifiers.HasFlag(ConsoleModifiers.Control)
                        && ev.Key.Key is ConsoleKey.Q or ConsoleKey.C))) break;
                // Both the last presented frame and current geometry must show a real choice.
                // A grow/resize while waiting cannot turn Enter on a warning into a blind selection.
                if (width < AgentPickerView.MinWidth || height < AgentPickerView.MinHeight
                    || _term.Width - 1 != width || _term.Height != height) continue;
                if (ev.Kind is ConsoleInputPump.EventKind.Paste or ConsoleInputPump.EventKind.InferredPaste)
                {
                    if (ev.PasteText is { } text) view.Paste(text);
                    continue;
                }
                if (ev.Kind == ConsoleInputPump.EventKind.Wheel)
                {
                    if (ev.WheelDir != 0)
                        view.Handle(new ConsoleKeyInfo('\0', ev.WheelDir > 0 ? ConsoleKey.UpArrow : ConsoleKey.DownArrow, false, false, false), 1);
                    continue;
                }
                if (ev.Kind == ConsoleInputPump.EventKind.Mouse)
                {
                    // GUI convention: single click = move the cursor there; DOUBLE click = the
                    // SAME Enter the keyboard path uses for selection intent.
                    if (clicks.Feed(ev, hits, out var hit, out _, out _, out int clickCount) && hit.Kind == MouseTargetKind.PickerItem)
                    {
                        view.ClickItem(hit.Payload);
                        if (clickCount >= 2)
                        {
                            var clickAction = view.Handle(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), Math.Max(1, height - 8));
                            if (clickAction == AgentPickerView.Action.Select) { selected = view.Selected; break; }
                            if (clickAction == AgentPickerView.Action.Cancel) break;
                        }
                    }
                    continue;
                }
                if (ev.Kind != ConsoleInputPump.EventKind.Key) continue;
                var action = view.Handle(ev.Key, Math.Max(1, height - 8));
                if (action == AgentPickerView.Action.Cancel) break;
                if (action != AgentPickerView.Action.Select) continue;
                selected = view.Selected;
                break;
            }
            return true;
        }
        finally
        {
            lock (MuxConsole.ConsoleLock)
            {
                _modalPresenting = false;
                ApplyMouseMode();   // inline: hand native selection back with the alt screen
                // Restore ownership even if output fails during the alternate-screen handback.
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
