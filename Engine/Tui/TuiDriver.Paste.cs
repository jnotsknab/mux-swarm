namespace MuxSwarm.Engine.Tui;

internal sealed partial class TuiDriver
{
    private TerminalPasteProtocol? _terminalPaste;
    private CancellationTokenSource? _pasteCancellation;
    private long _draftGeneration, _pasteGeneration;
    private Task<ClipboardCapture.Payload>? _pasteTask;
    private readonly LinkedList<ConsoleInputPump.InputEvent> _pasteDeferred = new();
    private string? _pasteStatus;
    private bool _inferredPasteGuard;
    private int _inferredStart = -1, _inferredEnd;

    private void ApplyInferredPaste(string text)
    {
        if (text.Length == 0) return;
        if (_editor.IsSearching)
        {
            foreach (char c in text)
                if (!char.IsControl(c)) _editor.SearchFeed(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
            return;
        }
        bool append = _inferredPasteGuard && _editor.Cursor == _inferredEnd;
        if (!append) _inferredStart = _editor.Cursor;
        _editor.InsertInferredPaste(text);
        _inferredEnd = _editor.Cursor;
        _editor.CollapsePasteRange(_inferredStart, _inferredEnd - _inferredStart);
        _inferredPasteGuard = true;
        _pasteStatus = "Unframed paste staged; F4 sends (or Ctrl+Enter).";
    }
    internal Func<CancellationToken, Task<ClipboardCapture.Payload>> ClipboardReader { get; set; } = ClipboardCapture.ReadAsync;
    internal Func<byte[], string> CaptureWriter { get; set; } = bytes => ClipboardCapture.Save(bytes,
        App.Config.Filesystem.SandboxPath ?? "", App.Config.Filesystem.AllowedPaths ?? []);

    private void BeginClipboardPaste()
    {
        if (_pasteTask is not null) return;
        _pasteCancellation?.Dispose();
        _pasteCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _pasteStatus = "Reading clipboard… Esc cancels paste";
        var token = _pasteCancellation.Token;
        _pasteGeneration = _draftGeneration;
        _pasteTask = Task.Run(() => ClipboardReader(token), token);
    }

    private void ApplyPastePayload(ClipboardCapture.Payload payload)
    {
        try
        {
            if (payload.Error is not null) { _pasteStatus = payload.Error; return; }
            if (payload.Image is not null)
            {
                string path = CaptureWriter(payload.Image);
                _editor.InsertPaste("\n[Attached screenshot: " + path + "]\n", Path.GetFileName(path));
                if (!_inferredPasteGuard) _pasteStatus = null; // Keep an inferred-draft send hint visible.
            }
            else if (!string.IsNullOrEmpty(payload.Text))
            {
                _editor.InsertPaste(payload.Text);
                if (!_inferredPasteGuard) _pasteStatus = null;
            }
            else _pasteStatus = "Clipboard is empty.";
            _paletteSel = -1;
        }
        catch (Exception ex) { _pasteStatus = "Paste not attached: " + ex.Message; }
    }

    private void BeginTextPaste(string text)
    {
        if (text.Length == 0) { BeginClipboardPaste(); return; }
        _pasteCancellation?.Dispose();
        _pasteCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = _pasteCancellation.Token;
        _pasteGeneration = _draftGeneration;
        _pasteTask = Task.Run(async () =>
        {
            try
            {
                var bytes = await ClipboardCapture.ReadImagePathAsync(text, App.Config.Filesystem.AllowedPaths ?? [], token);
                return bytes is null ? new ClipboardCapture.Payload(Text: text) : new ClipboardCapture.Payload(Image: bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new ClipboardCapture.Payload(Text: text); } // preserve text if file detection fails
        }, token);
    }

    private bool TakeDeferred(out ConsoleInputPump.InputEvent ev, bool protocolOnly = false)
    {
        for (var node = _pasteDeferred.First; node is not null; node = node.Next)
        {
            if (protocolOnly && node.Value.Kind != ConsoleInputPump.EventKind.Terminal) continue;
            ev = node.Value; _pasteDeferred.Remove(node); return true;
        }
        ev = default; return false;
    }

    private bool PastePending => _pasteTask is not null || _terminalPaste?.Busy == true;

    private void FinishPasteIfReady()
    {
        _terminalPaste?.Tick();
        if (_pasteTask?.IsCompleted != true) return;
        try
        {
            var payload = _pasteTask.GetAwaiter().GetResult();
            if (_pasteGeneration == _draftGeneration) ApplyPastePayload(payload);
        }
        catch (OperationCanceledException) { _pasteStatus = "Paste cancelled or timed out; draft retained."; }
        catch (Exception ex) { _pasteStatus = "Paste failed: " + ex.Message; }
        finally { _pasteTask = null; _pasteCancellation?.Dispose(); _pasteCancellation = null; }
    }

    private void CancelPendingPaste(bool discardQueued = false)
    {
        // Only a paste that was actually in flight earns the notice: ReadLine's exit calls this on every submit, and
        // a stale "cancelled" line would show in the mid-turn input box (console.inputDuringTurn = "steer").
        bool wasPending = PastePending;
        _pasteCancellation?.Cancel();
        // Observe failures on an abandoned operation; it can never mutate an editor or save a file.
        var cancellation = _pasteCancellation;
        if (_pasteTask is { } abandoned)
            _ = abandoned.ContinueWith(t => { _ = t.Exception; cancellation?.Dispose(); }, TaskScheduler.Default);
        else cancellation?.Dispose();
        _pasteCancellation = null;
        _pasteTask = null;
        _terminalPaste?.Cancel();
        if (discardQueued) _pasteDeferred.Clear();
        if (wasPending) _pasteStatus = "Paste cancelled; draft retained.";
    }

    private void HandleTerminalPaste(string packet) => _terminalPaste?.Handle(packet);

    private void ReceiveTerminalPayload(ClipboardCapture.Payload payload)
    {
        if (payload.Text is { Length: > 0 } text) BeginTextPaste(text);
        else ApplyPastePayload(payload);
    }

    private void RenderCompose(List<string> lines, int width, int availableRows, bool footerInEdge = false)
    {
        var display = _editor.Display;
        _composeMeta.Clear();
        string shape = ComposeShape.Active;
        // Shaped boxes draw their own field; the highlight band is for the classic look only.
        var input = TuiComponents.InputRowsWithCursor(display.Text, display.Cursor, ComposeShape.InnerWidth(shape, width),
            _inputHighlight && shape == "classic", _composeMeta, _turnInput ? TurnInputPlaceholder : null);
        // Keep the cursor neighborhood visible when ordinary composed text itself spans many rows.
        int statusRows = _pasteStatus is null ? 0 : 1;
        int minCardRows = _editor.Attachments.Items.Count == 0 ? 0 : _editor.Attachments.Expanded ? 3 : 2;
        int maxInput = Math.Max(1, Math.Min(Math.Min(6, Height / 3), availableRows - statusRows - minCardRows));
        int caret = _composeMeta.FindIndex(m => m.Caret);
        int start = Math.Clamp(caret - maxInput + 1, 0, Math.Max(0, input.Count - maxInput));
        int cardBudget = Math.Max(0, Math.Min(10, availableRows - Math.Min(input.Count, maxInput) - statusRows));
        lines.AddRange(_editor.Attachments.Render(_editor.Buffer, width, cardBudget));
        if (_pasteStatus is not null)
            lines.Add($"  [{TuiComponents.Muted}]{Spectre.Console.Markup.Escape(TuiMarkup.TruncatePlain(ComposeAttachments.SafeText(_pasteStatus), width - 3))}[/]");
        // Record which live-band lines are visible input rows + their meta-window offset, so
        // ComposeFrameRows can register ComposeArea regions (click-to-position). Meta rows are
        // parallel to `input`; the visible window is input[start .. start+maxInput).
        _composeVisible.Clear();
        _composeLeftCols = 0;
        int shown = Math.Min(maxInput, Math.Max(0, input.Count - start));
        if (shape == "classic")
        {
            for (int i = 0; i < shown; i++)
                _composeVisible.Add((lines.Count + i, start + i));
            lines.AddRange(input.Skip(start).Take(maxInput));
            return;
        }
        string? title = null;
        int titleCols = 0;
        if (footerInEdge)
        {
            // The footer row (built at the edge's width, its two-space lead dropped) becomes the box's title.
            title = BuildFooterRow(ComposeShape.TitleWidth(shape, width) + 2);
            if (title.StartsWith("  ", StringComparison.Ordinal)) title = title[2..];
            titleCols = TuiMarkup.MarkupWidth(title);
        }
        var frame = ComposeShape.Wrap(shape, input.Skip(start).Take(maxInput).ToList(), width, title, titleCols);
        for (int i = 0; i < frame.InputLines.Count; i++)
            _composeVisible.Add((lines.Count + frame.InputLines[i], start + i));
        _composeLeftCols = frame.LeftCols;
        if (frame.TitleLine >= 0)
        {
            _footerLine = lines.Count + frame.TitleLine;
            _footerBadgeShift = frame.TitleCol - 2;   // badge columns were measured with the dropped two-space lead
        }
        else if (footerInEdge) _footerBadges.Clear();   // too narrow for the title: no chips to click
        lines.AddRange(frame.Lines);
    }

    // Columns a compose shape draws before each input row's own lead (box/bubble/smooth border + padding, rail).
    private int _composeLeftCols;

    private void EchoDraft(string fullText)
    {
        if (TuiCommands.OpensInteractivePrompt(fullText)) return;
        var display = _editor.Display.Text;
        CommitLayout(width => TuiComponents.UserEcho(display, width));
    }
}
