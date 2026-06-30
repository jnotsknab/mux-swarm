using System;
using System.IO;
using System.Linq;
using MuxSwarm.State;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// M-A taskboard resilience + artifacts: stale-task reaper requeue/heartbeat, bounded-retry circuit
/// breaker, artifact persistence + surfacing, and additive-field back-compat with old task JSON.
/// </summary>
public class TaskBoardResilienceTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mux-taskboard-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- Stale reaper -------------------------------------------------------------------

    [Fact]
    public void ReapStale_RequeuesDeadOwnerAndBumpsAttempts()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var t = board.Create("do x", "");
        Assert.True(board.TryClaim(t.Id, "CodeAgent", out _));
        Assert.Equal(1, board.Get(t.Id)!.Attempts);   // claim bumped attempts

        // TTL elapsed: a tiny ttl with now in the future reaps it.
        var reaped = board.ReapStale(TimeSpan.FromSeconds(1), maxAttempts: 3, now: DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Contains(t.Id, reaped);

        var after = board.Get(t.Id)!;
        Assert.Null(after.Owner);                       // requeued
        Assert.Equal(TeamTaskStatus.Pending, after.Status);
    }

    [Fact]
    public void ReapStale_HeartbeatKeepsTaskAlive()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var t = board.Create("do x", "");
        Assert.True(board.TryClaim(t.Id, "CodeAgent", out _));

        // Heartbeat now; a reap whose TTL window starts after that heartbeat must NOT reap.
        Assert.True(board.Heartbeat(t.Id, "CodeAgent"));
        var reaped = board.ReapStale(TimeSpan.FromMinutes(15), maxAttempts: 3, now: DateTimeOffset.UtcNow);
        Assert.DoesNotContain(t.Id, reaped);
        Assert.Equal(TeamTaskStatus.InProgress, board.Get(t.Id)!.Status);
    }

    [Fact]
    public void ReapStale_MaxAttempts_TripsCircuitBreakerToFailed()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var t = board.Create("flaky", "");
        var future = DateTimeOffset.UtcNow;

        // Burn the retry budget: claim -> reap -> claim -> reap ... until Attempts hits the cap.
        for (int i = 0; i < 3; i++)
        {
            Assert.True(board.TryClaim(t.Id, "CodeAgent", out _));
            future = future.AddMinutes(20);
            board.ReapStale(TimeSpan.FromMinutes(15), maxAttempts: 3, now: future);
        }

        var after = board.Get(t.Id)!;
        Assert.Equal(TeamTaskStatus.Failed, after.Status);   // circuit breaker, not infinite respawn
        Assert.True(after.Attempts >= 3);
        Assert.Null(after.Owner);
    }

    [Fact]
    public void Heartbeat_WrongOwner_IsRejected()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var t = board.Create("x", "");
        board.TryClaim(t.Id, "CodeAgent", out _);
        Assert.False(board.Heartbeat(t.Id, "WebAgent"));   // not the owner
    }

    // ---- Artifacts ----------------------------------------------------------------------

    [Fact]
    public void Artifacts_SetAtCreate_PersistAndReload()
    {
        var root = TempRoot();
        var board = TaskBoard.Open("t", root);
        var t = board.Create("x", "", artifacts: new[] { "a.cs", "b.md", "a.cs" });
        Assert.Equal(2, t.Artifacts.Count);   // dedup

        var reloaded = TaskBoard.Open("t", root).Get(t.Id)!;
        Assert.Contains("a.cs", reloaded.Artifacts);
        Assert.Contains("b.md", reloaded.Artifacts);
    }

    [Fact]
    public void SetArtifacts_AddRemoveSet()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var t = board.Create("x", "");
        Assert.True(board.SetArtifacts(t.Id, add: new[] { "one", "two" }, remove: null, set: null, out _));
        Assert.True(board.SetArtifacts(t.Id, add: null, remove: new[] { "one" }, set: null, out _));
        var after = board.Get(t.Id)!;
        Assert.Single(after.Artifacts);
        Assert.Equal("two", after.Artifacts[0]);

        board.SetArtifacts(t.Id, add: null, remove: null, set: new[] { "fresh" }, out _);
        Assert.Equal(new[] { "fresh" }, board.Get(t.Id)!.Artifacts.ToArray());
    }

    // ---- Back-compat: old JSON without the new fields loads --------------------------------

    [Fact]
    public void OldTaskJson_WithoutNewFields_LoadsWithDefaults()
    {
        var root = TempRoot();
        var tasksDir = Path.Combine(root, "tasks");
        Directory.CreateDirectory(tasksDir);
        // A pre-M-A task file: no attempts / lastHeartbeat / artifacts keys.
        File.WriteAllText(Path.Combine(tasksDir, "t1.json"),
            "{\"id\":\"t1\",\"subject\":\"legacy\",\"status\":\"Pending\"}");

        var board = TaskBoard.Open("t", root);
        var t = board.Get("t1")!;
        Assert.Equal(0, t.Attempts);
        Assert.Null(t.LastHeartbeat);
        Assert.NotNull(t.Artifacts);
        Assert.Empty(t.Artifacts);
    }
}
