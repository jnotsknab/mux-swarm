using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// The single-input-plane assembler: reassembles the raw key stream into typed events (keys,
/// wheel, paste). The load-bearing guarantee for the mouse-scroll char-leak fix: an SGR mouse
/// report can split at ANY byte and STILL never surfaces a fragment as a key event - the machine
/// holds the ESC until the sequence classifies, and a torn report is dropped, never leaked.
/// </summary>
public class SgrInputAssemblerTests
{
    private static ConsoleKeyInfo K(char c)
        => c == '\r'
            ? new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)
            : new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false);

    private static List<ConsoleInputPump.InputEvent> Feed(SgrInputAssembler asm, string s)
    {
        var outp = new List<ConsoleInputPump.InputEvent>();
        foreach (char c in s) outp.AddRange(asm.Feed(K(c)));
        return outp;
    }

    private static List<ConsoleInputPump.InputEvent> FeedChunked(SgrInputAssembler asm, string s, int chunk)
    {
        // Simulates a pty fragmenting the byte stream: feed slices, but per-key (the assembler is
        // fed one key at a time regardless of read chunking, so splitting anywhere is equivalent).
        return Feed(asm, s);
    }

    [Fact]
    public void PlainKeys_PassThroughUnchanged()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "hello");
        Assert.Equal(5, evs.Count);
        Assert.All(evs, e => Assert.Equal(ConsoleInputPump.EventKind.Key, e.Kind));
        Assert.Equal("hello", string.Concat(evs.Select(e => e.Key.KeyChar)));
    }

    [Fact]
    public void WheelUp_Report_YieldsSingleWheelEvent_NoKeys()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<64;30;7M");
        var wheel = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Wheel, wheel.Kind);
        Assert.Equal(+1, wheel.WheelDir);
    }

    [Fact]
    public void WheelDown_Report_YieldsNegativeWheel()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<65;30;7M");
        var wheel = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Wheel, wheel.Kind);
        Assert.Equal(-1, wheel.WheelDir);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void WheelReport_SplitAtAnyByte_NeverLeaksKeys(int split)
    {
        // THE regression guard for the "<[<64;…" leak: wherever the read boundary falls, the
        // report classifies as exactly one wheel event and zero key events.
        const string report = "\u001b[<64;30;7M";
        Assert.True(split < report.Length);
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = new List<ConsoleInputPump.InputEvent>();
        evs.AddRange(Feed(asm, report[..split]));
        evs.AddRange(Feed(asm, report[split..]));
        var wheel = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Wheel, wheel.Kind);
        Assert.Equal(+1, wheel.WheelDir);
        Assert.DoesNotContain(evs, e => e.Kind == ConsoleInputPump.EventKind.Key);
    }

    [Fact]
    public void TornReport_NoTerminator_FlushDropsIt_NoKeys()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<64;30");   // never terminated
        evs.AddRange(asm.FlushTimeout());
        Assert.Empty(evs);   // dropped, never leaked - even on flush
    }

    [Fact]
    public void TornReport_ThenNewReport_FirstDroppedSecondParses()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<64;30");        // torn (no M/m)
        evs.AddRange(Feed(asm, "\u001b[<65;31;8M")); // fresh report: the stray ESC re-enters Esc state
        var wheel = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Wheel, wheel.Kind);
        Assert.Equal(-1, wheel.WheelDir);
    }

    [Fact]
    public void LeftPressAndRelease_EmitTypedMouseEvents_NeverKeys()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<0;10;5M\u001b[<0;10;5m");   // press + release
        Assert.Equal(2, evs.Count);
        Assert.All(evs, e => Assert.Equal(ConsoleInputPump.EventKind.Mouse, e.Kind));
        Assert.Equal((0, 10, 5, false), (evs[0].MouseButton, evs[0].MouseCol, evs[0].MouseRow, evs[0].MouseRelease));
        Assert.Equal((0, 10, 5, true), (evs[1].MouseButton, evs[1].MouseCol, evs[1].MouseRow, evs[1].MouseRelease));
    }

    [Fact]
    public void LeftDragMotion_EmitsMouseEvent_WithRawMotionFlag()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<32;11;6M");   // motion with left held (0x20 | 0)
        var ev = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Mouse, ev.Kind);
        Assert.Equal(32, ev.MouseButton);           // motion flag preserved for MouseHandler.Classify
        Assert.Equal((11, 6, false), (ev.MouseCol, ev.MouseRow, ev.MouseRelease));
    }

    [Fact]
    public void ModifiedLeftPress_KeepsModifierBits()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<16;3;4M");    // ctrl+left press (0x10 | 0)
        var ev = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Mouse, ev.Kind);
        Assert.Equal(16, ev.MouseButton);
    }

    [Fact]
    public void MiddleAndRightButtonReports_AreSwallowed()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<1;10;5M\u001b[<2;10;5M\u001b[<35;7;7M");   // middle, right, no-button motion
        Assert.Empty(evs);
    }

    [Fact]
    public void MouseClassification_IsAlwaysOn_StrayReportNeverLeaksAsKeys()
    {
        // VT-side ENABLEMENT is what is scoped by the preset; the assembler always classifies
        // ESC [ < as a mouse report so a stray report arriving while tracking is off (terminal
        // race, dirty prior state) surfaces as a TYPED event - never as key events.
        var asm = new SgrInputAssembler(mouseTracking: false, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<0;10;5M");
        var ev = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Mouse, ev.Kind);

        // An incomplete prefix still classifies + drops on flush (torn report, not draft text).
        var torn = new SgrInputAssembler(mouseTracking: false, bracketedPaste: true);
        var tornEvs = Feed(torn, "\u001b[<");
        tornEvs.AddRange(torn.FlushTimeout());
        Assert.Empty(tornEvs);
    }

    [Fact]
    public void BareEsc_FlushEmitsEscapeKey()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b");
        Assert.Empty(evs);                    // held for classification
        evs.AddRange(asm.FlushTimeout());
        var esc = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Key, esc.Kind);
        Assert.Equal(ConsoleKey.Escape, esc.Key.Key);
    }

    [Theory]
    [InlineData("\u001b[")]
    [InlineData("\u001b[2")]
    public void EscBracket_TimeoutKeepsPrefix_ExplicitCancelRecovers(string prefix)
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, prefix);
        evs.AddRange(asm.FlushTimeout());
        Assert.Empty(evs);
        var cancel = Assert.Single(asm.Feed(new ConsoleKeyInfo('\x03', ConsoleKey.C, false, false, true)));
        Assert.Equal(ConsoleKey.C, cancel.Key.Key);
        Assert.False(asm.HasPending);
    }

    [Fact]
    public void AltChord_EmitsEscThenChar()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001bx");
        Assert.Equal(2, evs.Count);
        Assert.Equal(ConsoleKey.Escape, evs[0].Key.Key);
        Assert.Equal('x', evs[1].Key.KeyChar);
    }

    [Fact]
    public void BracketedPaste_ReassemblesWholePaste_OneEvent()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[200~line one\rline two\u001b[201~");
        var paste = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Paste, paste.Kind);
        Assert.Equal("line one\nline two", paste.PasteText);
    }

    [Theory]
    [InlineData(3)] [InlineData(7)] [InlineData(11)] [InlineData(15)]
    public void BracketedPaste_SplitAnywhere_StillOnePaste(int split)
    {
        const string seq = "\u001b[200~ab\rcd\u001b[201~";
        Assert.True(split < seq.Length);
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = new List<ConsoleInputPump.InputEvent>();
        evs.AddRange(Feed(asm, seq[..split]));
        evs.AddRange(Feed(asm, seq[split..]));
        var paste = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Paste, paste.Kind);
        Assert.Equal("ab\ncd", paste.PasteText);
    }

    /// <summary>A transport pause inside framed paste must not turn the remaining newlines into submits.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BracketedPaste_IdleInsideBodyOrCloser_WaitsForEndMarker(bool splitCloser)
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[200~partial paste\r");
        if (splitCloser) evs.AddRange(Feed(asm, "\u001b[20"));
        Assert.False(asm.PendingExpired(idleMs: 0));
        evs.AddRange(asm.FlushTimeout());
        Assert.Empty(evs);
        evs.AddRange(Feed(asm, splitCloser ? "1~" : "tail\r\u001b[201~"));
        var paste = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Paste, paste.Kind);
        Assert.Equal(splitCloser ? "partial paste\n" : "partial paste\ntail\n", paste.PasteText);
        Assert.False(asm.HasPending);
    }


    /// <summary>Explicit Ctrl+C recovers a missing paste end marker without submitting retained or late text.</summary>
    [Fact]
    public void BracketedPaste_CtrlC_CancelsIncompleteTransaction()
    {
        var asm = new SgrInputAssembler(true, true);
        Assert.Empty(Feed(asm, "\x1b[200~incomplete\r"));
        var cancel = Assert.Single(asm.Feed(new ConsoleKeyInfo('\x03', ConsoleKey.C, false, false, true)));
        Assert.Equal(ConsoleInputPump.EventKind.Key, cancel.Kind);
        Assert.Equal(ConsoleKey.C, cancel.Key.Key);
        Assert.False(asm.HasPending);
        Assert.Equal("ok", string.Concat(Feed(asm, "ok").Select(e => e.Key.KeyChar)));
    }


    /// <summary>The inline reader must keep framed text literal across an empty poll, just like frame mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InlineBracketedPaste_GapDoesNotEndTransaction_ExplicitCancelRecovers(bool cancel)
    {
        var input = new Queue<ConsoleKeyInfo?>();
        foreach (char c in "first\r") input.Enqueue(K(c));
        input.Enqueue(null); // transport chunk boundary, not the end of the paste
        if (cancel) input.Enqueue(new ConsoleKeyInfo('\x03', ConsoleKey.C, false, false, true));
        else foreach (char c in "last\r\x1b[201~") input.Enqueue(K(c));
        var result = TuiDriver.ReadBracketedPaste(() => input.Dequeue());
        Assert.Empty(input);
        if (cancel)
        {
            Assert.Equal(ConsoleInputPump.EventKind.Key, result.Kind);
            Assert.Equal(ConsoleKey.C, result.Key.Key);
        }
        else
        {
            Assert.Equal(ConsoleInputPump.EventKind.Paste, result.Kind);
            Assert.Equal("first\nlast\n", result.PasteText);
        }
    }

    [Fact]
    public void PendingSequence_ClassificationWindow_NotExpiredImmediately()
    {
        // The pump only flushes a pending sequence after the stream has been IDLE for the
        // classification window. Immediately after a fed key the window must NOT be expired -
        // this is what stops a chunked paste (ESC[2 | 00~text...) from tearing at the chunk
        // boundary and leaking "[200~" into the editor as literal text.
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        Feed(asm, "\u001b");                        // only bare ESC has an ambiguity window
        Assert.True(asm.HasPending);
        Assert.False(asm.PendingExpired(idleMs: 50));   // just fed: window still open
        Assert.True(asm.PendingExpired(idleMs: 0));     // zero window: expired at once
    }

    [Fact]
    public void ChunkedPasteOpener_NoFlushInsideWindow_PasteSurvivesIntact()
    {
        // Chunk boundary inside the ESC[200~ opener, with the pump polling in between: as long
        // as the flush is gated on the idle window (PendingExpired false), feeding the rest of
        // the sequence later still yields ONE whole paste and zero leaked keys.
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[2");
        Assert.Empty(evs);                            // held for classification
        Assert.False(asm.PendingExpired(idleMs: 50)); // pump would NOT flush here
        evs.AddRange(Feed(asm, "00~hello\u001b[201~"));
        var paste = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Paste, paste.Kind);
        Assert.Equal("hello", paste.PasteText);
    }

    [Fact]
    public void Pump_PushFront_WinsOverQueue_AndPreservesFifo()
    {
        // The transition-race contract: an event the mid-turn listener took after the prompt
        // claimed ownership is handed back via PushFront and MUST be the next event the prompt
        // sees, ahead of anything already in the inner queue - and multiple pushed-back events
        // keep their original order.
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var a = ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false));
        var b = ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false));
        var q = ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false));
        pump.Enqueue(q);                       // already buffered in the inner queue
        pump.PushFront(new[] { a, b });        // handed back by the listener
        Assert.Equal(3, pump.PendingCount);
        Assert.True(pump.TryTake(out var e1, 0)); Assert.Equal('a', e1.Key.KeyChar);
        Assert.True(pump.TryTake(out var e2, 0)); Assert.Equal('b', e2.Key.KeyChar);
        Assert.True(pump.TryTake(out var e3, 0)); Assert.Equal('q', e3.Key.KeyChar);
        Assert.False(pump.TryTake(out _, 0));
    }

    [Fact]
    public void Pump_PromptActive_Flag_RoundTrips()
    {
        // Ownership flag used by TuiDriver.ReadLine (set for its lifetime) and honored by the
        // EscapeKeyListener pump path (stands down while set).
        Assert.False(ConsoleInputPump.PromptActive);
        ConsoleInputPump.PromptActive = true;
        try { Assert.True(ConsoleInputPump.PromptActive); }
        finally { ConsoleInputPump.PromptActive = false; }
        Assert.False(ConsoleInputPump.PromptActive);
    }


    /// <summary>Negotiation and transport chunking cannot invalidate recognized protocol delimiters.</summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    [InlineData(false, 3)]
    public void PasteOpener_GapsAndDisabledNegotiation_StillFrameEntirePayload(bool negotiate, int split)
    {
        const string sequence = "\x1b[200~first\rlast\x1b[201~";
        var asm = new SgrInputAssembler(true, negotiate);
        var events = Feed(asm, sequence[..split]);
        events.AddRange(asm.FlushTimeout());
        Assert.Empty(events);
        events.AddRange(Feed(asm, sequence[split..]));
        var paste = Assert.Single(events);
        Assert.Equal(ConsoleInputPump.EventKind.Paste, paste.Kind);
        Assert.Equal("first\nlast", paste.PasteText);
        Assert.Equal(ConsoleKey.Enter, Assert.Single(Feed(asm, "\r")).Key.Key);
    }

    [Fact]
    public void FalsePasteOpener_PassesThroughAsKeys()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[2x");
        Assert.Empty(evs); // A malformed CSI must not replay control syntax into the draft.
    }

    [Fact]
    public void RunawayMouseBody_IsDropped()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<" + new string('9', 100));   // no terminator, way over cap
        evs.AddRange(asm.FlushTimeout());
        Assert.Empty(evs);
    }

    [Fact]
    public void WheelBurst_TwoReports_TwoWheelEvents()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<64;1;1M\u001b[<64;1;1M");
        Assert.Equal(2, evs.Count(e => e.Kind == ConsoleInputPump.EventKind.Wheel));
    }

    /// <summary>VT input preserves paste framing and changes navigation into CSI/SS3 sequences.</summary>
    [Theory]
    [InlineData("\x1b[A", ConsoleKey.UpArrow, 0)]
    [InlineData("\x1bOQ", ConsoleKey.F2, 0)]
    [InlineData("\x1b[3~", ConsoleKey.Delete, 0)]
    [InlineData("\x1b[5~", ConsoleKey.PageUp, 0)]
    [InlineData("\x1b[6~", ConsoleKey.PageDown, 0)]
    [InlineData("\x1b[1;5D", ConsoleKey.LeftArrow, 4)]
    [InlineData("\x1b[Z", ConsoleKey.Tab, 2)]
    [InlineData("\x1b[13;2u", ConsoleKey.Enter, 2)]
    public void VtKeyboard_DecodesBeforeEditor(string sequence, ConsoleKey expected, int modifiers)
    {
        var asm = new SgrInputAssembler(true, true);
        var events = Feed(asm, sequence);
        var key = Assert.Single(events);
        Assert.Equal(ConsoleInputPump.EventKind.Key, key.Kind);
        Assert.Equal(expected, key.Key.Key);
        Assert.Equal((ConsoleModifiers)modifiers, key.Key.Modifiers);
    }

    /// <summary>Captured ConPTY-style Unicode records retain framed payload and decode subsequent navigation.</summary>
    [Fact]
    public void ConPtyVtRecords_PreservePasteBeforeKeyboardAndMouse()
    {
        var asm = new SgrInputAssembler(true, true);
        const string payload = "first\rsecond\tUNICODE漢字";
        var events = new List<ConsoleInputPump.InputEvent>();
        foreach (char c in "\x1b[200~" + payload + "\x1b[201~\x1b[A\x1bOQ\x1b[<64;3;3M")
        {
            Assert.True(Win32ConsoleInput.TryTranslateKey(true, 0, c, 0, out var key));
            events.AddRange(asm.Feed(key));
        }
        Assert.Equal(4, events.Count);
        Assert.Equal("first\nsecond\tUNICODE漢字", events[0].PasteText);
        Assert.Equal(ConsoleKey.UpArrow, events[1].Key.Key);
        Assert.Equal(ConsoleKey.F2, events[2].Key.Key);
        Assert.Equal(ConsoleInputPump.EventKind.Wheel, events[3].Kind);
    }

    /// <summary>VT passthrough records have virtual-key zero even for Enter and Ctrl+V.</summary>
    [Theory]
    [InlineData('\r', ConsoleKey.Enter, false)]
    [InlineData('\t', ConsoleKey.Tab, false)]
    [InlineData('\x16', ConsoleKey.V, true)]
    [InlineData('\x03', ConsoleKey.C, true)]
    [InlineData('\x04', ConsoleKey.D, true)]
    public void VtControlBytes_WithNoVirtualKey_RetainEditorMeaning(char c, ConsoleKey expected, bool control)
    {
        var asm = new SgrInputAssembler(true, true);
        var ev = Assert.Single(asm.Feed(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false)));
        Assert.Equal(expected, ev.Key.Key);
        Assert.Equal(control, ev.Key.Modifiers.HasFlag(ConsoleModifiers.Control));
    }

    /// <summary>Canonicalizing raw VT control keys must not reinterpret literal bytes inside a framed paste.</summary>
    [Fact]
    public void FramedControlPayload_IsNotReclassifiedAsShortcut()
    {
        var asm = new SgrInputAssembler(true, true);
        var events = Feed(asm, "\x1b[200~a\u0003b\u0016c\x1b[201~");
        var paste = Assert.Single(events);
        Assert.Equal(ConsoleInputPump.EventKind.Paste, paste.Kind);
        Assert.Equal("a\u0003b\u0016c", paste.PasteText);
    }

    /// <summary>Lost end markers recover into guarded literal draft content, not executable key events.</summary>
    [Fact]
    public void IncompleteFramedPaste_WatchdogStagesGuardedText()
    {
        long now = 0;
        var asm = new SgrInputAssembler(true, true, () => now);
        Assert.Empty(Feed(asm, "\x1b[200~first\rsecond"));
        now = 100; Assert.False(asm.PendingExpired(50));
        Assert.Empty(asm.FlushTimeout());
        now = 2000; Assert.True(asm.PendingExpired(50));
        var paste = Assert.Single(asm.FlushTimeout());
        Assert.Equal(ConsoleInputPump.EventKind.InferredPaste, paste.Kind);
        Assert.Equal("first\nsecond", paste.PasteText);
        Assert.False(asm.HasPending);
    }
}
