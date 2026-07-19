using MuxSwarm.Utils.Tui;

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
    public void NonWheelMouseReport_IsSwallowed()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<0;10;5M\u001b[<0;10;5m");   // press + release
        Assert.Empty(evs);
    }

    [Fact]
    public void MouseTrackingOff_SgrPrefixIsNotSpecial()
    {
        // With tracking off the terminal never sends reports; ESC [ < must pass through as keys.
        var asm = new SgrInputAssembler(mouseTracking: false, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[<");
        evs.AddRange(asm.FlushTimeout());
        Assert.Equal(3, evs.Count);
        Assert.All(evs, e => Assert.Equal(ConsoleInputPump.EventKind.Key, e.Kind));
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

    [Fact]
    public void EscBracket_TimeoutEmitsEscAndBracket()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[");
        evs.AddRange(asm.FlushTimeout());
        Assert.Equal(2, evs.Count);
        Assert.Equal(ConsoleKey.Escape, evs[0].Key.Key);
        Assert.Equal('[', evs[1].Key.KeyChar);
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

    [Fact]
    public void BracketedPaste_NoCloser_FlushStillDeliversText()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[200~partial paste");
        evs.AddRange(asm.FlushTimeout());
        var paste = Assert.Single(evs);
        Assert.Equal(ConsoleInputPump.EventKind.Paste, paste.Kind);
        Assert.Equal("partial paste", paste.PasteText);
    }

    [Fact]
    public void FalsePasteOpener_PassesThroughAsKeys()
    {
        var asm = new SgrInputAssembler(mouseTracking: true, bracketedPaste: true);
        var evs = Feed(asm, "\u001b[2x");
        Assert.Equal(4, evs.Count);   // ESC, '[', '2', 'x' - nothing is lost
        Assert.Equal(ConsoleKey.Escape, evs[0].Key.Key);
        Assert.Equal('[', evs[1].Key.KeyChar);
        Assert.Equal('2', evs[2].Key.KeyChar);
        Assert.Equal('x', evs[3].Key.KeyChar);
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
}
