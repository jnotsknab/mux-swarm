using System.Linq;
using MuxSwarm.Utils.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Headless coverage for the v0.12.4 full-frame renderer (console.renderEngine = "frame") and the
/// TuiDriver running under the frame engine. Everything is driven through an in-memory fake
/// terminal, so the alternate-screen ownership, atomic-frame envelope, and changed-row-only diff
/// are verified without a real console.
/// </summary>
public class FrameRendererTests
{
    private sealed class FakeTerminal : ITuiTerminal
    {
        private readonly System.Text.StringBuilder _sb = new();
        public int Width { get; set; } = 40;
        public int Height { get; set; } = 8;
        public void Write(string s) => _sb.Append(s);
        public void Flush() { }
        public string Output => _sb.ToString();
        public void Clear() => _sb.Clear();
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void FirstPresent_EntersAltScreen_ClearsAndDrawsEveryRow()
    {
        var t = new FakeTerminal();
        var fr = new FrameRenderer(t);
        var rows = Enumerable.Range(0, t.Height).Select(i => "row" + i).ToList();

        fr.Present(rows);
        var o = t.Output;

        Assert.Contains(Ansi.EnterAltScreen, o);
        Assert.Contains(Ansi.ClearScreen, o);
        // Exactly one balanced synchronized-output envelope.
        Assert.Equal(1, Count(o, Ansi.BeginSyncOutput));
        Assert.Equal(1, Count(o, Ansi.EndSyncOutput));
        // Every row painted, each absolutely addressed.
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Contains(Ansi.MoveTo(i + 1, 1), o);
            Assert.Contains(rows[i], o);
        }
    }

    [Fact]
    public void SecondPresent_NoChange_EmitsNoRowWrites()
    {
        var t = new FakeTerminal();
        var fr = new FrameRenderer(t);
        var rows = Enumerable.Range(0, t.Height).Select(i => "row" + i).ToList();

        fr.Present(rows);
        t.Clear();
        fr.Present(rows.ToList());   // identical frame
        var o = t.Output;

        // Still a balanced (empty) sync envelope, but no clear and no row moves.
        Assert.Equal(1, Count(o, Ansi.BeginSyncOutput));
        Assert.Equal(1, Count(o, Ansi.EndSyncOutput));
        Assert.DoesNotContain(Ansi.ClearScreen, o);
        Assert.DoesNotContain(Ansi.MoveTo(1, 1), o);
    }

    [Fact]
    public void SecondPresent_OneRowChanged_RewritesOnlyThatRow()
    {
        var t = new FakeTerminal();
        var fr = new FrameRenderer(t);
        var rows = Enumerable.Range(0, t.Height).Select(i => "row" + i).ToList();

        fr.Present(rows);
        t.Clear();
        var next = rows.ToList();
        next[3] = "CHANGED";
        fr.Present(next);
        var o = t.Output;

        Assert.DoesNotContain(Ansi.ClearScreen, o);           // steady-state diff, no full redraw
        Assert.Contains(Ansi.MoveTo(4, 1), o);                // only the changed row (1-based)
        Assert.Contains("CHANGED", o);
        Assert.DoesNotContain(Ansi.MoveTo(1, 1), o);          // unchanged rows untouched
        Assert.DoesNotContain(Ansi.MoveTo(5, 1), o);
    }

    [Fact]
    public void WidthChange_ForcesFullRedraw()
    {
        var t = new FakeTerminal();
        var fr = new FrameRenderer(t);
        var rows = Enumerable.Range(0, t.Height).Select(i => "row" + i).ToList();

        fr.Present(rows);
        t.Clear();
        t.Width = 80;                       // geometry change
        fr.Present(rows.ToList());          // same content, new width
        var o = t.Output;

        Assert.Contains(Ansi.ClearScreen, o);   // never diff across geometry
    }

    [Fact]
    public void Leave_RestoresPrimaryScreen_ThenReEnters()
    {
        var t = new FakeTerminal();
        var fr = new FrameRenderer(t);
        var rows = Enumerable.Range(0, t.Height).Select(i => "row" + i).ToList();

        fr.Present(rows);
        t.Clear();
        fr.Leave();
        var leftOutput = t.Output;
        Assert.Contains(Ansi.LeaveAltScreen, leftOutput);
        Assert.Contains(Ansi.ShowCursor, leftOutput);

        t.Clear();
        fr.Present(rows.ToList());
        Assert.Contains(Ansi.EnterAltScreen, t.Output);   // re-enters from a clean slate
    }

    [Fact]
    public void Leave_WhenNeverEntered_IsNoOp()
    {
        var t = new FakeTerminal();
        var fr = new FrameRenderer(t);
        fr.Leave();
        Assert.Equal("", t.Output);
    }

    // --- driver under the frame engine ---------------------------------------

    [Fact]
    public void Driver_FrameEngine_CommitPresentsFrameWithFooterAndTranscript()
    {
        var t = new FakeTerminal();
        var d = new TuiDriver(t, frameEngine: true);
        Assert.True(d.FrameEngine);

        // Do NOT clear between calls: the alternate screen is entered on the FIRST present
        // (SetFooter), and steady-state presents thereafter only diff changed rows.
        d.SetFooter(50_000, 200_000, plan: false, ultra: false, psub: false);
        d.Commit(new[] { "hello world" });
        var o = t.Output;

        // Frame engine takes the alt screen and presents; committed content is retained + drawn,
        // and never pushed into native scrollback (no bare CommitAbove path).
        Assert.Contains(Ansi.EnterAltScreen, o);
        Assert.Contains("hello world", o);
        Assert.Contains("25%", o);     // footer meter still rendered in the frame
    }

