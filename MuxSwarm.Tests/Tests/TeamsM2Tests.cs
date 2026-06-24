using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MuxSwarm.State;
using MuxSwarm.Utils;
using MuxSwarm.Utils.Teams;
using Xunit;

namespace MuxSwarm.Tests.Tests;

public class TeamsM2Tests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- Tier-2 schema: additive + deletion-invariant ----

    [Fact]
    public void Teams_RoundTrip_PreservesFields()
    {
        var swarm = new SwarmConfig();
        swarm.Teams.Add(new TeamConfig
        {
            Name = "research-build",
            Description = "Research then build.",
            Lead = "Orchestrator",
            Members = new List<string> { "WebAgent", "CodeAgent" },
            Coordination = "taskboard",
            MaxParallel = 3,
            AgentView = "auto",
        });

        var json = JsonSerializer.Serialize(swarm, Opts);
        var back = JsonSerializer.Deserialize<SwarmConfig>(json, Opts)!;

        Assert.Single(back.Teams);
        var t = back.Teams[0];
        Assert.Equal("research-build", t.Name);
        Assert.Equal("Orchestrator", t.Lead);
        Assert.Equal(new[] { "WebAgent", "CodeAgent" }, t.Members.ToArray());
        Assert.Equal("taskboard", t.Coordination);
        Assert.Equal(3, t.MaxParallel);
        Assert.Contains("teams", json);
    }

    [Fact]
    public void Teams_Absent_DeserializesToEmpty_DeletionInvariant()
    {
        // A swarm.json that predates teams[] must parse with an empty (non-null) Teams list,
        // so removing the feature leaves prior configs working identically.
        var back = JsonSerializer.Deserialize<SwarmConfig>("{}", Opts)!;
        Assert.NotNull(back.Teams);
        Assert.Empty(back.Teams);
    }

    [Fact]
    public void TeamConfig_Defaults_AreFanoutAuto()
    {
        var t = new TeamConfig();
        Assert.Equal("fanout", t.Coordination);
        Assert.Equal("auto", t.AgentView);
        Assert.Null(t.MaxParallel);
        Assert.Empty(t.Members);
    }

    // ---- TaskBoard: dependency gating, race-safe claim, persistence ----

    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mux-teams-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TaskBoard_BlockedTask_CannotClaim_UntilDependencyDone()
    {
        var root = TempRoot();
        try
        {
            var board = TaskBoard.Open("t", root);
            var a = board.Create("design", "do design");
            var b = board.Create("build", "do build", new[] { a.Id });

            // b depends on a -> starts Blocked, cannot be claimed.
            Assert.False(board.IsClaimable(b.Id));
            Assert.False(board.TryClaim(b.Id, "CodeAgent", out _));

            // a is claimable, claim + complete it.
            Assert.True(board.TryClaim(a.Id, "WebAgent", out _));
            Assert.True(board.SetStatus(a.Id, TeamTaskStatus.Done, out _));

            // completing a auto-unblocks b.
            Assert.True(board.IsClaimable(b.Id));
            Assert.True(board.TryClaim(b.Id, "CodeAgent", out _));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TaskBoard_Claim_IsRaceSafe_SingleOwner()
    {
        var root = TempRoot();
        try
        {
            var board = TaskBoard.Open("t", root);
            var task = board.Create("job", "do job");

            int wins = 0;
            Parallel.For(0, 32, i =>
            {
                if (board.TryClaim(task.Id, $"agent{i}", out _))
                    System.Threading.Interlocked.Increment(ref wins);
            });

            Assert.Equal(1, wins);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TaskBoard_Persists_And_Reloads()
    {
        var root = TempRoot();
        try
        {
            var board = TaskBoard.Open("t", root);
            var a = board.Create("alpha", "first");
            board.TryClaim(a.Id, "WebAgent", out _);
            board.SetStatus(a.Id, TeamTaskStatus.Done, out _);
            board.Create("beta", "second", new[] { a.Id });

            // Re-open from disk: tasks + owner + status survive.
            var reload = TaskBoard.Open("t", root);
            var snap = reload.Snapshot();
            Assert.Equal(2, snap.Count);
            var alpha = snap.First(x => x.Subject == "alpha");
            Assert.Equal(TeamTaskStatus.Done, alpha.Status);
            Assert.Equal("WebAgent", alpha.Owner);

            var (total, done, _, _, _) = reload.Tally();
            Assert.Equal(2, total);
            Assert.Equal(1, done);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TaskBoard_UnknownTask_ClaimFails_Cleanly()
    {
        var root = TempRoot();
        try
        {
            var board = TaskBoard.Open("t", root);
            Assert.False(board.TryClaim("t999", "X", out var reason));
            Assert.Contains("no such task", reason);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- TeamState: slug safety + roundtrip ----

    [Fact]
    public void TeamState_Slug_SanitizesGigaPrefix()
    {
        Assert.Equal("giga-build", TeamState.Slug("giga:build"));
        Assert.DoesNotContain(":", TeamState.Slug("giga:build"));
    }
}
