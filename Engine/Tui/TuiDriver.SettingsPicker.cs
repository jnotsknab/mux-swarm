namespace MuxSwarm.Engine.Tui;

internal sealed partial class TuiDriver
{
    /// <summary>Browse/edit a detached setting on the shared pump. Return explicit Apply intent only after
    /// restoring renderer/input ownership; config setters execute outside the modal.</summary>
    internal bool RunSettingsPicker(SettingsPickerView view, out TuiConfigCommands.SettingSelection? selected,
        CancellationToken cancellationToken = default, ConsoleInputPump? input = null)
    {
        selected = null;
        var pump = input ?? ConsoleInputPump.Current;
        if (pump is null || pump.Disposed) return false;
        var presenter = _engineFrame ? _frame : new FrameRenderer(_term) { BracketedPaste = _bracketedPaste };
        bool previousPrompt, previousModal;
        Func<bool>? previousComposing;
        lock (MuxConsole.ConsoleLock)
        {
            if (_suspended || _shuttingDown || _navActive || _promptModalActive || _agentViewActive
                || _jobViewActive || _workflowViewActive || ConsoleInputPump.PromptActive || ConsoleInputPump.ModalActive)
                return false;
            previousPrompt = ConsoleInputPump.PromptActive;
            previousModal = ConsoleInputPump.ModalActive;
            previousComposing = ConsoleInputPump.IsComposing;
            _navActive = true;
            // This modal edits text; keep the shared producer's unframed-paste classifier active.
            ConsoleInputPump.IsComposing = () => true;
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
                if (width < SettingsPickerView.MinWidth || height < SettingsPickerView.MinHeight
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
                    // GUI convention: single click = select the setting; DOUBLE click = the SAME
                    // Enter the keyboard path uses to begin editing. Regions only exist while
                    // browsing, so edit mode is inert.
                    if (clicks.Feed(ev, hits, out var hit, out _, out _, out bool isDouble) && hit.Kind == MouseTargetKind.PickerItem)
                    {
                        view.ClickItem(hit.Payload);
                        if (isDouble)
                            view.Handle(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), Math.Max(1, height - 11));
                    }
                    continue;
                }
                if (ev.Kind != ConsoleInputPump.EventKind.Key) continue;
                var action = view.Handle(ev.Key, Math.Max(1, height - 11));
                if (action == SettingsPickerView.Action.Cancel) break;
                if (action != SettingsPickerView.Action.Apply) continue;
                selected = view.Selection;
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
                    ConsoleInputPump.IsComposing = previousComposing;
                    ConsoleInputPump.ModalActive = previousModal;
                    ConsoleInputPump.PromptActive = previousPrompt;
                }
                Repaint();
            }
        }
    }
}
