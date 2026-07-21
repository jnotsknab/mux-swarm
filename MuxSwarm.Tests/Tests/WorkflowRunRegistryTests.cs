using System.Text.Json;
using MuxSwarm.State;
using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers the v0.12.4 workflow-run plane: the registry's dynamic-journal tailer (manifest
/// pickup, task status folding, terminal run lines, driver-exit fallback) and the /workflows
/// viewer model (panel rendering, selection, bounded rows). Uses temp run dirs; no processes.
/// </summary>
[Collection("WorkflowRegistrySerial")]
public class WorkflowRunRegistryTests : IDisposable
{
    private readonly string _dir;

    public WorkflowRunRegistryTests()
    {
        WorkflowRunRegistry.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "mux-wfr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        WorkflowRunRegistry.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private WorkflowRun NewDynamicRun(string name = "wf")
    {
        var run = new WorkflowRun { Id = name, Name = name, Mode = "dynamic", RunDir = _dir };
        return WorkflowRunRegistry.Register(run);
    }

    private void WriteManifest(params (string Section, (string Id, string Agent, string Label)[] Tasks)[] sections)
    {
        var m = new WorkflowRunManifest
        {
            Id = "wf", Name = "wf", Mode = "dynamic",
            Sections = sections.Select(s => new WorkflowSection
            {
                Name = s.Section,
                Tasks = s.Tasks.Select(t => new WorkflowTask { Id = t.Id, Agent = t.Agent, Label = t.Label }).ToList(),
            }).ToList(),
        };
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), JsonSerializer.Serialize(m));
    }

    private void AppendStatus(string line)
        => File.AppendAllText(Path.Combine(_dir, "status.ndjson"), line + "\n");

    private static void Poll()
    {
        WorkflowRunRegistry.ForceNextPollForTests();
        WorkflowRunRegistry.PollDynamic();
    }

    [Fact]
    public void Tailer_PicksUpManifest_AndFoldsTaskStatuses()
    {
        var run = NewDynamicRun();
        WriteManifest(("Research", new[] { ("t1", "WebAgent", "look things up"), ("t2", "CodeAgent", "write code") }));
        AppendStatus("{\"task\":\"t1\",\"status\":\"running\"}");
        Poll();

        Assert.Single(run.Manifest.Sections);
        Assert.Equal("running", run.Manifest.Sections[0].Tasks[0].Status);
        Assert.Equal("pending", run.Manifest.Sections[0].Tasks[1].Status);

        AppendStatus("{\"task\":\"t1\",\"status\":\"done\",\"detail\":\"found it\"}");
        AppendStatus("{\"task\":\"t2\",\"status\":\"failed\",\"detail\":\"boom\"}");
        Poll();

        Assert.Equal("done", run.Manifest.Sections[0].Tasks[0].Status);
        Assert.Equal("found it", run.Manifest.Sections[0].Tasks[0].Detail);
        Assert.Equal("failed", run.Manifest.Sections[0].Tasks[1].Status);
        Assert.Equal(WorkflowRunState.Running, run.State);   // no terminal line yet
    }

    [Fact]
    public void Tailer_TerminalRunLine_FinishesTheRun()
    {
        var run = NewDynamicRun();
        WriteManifest(("S", new[] { ("t1", "A", "x") }));
        AppendStatus("{\"run\":\"done\"}");
        Poll();
        Assert.Equal(WorkflowRunState.Done, run.State);
        Assert.NotNull(run.Finished);

        var run2 = NewDynamicRun("wf2");
        AppendStatus("{\"run\":\"failed\",\"error\":\"driver blew up\"}");
        Poll();
        Assert.Equal(WorkflowRunState.Failed, run2.State);
        Assert.Equal("driver blew up", run2.Error);
    }

    [Fact]
    public void Tailer_MalformedLines_AreIgnored()
    {
        var run = NewDynamicRun();
        WriteManifest(("S", new[] { ("t1", "A", "x") }));
        AppendStatus("not json at all");
        AppendStatus("{\"task\":\"nope\",\"status\":\"running\"}");   // unknown task id
        Poll();
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Equal("pending", run.Manifest.Sections[0].Tasks[0].Status);
    }

    [Fact]
    public void Cancel_FlagsRun()
    {
        var run = NewDynamicRun();
        Assert.True(WorkflowRunRegistry.Cancel(run.Id));
        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        Assert.False(WorkflowRunRegistry.Cancel(run.Id));   // already terminal
    }

    [Fact]
    public void Viewer_MasterDetail_PhasesAndSelectedPhaseTasks()
    {
        var run = NewDynamicRun("build-site");
        WriteManifest(
            ("Research", new[] { ("t1", "WebAgent", "gather docs") }),
            ("Implement", new[] { ("t2", "CodeAgent", "write pages"), ("t3", "CodeAgent", "style pass") }));
        AppendStatus("{\"task\":\"t1\",\"status\":\"done\",\"secs\":95,\"tools\":14,\"tokens\":41300}");
        AppendStatus("{\"task\":\"t2\",\"status\":\"running\"}");
        Poll();

        var view = new WorkflowView();
        view.SetRuns(WorkflowRunRegistry.Snapshot());
        var rows = view.RenderDashboard(140);
        string plain = string.Join("\n", rows.Select(TuiMarkup.Plain));

        // Left panel: both phases with fraction counters.
        Assert.Contains("Phases", plain);
        Assert.Contains("Research", plain);
        Assert.Contains("Implement", plain);
        Assert.Contains("1/1", plain);
        Assert.Contains("0/2", plain);
        // Right panel: phase 0 (Research) is selected by default - its task + telemetry show,
        // the OTHER phase's task labels do not (master/detail, not a dense full dump).
        Assert.Contains("WebAgent", plain);
        Assert.Contains("gather docs", plain);
        Assert.Contains("14 tools", plain);
        Assert.Contains("1m35s", plain);
        Assert.Contains("41.3k tok", plain);
        Assert.DoesNotContain("write pages", plain);

        // Phase navigation swaps the detail panel.
        view.MovePhase(+1);
        plain = string.Join("\n", view.RenderDashboard(140).Select(TuiMarkup.Plain));
        Assert.Contains("write pages", plain);
        Assert.DoesNotContain("gather docs", plain);
    }

    [Fact]
    public void Viewer_TaskSelection_WindowFollowsAndExpands()
    {
        var run = NewDynamicRun("many-tasks");
        var tasks = Enumerable.Range(1, 20).Select(i => ($"t{i}", "WebAgent", $"task number {i}")).ToArray();
        WriteManifest(("Big", tasks));
        AppendStatus("{\"task\":\"t1\",\"status\":\"done\",\"detail\":\"a fairly long detail body that should only show when the row is expanded with enter\"}");
        Poll();

        var view = new WorkflowView();
        view.SetRuns(WorkflowRunRegistry.Snapshot());
        string plain = string.Join("\n", view.RenderDashboard(140).Select(TuiMarkup.Plain));
        // 20 tasks, window of 12: initial window shows t1..t12 and a "more" hint below.
        Assert.Contains("task number 1", plain);
        Assert.Contains("task number 12", plain);
        Assert.DoesNotContain("task number 13", plain);
        Assert.Contains("8 more", plain);

        // Scroll selection past the window: window follows.
        for (int i = 0; i < 15; i++) view.MoveTask(+1);
        plain = string.Join("\n", view.RenderDashboard(140).Select(TuiMarkup.Plain));
        Assert.Contains("task number 16", plain);
        Assert.DoesNotContain("task number 1 ", plain.Replace("task number 1\n", ""));

        // Expansion: back to t1, expanded detail body renders; collapsed it is absent (done task).
        for (int i = 0; i < 15; i++) view.MoveTask(-1);
        plain = string.Join("\n", view.RenderDashboard(140).Select(TuiMarkup.Plain));
        Assert.DoesNotContain("only show when the row is expanded", plain);
        view.ToggleTaskExpand();
        plain = string.Join("\n", view.RenderDashboard(140).Select(TuiMarkup.Plain));
        Assert.Contains("only show when the row is expanded", plain);
    }

    [Fact]
    public void Viewer_MultipleRuns_ListedAndSwitchable()
    {
        var a = NewDynamicRun("first"); a.State = WorkflowRunState.Done;
        var b = NewDynamicRun("second");
        WriteManifest(("S", new[] { ("t1", "A", "x") }));
        Poll();

        var view = new WorkflowView();
        view.SetRuns(WorkflowRunRegistry.Snapshot());
        string plain = string.Join("\n", view.RenderDashboard(140).Select(TuiMarkup.Plain));
        // Both runs listed; newest running selected.
        Assert.Contains("first", plain);
        Assert.Contains("second", plain);
        Assert.Equal(b.Id, view.SelectedId());
        // Ordinal jump + Tab-style cycle both re-target.
        view.SelectRunAt(1);
        Assert.Equal(a.Id, view.SelectedId());
        view.Move(+1);
        Assert.Equal(b.Id, view.SelectedId());
    }

    [Fact]
    public void Viewer_FailedTaskDetail_ShownInline()
    {
        var run = NewDynamicRun("wf-fail");
        WriteManifest(("S", new[] { ("t1", "WebAgent", "look it up") }));
        AppendStatus("{\"task\":\"t1\",\"status\":\"failed\",\"detail\":\"No endpoint provided\"}");
        Poll();
        var view = new WorkflowView();
        view.SetRuns(WorkflowRunRegistry.Snapshot());
        string plain = string.Join("\n", view.RenderDashboard(140).Select(TuiMarkup.Plain));
        Assert.Contains("No endpoint provided", plain);
    }

    [Fact]
    public void Validator_RejectsContractViolations_AcceptsSkeletonShape()
    {
        Assert.NotNull(DynamicWorkflow.ValidateScript(""));
        Assert.NotNull(DynamicWorkflow.ValidateScript("print('hi')"));
        // Missing cfg wiring is fatal (the live 'No endpoint provided' failure class).
        var noCfg = "import muxswarm, os\nRUN=os.environ['MUX_RUN_DIR']\n# manifest.json status.ndjson";
        Assert.Contains("No endpoint provided", DynamicWorkflow.ValidateScript(noCfg));
        // .text usage is fatal.
        var withText = "import muxswarm, os\nRUN=os.environ['MUX_RUN_DIR']\nos.environ.get('MUX_CFG')\n# manifest.json status.ndjson\nres.text";
        Assert.Contains(".text", DynamicWorkflow.ValidateScript(withText));
        // A script exercising the whole contract passes.
        var good = "import muxswarm, os\nRUN=os.environ['MUX_RUN_DIR']\ncfg=os.environ.get('MUX_CFG')\n" +
                   "# writes manifest.json and appends status.ndjson\nout = res.final_summary or res.streamed_text";
        Assert.Null(DynamicWorkflow.ValidateScript(good));
    }

    [Fact]
    public void Viewer_SelectionMoves_AndPrefersRunningRun()
    {
        var done = NewDynamicRun("old");
        done.State = WorkflowRunState.Done;
        var live = NewDynamicRun("live");

        var view = new WorkflowView();
        view.SetRuns(WorkflowRunRegistry.Snapshot());
        Assert.Equal(live.Id, view.SelectedId());   // newest RUNNING preferred

        view.Move(-1);
        Assert.Equal(done.Id, view.SelectedId());
        view.Move(-1);                              // clamped
        Assert.Equal(done.Id, view.SelectedId());
        view.Move(+1);
        Assert.Equal(live.Id, view.SelectedId());
    }

    [Fact]
    public void Viewer_EmptyRegistry_RendersHint()
    {
        var view = new WorkflowView();
        view.SetRuns(WorkflowRunRegistry.Snapshot());
        string plain = string.Join("\n", view.RenderDashboard(80).Select(TuiMarkup.Plain));
        Assert.Contains("no workflow runs", plain);
    }
}

[CollectionDefinition("WorkflowRegistrySerial", DisableParallelization = true)]
public class WorkflowRegistrySerialCollection { }
