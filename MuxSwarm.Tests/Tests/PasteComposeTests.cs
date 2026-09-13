using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Bounded regressions for draft fidelity, capture persistence, MIME framing, and submit ordering.</summary>
[Collection("ConsoleState")]
public class PasteComposeTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0eEAAAAASUVORK5CYII=");
    private static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool ctrl = false) => new(c, key, false, false, ctrl);
    private sealed class Terminal : ITuiTerminal
    {
        internal readonly StringBuilder Output = new();
        internal int ViewWidth = 65, ViewHeight = 24;
        public int Width => ViewWidth;
        public int Height => ViewHeight;
        public void Write(string text) => Output.Append(text);
        internal Action? OnFlush;
        public void Flush() => OnFlush?.Invoke();
    }

    [Fact]
    public void LargePaste_IsDisplayOnly_AtomicRemovalAndHistoryKeepPayload()
    {
        var editor = new LineEditor();
        string text = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"row {i}\t[Paste #1] 漢字"));
        editor.InsertText("before "); editor.InsertPaste(text); editor.InsertText(" after");
        Assert.Equal("before " + text + " after", editor.Buffer);
        Assert.Single(editor.Attachments.Items);
        Assert.DoesNotContain("row 29", editor.Display.Text);
        editor.Remember(editor.Buffer);
        editor.Feed(Key(ConsoleKey.LeftArrow, ctrl: true)); // before final word
        editor.Feed(Key(ConsoleKey.LeftArrow)); // start of trailing separator
        editor.Feed(Key(ConsoleKey.Backspace)); // remove the whole card, not one hidden character
        Assert.Equal("before  after", editor.Buffer);
        Assert.True(editor.UndoAttachment()); Assert.Equal("before " + text + " after", editor.Buffer);
        editor.Reset(); editor.Feed(Key(ConsoleKey.UpArrow));
        Assert.Contains(text, editor.Buffer);
        Assert.Single(editor.Attachments.Items);
        editor.Reset(); editor.InsertText("new "); editor.InsertPaste(text); editor.InsertText(" middle "); editor.InsertPaste(text); editor.InsertText(" end");
        string draft = editor.Buffer;
        editor.Feed(Key(ConsoleKey.UpArrow)); editor.Feed(Key(ConsoleKey.DownArrow));
        Assert.Equal(draft, editor.Buffer); Assert.Equal(2, editor.Attachments.Items.Count);
        var item = editor.Attachments.Items[0]; editor.RemoveRange(item.Start, item.Length);
        Assert.Equal("new  middle " + text + " end", editor.Buffer);
    }

    [Fact]
    public void CardPreview_IsBounded_NavigableAndTerminalSafe()
    {
        var editor = new LineEditor();
        string text = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"line {i} \x1b[2J [red]"));
        editor.InsertPaste(text);
        Assert.True(editor.Attachments.HandleKey(Key(ConsoleKey.F2), editor.RemoveRange));
        editor.Attachments.HandleKey(Key(ConsoleKey.Enter), editor.RemoveRange);
        var first = editor.Attachments.Render(editor.Buffer, 40, 6);
        for (int i = 0; i < 8; i++) editor.Attachments.HandleKey(Key(ConsoleKey.DownArrow), editor.RemoveRange);
        var second = editor.Attachments.Render(editor.Buffer, 40, 6);
        Assert.InRange(second.Count, 1, 6);
        Assert.NotEqual(string.Join("", first), string.Join("", second));
        Assert.DoesNotContain('\x1b', string.Join("", second));
        Assert.Equal(text, editor.Buffer);
        Assert.False(editor.Attachments.HandleKey(Key(ConsoleKey.X, 'x'), editor.RemoveRange));
        Assert.False(editor.Attachments.Focused);
        editor.Attachments.HandleKey(Key(ConsoleKey.F2), editor.RemoveRange);
        editor.Attachments.HandleKey(Key(ConsoleKey.Delete), editor.RemoveRange);
        Assert.Empty(editor.Buffer);
    }

    [Fact]
    public void PlainPasteAndEditsBeforeCard_DoNotCorruptRanges()
    {
        var editor = new LineEditor();
        editor.InsertPaste("short\ntext"); Assert.Empty(editor.Attachments.Items);
        editor.SetBuffer("lead "); editor.InsertPaste(new string('x', 1200));
        editor.Feed(Key(ConsoleKey.Home)); editor.InsertText("prefix ");
        Assert.Equal(12, editor.Attachments.Items[0].Start);
        Assert.Contains("lead [≡ 1]", editor.Display.Text);
        editor.Feed(Key(ConsoleKey.End)); editor.Feed(Key(ConsoleKey.Backspace));
        Assert.Equal("prefix lead ", editor.Buffer);
        editor.InsertPaste(new string('x', 1200) + " @file");
        Assert.False(editor.IsAtFilter); // hidden payload must not open file completion
        editor.ReplaceCurrentToken("@safe"); // even an explicit caller uses actual atomic bounds
        Assert.EndsWith("@safe ", editor.Buffer);
    }

    /// <summary>Image chips hide capture names without changing payloads, cursor mapping, or restored IDs.</summary>
    [Fact]
    public void ImageChips_CompactDisplayPreservesPayloadCursorAndHistory()
    {
        var editor = new LineEditor();
        const string first = "\n[Attached screenshot: C:\\sandbox\\captures\\capture-long-name-one.png]\n";
        const string second = "\n[Attached screenshot: C:\\sandbox\\captures\\capture-long-name-two.png]\n";
        editor.InsertText("before ");
        editor.InsertPaste(first, "capture-long-name-one.png");
        editor.InsertPaste(second, "capture-long-name-two.png");
        editor.InsertText(" after");
        string payload = "before " + first + second + " after";
        const string display = "before [▧ 1][▧ 2] after";
        Assert.Equal(payload, editor.Buffer);
        Assert.Equal(display, editor.Display.Text);
        Assert.Equal(display.Length, editor.Display.Cursor);
        editor.Feed(Key(ConsoleKey.Home));
        Assert.Equal(0, editor.Display.Cursor);
        editor.Feed(Key(ConsoleKey.End));
        for (int i = 0; i < " after".Length; i++) editor.Feed(Key(ConsoleKey.LeftArrow));
        Assert.Equal("before [▧ 1][▧ 2]".Length, editor.Display.Cursor);
        editor.Feed(Key(ConsoleKey.LeftArrow));
        Assert.Equal("before [▧ 1]".Length, editor.Display.Cursor);
        editor.Feed(Key(ConsoleKey.RightArrow));
        editor.Feed(Key(ConsoleKey.Backspace));
        Assert.Equal("before [▧ 1] after", editor.Display.Text);
        Assert.True(editor.UndoAttachment());
        Assert.Equal(payload, editor.Buffer);
        editor.Remember(editor.Buffer);
        editor.Reset(); editor.Feed(Key(ConsoleKey.UpArrow));
        Assert.Equal(payload, editor.Buffer);
        Assert.Equal(display, editor.Display.Text);
        Assert.DoesNotContain("▧", editor.Buffer);
    }

    /// <summary>Image-only drafts avoid duplicate rows; focused details stay navigable and width-bounded.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(65)]
    public void ImageCards_IdleIsQuiet_FocusAndPreviewRetainDetails(int width)
    {
        var editor = new LineEditor();
        const string name = "capture-long-name.png";
        string payload = "[Attached screenshot: C:\\sandbox\\captures\\" + name + "]";
        editor.InsertPaste(payload, name);
        var idle = editor.Attachments.Render(editor.Buffer, width, 6);
        Assert.Equal("  F2 images", TuiMarkup.Plain(Assert.Single(idle)));
        var input = TuiComponents.InputRowsWithCursor(editor.Display.Text, editor.Display.Cursor, width);
        Assert.Single(input);
        Assert.All(input, row => Assert.InRange(TuiMarkup.MarkupWidth(row), 1, width));
        Assert.Equal(5, TuiMarkup.Width("[▧ 1]"));
        editor.Attachments.HandleKey(Key(ConsoleKey.F2), editor.RemoveRange);
        var focused = editor.Attachments.Render(editor.Buffer, width, 6);
        Assert.Contains(focused, row => TuiMarkup.Plain(row).Contains("› [▧ 1]"));
        Assert.DoesNotContain(focused, row => TuiMarkup.Plain(row).Contains(name));
        Assert.All(focused, row => Assert.InRange(TuiMarkup.MarkupWidth(row), 1, width));
        editor.Attachments.HandleKey(Key(ConsoleKey.Enter), editor.RemoveRange);
        var preview = editor.Attachments.Render(editor.Buffer, width, 6);
        Assert.InRange(preview.Count, 1, 6);
        Assert.All(preview, row => Assert.InRange(TuiMarkup.MarkupWidth(row), 1, width));
        Assert.Contains(name, string.Join("\n", editor.Attachments.Render(editor.Buffer, 100, 6).Select(TuiMarkup.Plain)));
        Assert.Equal(payload, editor.Buffer);
        editor.Attachments.HandleKey(Key(ConsoleKey.Escape), editor.RemoveRange);
        editor.Attachments.HandleKey(Key(ConsoleKey.Escape), editor.RemoveRange);
        Assert.Equal("  F2 images", TuiMarkup.Plain(Assert.Single(editor.Attachments.Render(editor.Buffer, width, 6))));
        editor.InsertPaste(new string('x', 1200));
        Assert.Equal("[▧ 1][≡ 2]", editor.Display.Text);
        Assert.Equal("  F2 attachments", TuiMarkup.Plain(Assert.Single(editor.Attachments.Render(editor.Buffer, 100, 6))));
        editor.Attachments.HandleKey(Key(ConsoleKey.F2), editor.RemoveRange);
        var mixed = editor.Attachments.Render(editor.Buffer, 100, 6);
        Assert.Contains(mixed, row => TuiMarkup.Plain(row).Contains("[▧ 1]"));
        Assert.Contains(mixed, row => TuiMarkup.Plain(row).Contains("[≡ 2]"));
        Assert.DoesNotContain(mixed, row => TuiMarkup.Plain(row).Contains(name));
        Assert.Equal(payload + new string('x', 1200), editor.Buffer);
    }


    /// <summary>The real driver receives a classified Escape and collapses details without losing the draft.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewEscape_ThroughInputPump_CollapsesThenReturnsToDraft(bool frameEngine)
    {
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: frameEngine);
        driver.ClipboardReader = _ => Task.FromResult(new ClipboardCapture.Payload(Image: Png));
        driver.CaptureWriter = _ => @"C:\sandbox\captures\shot.png";
        int stage = 0;
        term.OnFlush = () =>
        {
            string view = string.Join("\n", driver.BuildLiveFrame(driver.Width).Select(TuiMarkup.Plain));
            ConsoleKeyInfo? next = null;
            if (stage == 0 && view.Contains("F2 images")) next = Key(ConsoleKey.F2);
            else if (stage == 1 && view.Contains("Enter preview")) next = Key(ConsoleKey.Enter, '\r');
            else if (stage == 2 && view.Contains("╭ Image #1"))
            {
                var assembler = new SgrInputAssembler(true, true);
                Assert.Empty(assembler.Feed(Key(ConsoleKey.Escape, '\x1b')));
                next = Assert.Single(assembler.FlushTimeout()).Key;
            }
            else if (stage == 3 && view.Contains("Enter preview") && !view.Contains("╭ Image"))
                next = Key(ConsoleKey.Escape, '\x1b');
            else if (stage == 4 && view.Contains("F2 images")) next = Key(ConsoleKey.Enter, '\r');
            if (next is { } key) { stage++; pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(key)); }
        };
        pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.V, '\x16', ctrl: true)));
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            Assert.Equal("\n[Attached screenshot: C:\\sandbox\\captures\\shot.png]\n",
                await read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(5, stage);
        }
        finally
        {
            term.OnFlush = null;
            pump.Dispose();
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            driver.Shutdown();
        }
    }


    /// <summary>Queued navigation cannot be mistaken for pasted text by the Enter burst heuristic.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewQueuedEnterAndArrows_DoNotMutateOrSubmitDraft(bool frameEngine)
    {
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: frameEngine);
        const string payload = "first\nsecond\nthird\nfourth\nfifth\nsixth\nseventh\neighth\nninth\ntenth\nlast";
        driver.ClipboardReader = _ => Task.FromResult(new ClipboardCapture.Payload(Text: payload));
        int stage = 0;
        term.OnFlush = () =>
        {
            string view = string.Join("\n", driver.BuildLiveFrame(driver.Width).Select(TuiMarkup.Plain));
            if (stage == 0 && view.Contains("F2"))
            {
                stage++;
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.F2)));
            }
            else if (stage == 1 && view.Contains("Enter preview"))
            {
                stage++;
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Enter, '\r')));
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.DownArrow)));
            }
            else if (stage == 2 && view.Contains("╭ Paste #1"))
            {
                stage++;
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Escape, '\x1b')));
            }
            else if (stage == 3 && view.Contains("Enter preview"))
            {
                stage++;
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Escape, '\x1b')));
            }
            else if (stage == 4 && !view.Contains("Enter preview"))
            {
                stage++;
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Enter, '\r')));
            }
        };
        pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.V, '\x16', ctrl: true)));
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            Assert.Equal(payload, await read.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(5, stage);
        }
        finally
        {
            term.OnFlush = null;
            pump.Dispose();
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            driver.Shutdown();
        }
    }


    /// <summary>The footer, preview, and editable draft share a bounded band at narrow/short sizes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargePaste_ShortViewport_KeepsFooterPreviewAndDraftVisible(bool frameEngine)
    {
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var term = new Terminal { ViewWidth = 32, ViewHeight = 10 };
        var driver = new TuiDriver(term, frameEngine: frameEngine);
        driver.SetFooter(1234, 10000, false, false, false);
        driver.SetThinking("background activity");
        const string payload = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\nlast";
        driver.ClipboardReader = _ => Task.FromResult(new ClipboardCapture.Payload(Text: payload));
        int stage = 0;
        List<string>? preview = null;
        term.OnFlush = () =>
        {
            var rows = driver.BuildLiveFrame(driver.Width).Select(TuiMarkup.Plain).ToList();
            string view = string.Join("\n", rows);
            if (stage == 0 && view.Contains("F2 pastes"))
            {
                stage++;
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.F2)));
                driver.ToggleSubAgentExpanded("background", string.Join("\n", Enumerable.Repeat("activity", 20)));
            }
            else if (stage == 1 && view.Contains("› [≡ 1]"))
            {
                stage++;
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Enter, '\r')));
            }
            else if (stage == 2 && view.Contains("╭ Paste"))
            {
                stage++;
                preview = rows;
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.C, '\x03', ctrl: true)));
            }
        };
        pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.V, '\x16', ctrl: true)));
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(3, stage);
            Assert.NotNull(preview);
            Assert.InRange(preview.Count, 1, term.Height);
            Assert.Contains(preview, row => row.Contains("1.2k"));
            Assert.Contains(preview, row => row.Contains("[≡ 1]"));
            Assert.Contains(preview, row => row.Contains("Esc close"));
        }
        finally
        {
            term.OnFlush = null;
            pump.Dispose();
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            driver.Shutdown();
        }
    }

    /// <summary>Large text uses a compact numbered chip; paging stays with the outer transcript.</summary>
    [Fact]
    public void TextChip_PreviewOwnsArrows_NotOuterPageKeys()
    {
        var editor = new LineEditor();
        string payload = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"row {i}"));
        editor.InsertPaste(payload);
        Assert.Equal("[≡ 1]", editor.Display.Text);
        Assert.Equal("  F2 pastes", TuiMarkup.Plain(Assert.Single(editor.Attachments.Render(editor.Buffer, 65, 6))));
        editor.Attachments.HandleKey(Key(ConsoleKey.F2), editor.RemoveRange);
        editor.Attachments.HandleKey(Key(ConsoleKey.Enter), editor.RemoveRange);
        Assert.True(editor.Attachments.HandleKey(Key(ConsoleKey.DownArrow), editor.RemoveRange));
        Assert.Equal(1, editor.Attachments.Scroll);
        Assert.False(editor.Attachments.HandleKey(Key(ConsoleKey.PageUp), editor.RemoveRange));
        Assert.False(editor.Attachments.HandleKey(Key(ConsoleKey.PageDown), editor.RemoveRange));
        Assert.True(editor.Attachments.Focused);
        Assert.True(editor.Attachments.Expanded);
        Assert.Equal(1, editor.Attachments.Scroll);
        var rows = editor.Attachments.Render(editor.Buffer, 100, 6);
        Assert.Contains(rows, row => TuiMarkup.Plain(row).Contains("↑↓ scroll"));
        Assert.DoesNotContain(rows, row => TuiMarkup.Plain(row).Contains("PgUp/PgDn scroll"));
        Assert.Equal(payload, editor.Buffer);
    }


    /// <summary>Already-classified Enter is submit regardless of unrelated queued keys or event types.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrameEnter_QueueBatchingCannotReclassifyAsPaste(bool queuedWheel)
    {
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var driver = new TuiDriver(new Terminal(), frameEngine: true);
        pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.A, 'a')));
        pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Enter, '\r')));
        if (queuedWheel) pump.Enqueue(ConsoleInputPump.InputEvent.OfWheel(1));
        else pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.B, 'b')));
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            Assert.Equal("a", await read.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(pump.TryTake(out var next, 0));
            Assert.Equal(queuedWheel ? ConsoleInputPump.EventKind.Wheel : ConsoleInputPump.EventKind.Key, next.Kind);
            if (!queuedWheel) Assert.Equal('b', next.Key.KeyChar);
        }
        finally
        {
            pump.Dispose();
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            driver.Shutdown();
        }
    }

    /// <summary>All characters of a delayed framed paste must produce one chip, then one explicit submit.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FramePaste_DelayedOpener_LongSingleOrMultiline_PreservesOneAttachment(bool multiline)
    {
        using var pump = ConsoleInputPump.CreateUnstartedForTest(bracketedPaste: false);
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: false);
        string payload = multiline ? string.Join("\r", Enumerable.Repeat("row\t漢字", 30)) + "\rSENTINEL"
            : new string('x', 1500) + "SENTINEL";
        void Feed(string raw)
        {
            foreach (char c in raw)
                foreach (var ev in asm.Feed(Key(c == '\r' ? ConsoleKey.Enter : ConsoleKey.NoName, c)))
                    pump.Enqueue(ev);
        }
        Feed("\x1b[2");
        foreach (var ev in asm.FlushTimeout()) pump.Enqueue(ev);
        Feed("00~" + payload + "\x1b[201~");
        int submitted = 0;
        term.OnFlush = () =>
        {
            if (submitted == 0 && driver.BuildLiveFrame(driver.Width).Any(row => TuiMarkup.Plain(row).Contains("F2 pastes")))
            {
                submitted++;
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Enter, '\r')));
            }
        };
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            Assert.Equal(payload.Replace("\r", "\n"), await read.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, submitted);
            Assert.Contains("≡", term.Output.ToString());
            Assert.DoesNotContain("SENTINEL", term.Output.ToString()); // hidden until expanded; echo also uses chip
        }
        finally
        {
            term.OnFlush = null;
            pump.Dispose();
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            driver.Shutdown();
        }
    }


    /// <summary>Rapid ordinary typing has no inferred paste identity; explicit Enter still submits once.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassifiedSingleLine_StaysTyping_NoTimingBasedAttachment(bool frameEngine)
    {
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var driver = new TuiDriver(new Terminal(), frameEngine: frameEngine);
        string text = new string('a', 1500);
        foreach (char c in text) pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.A, c)));
        pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Enter, '\r')));
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try { Assert.Equal(text, await read.WaitAsync(TimeSpan.FromSeconds(3))); }
        finally { pump.Dispose(); await read.WaitAsync(TimeSpan.FromSeconds(3)); driver.Shutdown(); }
    }


    /// <summary>Late unframed newlines stay in this draft and cannot trigger a command or model request.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InferredPaste_EnterCannotSubmit_F4ExplicitlySends(bool frameEngine)
    {
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var driver = new TuiDriver(new Terminal(), frameEngine: frameEngine);
        string payload = string.Join("\n", Enumerable.Repeat("row\ttext", 15));
        pump.Enqueue(ConsoleInputPump.InputEvent.OfInferredPaste(payload));
        pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Enter, '\r')));
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            await Task.Delay(100);
            Assert.False(read.IsCompleted);
            pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.F4)));
            Assert.Equal(payload + "\n", await read.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { pump.Dispose(); await read.WaitAsync(TimeSpan.FromSeconds(3)); driver.Shutdown(); }
    }


    /// <summary>Mid-turn paste replay retains event kind and FIFO instead of converting newlines to keys.</summary>
    [Fact]
    public void Replay_RetainsPasteIdentityAndOrdering()
    {
        var events = new List<ConsoleInputPump.InputEvent>();
        MuxSwarm.Engine.EscapeKeyListener.DrainReplayEventsTo(events);
        MuxSwarm.Engine.EscapeKeyListener.ReplayKey(Key(ConsoleKey.A, 'a'));
        var paste = ConsoleInputPump.InputEvent.OfInferredPaste("one\ntwo");
        MuxSwarm.Engine.EscapeKeyListener.ReplayEvent(paste);
        MuxSwarm.Engine.EscapeKeyListener.ReplayKey(Key(ConsoleKey.B, 'b'));
        events.Clear(); MuxSwarm.Engine.EscapeKeyListener.DrainReplayEventsTo(events);
        Assert.Equal(3, events.Count);
        Assert.Equal('a', events[0].Key.KeyChar); Assert.Equal(paste, events[1]); Assert.Equal('b', events[2].Key.KeyChar);
    }

    [Fact]
    public void CaptureSave_ValidatesFormatContainmentAndNeverOverwrites()
    {
        string root = Path.Combine(Path.GetTempPath(), "mux-capture-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string a = ClipboardCapture.Save(Png, root, [root]);
            string b = ClipboardCapture.Save(Png, root, [root]);
            Assert.NotEqual(a, b); Assert.Equal(Png, File.ReadAllBytes(a));
            Assert.Equal(Path.Combine(root, "captures"), Path.GetDirectoryName(a));
            Assert.Throws<IOException>(() => ClipboardCapture.Save("not an image"u8.ToArray(), root, [root]));
            Assert.Throws<UnauthorizedAccessException>(() => ClipboardCapture.Save(Png, root, []));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "captures")).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EnhancedPaste_FragmentedFraming_IndependentBase64ChunksAndNegotiation()
    {
        var emitted = new List<string>(); var payloads = new List<ClipboardCapture.Payload>();
        var protocol = new TerminalPasteProtocol(emitted.Add, payloads.Add);
        var assembler = new SgrInputAssembler(false, true);
        void Feed(string sequence)
        {
            foreach (char c in sequence)
                foreach (var ev in assembler.Feed(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false)))
                { Assert.Equal(ConsoleInputPump.EventKind.Terminal, ev.Kind); protocol.Handle(ev.PasteText!); }
        }
        string Packet(string metadata, string payload = "") => "\x1b]5522;" + metadata + ";" + payload + "\x1b\\";
        string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        protocol.Start(); Assert.Equal(TerminalPasteProtocol.Query, Assert.Single(emitted));
        Feed("\x1b[?5522;2$y"); Assert.True(protocol.Enabled);
        Feed(Packet("type=read:status=OK:pw=" + B64("one-use")));
        Feed(Packet("type=read:status=DATA:mime=" + B64("."), B64("text/plain image/png\n")));
        Feed(Packet("type=read:status=DONE")); Assert.True(protocol.Busy);
        Assert.Contains("name=UGFzdGUgZXZlbnQ=", emitted[^1]);
        Feed(Packet("type=read:status=OK"));
        Feed(Packet("type=read:status=DATA:mime=" + B64("image/png"), Convert.ToBase64String(Png[..7])));
        Feed(Packet("type=read:status=DATA:mime=" + B64("image/png"), Convert.ToBase64String(Png[7..])));
        Feed(Packet("type=read:status=DONE"));
        Assert.Equal(Png, Assert.Single(payloads).Image); Assert.False(protocol.Busy);
        protocol.Stop(); Assert.Equal(TerminalPasteProtocol.Disable, emitted[^1]);
    }

    [Fact]
    public void ProtocolInvalidOrOversizedInput_DoesNotLeakIntoTypedText()
    {
        var assembler = new SgrInputAssembler(false, true);
        var events = new List<ConsoleInputPump.InputEvent>();
        foreach (char c in "\x1b]5522;" + new string('A', TerminalPasteProtocol.MaxPacketChars + 100) + "\a")
            events.AddRange(assembler.Feed(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false)));
        Assert.Empty(events);
        var errors = new List<ClipboardCapture.Payload>();
        var p = new TerminalPasteProtocol(_ => { }, errors.Add);
        p.Start(); p.Handle("\x1b[?5522;2$y"); p.Handle("\x1b]5522;type=read:status=OK\a");
        p.Handle("\x1b]5522;type=read:status=DATA:mime=not-base64;invalid\a");
        Assert.NotNull(Assert.Single(errors).Error); Assert.False(p.Busy);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PendingClipboard_ImmediateEnterWaits_CancelCannotAttachToNextDraft(bool cancel, bool frameEngine)
    {
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var term = new Terminal(); var driver = new TuiDriver(term, frameEngine: frameEngine);
        var release = new TaskCompletionSource<ClipboardCapture.Payload>(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        driver.ClipboardReader = _ => release.Task;
        driver.CaptureWriter = _ => { writes++; return $"C:\\sandbox\\captures\\shot{writes}.png"; };
        pump.Enqueue(ConsoleInputPump.InputEvent.OfTerminal("\x1b[?5522;2$y"));
        pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.V, '\x16', ctrl: true)));
        if (!cancel)
        {
            string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
            void Packet(string metadata, string payload = "") => pump.Enqueue(ConsoleInputPump.InputEvent.OfTerminal("\x1b]5522;" + metadata + ";" + payload + "\a"));
            Packet("type=read:status=OK");
            Packet("type=read:status=DATA:mime=" + B64("."), B64("image/png"));
            Packet("type=read:status=DONE");
            Packet("type=read:status=OK");
            Packet("type=read:status=DATA:mime=" + B64("image/png"), Convert.ToBase64String(Png));
            Packet("type=read:status=DONE");
        }
        if (cancel) pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Escape)));
        pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Key(ConsoleKey.Enter, '\r')));
        try
        {
            var read = Task.Run(() => driver.ReadLineCore(pump));
            if (!cancel) { await Task.Delay(80); Assert.False(read.IsCompleted); }
            else { Assert.Equal("", await read.WaitAsync(TimeSpan.FromSeconds(3))); }
            release.SetResult(new(Image: Png));
            string? submitted = await read.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(cancel ? 0 : 2, writes);
            if (!cancel)
            {
                Assert.Contains("captures\\shot1.png", submitted); Assert.Contains("captures\\shot2.png", submitted);
                Assert.True(submitted!.IndexOf("shot1.png", StringComparison.Ordinal) < submitted.IndexOf("shot2.png", StringComparison.Ordinal));
                Assert.DoesNotContain("Image #", submitted);
                Assert.DoesNotContain("▧", submitted);
                Assert.Contains("▧", term.Output.ToString());
                Assert.DoesNotContain("Saved to captures", term.Output.ToString());
                Assert.DoesNotContain("shot1.png", term.Output.ToString());
                Assert.DoesNotContain("shot2.png", term.Output.ToString());
            }
        }
        finally { driver.Shutdown(); }
    }
}
