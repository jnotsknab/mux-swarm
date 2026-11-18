namespace MuxSwarm.Engine.Tui;

internal sealed partial class TuiDriver
{
    /// <summary>
    /// Run the alt-screen <c>/extensions</c> panel on the shared pump. Actions that need no console
    /// (toggle, trust, reload, open folder) go to <paramref name="apply"/> while the panel stays up,
    /// so it never flips back to the transcript; the panel returns for the rest (edit, new, close).
    /// Mouse: click selects, double click toggles, wheel scrolls the list or the details pane, and
    /// action-bar buttons click like their keys. Returns false when the TUI cannot host the panel
    /// right now (another modal owns the screen) so the caller falls back to the text list.
    /// </summary>
    internal bool RunExtensionsPanel(ExtensionsPanelView view, out ExtensionsPanelView.Action action,
        CancellationToken cancellationToken = default, ConsoleInputPump? input = null,
        Action<ExtensionsPanelView.Action>? apply = null)
    {
        action = ExtensionsPanelView.Action.Close;
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
        var result = ExtensionsPanelView.Action.Close;
        // True = leave the panel with this action; false = applied in place, keep running.
        bool Finish(ExtensionsPanelView.Action a)
        {
            if (apply is not null && ExtensionsPanelView.InPlace(a)) { apply(a); return false; }
            result = a;
            return true;
        }
        int lastMouseRow = 0, lastMouseCol = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !pump.Disposed && !_shuttingDown)
            {
                int width = Math.Max(1, _term.Width - 1), height = Math.Max(1, _term.Height);
                var rows = view.Render(width, height, hits).Select(TuiMarkup.ToAnsi).ToList();
                lock (MuxConsole.ConsoleLock) presenter.Present(rows);
                if (!pump.TryTake(out var ev, 100)) continue;
                if (width < ExtensionsPanelView.MinWidth || height < ExtensionsPanelView.MinHeight
                    || _term.Width - 1 != width || _term.Height != height)
                {
                    if (ev.Kind == ConsoleInputPump.EventKind.Key && ev.Key.Key == ConsoleKey.Escape) return true;
                    continue;
                }
                if (ev.Kind is ConsoleInputPump.EventKind.Paste or ConsoleInputPump.EventKind.InferredPaste)
                {
                    if (ev.PasteText is { } text) view.Paste(text);
                    continue;
                }
                if (ev.Kind == ConsoleInputPump.EventKind.Wheel)
                {
                    if (ev.WheelDir == 0) continue;
                    // Over the details pane the wheel scrolls it; elsewhere it moves the selection.
                    if (hits.TryHit(lastMouseRow, lastMouseCol, out var under, out _, out _) && under.Kind == MouseTargetKind.ExpandedPanel)
                        view.ScrollDetail(ev.WheelDir > 0 ? -3 : 3);
                    else
                        view.Handle(new ConsoleKeyInfo('\0', ev.WheelDir > 0 ? ConsoleKey.UpArrow : ConsoleKey.DownArrow, false, false, false), 1);
                    continue;
                }
                if (ev.Kind == ConsoleInputPump.EventKind.Mouse)
                {
                    lastMouseRow = ev.MouseRow; lastMouseCol = ev.MouseCol;
                    if (!clicks.Feed(ev, hits, out var hit, out _, out _, out int clickCount)) continue;
                    if (hit.Kind == MouseTargetKind.PickerItem)
                    {
                        view.ClickItem(hit.Payload);
                        if (clickCount == 2 && view.Selected is not null && Finish(ExtensionsPanelView.Action.Toggle)) return true;
                    }
                    else if (hit.Kind == MouseTargetKind.PromptModalOption
                             && view.Click(hit.Payload) is var button and not ExtensionsPanelView.Action.None
                             && Finish(button))
                        return true;
                    else if (hit.Kind == MouseTargetKind.PickerSearchBox)
                        view.Handle(new ConsoleKeyInfo('/', ConsoleKey.Oem2, false, false, false), 1);
                    continue;
                }
                if (ev.Kind != ConsoleInputPump.EventKind.Key) continue;
                var picked = view.Handle(ev.Key, Math.Max(1, height - 10));
                if (picked != ExtensionsPanelView.Action.None && Finish(picked)) return true;
            }
            return true;
        }
        finally
        {
            action = result;
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
