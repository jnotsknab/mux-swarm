using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.State;
using MuxSwarm.Utils.Teams;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// M3 (g12.16): peer self-claiming + persistent member context. Covers the new TaskBoard
/// member-claim query (assigned vs open pickup, dependency gating, race-safety), the member
/// context-management policy/threshold decisions, and per-member state persistence + resume.
/// </summary>
public class TeamsM3PeerTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mux-teams-m3-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- Config: additive defaults ----

    [Fact]
    public void TeamConfig_PeerDefaults_ArePersistentAndAssigned()
    {
        var t = new MuxSwarm.Utils.TeamConfig();
        Assert.Equal("persistent", t.MemberContext);
        Assert.Equal("assigned", t.PickupPolicy);
    }

    [Fact]
    public void CompacterConfig_MemberThreshold_DefaultsToZero_FallsBackToDefault()
    {
        var c = new MuxSwarm.Utils.CompacterConfig();
        Assert.Equal(0, c.MemberAutoCompactTokenThreshold);
        // 0 -> the manager's bounded default, never "no limit".
        Assert.Equal(MemberContextManager.DefaultThresholdTokens,
            MemberContextManager.EffectiveThreshold(c.MemberAutoCompactTokenThreshold));
        Assert.Equal(12345, MemberContextManager.EffectiveThreshold(12345));
    }

    // ---- NextClaimableFor: assigned-only vs open pool ----

    [Fact]
    public void NextClaimableFor_AssignedOnly_OnlyReturnsOwnAssignedTask()
    {
        var board = TaskBoard.Open("t", TempRoot());
        board.Create("mine", "", null, assignee: "Alice");
        board.Create("hers", "", null, assignee: "Bob");
        board.Create("open", "", null, assignee: null);

        var pick = board.NextClaimableFor("Alice", openPool: false, DateTimeOffset.UtcNow);
        Assert.NotNull(pick);
        Assert.Equal("Alice", pick!.Assignee);

        // Assigned-only must NOT hand Alice the unassigned task.
        board.Reassign(pick.Id, "Bob", out _); // remove Alice's only assigned task
        var none = board.NextClaimableFor("Alice", openPool: false, DateTimeOffset.UtcNow);
        Assert.Null(none);
    }

    [Fact]
    public void NextClaimableFor_OpenPool_ReturnsUnassignedReadyTask()
    {
        var board = TaskBoard.Open("t", TempRoot());
        board.Create("open", "", null, assignee: null);

        Assert.Null(board.NextClaimableFor("Alice", openPool: false, DateTimeOffset.UtcNow));
        var pick = board.NextClaimableFor("Alice", openPool: true, DateTimeOffset.UtcNow);
        Assert.NotNull(pick);
        Assert.Null(pick!.Assignee);
    }

    [Fact]
    public void NextClaimableFor_RespectsDependencyGating()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var a = board.Create("first", "", null, assignee: "Alice");
        var b = board.Create("second", "", new[] { a.Id }, assignee: "Alice");

        // b is blocked by a -> not claimable yet.
        var pick1 = board.NextClaimableFor("Alice", openPool: false, DateTimeOffset.UtcNow);
        Assert.Equal(a.Id, pick1!.Id);

        board.TryClaim(a.Id, "Alice", out _);
        board.SetStatus(a.Id, TeamTaskStatus.Done, out _);

        // Now b auto-unblocks and becomes the next claimable.
        var pick2 = board.NextClaimableFor("Alice", openPool: false, DateTimeOffset.UtcNow);
        Assert.Equal(b.Id, pick2!.Id);
    }

    [Fact]
    public void NextClaimableFor_RespectsStartAt()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var now = DateTimeOffset.UtcNow;
        board.Create("later", "", null, assignee: "Alice", startAt: now.AddMinutes(5));
        Assert.Null(board.NextClaimableFor("Alice", openPool: false, now));
        Assert.NotNull(board.NextClaimableFor("Alice", openPool: false, now.AddMinutes(6)));
    }

    [Fact]
    public void OpenPool_ConcurrentClaim_NeverDoubleOwns()
    {
        // Two members race for one open task; file-locked TryClaim must let exactly one win.
        var board = TaskBoard.Open("t", TempRoot());
        var task = board.Create("the-one", "", null, assignee: null);

        int wins = 0;
        Parallel.For(0, 32, i =>
        {
            var who = Guid.NewGuid().ToString("N").Substring(0, 6);
            var pick = board.NextClaimableFor(who, openPool: true, DateTimeOffset.UtcNow);
            if (pick is not null && board.TryClaim(pick.Id, who, out _))
                Interlocked.Increment(ref wins);
        });

        Assert.Equal(1, wins);
        Assert.NotNull(board.Get(task.Id)!.Owner);
    }

    // ---- MemberContextManager: policy decisions ----

    [Fact]
    public void MemberContext_Normalize_DefaultsToPersistent()
    {
        Assert.Equal("persistent", MemberContextManager.NormalizeContext(null));
        Assert.Equal("persistent", MemberContextManager.NormalizeContext("bogus"));
        Assert.Equal("persistent", MemberContextManager.NormalizeContext("PERSISTENT"));
        Assert.Equal("fresh", MemberContextManager.NormalizeContext("Fresh"));
    }

    [Fact]
    public void ShouldCompact_OnlyOverThreshold()
    {
        Assert.False(MemberContextManager.ShouldCompact(estTokens: 100, thresholdTokens: 1000));
        Assert.False(MemberContextManager.ShouldCompact(estTokens: 1000, thresholdTokens: 1000));
        Assert.True(MemberContextManager.ShouldCompact(estTokens: 1001, thresholdTokens: 1000));
        // A non-positive threshold never trips (defensive; callers pass EffectiveThreshold).
        Assert.False(MemberContextManager.ShouldCompact(estTokens: 999999, thresholdTokens: 0));
    }

    [Fact]
    public void Manager_FreshMode_IsNotPersistent()
    {
        var fresh = new MemberContextManager("team", "fresh", 0, null, null);
        Assert.False(fresh.IsPersistent);
        var warm = new MemberContextManager("team", "persistent", 0, null, null);
        Assert.True(warm.IsPersistent);
    }

    [Fact]
    public async Task Manager_RunAsync_FreshPassesCleanSession_PersistentDoesNot()
    {
        var freshMgr = new MemberContextManager("teamFresh-" + Guid.NewGuid().ToString("N"), "fresh", 0, null, null);
        bool? seenClean = null;
        await freshMgr.RunAsync("Alice", clean => { seenClean = clean; return Task.FromResult("ok"); }, CancellationToken.None);
        Assert.True(seenClean);

        var warmMgr = new MemberContextManager("teamWarm-" + Guid.NewGuid().ToString("N"), "persistent", 0, null, null);
        seenClean = null;
        // No specialist registered for "Alice" -> MaybeCompact is a no-op; RunAsync still returns cleanly.
        await warmMgr.RunAsync("Alice", clean => { seenClean = clean; return Task.FromResult("ok"); }, CancellationToken.None);
        Assert.False(seenClean);
    }

    // ---- MemberState: persistence + resume ----

    [Fact]
    public void MemberState_Persist_AndReload()
    {
        var team = "persist-team-" + Guid.NewGuid().ToString("N");
        var st = new MemberState
        {
            Name = "CodeAgent", Status = "running", Context = "persistent",
            CompletedTasks = 3, Compactions = 1, SessionTokens = 1234,
        };
        st.Save(team);

        var back = MemberState.Load(team, "CodeAgent");
        Assert.NotNull(back);
        Assert.Equal("CodeAgent", back!.Name);
        Assert.Equal(3, back.CompletedTasks);
        Assert.Equal(1, back.Compactions);
        Assert.Equal(1234, back.SessionTokens);

        Assert.Single(MemberState.LoadAll(team));
    }

    [Fact]
    public void MemberState_Load_Missing_ReturnsNull()
    {
        Assert.Null(MemberState.Load("no-such-team-" + Guid.NewGuid().ToString("N"), "Ghost"));
    }

    // ---- Dangling-dependency gating (stale/typo'd dep id must NOT count as satisfied) ----

    [Fact]
    public void Create_WithUnknownDep_StartsBlocked_NotPending()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var t = board.Create("orphan", "", new[] { "t999" });   // t999 never existed
        Assert.Equal(TeamTaskStatus.Blocked, t.Status);
        // and it must not be claimable while the dep is unresolved
        Assert.False(board.IsClaimable(t.Id));
        Assert.Null(board.NextClaimableFor("Alice", openPool: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Create_WithStaleClearedDep_StaysBlocked()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var a = board.Create("first", "");
        board.Remove(a.Id, out _);                              // clear t1; counter keeps advancing
        var t = board.Create("second", "", new[] { a.Id });    // depends on the now-stale id
        Assert.Equal(TeamTaskStatus.Blocked, t.Status);
        Assert.False(board.IsClaimable(t.Id));
    }

    [Fact]
    public void UnknownDeps_ReportsOnlyMissingIds()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var a = board.Create("real", "");
        var unknown = board.UnknownDeps(new[] { a.Id, "t777", "  ", "t777" });
        Assert.Equal(new[] { "t777" }, unknown.ToArray());      // dedup + trim + only-missing
        Assert.Empty(board.UnknownDeps(new[] { a.Id }));
    }

    [Fact]
    public void Exists_TracksPresence()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var a = board.Create("x", "");
        Assert.True(board.Exists(a.Id));
        board.Remove(a.Id, out _);
        Assert.False(board.Exists(a.Id));
        Assert.False(board.Exists("t12345"));
    }

    // ---- Lead preamble (teamScope-gated coordination guide) ----

    [Fact]
    public void LeadPreamble_TaskboardTeam_MentionsKeyToolsAndMembers()
    {
        var scope = new TeamScope
        {
            DisplayName = "research-build",
            LeadDef = new MuxSwarm.Utils.Common.AgentDefinition("Orchestrator", "", "", false, tools => tools),
            Members = new List<string> { "WebAgent", "CodeAgent" },
            Coordination = "taskboard",
            Board = TaskBoard.Open("research-build", TempRoot()),
            Tools = new List<Microsoft.Extensions.AI.AITool>(),
            State = new TeamState { Name = "research-build" },
        };
        var p = scope.LeadPreamble();
        Assert.Contains("leading a team", p);
        Assert.Contains("WebAgent, CodeAgent", p);
        Assert.Contains("team_dispatch", p);
        Assert.Contains("task_create", p);
        Assert.Contains("team_peerwork", p);
        Assert.Contains("/kanban", p);
    }

    [Fact]
    public void LeadPreamble_FanoutTeam_OmitsBoardTools()
    {
        var scope = new TeamScope
        {
            DisplayName = "fan",
            LeadDef = new MuxSwarm.Utils.Common.AgentDefinition("Orchestrator", "", "", false, tools => tools),
            Members = new List<string> { "WebAgent" },
            Coordination = "fanout",
            Board = null,
            Tools = new List<Microsoft.Extensions.AI.AITool>(),
            State = new TeamState { Name = "fan" },
        };
        var p = scope.LeadPreamble();
        Assert.Contains("team_dispatch", p);
        Assert.DoesNotContain("task_create", p);
        Assert.DoesNotContain("team_peerwork", p);
    }
}
