using System.Text.Json;

namespace MuxSwarm.Engine.Tui;

internal sealed partial class TuiDriver
{
    /// <summary>
    /// Own the /history browser (or the bare-/resume picker) on the shared input pump until close.
    /// LIST Enter either opens the session READ view in place (history) or returns the id as the
    /// resume selection (picker). <paramref name="loadSession"/> parses a session id into its
    /// statebag root; failures surface in the view without leaving the modal. Returns false only
    /// when the modal cannot open (another modal owns input / pump gone).
    /// </summary>
    internal bool RunHistoryBrowser(HistoryView view, Func<string, JsonElement?> loadSession,
        out string? resumeId, CancellationToken cancellationToken = default, ConsoleInputPump? input = null)
    {
        resumeId = null;
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
                if (width < HistoryView.MinWidth || height < HistoryView.MinHeight
                    || _term.Width - 1 != width || _term.Height != height) continue;
                if (ev.Kind is ConsoleInputPump.EventKind.Paste or ConsoleInputPump.EventKind.InferredPaste)
                { if (ev.PasteText is { } text) view.Paste(text); continue; }
                if (ev.Kind == ConsoleInputPump.EventKind.Wheel)
                { if (ev.WheelDir != 0) view.Wheel(ev.WheelDir, Math.Max(1, height - 6)); continue; }
                if (ev.Kind == ConsoleInputPump.EventKind.Mouse)
                {
                    if (clicks.Feed(ev, hits, out var hit, out _, out _, out int clickCount)
                        && hit.Kind == MouseTargetKind.PickerItem && view.CurrentMode == HistoryView.Mode.List)
                    {
                        view.ClickItem(hit.Payload);
                        if (clickCount >= 2)
                        {
                            var (a, open) = view.Handle(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), Math.Max(1, height - 6));
                            if (a == HistoryView.Action.Resume) { resumeId = view.SelectedId; break; }
                            if (open is { } id0) TryOpen(view, loadSession, id0);
                        }
                    }
                    continue;
                }
                if (ev.Kind != ConsoleInputPump.EventKind.Key) continue;
                var (action, openRequest) = view.Handle(ev.Key, Math.Max(1, height - 6));
                if (action == HistoryView.Action.Cancel) break;
                if (action == HistoryView.Action.Resume) { resumeId = view.SelectedId; break; }
                if (openRequest is { } id) TryOpen(view, loadSession, id);
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

        static void TryOpen(HistoryView view, Func<string, JsonElement?> loadSession, string id)
        {
            try
            {
                if (loadSession(id) is { } root) view.OpenTranscript(id, root);
            }
            catch { /* unreadable session stays in list mode */ }
        }
    }
}
