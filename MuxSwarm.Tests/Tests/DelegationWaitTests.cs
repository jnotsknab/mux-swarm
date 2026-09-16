using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

// check_delegations wait semantics (v0.14.1): WaitForProgressAsync blocks until a watched job
// has NEW progress vs the lead's read cursor, returns early on terminal states, and the report
// renderer commits cursors so already-collected results are never re-dumped. Serialized: the
// DetachedRunner job registry is process-static.
[Collection("ConsoleState")]
public class DelegationWaitTests : IDisposable
{
    public DelegationWaitTests() => DetachedRunner.ResetForTests();
    public void Dispose() => DetachedRunner.ResetForTests();

    [Fact]
    public async Task NeverReadJob_ReportsImmediately_NoBlocking()
    {
        DetachedRunner.InjectJobForTests("CodeAgent", DetachedStatus.Running);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var changed = await DetachedRunner.WaitForProgressAsync(null, waitSeconds: 30, CancellationToken.None);
        sw.Stop();
        Assert.Single(changed);
        Assert.True(sw.ElapsedMilliseconds < 2000, "unread job must return immediately, not wait out the budget");
    }

    [Fact]
    public async Task ReadJob_NoChange_WaitsOutBudget_ThenEmpty()
    {
        var job = DetachedRunner.InjectJobForTests("CodeAgent", DetachedStatus.Running);
        // First read commits the cursor.
        DetachedRunner.RenderCheckReport(job.Id, changedIds: null, waited: false, waitedSeconds: 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var changed = await DetachedRunner.WaitForProgressAsync(job.Id, waitSeconds: 1, CancellationToken.None);
        sw.Stop();
        Assert.Empty(changed);
        Assert.True(sw.ElapsedMilliseconds >= 900, "no-change wait must block for the budget");
    }

    [Fact]
    public async Task TailChurn_DoesNotWake_ToolCallDelta_Does()
    {
        // v0.14.1 wake tightening: streamed prose (tail/activity text changes) must NOT wake a
        // waiter - sub-agents stream continuously and text-delta wakes turned every wait into
        // ~2s spam. Only a DISCRETE event (tool-call count increase / status transition) wakes.
        var job = DetachedRunner.InjectJobForTests("CodeAgent", DetachedStatus.Running);
        int toolCalls = 1;
        string tail = "first words";
        DetachedRunner.LiveDetailOverrideForTests = _ => (toolCalls, "working", tail);
        DetachedRunner.RenderCheckReport(job.Id, changedIds: null, waited: false, waitedSeconds: 0);

        // Churn ONLY the tail: wait must ride out its full budget and return empty.
        tail = "first words and a lot more streamed prose";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var changed = await DetachedRunner.WaitForProgressAsync(job.Id, waitSeconds: 1, CancellationToken.None);
        sw.Stop();
        Assert.Empty(changed);
        Assert.True(sw.ElapsedMilliseconds >= 900, "tail-only churn must not wake the waiter");

        // A tool call landing wakes promptly.
        _ = Task.Run(async () => { await Task.Delay(300); toolCalls = 2; });
        sw.Restart();
        changed = await DetachedRunner.WaitForProgressAsync(job.Id, waitSeconds: 10, CancellationToken.None);
        sw.Stop();
        Assert.Single(changed);
        Assert.True(sw.ElapsedMilliseconds < 5000, "tool-call delta must wake the waiter early");
    }

    [Fact]
    public async Task StatusTransition_WakesWaiter()
    {
        var job = DetachedRunner.InjectJobForTests("CodeAgent", DetachedStatus.Running);
        DetachedRunner.RenderCheckReport(job.Id, changedIds: null, waited: false, waitedSeconds: 0);
        // Flip to Done shortly after the wait begins (background thread).
        _ = Task.Run(async () => { await Task.Delay(300); job.Status = DetachedStatus.Done; job.Result = "finished!"; });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var changed = await DetachedRunner.WaitForProgressAsync(job.Id, waitSeconds: 10, CancellationToken.None);
        sw.Stop();
        Assert.Single(changed);
        Assert.True(sw.ElapsedMilliseconds < 5000, "terminal transition must wake the waiter early");
    }

    [Fact]
    public async Task AllJobsFinished_ReturnsWithoutWaiting()
    {
        var job = DetachedRunner.InjectJobForTests("CodeAgent", DetachedStatus.Done, result: "done");
        DetachedRunner.RenderCheckReport(job.Id, changedIds: null, waited: false, waitedSeconds: 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var changed = await DetachedRunner.WaitForProgressAsync(null, waitSeconds: 30, CancellationToken.None);
        sw.Stop();
        Assert.Empty(changed);   // nothing new AND nothing running
        Assert.True(sw.ElapsedMilliseconds < 2000, "no running jobs -> no reason to hold the wall");
    }

    [Fact]
    public void FinishedResult_ReportedOnce_ThenMarkedCollected()
    {
        var job = DetachedRunner.InjectJobForTests("CodeAgent", DetachedStatus.Done, result: "the deliverable text");
        string first = DetachedRunner.RenderCheckReport(job.Id, changedIds: null, waited: false, waitedSeconds: 0);
        Assert.Contains("the deliverable text", first);
        string second = DetachedRunner.RenderCheckReport(job.Id, changedIds: null, waited: false, waitedSeconds: 0);
        Assert.DoesNotContain("the deliverable text", second);
        Assert.Contains("already collected", second);
    }

    [Fact]
    public void WaitReport_DeltaOnly_ListsOnlyChangedJobs()
    {
        var a = DetachedRunner.InjectJobForTests("CodeAgent", DetachedStatus.Running);
        var b = DetachedRunner.InjectJobForTests("WebAgent", DetachedStatus.Running);
        // Read both (commits cursors), then report a wait where only b changed.
        DetachedRunner.RenderCheckReport(null, changedIds: null, waited: false, waitedSeconds: 0);
        string rpt = DetachedRunner.RenderCheckReport(null, new[] { b.Id }, waited: true, waitedSeconds: 15);
        Assert.Contains(b.Id, rpt);
        Assert.Contains("NEW progress on 1 job(s)", rpt);
        Assert.DoesNotContain($"- {a.Id} ", rpt);
    }

    [Fact]
    public void WaitTimeout_ReportSaysNoNewProgress()
    {
        var job = DetachedRunner.InjectJobForTests("CodeAgent", DetachedStatus.Running);
        DetachedRunner.RenderCheckReport(null, changedIds: null, waited: false, waitedSeconds: 0);
        string rpt = DetachedRunner.RenderCheckReport(null, changedIds: new List<string>(), waited: true, waitedSeconds: 20);
        Assert.Contains("No new progress within 20s", rpt);
    }
}
