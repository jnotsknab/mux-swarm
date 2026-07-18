using System.Text;
using MuxSwarm.Utils.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Headless coverage for the v0.11.0 live-region TUI core (Workstream G, Option B):
/// ANSI builders, the markup-&gt;span pipeline, width/wrap/truncate math, the in-place
/// live-region repaint algorithm (driven by an in-memory fake terminal), and the line
/// editor state machine. None of this touches a real console, so the renderer's logic is
/// fully verifiable in CI. These do NOT mutate global MuxConsole state, so they don't need
/// the ConsoleState collection.
/// </summary>
public class TuiCoreTests
{
    // --- Ansi ----------------------------------------------------------------

    [Fact]
    public void Ansi_EraseLines_Zero_IsNoOp()
        => Assert.Equal("", Ansi.EraseLines(0));

    [Fact]
    public void Ansi_EraseLines_BuildsEraseAndUpThenColumnReset()
    {
        // 3 lines: erase, up, erase, up, erase, then column-1.
        var s = Ansi.EraseLines(3);
        Assert.StartsWith(Ansi.EraseLine, s);
        Assert.EndsWith(Ansi.CursorLeft, s);
        // Two "cursor up 1" between three erases.
        int ups = CountOccurrences(s, Ansi.CursorUp(1));
        Assert.Equal(2, ups);
        Assert.Equal(3, CountOccurrences(s, Ansi.EraseLine));
    }

    [Fact]
    public void Ansi_Fg_Is24BitTruecolor()
        => Assert.Equal("\u001b[38;2;100;180;220m", Ansi.Fg(100, 180, 220));

    // --- markup --------------------------------------------------------------

    [Fact]
    public void Markup_Plain_StripsTags()
        => Assert.Equal("hello world", TuiMarkup.Plain("[#64B4DC]hello[/] [dim]world[/]"));

    [Fact]
    public void Markup_EscapedBrackets_AreLiteral()
        => Assert.Equal("[x]", TuiMarkup.Plain("[[x]]"));

    [Fact]
    public void Markup_Parse_NestedStyles_InheritAndPop()
    {
        var spans = TuiMarkup.Parse("[#FF0000]a[bold]b[/]c[/]");
        Assert.Equal(3, spans.Count);
        Assert.Equal("a", spans[0].Text);
        Assert.False(spans[0].Style.Bold);
        Assert.Equal("b", spans[1].Text);
        Assert.True(spans[1].Style.Bold);                 // inherits red + bold
        Assert.Equal((byte)255, spans[1].Style.Fg!.Value.R);
        Assert.Equal("c", spans[2].Text);
        Assert.False(spans[2].Style.Bold);                // bold popped
        Assert.Equal((byte)255, spans[2].Style.Fg!.Value.R); // red still active
    }

    [Fact]
    public void Markup_ToAnsi_EmitsColorAndResets()
    {
        var ansi = TuiMarkup.ToAnsi("[#64B4DC]hi[/]");
        Assert.Contains("\u001b[38;2;100;180;220m", ansi);
        Assert.Contains("hi", ansi);
        Assert.EndsWith(Ansi.Reset, ansi);
    }

    [Fact]
    public void Markup_UnknownTag_IsIgnored_StyleUnchanged()
    {
        var spans = TuiMarkup.Parse("[wat]x[/]");
        Assert.Single(spans);
        Assert.Equal("x", spans[0].Text);
        Assert.Null(spans[0].Style.Fg);
    }

    [Fact]
    public void Markup_DanglingBracket_IsLiteral()
        => Assert.Equal("a[b", TuiMarkup.Plain("a[b"));

    // --- width / wrap / truncate --------------------------------------------

    [Fact]
    public void Width_CountsWideRunesAsTwo()
    {
        Assert.Equal(2, TuiMarkup.Width("\u4f60"));      // CJK
        Assert.Equal(4, TuiMarkup.Width("\u4f60\u597d"));
        Assert.Equal(3, TuiMarkup.Width("a\u4f60"));     // 1 + 2
    }

    [Fact]
    public void Truncate_AddsEllipsisWithinBudget()
    {
        var t = TuiMarkup.TruncatePlain("abcdefgh", 5);
        Assert.Equal(5, TuiMarkup.Width(t));
        Assert.EndsWith("\u2026", t);
        Assert.StartsWith("abcd", t);
    }

    [Fact]
    public void Truncate_NoOp_WhenWithinWidth()
        => Assert.Equal("abc", TuiMarkup.TruncatePlain("abc", 10));

