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
    public void Viewer_RendersPanels_TasksAndSelection()
    {
        var run = NewDynamicRun("build-site");
        WriteManifest(
            ("Research", new[] { ("t1", "WebAgent", "gather docs") }),
            ("Implement", new[] { ("t2", "CodeAgent", "write pages"), ("t3", "CodeAgent", "style pass") }));
        AppendStatus("{\"task\":\"t1\",\"status\":\"done\",\"detail\":\"12 sources\"}");
        AppendStatus("{\"task\":\"t2\",\"status\":\"running\"}");
        Poll();

        var view = new WorkflowView();
        view.SetRuns(WorkflowRunRegistry.Snapshot());
        var rows = view.RenderDashboard(120);
        string plain = string.Join("\n", rows.Select(TuiMarkup.Plain));

        Assert.Contains("workflows", plain);
        Assert.Contains("build-site", plain);
        Assert.Contains("Research", plain);
        Assert.Contains("Implement", plain);
        Assert.Contains("WebAgent", plain);
        Assert.Contains("12 sources", plain);
        Assert.Contains("1/1", plain);       // Research done-count
        Assert.Contains("0/2", plain);       // Implement done-count
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
