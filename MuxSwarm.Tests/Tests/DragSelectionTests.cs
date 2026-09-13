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
    public void HorizontalOnlyMotion_NeverArms()
    {
        var driver = FrameDriver(new Terminal());
        Mouse(driver, 0, 10, 5);
        Mouse(driver, 32, 40, 5);   // dragged along the same row
        Assert.False(driver.DragSelecting);
        Mouse(driver, 0, 40, 5, release: true);
    }

    [Fact]
    public void SelectionOverlay_InvertsExactlyTheDraggedRows()
    {
        var term = new Terminal();
        var driver = FrameDriver(term);
        Mouse(driver, 0, 10, 5);
        Mouse(driver, 32, 10, 8);
        Assert.True(driver.DragSelecting);
        var rows = ComposeRows(driver);
        for (int r = 1; r <= rows.Count; r++)
        {
            bool inverted = rows[r - 1].StartsWith(Ansi.Invert, StringComparison.Ordinal);
            Assert.Equal(r >= 5 && r <= 8, inverted);
        }
        Mouse(driver, 0, 10, 8, release: true);
        // After release the overlay is gone.
        var after = ComposeRows(driver);
        Assert.DoesNotContain(after, r => r.StartsWith(Ansi.Invert, StringComparison.Ordinal));
    }

    [Fact]
    public void SelectionOverlay_DragUpward_InvertsSameRange()
    {
        var driver = FrameDriver(new Terminal());
        Mouse(driver, 0, 10, 8);
        Mouse(driver, 32, 10, 5);   // upward drag
        var rows = ComposeRows(driver);
        for (int r = 1; r <= rows.Count; r++)
            Assert.Equal(r >= 5 && r <= 8, rows[r - 1].StartsWith(Ansi.Invert, StringComparison.Ordinal));
        Mouse(driver, 0, 10, 5, release: true);
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
        term.Output.Clear();
        Mouse(driver, 0, 10, r0);
        Mouse(driver, 32, 10, r0 + 1);
        Mouse(driver, 0, 10, r0 + 1, release: true);
        string output = term.Output.ToString();
        int at = output.IndexOf("]52;c;", StringComparison.Ordinal);
        Assert.True(at >= 0, "OSC 52 copy must be emitted through the terminal sink");
        string b64 = output[(at + 6)..output.IndexOf('\a', at)];
        string copied = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Equal("history line 10\nhistory line 11", copied);
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
