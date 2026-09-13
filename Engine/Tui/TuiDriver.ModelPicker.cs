namespace MuxSwarm.Engine.Tui;

internal sealed partial class TuiDriver
{
    /// <summary>Own a full-screen model picker until explicit Apply or cancel. Shares the input pump
    /// and restores frame/inline presentation on every exit. Returns false only when unavailable.</summary>
    public bool RunModelPicker(ModelPickerView view,
        Func<CancellationToken, Task<ProviderModelCatalog.Result>> load,
        Action<string, string, string?> apply,
        CancellationToken cancellationToken = default,
        ConsoleInputPump? input = null)
    {
        var pump = input ?? ConsoleInputPump.Current;
        if (pump is null || pump.Disposed) return false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? request = null;
        Task<ProviderModelCatalog.Result>? loading = null;
        var presenter = _engineFrame ? _frame : new FrameRenderer(_term) { BracketedPaste = _bracketedPaste };
        bool previousPrompt = ConsoleInputPump.PromptActive;
        bool previousModal = ConsoleInputPump.ModalActive;
        lock (MuxConsole.ConsoleLock)
        {
            if (_suspended || _shuttingDown || _navActive || _promptModalActive || _agentViewActive
                || _jobViewActive || _workflowViewActive || ConsoleInputPump.PromptActive || ConsoleInputPump.ModalActive)
                return false;
            _navActive = true;
            ConsoleInputPump.PromptActive = true;
            ConsoleInputPump.ModalActive = true;
            _modalPresenting = true;
            ApplyMouseMode();   // inline: scope mouse tracking to the alt-screen modal
        }
        void Refresh()
        {
            request?.Cancel();
            request?.Dispose();
            if (loading is not null)
                _ = loading.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            view.Loading = true;
            // Catalog implementations capture errors; cancelled older requests are observed below.
            loading = load(request.Token);
        }
        var hits = new MouseHitMap();
        var clicks = new MouseClickTracker();
        void Paint()
        {
            int width = Math.Max(1, _term.Width - 1);
            var rows = view.Render(width, Math.Max(1, _term.Height), hits).Select(TuiMarkup.ToAnsi).ToList();
            lock (MuxConsole.ConsoleLock) presenter.Present(rows);
        }
        try
        {
            Refresh();
            while (!lifetime.IsCancellationRequested && !pump.Disposed && !_shuttingDown)
            {
                if (loading is { IsCompleted: true })
                {
                    try
                    {
                        var result = loading.GetAwaiter().GetResult();
                        view.SetModels(result.Models, result.Error);
                    }
                    catch (OperationCanceledException) { view.Loading = false; }
                    catch { view.SetModels([], "Model discovery failed. F5 retries; F2 allows a manual model."); }
                    loading = null;
                }
                Paint();
                if (!pump.TryTake(out var ev, 100)) continue;
                if (ev.Kind is ConsoleInputPump.EventKind.Paste or ConsoleInputPump.EventKind.InferredPaste)
                {
                    if (ev.PasteText is { } text) view.Paste(text);
                    continue;
                }
                if (ev.Kind == ConsoleInputPump.EventKind.Wheel)
                {
                    view.Handle(new ConsoleKeyInfo('\0', ev.WheelDir > 0 ? ConsoleKey.UpArrow : ConsoleKey.DownArrow, false, false, false), 1);
                    continue;
                }
                if (ev.Kind == ConsoleInputPump.EventKind.Mouse)
                {
                    // GUI convention: single click = move the selection there (the arrows alias);
                    // DOUBLE click = the Enter the keyboard path dispatches (advance pane /
                    // choose); TRIPLE click = the F4 Apply alias (write the staged selection).
                    // A single click never advances or applies.
                    if (clicks.Feed(ev, hits, out var hit, out _, out _, out int clickCount) && hit.Kind == MouseTargetKind.PickerItem)
                    {
                        view.ClickItem(hit.Payload);
                        if (clickCount == 2)
                        {
                            var clickAction = view.Handle(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), Math.Max(1, _term.Height - 10));
                            if (clickAction == ModelPickerView.Action.Cancel) break;
                            if (clickAction == ModelPickerView.Action.Refresh) Refresh();
                        }
                        else if (clickCount == 3)
                        {
                            var applyAction = view.Handle(new ConsoleKeyInfo('\0', ConsoleKey.F4, false, false, false), Math.Max(1, _term.Height - 10));
                            if (applyAction == ModelPickerView.Action.Apply)
                            {
                                try { apply(view.Slot!.Id, view.Model, view.Effort); return true; }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
                                {
                                    view.Message = ex is IOException
                                        ? "Could not save: file changed or unavailable. Close and reopen the picker."
                                        : "Could not save this selection. Check model, effort, and configuration permissions.";
                                }
                            }
                        }
                    }
                    continue;
                }
                if (ev.Kind != ConsoleInputPump.EventKind.Key) continue;
                if (_term.Height < 12 && ev.Key.Key != ConsoleKey.Escape
                    && !(ev.Key.Key == ConsoleKey.Q && ev.Key.Modifiers.HasFlag(ConsoleModifiers.Control))) continue;
                var action = view.Handle(ev.Key, Math.Max(1, _term.Height - 10));
                if (action == ModelPickerView.Action.Cancel) break;
                if (action == ModelPickerView.Action.Refresh) { Refresh(); continue; }
                if (action != ModelPickerView.Action.Apply) continue;
                try
                {
                    apply(view.Slot!.Id, view.Model, view.Effort);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    view.Message = ex is IOException
                        ? "Could not save: file changed or unavailable. Close and reopen the picker."
                        : "Could not save this selection. Check model, effort, and configuration permissions.";
                }
            }
            return true;
        }
        finally
        {
            lifetime.Cancel();
            request?.Cancel();
            request?.Dispose();
            if (loading is not null)
                _ = loading.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            ConsoleInputPump.ModalActive = previousModal;
            ConsoleInputPump.PromptActive = previousPrompt;
            lock (MuxConsole.ConsoleLock)
            {
                _modalPresenting = false;
                ApplyMouseMode();   // inline: hand native selection back with the alt screen
                if (!_engineFrame) presenter.Leave();
                _frame.Invalidate();
                _navActive = false;
                Repaint();
            }
        }
    }
}
