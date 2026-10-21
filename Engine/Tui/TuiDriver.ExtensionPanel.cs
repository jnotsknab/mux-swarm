using MuxSwarm.Engine.Extensions;

namespace MuxSwarm.Engine.Tui;

internal sealed partial class TuiDriver
{
    /// <summary>
    /// Show open extension panels (<see cref="ExtensionPanels"/>) from the idle prompt, which owns the
    /// input pump: oldest first, one at a time, each in an alt-screen view until the user (Esc) or the
    /// extension (<c>ui/close</c>) closes it. Only while the draft is empty, so a panel never takes over
    /// typing. Returns true when a panel ran (the caller repaints the prompt).
    /// </summary>
    private bool RunOpenPanels(ConsoleInputPump pump)
    {
        if (!_inInput || !_editor.IsEmpty || PastePending || _editor.IsSearching) return false;
        bool ran = false;
        while (ExtensionPanels.All().FirstOrDefault(p => DockedPlacement(p) == "screen") is { } panel && !pump.Disposed && !_shuttingDown)
        {
            if (!RunExtensionPanel(panel, pump)) break;
            ran = true;
        }
        return ran;
    }

    /// <summary>
    /// Run one panel on the shared pump until it closes. The caller owns the pump (idle prompt), so
    /// this borrows it like an in-frame modal. Key/click outcomes become <c>ui/event</c> notifications;
    /// a newer tree (<c>ui/update</c>) is picked up on the next tick. False when another view owns the screen.
    /// </summary>
    internal bool RunExtensionPanel(ExtensionPanels.Panel panel, ConsoleInputPump pump)
    {
        var presenter = _engineFrame ? _frame : new FrameRenderer(_term) { BracketedPaste = _bracketedPaste };
        bool previousPrompt, previousModal;
        lock (MuxConsole.ConsoleLock)
        {
            if (_suspended || _shuttingDown || _navActive || _promptModalActive || _agentViewActive
                || _jobViewActive || _workflowViewActive || ConsoleInputPump.ModalActive)
                return false;
            previousPrompt = ConsoleInputPump.PromptActive;
            previousModal = ConsoleInputPump.ModalActive;
            _navActive = true;
            ConsoleInputPump.PromptActive = true;
            ConsoleInputPump.ModalActive = true;
            _modalPresenting = true;
            ApplyMouseMode();
        }
        var view = new ExtensionPanelView(panel);
        var hits = new MouseHitMap();
        var clicks = new MouseClickTracker();
        void Send(ExtensionPanelView.Outcome o)
        {
            if (o.IsEvent) ExtensionPanels.SendEvent(panel.Extension, panel.Id, o.Key, o.Type!, o.Value);
        }
        try
        {
            while (!pump.Disposed && !_shuttingDown)
            {
                // Closed or replaced by the extension since the last tick.
                if (ExtensionPanels.Get(panel.Extension, panel.Id) is not { } latest) return true;
                if (latest.Version != view.Panel.Version) view.Update(latest);

                int width = Math.Max(1, _term.Width - 1), height = Math.Max(1, _term.Height);
                var rows = view.Render(width, height, hits).Select(TuiMarkup.ToAnsi).ToList();
                lock (MuxConsole.ConsoleLock) presenter.Present(rows);
                if (!pump.TryTake(out var ev, 100)) continue;
                if (ev.Kind is ConsoleInputPump.EventKind.Paste or ConsoleInputPump.EventKind.InferredPaste)
                {
                    if (ev.PasteText is { } text) view.Paste(text);
                    continue;
                }
                if (ev.Kind == ConsoleInputPump.EventKind.Wheel)
                {
                    if (ev.WheelDir != 0) view.Scroll(ev.WheelDir > 0 ? -3 : 3);
                    continue;
                }
                if (ev.Kind == ConsoleInputPump.EventKind.Mouse)
                {
                    if (clicks.Feed(ev, hits, out var hit, out _, out _, out _) && hit.Kind == MouseTargetKind.PromptModalOption)
                    {
                        var (index, row) = ExtensionPanelView.DecodeHit(hit.Payload);
                        Send(view.Click(index, row));
                    }
                    continue;
                }
                if (ev.Kind != ConsoleInputPump.EventKind.Key) continue;
                var outcome = view.Handle(ev.Key, Math.Max(1, height - 6));
                if (outcome.Close)
                {
                    ExtensionPanels.Close(panel.Extension, panel.Id, byUser: true);
                    return true;
                }
                Send(outcome);
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
