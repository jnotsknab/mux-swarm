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
        public int Width => 65;
        public int Height => 24;
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
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
        editor.Attachments.HandleKey(Key(ConsoleKey.PageDown), editor.RemoveRange);
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
        Assert.Contains("lead [Paste", editor.Display.Text);
        editor.Feed(Key(ConsoleKey.End)); editor.Feed(Key(ConsoleKey.Backspace));
        Assert.Equal("prefix lead ", editor.Buffer);
        editor.InsertPaste(new string('x', 1200) + " @file");
        Assert.False(editor.IsAtFilter); // hidden payload must not open file completion
        editor.ReplaceCurrentToken("@safe"); // even an explicit caller uses actual atomic bounds
        Assert.EndsWith("@safe ", editor.Buffer);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingClipboard_ImmediateEnterWaits_CancelCannotAttachToNextDraft(bool cancel)
    {
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var term = new Terminal(); var driver = new TuiDriver(term, frameEngine: true);
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
            }
        }
        finally { driver.Shutdown(); }
    }
}