    [Fact]
    public void Driver_FrameEngine_SuspendLeavesAltScreen()
    {
        var t = new FakeTerminal();
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 0, false, false, false);   // first present -> enter alt screen
        t.Clear();
        d.Suspend();
        Assert.Contains(Ansi.LeaveAltScreen, t.Output);
    }

    [Fact]
    public void Driver_InlineEngine_NeverEntersAltScreen()
    {
        var t = new FakeTerminal();
        var d = new TuiDriver(t, frameEngine: false);
        Assert.False(d.FrameEngine);
        d.SetFooter(1, 0, false, false, false);
        d.Commit(new[] { "hello" });
        Assert.DoesNotContain(Ansi.EnterAltScreen, t.Output);   // inline path unchanged
    }

    // --- suspend envelope (the sub-prompt fix) --------------------------------

    [Fact]
    public void Driver_FrameEngine_SuspendLatch_DefersTimerPresents()
    {
        // The bug that killed the first frame engine: a blocking Spectre [y/n] prompt suspended the
        // alt screen, then the ~100ms ticker re-presented and re-entered the alt screen OVER the
        // prompt. The suspend latch must defer every present until Resume().
        var t = new FakeTerminal();
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 0, false, false, false);   // enter alt screen
        d.Suspend();
        Assert.True(d.Suspended);
        t.Clear();

        // Timer-driven paints while a prompt owns the primary buffer: must emit NOTHING.
        d.SetFooter(2, 0, false, false, false);
        d.Commit(new[] { "committed while suspended" });
        d.PollResize();
        Assert.Equal("", t.Output);

        // Resume: re-enters the alt screen and repaints everything retained while suspended.
        d.Resume();
        Assert.False(d.Suspended);
        var o = t.Output;
        Assert.Contains(Ansi.EnterAltScreen, o);
        Assert.Contains("committed while suspended", o);
    }

    [Fact]
    public void Driver_FrameEngine_ResumeWithoutSuspend_IsNoOp()
    {
        var t = new FakeTerminal();
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 0, false, false, false);
        t.Clear();
        d.Resume();               // never suspended - must not clear/redraw anything
        Assert.Equal("", t.Output);
    }

    [Fact]
    public void Driver_InlineEngine_SuspendedIsAlwaysFalse()
    {
        var t = new FakeTerminal();
        var d = new TuiDriver(t, frameEngine: false);
        d.Suspend();
        Assert.False(d.Suspended);   // latch is a frame-engine concept only
    }

    [Fact]
    public void Driver_FrameEngine_SuspendResumeCycle_IsReentrant()
    {
        // Suspend twice (nested prompts) then resume: must come back cleanly, and a second Resume
        // must be a no-op (idempotent unlatch).
        var t = new FakeTerminal();
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 0, false, false, false);
        d.Suspend();
        d.Suspend();                 // idempotent latch
        d.Resume();
        Assert.False(d.Suspended);
        t.Clear();
        d.Resume();                  // second resume: nothing to do
        Assert.Equal("", t.Output);
    }

    // --- torn SGR mouse reports ------------------------------------------------

    [Fact]
    public void MouseSgrParser_ReassemblesPrefixStrippedWheelReport()
    {
        var parser = new MouseSgrParser();
        (int Button, int X, int Y, bool Release)? parsed = null;

        foreach (char ch in "<65;80;11M")
        {
            Assert.True(parser.Feed(ch, allowStart: true, out var current));
            parsed = current ?? parsed;
        }

        Assert.Equal((65, 80, 11, false), parsed);
        Assert.False(parser.InProgress);
    }

    [Fact]
    public void MouseSgrParser_ReassemblesFullyPrefixedReportAcrossCharacters()
    {
        var parser = new MouseSgrParser();
        (int Button, int X, int Y, bool Release)? parsed = null;

        foreach (char ch in "\u001b[<64;5;7M")
        {
            Assert.True(parser.Feed(ch, allowStart: true, out var current));
            parsed = current ?? parsed;
        }

        Assert.Equal((64, 5, 7, false), parsed);
    }

    [Fact]
    public void MouseSgrParser_ResynchronizesWhenANewPrefixInterruptsATornOne()
    {
        var parser = new MouseSgrParser();
        (int Button, int X, int Y, bool Release)? parsed = null;

        foreach (char ch in "<6<65;80;11M")
        {
            Assert.True(parser.Feed(ch, allowStart: true, out var current));
            parsed = current ?? parsed;
        }

        Assert.Equal((65, 80, 11, false), parsed);
    }

    [Fact]
    public void MouseSgrParser_ReplaysLegitimateKeyAfterTornFragment()
    {
        var parser = new MouseSgrParser();
        Assert.True(parser.Feed('<', allowStart: true, out _));
        Assert.True(parser.Feed('6', allowStart: true, out _));
        Assert.False(parser.Feed('x', allowStart: true, out var parsed));
        Assert.Null(parsed);
        Assert.False(parser.InProgress);
    }

    [Fact]
    public void MouseSgrParser_DoesNotConsumeOrdinaryAngleBracketWithoutActiveSequence()
    {
        var parser = new MouseSgrParser();
        Assert.False(parser.Feed('>', allowStart: false, out var parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('7')]
    [InlineData(';')]
    [InlineData('M')]
    public void MouseSgrParser_FragmentAlphabet_CoversOrphanStragglers(char ch)
        => Assert.True(MouseSgrParser.IsFragmentChar(ch));
}
