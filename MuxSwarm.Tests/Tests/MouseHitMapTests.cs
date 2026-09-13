using System.Reflection;
using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>MouseHitMap registry contract plus the frame driver's scrollbar hit regions, capture
/// semantics, and thumb-drag math - all headless (no live stdin, no real console modes).</summary>
[Collection("ConsoleState")]
public class MouseHitMapTests
{
    // ---- registry ----

    [Fact]
    public void TryHit_MissesOutsideFrameAndOnEmptyMap()
    {
        var map = new MouseHitMap();
        map.Begin(80, 24);
        Assert.False(map.TryHit(1, 1, out _, out _, out _));
        map.Add(new HitRegion(1, 1, 24, 80, MouseTargetKind.ComposeArea, 0));
        Assert.False(map.TryHit(0, 5, out _, out _, out _));    // above frame
        Assert.False(map.TryHit(25, 5, out _, out _, out _));   // below frame
        Assert.False(map.TryHit(5, 81, out _, out _, out _));   // right of frame
    }

    [Fact]
    public void TryHit_ReverseOrderScan_TopmostWins_WithLocalCoords()
    {
        var map = new MouseHitMap();
        map.Begin(80, 24);
        map.Add(new HitRegion(1, 79, 20, 2, MouseTargetKind.ScrollBarRail, 0));
        map.Add(new HitRegion(5, 79, 4, 2, MouseTargetKind.ScrollBarThumb, 0));   // painted later = on top
        Assert.True(map.TryHit(6, 80, out var hit, out int localRow, out int localCol));
        Assert.Equal(MouseTargetKind.ScrollBarThumb, hit.Kind);
        Assert.Equal(1, localRow);   // row 6 in a region starting at 5
        Assert.Equal(1, localCol);   // col 80 in a region starting at 79
        Assert.True(map.TryHit(3, 80, out hit, out localRow, out _));
        Assert.Equal(MouseTargetKind.ScrollBarRail, hit.Kind);
        Assert.Equal(2, localRow);
    }

    [Fact]
    public void Begin_ClearsPriorRegions_AndDegenerateRectsAreIgnored()
    {
        var map = new MouseHitMap();
        map.Begin(80, 24);
        map.Add(new HitRegion(1, 1, 5, 5, MouseTargetKind.NavRow, 7));
        Assert.Equal(1, map.Count);
        map.Begin(80, 24);
        Assert.Equal(0, map.Count);
        map.Add(new HitRegion(1, 1, 0, 5, MouseTargetKind.NavRow, 7));
        map.Add(new HitRegion(1, 1, 5, 0, MouseTargetKind.NavRow, 7));
        Assert.Equal(0, map.Count);
        Assert.False(map.TryHit(2, 2, out _, out _, out _));
    }