    [Fact]
    public void Wrap_BreaksOnWidth_AndHonorsNewlines()
    {
        var lines = TuiMarkup.WrapPlain("the quick brown", 9);
        Assert.True(lines.Count >= 2);
        Assert.All(lines, l => Assert.True(TuiMarkup.Width(l) <= 9));
    }

    [Fact]
    public void Wrap_HardBreaksOverlongWord()
    {
        var lines = TuiMarkup.WrapPlain("abcdefghijklmnop", 5);
        Assert.All(lines, l => Assert.True(TuiMarkup.Width(l) <= 5));
        Assert.Equal("abcdefghijklmnop", string.Concat(lines));
    }

    // --- LiveRegion (fake terminal) -----------------------------------------

    private sealed class FakeTerminal : ITuiTerminal
    {
        private readonly StringBuilder _sb = new();
        public int Width { get; set; } = 40;
        public int Height { get; set; } = 20;
        public void Write(string s) => _sb.Append(s);
        public void Flush() { }
        public string Output => _sb.ToString();
        public void Clear() => _sb.Clear();
    }

    [Fact]
    public void LiveRegion_SetLive_PaintsRowsAndHidesCursor()
    {
        var term = new FakeTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "[#64B4DC]status[/]", "prompt >" });

        Assert.Equal(2, lr.PaintedRows);
        Assert.Contains(Ansi.HideCursor, term.Output);
        Assert.Contains("status", term.Output);
        Assert.Contains("prompt >", term.Output);
    }

    [Fact]
    public void LiveRegion_Repaint_ErasesPriorRowsFirst()
    {
        var term = new FakeTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "one", "two" });
        term.Clear();

        lr.SetLive(new List<string> { "x" });
        // Must move up over the 2 previously painted rows and erase down before repaint.
        Assert.Contains(Ansi.CursorUp(2), term.Output);
        Assert.Contains(Ansi.EraseDown, term.Output);
        Assert.Equal(1, lr.PaintedRows);
    }

    [Fact]
    public void LiveRegion_CommitAbove_WritesCommittedThenRepaintsLive()
    {
        var term = new FakeTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "footer" });
        term.Clear();

        lr.CommitAbove(new List<string> { "permanent line" });
        Assert.Contains("permanent line", term.Output);
        Assert.Contains("footer", term.Output); // live region repainted after commit
        Assert.Equal(1, lr.PaintedRows);
    }

    [Fact]
    public void WrapMarkupLine_HangIndents_ContinuationRowsOfIndentedLine()
    {
        // A committed agent-prose line that is visually indented (2 leading spaces) and longer than
        // the width must wrap so that the CONTINUATION rows re-apply the indent - otherwise long
        // lines soft-wrap back to column 0 and break the aligned output column (the off-indentation
        // bug). Every wrapped row's first non-ANSI char should be a space (the hang indent).
        int cols = 24;
        string longBody = "alpha beta gamma delta epsilon zeta eta theta";
        var rows = LiveRegion.WrapMarkupLine("  " + longBody, cols);
        Assert.True(rows.Count >= 2, "the line should wrap into multiple rows at this width");
        foreach (var r in rows)
        {
            // Strip any leading ANSI SGR (ESC[...m) then assert the visible text starts with 2 spaces.
            string visible = System.Text.RegularExpressions.Regex.Replace(r, "\u001b\\[[0-9;]*m", "");
            Assert.StartsWith("  ", visible);
            // CRITICAL: hang(2) + text must NOT exceed cols, else the terminal soft-wraps the overflow
            // back to col 0 (the residual flush-left wrap bug). Every row must fit within the width.
            Assert.True(TuiMarkup.Width(visible) <= cols,
                $"row exceeds width {cols}: '{visible}' (w={TuiMarkup.Width(visible)})");
        }
    }

    [Fact]
    public void WrapMarkupLine_HangIndents_ContinuationRowsUnderLeadDot()
    {
        // A streamed answer's first line is rendered "  *  text" (dot at col 2 => text at col 4). When
        // it wraps, the continuation rows must align under that col-4 text (4-space hang indent), not
        // fall back to col 0.
        int cols = 24;
        string dotLine = "  [grey]\u25cf[/] alpha beta gamma delta epsilon zeta eta";
        var rows = LiveRegion.WrapMarkupLine(dotLine, cols);
        Assert.True(rows.Count >= 2);
        // Row 0 begins with the dot; later rows begin with the 4-space hang indent.
        for (int i = 0; i < rows.Count; i++)
        {
            string visible = System.Text.RegularExpressions.Regex.Replace(rows[i], "\u001b\\[[0-9;]*m", "");
            // No row may exceed the width (dot row included) - overflow would reflow to col 0.
            Assert.True(TuiMarkup.Width(visible) <= cols,
                $"row {i} exceeds width {cols}: '{visible}' (w={TuiMarkup.Width(visible)})");
            if (i == 0) continue;
            Assert.StartsWith("    ", visible);   // 4-space hang (dot col 2 => text col 4)
            Assert.DoesNotContain("\u25cf", visible); // dot only on the first row
        }
    }

    [Fact]
    public void LiveRegion_Clear_ErasesAndShowsCursor()
    {
        var term = new FakeTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "a", "b" });
        term.Clear();

        lr.Clear();
        Assert.Contains(Ansi.ShowCursor, term.Output);
        Assert.Equal(0, lr.PaintedRows);
    }

    [Fact]
    public void LiveRegion_WrapsLongLine_IntoMultiplePhysicalRows()
    {
        var term = new FakeTerminal { Width = 10 };
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "abcdefghijklmnopqrstuvwxyz" }); // 26 cols / 10 => 3 rows
        Assert.Equal(3, lr.PaintedRows);
    }

    // --- LineEditor ----------------------------------------------------------

    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k, bool ctrl = false)
        => new('\0', k, false, false, ctrl);

    [Fact]
    public void LineEditor_InsertsPrintableChars()
    {
        var ed = new LineEditor();
        foreach (var c in "hi") ed.Feed(Ch(c));
        Assert.Equal("hi", ed.Buffer);
        Assert.Equal(2, ed.Cursor);
    }

    [Fact]
    public void LineEditor_Backspace_RemovesBeforeCursor()
    {
        var ed = new LineEditor();
        foreach (var c in "abc") ed.Feed(Ch(c));
        ed.Feed(Key(ConsoleKey.Backspace));
        Assert.Equal("ab", ed.Buffer);
    }

    [Fact]
    public void LineEditor_CursorMovement_AndMidInsert()
    {
        var ed = new LineEditor();
        foreach (var c in "ac") ed.Feed(Ch(c));
        ed.Feed(Key(ConsoleKey.LeftArrow));     // between a and c
        ed.Feed(Ch('b'));
        Assert.Equal("abc", ed.Buffer);
    }

    [Fact]
    public void LineEditor_Enter_Submits()
        => Assert.Equal(LineEditSignal.Submit, new LineEditor().Feed(Key(ConsoleKey.Enter)));

    [Fact]
    public void LineEditor_CtrlC_Cancels()
        => Assert.Equal(LineEditSignal.Cancel, new LineEditor().Feed(Key(ConsoleKey.C, ctrl: true)));

    [Fact]
    public void LineEditor_CtrlD_OnEmpty_IsEof()
        => Assert.Equal(LineEditSignal.Eof, new LineEditor().Feed(Key(ConsoleKey.D, ctrl: true)));

    [Fact]
    public void LineEditor_History_UpDown_RecallsAndRestoresStash()
    {
        var ed = new LineEditor();
        ed.Remember("first");
        ed.Remember("second");
        ed.Reset();
        foreach (var c in "wip") ed.Feed(Ch(c));     // in-progress line
        ed.Feed(Key(ConsoleKey.UpArrow));            // -> "second"
        Assert.Equal("second", ed.Buffer);
        ed.Feed(Key(ConsoleKey.UpArrow));            // -> "first"
        Assert.Equal("first", ed.Buffer);
        ed.Feed(Key(ConsoleKey.DownArrow));          // -> "second"
        Assert.Equal("second", ed.Buffer);
        ed.Feed(Key(ConsoleKey.DownArrow));          // -> restored "wip"
        Assert.Equal("wip", ed.Buffer);
    }

    [Fact]
    public void LineEditor_SlashFilter_DetectedUntilSpace()
    {
        var ed = new LineEditor();
        foreach (var c in "/he") ed.Feed(Ch(c));
        Assert.True(ed.IsSlashFilter);
        Assert.Equal("/he", ed.SlashFilter);
        ed.Feed(Ch(' '));
        Assert.False(ed.IsSlashFilter);
        Assert.Null(ed.SlashFilter);
    }

    [Fact]
    public void LineEditor_CtrlU_KillsToStart()
    {
        var ed = new LineEditor();
        foreach (var c in "hello") ed.Feed(Ch(c));
        ed.Feed(Key(ConsoleKey.LeftArrow));
        ed.Feed(Key(ConsoleKey.LeftArrow));          // cursor after "hel"
        ed.Feed(Key(ConsoleKey.U, ctrl: true));
        Assert.Equal("lo", ed.Buffer);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void LiveRegion_DiffRepaint_SameRowCount_OnlyRewritesChangedRow()
    {
        var term = new FakeTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "alpha", "beta", "gamma" });
        term.Clear();

        // Only the middle row changes; row count is unchanged => diff path.
        lr.SetLive(new List<string> { "alpha", "BETA", "gamma" });
        var outp = term.Output;
        Assert.Contains("BETA", outp);
        // The unchanged rows must NOT be rewritten.
        Assert.DoesNotContain("alpha", outp);
        Assert.DoesNotContain("gamma", outp);
        // No full-region teardown on the diff path.
        Assert.DoesNotContain(Ansi.EraseDown, outp);
        Assert.Equal(3, lr.PaintedRows);
    }

    [Fact]
    public void LiveRegion_DiffRepaint_NoChange_WritesNothing()
    {
        var term = new FakeTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "one", "two" });
        term.Clear();

        lr.SetLive(new List<string> { "one", "two" }); // identical => no paint
        Assert.Equal("", term.Output);
        Assert.Equal(2, lr.PaintedRows);
    }

    [Fact]
    public void LiveRegion_DiffRepaint_RowCountChange_FullRepaint()
    {
        var term = new FakeTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "one", "two" });
        term.Clear();

        lr.SetLive(new List<string> { "only" }); // count change => full erase+repaint
        Assert.Contains(Ansi.EraseDown, term.Output);
        Assert.Equal(1, lr.PaintedRows);
    }

    // --- Synchronized Output (flicker fix) -----------------------------------

    [Fact]
    public void LiveRegion_DiffRepaint_WrapsFrameInSynchronizedOutput()
    {
        // The in-place spinner/timer tick path must batch its writes in BSU/ESU so the
        // terminal presents one atomic frame (no half-painted row flicker).
        var term = new FakeTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "alpha", "beta", "gamma" });
        term.Clear();
        lr.SetLive(new List<string> { "alpha", "BETA", "gamma" }); // diff path
        var outp = term.Output;
        Assert.Contains(Ansi.BeginSyncOutput, outp);
        Assert.Contains(Ansi.EndSyncOutput, outp);
        // BSU must come before ESU.
        Assert.True(outp.IndexOf(Ansi.BeginSyncOutput, StringComparison.Ordinal)
                  < outp.IndexOf(Ansi.EndSyncOutput, StringComparison.Ordinal));
    }

    [Fact]
    public void LiveRegion_DiffRepaint_NoChange_EmitsNoSyncMarkers()
    {
        // A no-op tick must stay byte-empty - no BSU/ESU, no paint, no flicker.
        var term = new FakeTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "one", "two" });
        term.Clear();
        lr.SetLive(new List<string> { "one", "two" }); // identical => no paint
        Assert.Equal("", term.Output);
    }

    [Fact]
    public void LiveRegion_ForceRepaint_WrapsInSynchronizedOutput()
    {
        var term = new FakeTerminal { Width = 40 };
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "alpha", "beta" });
        term.Clear();
        lr.ForceRepaint();
        var outp = term.Output;
        Assert.Contains(Ansi.BeginSyncOutput, outp);
        Assert.Contains(Ansi.EndSyncOutput, outp);
        Assert.Contains(Ansi.ClearScreen, outp);
    }

    // --- resize invalidation (the resize-artifact fix) -----------------------

    [Fact]
    public void LiveRegion_WidthChange_ForcesFullRepaint_NotDiffFastPath()
    {
        var term = new FakeTerminal { Width = 40 };
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "alpha", "beta" });
        Assert.Contains(Ansi.AutoWrapOff, term.Output);   // live rows protected from reflow from the first paint
        term.Clear();

        // Shrink the terminal, then repaint IDENTICAL markup (as a spinner tick would).
        // Pre-fix this hit the no-op/diff path and left the old frame stranded; now a width
        // change must force a full erase+repaint.
        term.Width = 20;
        lr.SetLive(new List<string> { "alpha", "beta" });

        Assert.NotEqual("", term.Output);                 // must NOT be a no-op
        Assert.Contains(Ansi.EraseDown, term.Output);     // full teardown, not in-place diff
        Assert.Equal(2, lr.PaintedRows);
    }

    [Fact]
    public void LiveRegion_WidthShrink_EmitsAutoWrapOff_AndFullRepaint()
    {
        // Live rows are written with auto-wrap OFF so the emulator cannot soft-wrap/reflow them
        // on resize (reflow is what stranded old frames). A 26-col line is ONE hard row at
        // width 40; shrinking to width 10 re-wraps the MARKUP to 3 hard rows and forces a full
        // erase+repaint. The erase moves up by the (un-reflowed) painted count, which stays exact
        // precisely because auto-wrap was off.
        var term = new FakeTerminal { Width = 40 };
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "abcdefghijklmnopqrstuvwxyz" });
        Assert.Equal(1, lr.PaintedRows);
        Assert.Contains(Ansi.AutoWrapOff, term.Output); // rows protected from reflow from the first paint
        term.Clear();

        term.Width = 10;
        lr.SetLive(new List<string> { "abcdefghijklmnopqrstuvwxyz" });

        Assert.Contains(Ansi.CursorUp(1), term.Output); // erase the 1 hard row that was on screen
        Assert.Contains(Ansi.EraseDown, term.Output);
        Assert.Equal(3, lr.PaintedRows); // 26 / 10 => 3 rows at the new width
    }

    [Fact]
    public void LiveRegion_WidthGrow_RepaintsCleanly()
    {
        var term = new FakeTerminal { Width = 10 };
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "abcdefghijklmnopqrstuvwxyz" }); // 3 rows at width 10
        Assert.Equal(3, lr.PaintedRows);
        term.Clear();

        term.Width = 40;
        lr.SetLive(new List<string> { "abcdefghijklmnopqrstuvwxyz" }); // 1 row at width 40
        Assert.Contains(Ansi.EraseDown, term.Output);
        Assert.Equal(1, lr.PaintedRows);
    }

    [Fact]
    public void LiveRegion_SameWidth_StillUsesDiffFastPath()
    {
        // Guard: the resize handling must not regress the no-op fast path when width is stable.
        var term = new FakeTerminal { Width = 40 };
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "one", "two" });
        term.Clear();

        lr.SetLive(new List<string> { "one", "two" }); // identical, same width => no paint
        Assert.Equal("", term.Output);
        Assert.Equal(2, lr.PaintedRows);
    }

    // --- ForceRepaint (Ctrl+L / resize-settle full redraw) -------------------

    [Fact]
    public void LiveRegion_ForceRepaint_ClearsScreen_AndRepaintsFromScratch()
    {
        var term = new FakeTerminal { Width = 40 };
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "alpha", "beta" });
        term.Clear();

        lr.ForceRepaint();
        var outp = term.Output;
        // A full-clear redraw: clear the viewport, home the cursor, then repaint the content.
        Assert.Contains(Ansi.ClearScreen, outp);
        Assert.Contains(Ansi.Home, outp);
        Assert.Contains("alpha", outp);
        Assert.Contains("beta", outp);
        Assert.Equal(2, lr.PaintedRows);
    }

    [Fact]
    public void LiveRegion_ForceRepaint_AfterWidthChange_RepaintsAtNewWidth()
    {
        var term = new FakeTerminal { Width = 40 };
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "abcdefghijklmnopqrstuvwxyz" }); // 1 row at width 40
        Assert.Equal(1, lr.PaintedRows);
        term.Clear();

        // Simulate a resize, then a forced redraw (what the resize poll does).
        term.Width = 10;
        lr.ForceRepaint();
        Assert.Contains(Ansi.ClearScreen, term.Output);
        Assert.Equal(3, lr.PaintedRows); // 26 / 10 => 3 rows at the new width
    }

    // --- v0.12.4 inline-hardening: single-write + atomic synchronized transactions -----------

    /// <summary>A fake terminal that counts Write/Flush calls, to assert each repaint transaction
    /// is emitted as ONE Write + ONE Flush (buffered, not per-escape auto-flushed).</summary>
    private sealed class CountingTerminal : ITuiTerminal
    {
        private readonly StringBuilder _sb = new();
        public int Width { get; set; } = 40;
        public int Height { get; set; } = 20;
        public int Writes { get; private set; }
        public int Flushes { get; private set; }
        public void Write(string s) { Writes++; _sb.Append(s); }
        public void Flush() { Flushes++; }
        public string Output => _sb.ToString();
        public void Reset() { _sb.Clear(); Writes = 0; Flushes = 0; }
    }


    [Fact]
    public void LiveRegion_SetLive_FullRepaint_IsSingleWriteAndFlush()
    {
        var term = new CountingTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "one", "two" });
        term.Reset();

        // Row-count change forces the full erase+repaint path.
        lr.SetLive(new List<string> { "a", "b", "c" });
        Assert.Equal(1, term.Writes);
        Assert.Equal(1, term.Flushes);
    }

    [Fact]
    public void LiveRegion_CommitAbove_IsSingleAtomicSynchronizedWrite()
    {
        var term = new CountingTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "footer" });
        term.Reset();

        lr.CommitAbove(new List<string> { "permanent line" }, new List<string> { "footer2" });

        // One buffered Write + one Flush for the whole erase->commit->repaint transaction.
        Assert.Equal(1, term.Writes);
        Assert.Equal(1, term.Flushes);
        // Exactly one balanced synchronized-output envelope wraps the transaction, so a slow
        // WSL/SSH path can never present the erased-but-not-repainted intermediate frame.
        Assert.Equal(1, CountOccurrences(term.Output, Ansi.BeginSyncOutput));
        Assert.Equal(1, CountOccurrences(term.Output, Ansi.EndSyncOutput));
        Assert.Contains("permanent line", term.Output);
        Assert.Contains("footer2", term.Output);
    }

    [Fact]
    public void LiveRegion_SetLive_NoChange_EmitsNoRepaintWrite()
    {
        var term = new CountingTerminal();
        var lr = new LiveRegion(term);
        lr.SetLive(new List<string> { "stable" });
        term.Reset();

        lr.SetLive(new List<string> { "stable" });   // identical frame
        // No visible repaint: at most a flush, and no erase/paint bytes.
        Assert.DoesNotContain(Ansi.EraseLine, term.Output);
        Assert.DoesNotContain(Ansi.EraseDown, term.Output);
    }
    // --- emoji / wide-grapheme display width (table border alignment) -----------------------

    [Fact]
    public void Width_EmojiAndSymbols_AreDoubleWidth()
    {
        // The posture-report emoji set: red/yellow/green circles + check mark all render width 2.
        Assert.Equal(2, TuiMarkup.Width("\U0001F534")); // red circle
        Assert.Equal(2, TuiMarkup.Width("\U0001F7E1")); // yellow circle
        Assert.Equal(2, TuiMarkup.Width("\U0001F7E2")); // green circle
        Assert.Equal(2, TuiMarkup.Width("\u2705"));      // white heavy check mark (was mis-sized as 1)
        Assert.Equal(2, TuiMarkup.Width("\u26A0\uFE0F")); // warning sign + VS16 (emoji presentation)
    }

    [Fact]
    public void Width_AsciiAndCjk_Unchanged()
    {
        Assert.Equal(5, TuiMarkup.Width("hello"));
        Assert.Equal(2, TuiMarkup.Width("\u4E2D"));      // CJK char = width 2
        Assert.Equal(0, TuiMarkup.Width(""));
    }

    [Fact]
    public void Table_WithEmojiCells_InnerSeparatorAligns()
    {
        // Emoji cells must be measured as width 2 so the INNER column separator lines up across rows.
        // (Trailing spaces on the final cell are trimmed, so we check the inner separator position by
        // display width, not raw line length.) Pre-fix, the check-mark (U+2705) was sized as 1 so the
        // first column was a cell short and the inner border drifted.
        var rows = new List<string>
        {
            "Vector | Status",
            "Privileged | \u2705 Closed",
            "Root remap | \U0001F7E1 Standard",
            "Docker socket | \u2705 Closed",
        };
        var outp = TuiTable.Render(rows, width: 60);
        // Every non-blank rendered row (borders + data) must have the SAME display width, so the right
        // border lines up. The check-mark (U+2705) rows have fewer CHARS but equal DISPLAY width because
        // the emoji is now measured as 2. Pre-fix those rows were one column short and the border drifted.
        int? rowW = null;
        int contentRows = 0;
        foreach (var line in outp)
        {
            string plain = TuiMarkup.Plain(line);
            if (plain.Trim().Length == 0) continue;     // skip the leading/trailing blank lines
            int w = TuiMarkup.Width(plain);
            if (rowW is null) rowW = w;
            else Assert.Equal(rowW, w);
            contentRows++;
        }
        Assert.True(contentRows >= 6);                  // top border + header + sep + 3 data + bottom
    }
}
