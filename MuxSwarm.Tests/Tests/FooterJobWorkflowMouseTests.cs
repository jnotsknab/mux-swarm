using System.Reflection;
using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Batch 7 mouse surfaces: footer badges (model/effort), /background job rows, and
/// /workflows run+task rows - each click a pure alias of its documented keyboard/command path.</summary>
[Collection("ConsoleState")]
public class FooterJobWorkflowMouseTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 120;
        public int Height { get; set; } = 30;
        public StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Compose(TuiDriver driver)
        => typeof(TuiDriver).GetMethod("ComposeFrameRows", PrivateInstance | BindingFlags.Public)!.Invoke(driver, null);

    private static void Mouse(TuiDriver driver, int button, int col, int row, bool release = false)
        => driver.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(button, col, row, release));

    // ---- footer badges ----

    [Fact]
    public void Footer_BadgeOverload_ReportsModelAndEffortExtents_OnComposedString()
    {
        var badges = new List<(int Col, int Width, string Id)>();
        string line = TuiComponents.Footer(1000, 100000, false, false, false, false, "high",
            false, 0, 0, 0, null, false, null, null, 0, "prov/some-model", 200, -1, null, badges);
        Assert.Contains(badges, b => b.Id == TuiComponents.BadgeModel);
        Assert.Contains(badges, b => b.Id == TuiComponents.BadgeEffort);
        // Extents are measured on the same visible string: the model text must sit at the
        // reported column (strip markup to plain and check).
        var model = badges.First(b => b.Id == TuiComponents.BadgeModel);
        string plain = TuiMarkup.Plain(line);
        Assert.Equal("some-model", plain.Substring(model.Col, model.Width));
    }

    [Fact]
    public void Footer_NoModelNoEffort_ReportsNoBadges()
    {
        var badges = new List<(int Col, int Width, string Id)>();
        TuiComponents.Footer(0, 0, false, false, false, false, null,
            false, 0, 0, 0, null, false, null, null, 0, null, 200, -1, null, badges);
        Assert.Empty(badges);
    }

    private static TuiDriver FrameDriverAtPrompt(Terminal term, string model = "prov/m1", string? effort = "high")
    {
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        driver.SetModel(model);
        driver.SetEffort(effort);
        typeof(TuiDriver).GetField("_inInput", PrivateInstance)!.SetValue(driver, true);
        Compose(driver);
        return driver;
    }

    private static (HitRegion Region, int Row, int Col) FindBadge(TuiDriver driver, string id)
    {
        var map = driver.FrameHitMap!;
        for (int r = 1; r <= 40; r++)
            for (int c = 1; c <= 130; c++)
                if (map.TryHit(r, c, out var hit, out _, out _)
                    && hit.Kind == MouseTargetKind.FooterBadge && hit.Tag == id)
                    return (hit, r, c);
        throw new Xunit.Sdk.XunitException($"no FooterBadge region tagged {id}");
    }

    [Fact]
    public void ModelBadgeClick_QueuesSetmodelCommand()
    {
        var driver = FrameDriverAtPrompt(new Terminal());
        var (_, row, col) = FindBadge(driver, TuiComponents.BadgeModel);
        Mouse(driver, 0, col, row);
        Mouse(driver, 0, col, row, release: true);
        Assert.Equal("/setmodel", driver.TakePendingBadgeCommand());
        Assert.Null(driver.TakePendingBadgeCommand());   // consumed
    }

    [Fact]
    public void EffortBadgeClick_InvokesModeCycle_LikeShiftTab()
    {
        var driver = FrameDriverAtPrompt(new Terminal());
        int cycles = 0;
        driver.OnModeCycle = () => { cycles++; return "max"; };
        Compose(driver);   // re-publish with the cycle hint wired
        var (_, row, col) = FindBadge(driver, TuiComponents.BadgeEffort);
        Mouse(driver, 0, col, row);
        Mouse(driver, 0, col, row, release: true);
        Assert.Equal(1, cycles);
        Assert.Null(driver.TakePendingBadgeCommand());   // effort never queues a command
    }

    [Fact]
    public void BadgeClicks_MidTurn_AreDropped()
    {
        var driver = FrameDriverAtPrompt(new Terminal());
        int cycles = 0;
        driver.OnModeCycle = () => { cycles++; return "max"; };
        Compose(driver);
        var (_, mRow, mCol) = FindBadge(driver, TuiComponents.BadgeModel);
        driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, mCol, mRow, false));
        driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, mCol, mRow, true));
        Assert.Null(driver.TakePendingBadgeCommand());
        var (_, eRow, eCol) = FindBadge(driver, TuiComponents.BadgeEffort);
        driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, eCol, eRow, false));
        driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, eCol, eRow, true));
        Assert.Equal(0, cycles);
    }

    // ---- job rows ----

    private static JobView.JobRow Job(string id, bool running = false)
        => new(id, "CodeAgent", running ? "running" : "done", "activity", "goal text", 12, running, "blue");

    [Fact]
    public void JobView_RenderOverload_ReportsRowIds_AndSelectIsClickAlias()
    {
        var view = new JobView();
        view.Open();
        view.SetRows(new[] { Job("job1"), Job("job2", running: true), Job("job3") });
        var jobRows = new List<(int Row, string Id)>();
        var rows = view.RenderDashboard(90, 0, jobRows);
        Assert.Equal(3, jobRows.Count);
        Assert.Equal(new[] { "job1", "job2", "job3" }, jobRows.Select(j => j.Id).ToArray());
        Assert.All(jobRows, jr => Assert.InRange(jr.Row, 0, rows.Count - 1));
        view.Select("job3");
        Assert.Equal("job3", view.SelectedId());
        view.Select("nope");   // unknown id degrades to the first-row fallback
        Assert.Equal("job1", view.SelectedId());
    }

    [Fact]
    public void JobRowClick_SelectsJob_DoubleRequestsActivate()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        long tick = 1000;
        driver.MouseClock = () => tick;
        var view = (JobView)typeof(TuiDriver).GetField("_jobView", PrivateInstance)!.GetValue(driver)!;
        view.Open();
        view.SetRows(new[] { Job("job1"), Job("job2") });
        typeof(TuiDriver).GetField("_jobViewActive", PrivateInstance)!.SetValue(driver, true);
        Compose(driver);
        var map = driver.FrameHitMap!;
        int row = -1;
        for (int r = 1; r <= 30 && row < 0; r++)
            if (map.TryHit(r, 5, out var hit, out _, out _) && hit is { Kind: MouseTargetKind.JobRow, Tag: "job2" })
                row = r;
        Assert.True(row > 0, "job2 row must register a JobRow region");
        // Single click: selects only.
        Mouse(driver, 0, 5, row);
        Mouse(driver, 0, 5, row, release: true);
        Assert.Equal("job2", view.SelectedId());
        Assert.False(driver.TakePendingJobActivate());
        // Double click: requests the o/Enter reopen.
        tick += 200;
        Compose(driver);
        var map2 = driver.FrameHitMap!;
        int row2 = -1;
        for (int r = 1; r <= 30 && row2 < 0; r++)
            if (map2.TryHit(r, 5, out var hit, out _, out _) && hit is { Kind: MouseTargetKind.JobRow, Tag: "job2" })
                row2 = r;
        Mouse(driver, 0, 5, row2);
        Mouse(driver, 0, 5, row2, release: true);
        Assert.True(driver.TakePendingJobActivate());
        Assert.False(driver.TakePendingJobActivate());   // consumed
    }

    // ---- workflow rows ----

    [Fact]
    public void WorkflowView_RenderOverload_ReportsRunAndTaskRows()
    {
        var view = new WorkflowView();
        view.Open();
        var run = new MuxSwarm.State.WorkflowRun
        {
            Id = "wf1",
            Name = "release",
            Mode = "dynamic",
            State = MuxSwarm.State.WorkflowRunState.Running,
        };
        run.Manifest.Sections.Add(new MuxSwarm.State.WorkflowSection
        {
            Name = "build",
            Tasks =
            {
                new MuxSwarm.State.WorkflowTask { Label = "compile", Agent = "CodeAgent", Status = "done" },
                new MuxSwarm.State.WorkflowTask { Label = "test", Agent = "CodeAgent", Status = "running" },
            },
        });
        view.SetRuns(new[] { run });
        var runRows = new List<(int Row, int Ordinal)>();
        var taskRows = new List<(int Row, int Task)>();
        var rows = view.RenderDashboard(120, 30, runRows, taskRows);
        Assert.Single(runRows);
        Assert.Equal(1, runRows[0].Ordinal);
        Assert.Equal(2, taskRows.Count);
        Assert.Equal(new[] { 0, 1 }, taskRows.Select(t => t.Task).ToArray());
        Assert.All(runRows.Select(r => r.Row).Concat(taskRows.Select(t => t.Row)),
            r => Assert.InRange(r, 0, rows.Count - 1));
        // Expanded task: task rows are suppressed (the output pane owns those rows).
        view.SelectTaskAt(1);
        view.ToggleTaskExpand();
        view.RenderDashboard(120, 30, runRows, taskRows);
        Assert.Empty(taskRows);
        // SelectTaskAt collapses the expansion and moves the selection (click alias).
        view.SelectTaskAt(0);
        Assert.False(view.IsTaskExpanded);
    }
}
