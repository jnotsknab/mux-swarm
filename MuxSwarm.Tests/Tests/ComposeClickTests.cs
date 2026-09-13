using System.Reflection;
using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Batch 5 compose click-to-position: input-row geometry metadata, display-to-raw offset
/// mapping (attachment cards), and the end-to-end ComposeArea click moving the caret.</summary>
[Collection("ConsoleState")]
public class ComposeClickTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 60;
        public int Height { get; set; } = 24;
        public StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    // ---- InputRowsWithCursor meta ----

    [Fact]
    public void InputRows_Meta_ParallelToRows_TracksSegmentsAndWraps()
    {
        var meta = new List<TuiComponents.InputRowMeta>();
        // Width 30, prompt lead 4 cols -> first row cap 26. 40 chars wrap into 26 + 14.
        string buffer = new string('a', 40) + "\n" + "bb";
        var rows = TuiComponents.InputRowsWithCursor(buffer, 0, 30, false, meta);
        Assert.Equal(rows.Count, meta.Count);
        Assert.Equal(3, meta.Count);
        Assert.Equal((0, 0, 26), (meta[0].Seg, meta[0].Pos, meta[0].Take));
        Assert.Equal((0, 26, 14), (meta[1].Seg, meta[1].Pos, meta[1].Take));
        Assert.Equal((1, 0, 2), (meta[2].Seg, meta[2].Pos, meta[2].Take));
        Assert.True(meta[0].LeadCols >= 4);   // "  › "
    }

    [Fact]
    public void InputRows_Meta_EmptyBuffer_ReportsPlaceholderRow()
    {
        var meta = new List<TuiComponents.InputRowMeta>();
        var rows = TuiComponents.InputRowsWithCursor("", 0, 40, false, meta);
        Assert.Single(rows);
        Assert.Single(meta);
        Assert.Equal(0, meta[0].Take);
    }

    // ---- DisplayToRaw (attachment inverse mapping) ----

    [Fact]
    public void DisplayToRaw_NoCards_IsIdentity()
    {
        var editor = new LineEditor();
        editor.InsertText("hello world");
        Assert.Equal(7, editor.Attachments.DisplayToRaw(editor.Buffer, 7));
        Assert.Equal(0, editor.Attachments.DisplayToRaw(editor.Buffer, 0));
        Assert.Equal(11, editor.Attachments.DisplayToRaw(editor.Buffer, 99));   // clamped
    }

    [Fact]
    public void MoveCursorTo_ClampsAndMoves()
    {
        var editor = new LineEditor();
        editor.InsertText("hello");
        editor.MoveCursorTo(2);
        Assert.Equal(2, editor.Cursor);
        editor.MoveCursorTo(-5);
        Assert.Equal(0, editor.Cursor);
        editor.MoveCursorTo(999);
        Assert.Equal(5, editor.Cursor);
    }

    // ---- end-to-end ComposeArea click ----

    private static TuiDriver FrameDriverAtPrompt(Terminal term, string draft)
    {
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(driver)!;
        editor.InsertText(draft);
        typeof(TuiDriver).GetField("_inInput", PrivateInstance)!.SetValue(driver, true);
        typeof(TuiDriver).GetMethod("ComposeFrameRows", PrivateInstance | BindingFlags.Public)!.Invoke(driver, null);
        return driver;
    }

    [Fact]
    public void ComposeFrameRows_AtPrompt_RegistersComposeRegions()
    {
        var term = new Terminal();
        var driver = FrameDriverAtPrompt(term, "hello world");
        var map = driver.FrameHitMap!;
        bool found = false;
        for (int r = 1; r <= term.Height; r++)
            if (map.TryHit(r, 6, out var hit, out _, out _) && hit.Kind == MouseTargetKind.ComposeArea)
                found = true;
        Assert.True(found, "prompt compose row must register a ComposeArea region");
    }

    [Fact]
    public void ComposeClick_MovesCaret_ToClickedColumn()
    {
        var term = new Terminal();
        var driver = FrameDriverAtPrompt(term, "hello world");
        var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(driver)!;
        Assert.Equal(11, editor.Cursor);   // InsertText leaves the caret at the end
        var map = driver.FrameHitMap!;
        int row = -1; HitRegion region = default;
        for (int r = 1; r <= term.Height && row < 0; r++)
            if (map.TryHit(r, 6, out var hit, out _, out _) && hit.Kind == MouseTargetKind.ComposeArea)
            { row = r; region = hit; }
        Assert.True(row > 0);
        // Lead is "  › " (4 cols): buffer col 2 = physical col left+4+2. Click "hello|world" gap.
        var meta = TuiComponents.InputRowsWithCursor("hello world", 0, driver.Width, false,
            new List<TuiComponents.InputRowMeta>());
        int leadCols = 4;
        int col = region.Left + leadCols + 5;   // buffer offset 5 (the space)
        driver.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, col, row, false));
        driver.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, col, row, true));
        Assert.Equal(5, editor.Cursor);
    }

    [Fact]
    public void ComposeClick_PastEndOfText_SnapsToChunkEnd()
    {
        var term = new Terminal();
        var driver = FrameDriverAtPrompt(term, "hi");
        var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(driver)!;
        editor.MoveCursorTo(0);
        var map = driver.FrameHitMap!;
        int row = -1; HitRegion region = default;
        for (int r = 1; r <= term.Height && row < 0; r++)
            if (map.TryHit(r, 6, out var hit, out _, out _) && hit.Kind == MouseTargetKind.ComposeArea)
            { row = r; region = hit; }
        Assert.True(row > 0);
        int col = region.Left + region.Width - 2;   // far right of the compose row
        driver.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, col, row, false));
        driver.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, col, row, true));
        Assert.Equal(2, editor.Cursor);
    }

    [Fact]
    public void ComposeClick_MidTurn_IsDropped()
    {
        var term = new Terminal();
        var driver = FrameDriverAtPrompt(term, "hello");
        var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(driver)!;
        int before = editor.Cursor;
        var map = driver.FrameHitMap!;
        int row = -1;
        for (int r = 1; r <= term.Height && row < 0; r++)
            if (map.TryHit(r, 6, out var hit, out _, out _) && hit.Kind == MouseTargetKind.ComposeArea)
                row = r;
        Assert.True(row > 0);
        Assert.Null(driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 7, row, false)));
        Assert.Null(driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 7, row, true)));
        Assert.Equal(before, editor.Cursor);
    }

    [Fact]
    public void ReverseSearch_RegistersNoComposeRegions()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(driver)!;
        editor.Remember("prior command");
        editor.BeginReverseSearch();
        Assert.True(editor.IsSearching);
        typeof(TuiDriver).GetField("_inInput", PrivateInstance)!.SetValue(driver, true);
        typeof(TuiDriver).GetMethod("ComposeFrameRows", PrivateInstance | BindingFlags.Public)!.Invoke(driver, null);
        var map = driver.FrameHitMap!;
        for (int r = 1; r <= term.Height; r++)
            if (map.TryHit(r, 6, out var hit, out _, out _))
                Assert.NotEqual(MouseTargetKind.ComposeArea, hit.Kind);
    }
}
