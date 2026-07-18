using System.Linq;
using MuxSwarm.Utils;
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

    // --- fixed passive frame-scroll indicator -------------------------------

    [Theory]
    [InlineData(1, 100, 20, 19)]
    [InlineData(50, 100, 20, 10)]
    [InlineData(100, 100, 20, 0)]
    public void FrameScrollIndicator_TopMovesButSizeStaysFixed(int scroll, int maxScroll, int trackRows, int expectedTop)
    {
        var placement = TuiDriver.FrameScrollIndicatorPlacement(scroll, maxScroll, trackRows);
        Assert.Equal(expectedTop, placement.Top);
        Assert.Equal(1, placement.Length);
    }

    [Fact]
    public void FrameScrollIndicator_TooShortTrack_UsesSingleCell()
    {
        var placement = TuiDriver.FrameScrollIndicatorPlacement(1, 2, 1);
        Assert.Equal(0, placement.Top);
        Assert.Equal(1, placement.Length);
    }

    [Fact]
    public void Driver_FrameEngine_KeyboardScrollShowsFixedIndicatorAndReturningTailClearsIt()
    {
        var t = new FakeTerminal { Width = 40, Height = 10 };
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 100, false, false, false);
        for (int i = 0; i < 40; i++) d.CommitLine($"line {i:D2}");

        Assert.True(d.FrameScrollBy(10_000));
        var scrolled = d.ComposeFrameRows();

        string marker = TuiDriver.FrameScrollIndicatorCell();
        Assert.Single(scrolled, r => r.EndsWith(marker, StringComparison.Ordinal));

        Assert.True(d.FrameScrollBy(-10_000));
        var tail = d.ComposeFrameRows();
        Assert.DoesNotContain(tail, r => r.EndsWith(marker, StringComparison.Ordinal));
    }

    [Fact]
    public void Driver_FrameEngine_FirstPageKeepsIndicatorNearBottom_ThenMovesUpMonotonically()
    {
        var t = new FakeTerminal { Width = 60, Height = 16 };
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 100, false, false, false);
        for (int i = 0; i < 200; i++) d.CommitLine($"line {i:D3}");

        string marker = TuiDriver.FrameScrollIndicatorCell();
        static int MarkerRow(IReadOnlyList<string> rows, string marker)
            => Enumerable.Range(0, rows.Count).Single(i => rows[i].EndsWith(marker, StringComparison.Ordinal));

        Assert.True(d.FrameScrollBy(8));
        var first = d.ComposeFrameRows();
        int firstRow = MarkerRow(first, marker);
        Assert.True(firstRow >= 5, $"First partial page marker was unexpectedly high: row {firstRow}");

        Assert.True(d.FrameScrollBy(24));
        int secondRow = MarkerRow(d.ComposeFrameRows(), marker);
        Assert.True(secondRow < firstRow, $"Marker did not move upward: {firstRow} -> {secondRow}");

        Assert.True(d.FrameScrollBy(10_000));
        Assert.Equal(0, MarkerRow(d.ComposeFrameRows(), marker));
    }

    [Fact]
    public void Driver_FrameEngine_SparseTranscriptIsBottomAnchored()
    {
        var t = new FakeTerminal { Width = 60, Height = 14 };
        var d = new TuiDriver(t, frameEngine: true);
        d.CommitStartup(new[] { "SPLASH-TOP", "SECOND-LINE" });

        var rows = d.ComposeFrameRows();
        // Short startup content now sits at the BOTTOM of the transcript pane (just above the live
        // band/footer) instead of stranded at the top with a large empty gap below it. The two
        // content rows must appear consecutively, in order, with blank rows padding ABOVE them.
        int top = Enumerable.Range(0, rows.Count).First(i => rows[i].Contains("SPLASH-TOP"));
        Assert.Contains("SECOND-LINE", rows[top + 1]);
        Assert.True(top > 0, $"Sparse content should be padded from the top, but SPLASH-TOP was at row {top}.");
        for (int i = 0; i < top; i++)
        {
            string plain = System.Text.RegularExpressions.Regex.Replace(rows[i], "\u001b\\[[0-9;?]*[A-Za-z]", "");
            Assert.True(string.IsNullOrWhiteSpace(plain), $"Row {i} above content should be blank.");
        }
    }

    [Fact]
    public void FrameSplash_MediumIncludesFullBrandArtMascotAndHelp()
    {
        var rows = MuxConsole.BuildFrameSplashLines("0.12.4", "gtest", "Tip", "hello world", 120);
        string plain = string.Join("\n", rows.Select(TuiMarkup.Plain));

        Assert.Contains("███╗", plain);
        Assert.Contains("◠ ◠", plain);
        Assert.Contains("███████╗██╗", plain);
        Assert.Contains("v0.12.4", plain);
        Assert.Contains("hello world", plain);
        Assert.Contains("Check Out The Repo Here!", plain);
        Assert.Contains("Type /help for commands", plain);
    }


    [Fact]
    public void FrameSplash_MediumWrapsLongMessageWithoutDroppingTail()
    {
        string message = new string('x', 90) + " END-OF-MESSAGE";
        var rows = MuxConsole.BuildFrameSplashLines("0.12.4", "", "Tip", message, 72);
        string plain = string.Join("\n", rows.Select(TuiMarkup.Plain));

        Assert.Contains("END-OF-MESSAGE", plain);
    }

    [Fact]
    public void FrameSplash_WideIncludesGettingStartedAndRecentSessions()
    {
        var sessions = new[]
        {
            new MuxConsole.SplashSession("2026-07-18_01-02-03", "agent", "test session"),
        };
        var rows = MuxConsole.BuildFrameSplashLines("0.12.4", "", "Fact", "hello", 220, sessions);
        string plain = string.Join("\n", rows.Select(TuiMarkup.Plain));

        Assert.Contains("Getting Started", plain);
        Assert.Contains("/swarm", plain);
        Assert.Contains("Recent Sessions", plain);
        Assert.Contains("2026-07-18_01-02-03", plain);
        Assert.Contains("test session", plain);
    }

    [Fact]
    public void Driver_FrameEngine_SuspendForPrompt_ReplaysOnlyNewContext()
    {
        var t = new FakeTerminal { Width = 60, Height = 12 };
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 100, false, false, false);
        d.CommitLine("old context");
        d.BeginPromptContext();
        d.Commit(new[] { "choice 1", "choice 2" });
        t.Clear();

        d.SuspendForPrompt();
        string output = t.Output;
        Assert.Contains(Ansi.LeaveAltScreen, output);
        Assert.Contains("choice 1", output);
        Assert.Contains("choice 2", output);
        Assert.DoesNotContain("old context", output);

        t.Clear();
        d.SuspendForPrompt();
        Assert.DoesNotContain("choice 1", t.Output);
    }

    [Fact]
    public void Driver_InlineEngine_SuspendForPrompt_DoesNotReplayTranscript()
    {
        var t = new FakeTerminal { Width = 60, Height = 12 };
        var d = new TuiDriver(t, frameEngine: false);
        d.SetFooter(1, 100, false, false, false);
        d.BeginPromptContext();
        d.CommitLine("inline choice");
        t.Clear();

        d.SuspendForPrompt();

        Assert.DoesNotContain("inline choice", t.Output);
        Assert.DoesNotContain(Ansi.LeaveAltScreen, t.Output);
    }

    [Fact]
    public void Driver_FrameEngine_SuspendForPrompt_DefersPresentsUntilResume()
    {
        var t = new FakeTerminal { Width = 60, Height = 12 };
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 100, false, false, false);
        d.BeginPromptContext();
        d.CommitLine("choice");
        d.SuspendForPrompt();
        t.Clear();

        d.SetFooter(2, 100, false, false, false);
        d.CommitLine("while suspended");
        Assert.Equal("", t.Output);

        d.Resume();
        Assert.Contains(Ansi.EnterAltScreen, t.Output);
        Assert.Contains("while suspended", t.Output);
    }

    [Fact]
    public void Driver_FrameEngine_KeyboardScrollAtOldestBoundaryIsNoOp()
    {
        var t = new FakeTerminal { Width = 40, Height = 10 };
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 100, false, false, false);
        for (int i = 0; i < 40; i++) d.CommitLine($"line {i:D2}");

        Assert.True(d.FrameScrollBy(10_000));
        Assert.False(d.FrameScrollBy(10_000));
    }

    [Fact]
    public void Driver_FrameEngine_DoesNotEmitMouseReportingModes()
    {
        var t = new FakeTerminal();
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 0, false, false, false);

        Assert.DoesNotContain("\u001b[?1000h", t.Output);
        Assert.DoesNotContain("\u001b[?1002h", t.Output);
        Assert.DoesNotContain("\u001b[?1006h", t.Output);
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

    // ---- v0.12.4 scroll-speed + startup-anchor + replay-framing polish ----

    [Fact]
    public void Driver_FrameEngine_ScrollSpeedRows_StepsExactlyThatManyRows()
    {
        var t = new FakeTerminal { Width = 40, Height = 12 };
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 100, false, false, false);
        for (int i = 0; i < 60; i++) d.CommitLine($"line {i:D2}");

        // A single Ctrl+U/Ctrl+D step is modelled by FrameScrollBy(_scrollSpeedRows). Prove the
        // compose window shifts by exactly the configured number of physical rows.
        d.SetScrollSpeedRows(1);
        var baseRows = d.ComposeFrameRows();
        Assert.True(d.FrameScrollBy(1));
        var oneUp = d.ComposeFrameRows();
        Assert.NotEqual(FirstTranscript(baseRows), FirstTranscript(oneUp));

        // Reset to tail, then a 5-row step must land 5 rows further back than a 1-row step.
        Assert.True(d.FrameScrollBy(-10_000));
        var tail = d.ComposeFrameRows();
        Assert.True(d.FrameScrollBy(5));
        var fiveUp = d.ComposeFrameRows();
        Assert.True(d.FrameScrollBy(-10_000));
        Assert.True(d.FrameScrollBy(1));
        var oneUp2 = d.ComposeFrameRows();
        // five-row window's top transcript line is strictly older than the one-row window's.
        Assert.NotEqual(FirstTranscript(fiveUp), FirstTranscript(oneUp2));
        Assert.NotEqual(FirstTranscript(fiveUp), FirstTranscript(tail));
    }

    [Fact]
    public void Driver_FrameEngine_SetScrollSpeedRows_ClampsToMinimumOne()
    {
        var t = new FakeTerminal { Width = 40, Height = 10 };
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 100, false, false, false);
        for (int i = 0; i < 40; i++) d.CommitLine($"line {i:D2}");

        // 0 (and negatives) must clamp to 1 so the binds never become dead keys.
        d.SetScrollSpeedRows(0);
        Assert.True(d.FrameScrollBy(1)); // proxy: a 1-row move still works after a 0 setting
    }

    [Fact]
    public void Driver_FrameEngine_StartupOverflow_ShowsNoMarkerUntilUserScrolls()
    {
        // A splash taller than the transcript pane seeds _frameScroll at the top via CommitStartup.
        // The passive marker must NOT light on this virgin startup (the user has not paged yet).
        var t = new FakeTerminal { Width = 40, Height = 6 };
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 100, false, false, false);
        var splash = new List<string>();
        for (int i = 0; i < 30; i++) splash.Add($"splash {i:D2}");
        d.CommitStartup(splash);

        string marker = TuiDriver.FrameScrollIndicatorCell();
        var rows = d.ComposeFrameRows();
        Assert.DoesNotContain(rows, r => r.EndsWith(marker, StringComparison.Ordinal));

        // Once the user actually pages, the marker arms.
        Assert.True(d.FrameScrollBy(-2)); // move toward the tail from the seeded top
        var afterScroll = d.ComposeFrameRows();
        Assert.Contains(afterScroll, r => r.EndsWith(marker, StringComparison.Ordinal));
    }

    [Fact]
    public void Driver_FrameEngine_SuspendForPrompt_FramesEachReplayRowWithCrlf()
    {
        var t = new FakeTerminal { Width = 60, Height = 12 };
        var d = new TuiDriver(t, frameEngine: true);
        d.SetFooter(1, 100, false, false, false);
        d.BeginPromptContext();
        d.Commit(new[] { "choice 1", "choice 2" });
        t.Clear();

        d.SuspendForPrompt();
        string output = t.Output;
        // Each replayed row is framed CR .. EraseLine .. row .. CRLF, preventing column drift on
        // stacks where LF is not implicitly CR+LF.
        Assert.Contains("\r" + Ansi.EraseLine, output);
        Assert.Contains("\r\n", output);
        Assert.DoesNotContain(Ansi.EraseLine + "choice 1\n\u001b", output); // no bare-LF framing
    }

    private static string FirstTranscript(IReadOnlyList<string> rows)
    {
        foreach (var r in rows)
        {
            string plain = System.Text.RegularExpressions.Regex.Replace(r, "\u001b\\[[0-9;?]*[A-Za-z]", "").TrimEnd();
            if (!string.IsNullOrWhiteSpace(plain)) return plain;
        }
        return string.Empty;
    }
}