    // ---- frame driver integration ----

    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 80;
        public int Height { get; set; } = 24;
        public StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>A frame driver with enough committed history to overflow the viewport (visible
    /// scrollbar), buttons preset, and a composed frame (hit map published).</summary>
    private static (TuiDriver Driver, Terminal Term) NewScrolledFrameDriver(int lines = 200)
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < lines; i++) driver.CommitLine($"history line {i}");
        Compose(driver);
        return (driver, term);
    }

    private static void Compose(TuiDriver driver)
        => typeof(TuiDriver).GetMethod("ComposeFrameRows", PrivateInstance | BindingFlags.Public)!.Invoke(driver, null);

    private static int FrameScroll(TuiDriver driver)
        => (int)typeof(TuiDriver).GetField("_frameScroll", PrivateInstance)!.GetValue(driver)!;

    private static void Mouse(TuiDriver driver, int button, int col, int row, bool release = false)
        => driver.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(button, col, row, release));

    [Fact]
    public void ComposeFrameRows_PublishesScrollbarRegions_InLastTwoColumns()
    {
        var (driver, term) = NewScrolledFrameDriver();
        var map = driver.FrameHitMap;
        Assert.NotNull(map);
        var sb = driver.ScrollBar;
        Assert.True(sb.Visible);
        // Rail hit zone = last TWO physical columns (a358fde lesson).
        Assert.True(map!.TryHit(1, term.Width, out var rail, out _, out _));
        Assert.Equal(MouseTargetKind.ScrollBarRail, rail.Kind);
        Assert.True(map.TryHit(1, term.Width - 1, out rail, out _, out _));
        Assert.Equal(MouseTargetKind.ScrollBarRail, rail.Kind);
        // Width-2 is OUTSIDE the rail: since batch 8 it belongs to the transcript backdrop
        // (drag-selection surface), never to a scrollbar region.
        Assert.True(map.TryHit(1, term.Width - 2, out var backdrop, out _, out _));
        Assert.Equal(MouseTargetKind.ExpandedPanel, backdrop.Kind);
        // Thumb region overlays the rail at the composed thumb rows and wins the scan.
        Assert.True(map.TryHit(sb.Top + 1, term.Width, out var thumb, out _, out _));
        Assert.Equal(MouseTargetKind.ScrollBarThumb, thumb.Kind);
    }

    [Fact]
    public void InlineDriver_NeverPublishesFrameHitMap()
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: false);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < 50; i++) driver.CommitLine($"line {i}");
        Assert.Null(driver.FrameHitMap);
    }

    [Fact]
    public void RailClick_AboveThumb_PagesBackIntoHistory()
    {
        var (driver, term) = NewScrolledFrameDriver();
        Assert.Equal(0, FrameScroll(driver));
        var sb = driver.ScrollBar;
        Assert.True(sb.Top > 0, "thumb must start below the rail top at the live tail");
        // Press + release on the rail ABOVE the thumb = page toward older content.
        Mouse(driver, 0, term.Width, 1);
        Mouse(driver, 0, term.Width, 1, release: true);
        Assert.True(FrameScroll(driver) > 0);
    }

    [Fact]
    public void RailClick_BelowThumb_PagesTowardLiveTail()
    {
        var (driver, term) = NewScrolledFrameDriver();
        driver.FrameScrollBy(1000);   // clamped to oldest
        Compose(driver);
        int scrolledBack = FrameScroll(driver);
        Assert.True(scrolledBack > 0);
        var sb = driver.ScrollBar;
        int belowThumbRow = sb.Top + sb.Length + 1;
        Assert.True(belowThumbRow <= sb.TrackRows, "need rail rows below the thumb at the oldest position");
        Mouse(driver, 0, term.Width, belowThumbRow);
        Mouse(driver, 0, term.Width, belowThumbRow, release: true);
        Assert.True(FrameScroll(driver) < scrolledBack);
    }

    [Fact]
    public void ThumbDrag_TracksPointer_WithGrabOffset_NoJumpCenter()
    {
        var (driver, term) = NewScrolledFrameDriver();
        var sb = driver.ScrollBar;
        int grabRow = sb.Top + 1;   // press on the thumb's first row (grab offset 0)
        Mouse(driver, 0, term.Width, grabRow);
        Assert.Equal(0, FrameScroll(driver));   // press alone must not move the viewport
        // Drag to the rail top: thumb top -> 0 == fully scrolled back.
        Mouse(driver, 32, term.Width, 1);
        Compose(driver);
        int totalRows = (int)typeof(TuiDriver).GetMethod("GetFrameTotalRows", PrivateInstance)!
            .Invoke(driver, new object[] { driver.Width })!;
        Assert.Equal(Math.Max(0, totalRows - sb.TrackRows), FrameScroll(driver));
        // Drag back to the bottom: fully forward to the live tail.
        Mouse(driver, 32, term.Width, sb.TrackRows);
        Assert.Equal(0, FrameScroll(driver));
        Mouse(driver, 0, term.Width, sb.TrackRows, release: true);
    }

    [Fact]
    public void ThumbDrag_PointerMayDriftOffRail_StillTracksVertically()
    {
        var (driver, term) = NewScrolledFrameDriver();
        var sb = driver.ScrollBar;
        Mouse(driver, 0, term.Width, sb.Top + 1);
        Mouse(driver, 32, 5, 1);   // drift far left while dragging - vertical position rules
        Assert.True(FrameScroll(driver) > 0);
        Mouse(driver, 0, 5, 1, release: true);
    }

    [Fact]
    public void ReleaseOutsideCaptureRegion_IsDragCancel_NoAction()
    {
        var (driver, term) = NewScrolledFrameDriver();
        // Press on the rail (above thumb), release far away: no page, no crash.
        Mouse(driver, 0, term.Width, 1);
        Mouse(driver, 0, 10, 12, release: true);
        Assert.Equal(0, FrameScroll(driver));
    }

    [Fact]
    public void PressOutsideAnyRegion_ThenRelease_DoesNothing()
    {
        var (driver, _) = NewScrolledFrameDriver();
        Mouse(driver, 0, 10, 10);
        Mouse(driver, 0, 10, 10, release: true);
        Assert.Equal(0, FrameScroll(driver));
    }

    [Fact]
    public void WheelPreset_RoutesNothing_ClicksStaySwallowed()
    {
        var (driver, term) = NewScrolledFrameDriver();
        driver.SetMouseTrackingPreset("wheel");   // gates ButtonsEnabled off
        Mouse(driver, 0, term.Width, 1);
        Mouse(driver, 0, term.Width, 1, release: true);
        Assert.Equal(0, FrameScroll(driver));
    }
}
