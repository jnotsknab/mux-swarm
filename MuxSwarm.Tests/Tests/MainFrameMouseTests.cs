using System.Reflection;
using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Batch 4 main-frame mouse surfaces: agent-lane regions (activity strip + dashboard),
/// transcript expand/collapse regions, and the mid-turn target filter. Headless throughout.</summary>
[Collection("ConsoleState")]
public class MainFrameMouseTests
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

    private static void Mouse(TuiDriver driver, int button, int col, int row, bool release = false)
        => driver.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(button, col, row, release));

    private static (HitRegion Region, int Row) FindRegion(TuiDriver driver, MouseTargetKind kind, string? tag = null, int col = 2)
    {
        var map = driver.FrameHitMap!;
        for (int r = 1; r <= 60; r++)
            if (map.TryHit(r, col, out var hit, out _, out _) && hit.Kind == kind
                && (tag is null || hit.Tag == tag))
                return (hit, r);
        throw new Xunit.Sdk.XunitException($"no {kind} region{(tag is null ? "" : $" tagged {tag}")} in the composed map");
    }

    [Fact]
    public void ActivityStrip_RegistersAgentLaneRegions_WithLaneTags()
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        driver.SetSubAgentActivity(new[] { ("CodeAgent", "building", "blue"), ("WebAgent", "searching", "green") }, 0);
        Compose(driver);
        var (code, _) = FindRegion(driver, MouseTargetKind.AgentLane, "CodeAgent");
        var (web, _) = FindRegion(driver, MouseTargetKind.AgentLane, "WebAgent");
        Assert.Equal("CodeAgent", code.Tag);
        Assert.Equal("WebAgent", web.Tag);
    }

    [Fact]
    public void LaneClick_SelectsLane_AndRequestsDashboardOpen()
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        driver.SetSubAgentActivity(new[] { ("CodeAgent", "building", "blue"), ("WebAgent", "searching", "green") }, 0);
        Compose(driver);
        var (_, row) = FindRegion(driver, MouseTargetKind.AgentLane, "WebAgent");
        Mouse(driver, 0, 3, row);
        Mouse(driver, 0, 3, row, release: true);
        Assert.Equal("WebAgent", driver.TakePendingLaneOpen());
        Assert.Null(driver.TakePendingLaneOpen());   // consumed
        // The lane is pre-selected on the Agent View, so the opened dashboard lands on it.
        var view = (AgentView)typeof(TuiDriver).GetField("_agentView", PrivateInstance)!.GetValue(driver)!;
        view.SetRows(new[] { ("CodeAgent", "building", "blue"), ("WebAgent", "searching", "green") }, DateTime.UtcNow);
        Assert.Equal("WebAgent", view.SelectedAgent(DateTime.UtcNow));
    }

    [Fact]
    public void TranscriptEntry_ExpandableClick_TogglesExpansion()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        long tick = 1000;
        driver.MouseClock = () => tick;   // separate the two toggles beyond the double window
        driver.CommitLine("plain line one");
        driver.CommitCollapsed("· CodeAgent finished (ctrl+e)", "CodeAgent", "full transcript body\nwith lines");
        driver.CommitLine("plain line two");
        Compose(driver);
        var (hit, row) = FindRegion(driver, MouseTargetKind.TranscriptEntry);
        // Payload indexes _transcript; that entry must be the expandable one.
        var transcript = (System.Collections.IList)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!;
        var entry = transcript[hit.Payload]!;
        var expandedField = entry.GetType().GetField("Expanded")!;
        Assert.False((bool)expandedField.GetValue(entry)!);
        Mouse(driver, 0, 4, row);
        Mouse(driver, 0, 4, row, release: true);
        Assert.True((bool)expandedField.GetValue(entry)!);
        // Click again OUTSIDE the double window (map was rebuilt by the repaint) -> collapse.
        tick += MouseClickTracker.DoubleClickWindowMs + 1;
        var (hit2, row2) = FindRegion(driver, MouseTargetKind.TranscriptEntry);
        Assert.Equal(hit.Payload, hit2.Payload);
        Mouse(driver, 0, 4, row2);
        Mouse(driver, 0, 4, row2, release: true);
        Assert.False((bool)expandedField.GetValue(entry)!);
    }

    [Fact]
    public void PlainTranscriptRows_RegisterNoRegions()
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < 5; i++) driver.CommitLine($"plain {i}");
        Compose(driver);
        var map = driver.FrameHitMap!;
        for (int r = 1; r <= 24; r++)
            if (map.TryHit(r, 2, out var hit, out _, out _))
                Assert.NotEqual(MouseTargetKind.TranscriptEntry, hit.Kind);
    }

    [Fact]
    public void MidTurnRouting_HonorsLanesAndTranscriptToggles_DropsCompose()
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        driver.CommitCollapsed("· CodeAgent finished (ctrl+e)", "CodeAgent", "body");
        driver.SetSubAgentActivity(new[] { ("WebAgent", "searching", "green") }, 0);
        Compose(driver);

        // Transcript click MID-TURN: honored (the Ctrl+E alias already works during streaming).
        var (hit, row) = FindRegion(driver, MouseTargetKind.TranscriptEntry);
        var transcript = (System.Collections.IList)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!;
        var entry = transcript[hit.Payload]!;
        var expandedField = entry.GetType().GetField("Expanded")!;
        Assert.Null(driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 4, row, false)));
        Assert.Null(driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 4, row, true)));
        Assert.True((bool)expandedField.GetValue(entry)!);

        // Lane click mid-turn: honored - returns the lane for the caller to open.
        var (_, laneRow) = FindRegion(driver, MouseTargetKind.AgentLane, "WebAgent");
        Assert.Null(driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 3, laneRow, false)));
        Assert.Equal("WebAgent", driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 3, laneRow, true)));
    }

    [Fact]
    public void TranscriptDoubleClick_SecondClickSuppressed_NoInstantReclose()
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        long tick = 1000;
        driver.MouseClock = () => tick;
        driver.CommitCollapsed("· CodeAgent finished (ctrl+e)", "CodeAgent", "body");
        Compose(driver);
        var (hit, row) = FindRegion(driver, MouseTargetKind.TranscriptEntry);
        var transcript = (System.Collections.IList)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!;
        var entry = transcript[hit.Payload]!;
        var expandedField = entry.GetType().GetField("Expanded")!;
        Mouse(driver, 0, 4, row);
        Mouse(driver, 0, 4, row, release: true);
        Assert.True((bool)expandedField.GetValue(entry)!);
        // Second click of a rapid double: suppressed - the card must stay OPEN.
        tick += 200;
        var (hit2, row2) = FindRegion(driver, MouseTargetKind.TranscriptEntry);
        Assert.Equal(hit.Payload, hit2.Payload);
        Mouse(driver, 0, 4, row2);
        Mouse(driver, 0, 4, row2, release: true);
        Assert.True((bool)expandedField.GetValue(entry)!);
        // A slow third click (outside the window) toggles it closed as a normal single.
        tick += MouseClickTracker.DoubleClickWindowMs + 1;
        var (hit3, row3) = FindRegion(driver, MouseTargetKind.TranscriptEntry);
        Mouse(driver, 0, 4, row3);
        Mouse(driver, 0, 4, row3, release: true);
        Assert.False((bool)expandedField.GetValue(entry)!);
    }

    [Fact]
    public void LaneDoubleClick_WithDashboardOpen_RequestsActivate()
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        long tick = 1000;
        driver.MouseClock = () => tick;
        var view = (AgentView)typeof(TuiDriver).GetField("_agentView", PrivateInstance)!.GetValue(driver)!;
        view.Open();
        view.SetRows(new[] { ("CodeAgent", "building", "blue"), ("WebAgent", "searching", "green") }, DateTime.UtcNow);
        typeof(TuiDriver).GetField("_agentViewActive", PrivateInstance)!.SetValue(driver, true);
        driver.SetSubAgentActivity(new[] { ("CodeAgent", "building", "blue"), ("WebAgent", "searching", "green") }, 0);
        Compose(driver);
        var (_, laneRow) = FindRegion(driver, MouseTargetKind.AgentLane, "WebAgent");
        // Single click: selects only.
        Mouse(driver, 0, 3, laneRow);
        Mouse(driver, 0, 3, laneRow, release: true);
        Assert.False(driver.TakePendingLaneActivate());
        Assert.Equal("WebAgent", view.SelectedAgent(DateTime.UtcNow));
        // Double click: requests the Enter/foreground action.
        tick += 200;
        var (_, laneRow2) = FindRegion(driver, MouseTargetKind.AgentLane, "WebAgent");
        Mouse(driver, 0, 3, laneRow2);
        Mouse(driver, 0, 3, laneRow2, release: true);
        Assert.True(driver.TakePendingLaneActivate());
        Assert.False(driver.TakePendingLaneActivate());   // consumed
    }

    [Fact]
    public void MidTurnScrollbar_StillDragsThumb()
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < 200; i++) driver.CommitLine($"history {i}");
        Compose(driver);
        var sb = driver.ScrollBar;
        Assert.True(sb.Visible);
        int scrollBefore = (int)typeof(TuiDriver).GetField("_frameScroll", PrivateInstance)!.GetValue(driver)!;
        Assert.Equal(0, scrollBefore);
        driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 90, sb.Top + 1, false));
        driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(32, 90, 1, false));
        int scrollAfter = (int)typeof(TuiDriver).GetField("_frameScroll", PrivateInstance)!.GetValue(driver)!;
        Assert.True(scrollAfter > 0, "mid-turn thumb drag must move the viewport");
        driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 90, 1, true));
    }

    [Fact]
    public void AgentView_Select_IsClickAliasForArrows()
    {
        var view = new AgentView();
        view.Open();
        view.SetRows(new[] { ("A", "s1", "blue"), ("B", "s2", "green"), ("C", "s3", "red") }, DateTime.UtcNow);
        Assert.Equal("A", view.SelectedAgent(DateTime.UtcNow));
        view.Select("C");
        Assert.Equal("C", view.SelectedAgent(DateTime.UtcNow));
        // Unknown name degrades to the first-visible fallback (same as before any selection).
        view.Select("nope");
        Assert.Equal("A", view.SelectedAgent(DateTime.UtcNow));
    }

    [Fact]
    public void AgentView_RenderDashboard_ReportsLaneRows()
    {
        var view = new AgentView();
        view.Open();
        view.SetRows(new[] { ("A", "s1", "blue"), ("B", "s2", "green") }, DateTime.UtcNow);
        var laneRows = new List<(int Row, string Agent)>();
        var rows = view.RenderDashboard(90, DateTime.UtcNow, 0, null, laneRows);
        Assert.Equal(2, laneRows.Count);
        Assert.Equal(new[] { "A", "B" }, laneRows.Select(l => l.Agent).ToArray());
        Assert.All(laneRows, lr => Assert.InRange(lr.Row, 0, rows.Count - 1));
    }
}
