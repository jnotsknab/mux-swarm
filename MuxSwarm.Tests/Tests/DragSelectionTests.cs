using System.Reflection;
using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Batch 8 transcript drag-selection (frame engine): motion-vs-click disambiguation,
/// reverse-video overlay, ANSI-stripped line copy through the NAV yank pair (OSC 52 asserted on
/// the terminal sink), and the inline/mid-turn exclusions. Headless throughout.</summary>
[Collection("ConsoleState")]
public class DragSelectionTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 90;
        public int Height { get; set; } = 24;
        public StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Compose(TuiDriver driver)
        => typeof(TuiDriver).GetMethod("ComposeFrameRows", PrivateInstance | BindingFlags.Public)!.Invoke(driver, null);

    private static List<string> ComposeRows(TuiDriver driver)
        => (List<string>)typeof(TuiDriver).GetMethod("ComposeFrameRows", PrivateInstance | BindingFlags.Public)!.Invoke(driver, null)!;

    private static void Mouse(TuiDriver driver, int button, int col, int row, bool release = false)
        => driver.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(button, col, row, release));

    // The text an ANSI row paints in reverse video (CSI 7m on; 27m / 0m / bare m off).
    private static string InvertedText(string row)
    {
        var sb = new StringBuilder();
        bool inv = false;
        int pos = 0;
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(row, "\u001b\\[[0-9;?]*[A-Za-z]|\u001b\\][^\u0007]*\u0007"))
        {
            if (inv) sb.Append(row, pos, m.Index - pos);
            if (m.Value == Ansi.Invert) inv = true;
            else if (m.Value is "\u001b[27m" or "\u001b[0m" or "\u001b[m") inv = false;
            pos = m.Index + m.Length;
        }
        if (inv) sb.Append(row, pos, row.Length - pos);
        return sb.ToString();
    }

    private static string CopiedText(Terminal term)
    {
        string output = term.Output.ToString();
        int at = output.LastIndexOf("]52;c;", StringComparison.Ordinal);
        Assert.True(at >= 0, "expected an OSC 52 copy");
        string b64 = output[(at + 6)..output.IndexOf('\a', at)];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    // 1-based (row, display cell) of the first cell of `needle` in the composed rows.
    private static (int Row, int Col) Cell(List<string> rows, string needle)
    {
        for (int r = 1; r <= rows.Count; r++)
        {
            string plain = TuiMarkup.StripAnsi(rows[r - 1]);
            int i = plain.IndexOf(needle, StringComparison.Ordinal);
            if (i >= 0) return (r, TuiMarkup.Width(plain[..i]) + 1);
        }
        throw new Xunit.Sdk.XunitException($"'{needle}' not visible");
    }

    private static TuiDriver FrameDriver(Terminal term, int lines = 30)
    {
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < lines; i++) driver.CommitLine($"history line {i}");
        Compose(driver);
        return driver;
    }

    // ---- gesture semantics ----

    [Fact]
    public void PressAndReleaseSameRow_IsClick_NeverSelection()
    {
        var term = new Terminal();
        var driver = FrameDriver(term);
        Mouse(driver, 0, 10, 5);
        Assert.False(driver.DragSelecting);
        Mouse(driver, 0, 12, 5, release: true);   // same row, different col: still a click
        Assert.False(driver.DragSelecting);
        Assert.DoesNotContain("]52;c;", term.Output.ToString());   // nothing copied
    }

    [Fact]
    public void VerticalMotionAcrossRowBoundary_ArmsSelection()
    {
        var driver = FrameDriver(new Terminal());
        Mouse(driver, 0, 10, 5);
        Assert.False(driver.DragSelecting);
        Mouse(driver, 32, 10, 7);   // motion with left held, crossed rows
        Assert.True(driver.DragSelecting);
        Mouse(driver, 0, 10, 7, release: true);   // ends the gesture
        Assert.False(driver.DragSelecting);
    }

    [Fact]
    public void HorizontalMotion_SameRow_SelectsCharacters()
    {
        var term = new Terminal();
        var driver = FrameDriver(term);
        var rows = ComposeRows(driver);
        var (r, c) = Cell(rows, "line 10");
        term.Output.Clear();
        Mouse(driver, 0, c, r);
        Mouse(driver, 32, c + 5, r);   // dragged along the same row
        Assert.True(driver.DragSelecting);
        Mouse(driver, 0, c + 5, r, release: true);
        Assert.Equal("line 1", CopiedText(term));
    }

    [Fact]
    public void SelectionOverlay_InvertsExactlyTheSelectedCells()
    {
        var driver = FrameDriver(new Terminal());
        var rows = ComposeRows(driver);
        var (r0, c0) = Cell(rows, "line 10");
        var (r2, c2) = Cell(rows, "history line 12");
        Mouse(driver, 0, c0, r0);
        Mouse(driver, 32, c2 + 2, r2);   // ends on the 's' of "history"
        var sel = ComposeRows(driver);
        for (int r = 1; r <= sel.Count; r++)
        {
            string plain = TuiMarkup.StripAnsi(rows[r - 1]);
            string expected = r == r0 ? plain[plain.IndexOf("line 10", StringComparison.Ordinal)..]
                : r == r0 + 1 ? plain
                : r == r2 ? plain[..(plain.IndexOf("history", StringComparison.Ordinal) + 3)]
                : "";
            Assert.Equal(expected, InvertedText(sel[r - 1]));
        }
        Mouse(driver, 0, c2 + 2, r2, release: true);
        // After release the overlay is gone.
        Assert.All(ComposeRows(driver).Take(r2 + 1), row => Assert.Equal("", InvertedText(row)));
    }

    [Fact]
    public void SelectionOverlay_DragUpward_InvertsSameSpan()
    {
        var down = FrameDriver(new Terminal());
        var up = FrameDriver(new Terminal());
        var rows = ComposeRows(down);
        var (r0, c0) = Cell(rows, "line 10");
        var (r2, c2) = Cell(rows, "line 12");
        Mouse(down, 0, c0, r0); Mouse(down, 32, c2, r2);
        Mouse(up, 0, c2, r2); Mouse(up, 32, c0, r0);   // upward drag, same two cells
        var a = ComposeRows(down);
        var b = ComposeRows(up);
        Assert.Equal(a.Select(InvertedText), b.Select(InvertedText));
        Assert.StartsWith("line 10", InvertedText(a[r0 - 1]));
        Assert.DoesNotContain("history", InvertedText(a[r0 - 1]));
    }

    // ---- copy on release ----

    [Fact]
    public void ReleaseAfterDrag_CopiesVisibleText_ViaOsc52()
    {
        var term = new Terminal();
        var driver = FrameDriver(term);
        // Bottom-anchored transcript: find two adjacent rows with known content.
        var rows = ComposeRows(driver);
        int r0 = -1;
        for (int r = 1; r <= rows.Count && r0 < 0; r++)
            if (TuiMarkup.StripAnsi(rows[r - 1]).Contains("history line 10")) r0 = r;
        Assert.True(r0 > 0, "row with 'history line 10' must be visible");
        var (_, c0) = Cell(rows, "line 10");
        var (r1, c1) = Cell(rows, "history line 11");
        term.Output.Clear();
        Mouse(driver, 0, c0, r0);
        Mouse(driver, 32, c1 + 6, r1);
        Mouse(driver, 0, c1 + 6, r1, release: true);   // release on the 'y' of "history"
        Assert.Equal("line 10\nhistory", CopiedText(term));
    }

    [Fact]
    public void UpwardDrag_CopiesSameStreamSpan()
    {
        var term = new Terminal();
        var driver = FrameDriver(term);
        var rows = ComposeRows(driver);
        var (r0, c0) = Cell(rows, "line 10");
        var (r1, c1) = Cell(rows, "line 11");
        term.Output.Clear();
        Mouse(driver, 0, c1 + 3, r1);   // press on the 'e' of "line 11"
        Mouse(driver, 32, c0, r0);
        Mouse(driver, 0, c0, r0, release: true);
        Assert.Equal("line 10\nhistory line", CopiedText(term));
    }

    [Fact]
    public void WideChars_MapByDisplayCell()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < 30; i++) driver.CommitLine($"wide {i} \u4E2D\u6587\u5B57\u7B26 \U0001F600 tail");
        var rows = ComposeRows(driver);
        var (r, c) = Cell(rows, "wide 20 ");
        int zhong = c + 8;   // first cell of the first CJK char
        term.Output.Clear();
        Mouse(driver, 0, zhong + 3, r);   // SECOND cell of the 2nd CJK char: whole char selected
        Mouse(driver, 32, zhong + 9, r);  // first cell of the emoji
        var sel = ComposeRows(driver);
        Assert.Equal("\u6587\u5B57\u7B26 \U0001F600", InvertedText(sel[r - 1]));
        Mouse(driver, 0, zhong + 9, r, release: true);
        Assert.Equal("\u6587\u5B57\u7B26 \U0001F600", CopiedText(term));
    }

    [Fact]
    public void StyledMarkupRow_ColumnsMapToVisibleText()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < 30; i++)
            driver.CommitLine($"[#64B4DC]agent[/] [#3A3A3A]\u00b7[/] styled {i} [#F0F0F0]tail[/]");
        var rows = ComposeRows(driver);
        var (r, c) = Cell(rows, "styled 20 tail");
        term.Output.Clear();
        Mouse(driver, 0, c + 3, r);
        Mouse(driver, 32, c + 11, r);   // through the 'a' of the styled "tail" span
        var sel = ComposeRows(driver);
        Assert.Equal("led 20 ta", InvertedText(sel[r - 1]));
        Mouse(driver, 0, c + 11, r, release: true);
        Assert.Equal("led 20 ta", CopiedText(term));
    }

    [Fact]
    public void DragBelowPane_ClampsToEndOfLastPaneRow()
    {
        var term = new Terminal();
        var driver = FrameDriver(term);
        var rows = ComposeRows(driver);
        var (r0, c0) = Cell(rows, "line 29");
        term.Output.Clear();
        Mouse(driver, 0, c0, r0);
        Mouse(driver, 32, 3, term.Height + 5);   // far below the pane (footer/compose rows)
        Mouse(driver, 0, 3, term.Height + 5, release: true);
        Assert.Equal("line 29", CopiedText(term));   // the pane rows below are blank / absent
    }

    [Fact]
    public void ReleaseAfterDrag_NeverFallsThroughToClickRouting()
    {
        var term = new Terminal();
        var driver = FrameDriver(term);
        driver.CommitCollapsed("· CodeAgent finished (ctrl+e)", "CodeAgent", "body");
        Compose(driver);
        var map = driver.FrameHitMap!;
        int cardRow = -1;
        for (int r = 1; r <= term.Height && cardRow < 0; r++)
            if (map.TryHit(r, 4, out var hit, out _, out _) && hit.Kind == MouseTargetKind.TranscriptEntry)
                cardRow = r;
        Assert.True(cardRow > 1, "expandable card must be visible below row 1");
        var transcript = (System.Collections.IList)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!;
        var entry = transcript[map.TryHit(cardRow, 4, out var h2, out _, out _) ? h2.Payload : 0]!;
        var expandedField = entry.GetType().GetField("Expanded")!;
        // Press ON the card, drag off it, release: copy, but NO expand toggle.
        Mouse(driver, 0, 4, cardRow);
        Mouse(driver, 32, 4, cardRow - 1);
        Mouse(driver, 0, 4, cardRow - 1, release: true);
        Assert.False((bool)expandedField.GetValue(entry)!);
    }

    [Fact]
    public void CopyStatusChip_ShowsThenExpires()
    {
        var term = new Terminal();
        var driver = FrameDriver(term);
        long tick = 1000;
        driver.MouseClock = () => tick;
        Mouse(driver, 0, 10, 5);
        Mouse(driver, 32, 10, 7);
        Mouse(driver, 0, 10, 7, release: true);
        var rows = ComposeRows(driver);
        Assert.Contains(rows, r => r.Contains("copied 3 lines", StringComparison.Ordinal));
        tick += 3000;   // past the 2.5s chip deadline
        rows = ComposeRows(driver);
        Assert.DoesNotContain(rows, r => r.Contains("copied 3 lines", StringComparison.Ordinal));
    }

    [Fact]
    public void WhitespaceOnlySelection_CopiesNothing()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        driver.CommitLine("only one line");   // pane is mostly blank filler rows
        Compose(driver);
        term.Output.Clear();
        Mouse(driver, 0, 10, 1);
        Mouse(driver, 32, 10, 3);   // all blank filler rows at the pane top
        Mouse(driver, 0, 10, 3, release: true);
        Assert.DoesNotContain("]52;c;", term.Output.ToString());
    }

    // ---- exclusions ----

    [Fact]
    public void ThumbDrag_IsNeverASelection()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < 200; i++) driver.CommitLine($"history {i}");
        Compose(driver);
        var sb = driver.ScrollBar;
        Assert.True(sb.Visible);
        Mouse(driver, 0, term.Width, sb.Top + 1);
        Mouse(driver, 32, term.Width, 1);
        Assert.False(driver.DragSelecting);   // scrollbar owns its own drag semantics
        Mouse(driver, 0, term.Width, 1, release: true);
        Assert.DoesNotContain("]52;c;", term.Output.ToString());
    }

    [Fact]
    public void InlineEngine_HasNoBackdropRegion_NativeSelectionOwnsThePlane()
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: false);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < 20; i++) driver.CommitLine($"line {i}");
        Assert.Null(driver.FrameHitMap);   // no frame compose, no backdrop, no app selection inline
    }

    // ---- StripAnsi helper contract ----

    [Fact]
    public void StripAnsi_RemovesCsiAndOsc_PreservesText()
    {
        Assert.Equal("hello world", TuiMarkup.StripAnsi("\u001b[38;5;10mhello\u001b[0m \u001b]52;c;YWJj\u0007world"));
        Assert.Equal("plain", TuiMarkup.StripAnsi("plain"));
        Assert.Equal("", TuiMarkup.StripAnsi(""));
    }
}
