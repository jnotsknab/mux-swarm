using System.Collections.Generic;
using MuxSwarm.Setup;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Coverage for SwarmDefaults.RankModels - the keyword heuristic that turns a flat /v1/models list into
/// sensible default model ids for the swarm roles. This is what fixes the "subscription login through the
/// loopback sidecar endpoint resolves to llama3" bug: real probed ids now drive the defaults.
/// </summary>
public class SwarmModelRankingTests
{
    [Fact]
    public void RankModels_Empty_ReturnsNull()
    {
        Assert.Null(SwarmDefaults.RankModels(new List<string>()));
        Assert.Null(SwarmDefaults.RankModels(new List<string> { "", "   " }));
    }

    [Fact]
    public void RankModels_Claude_PicksOpusForAgent_HaikuForLight()
    {
        var models = new List<string>
        {
            "claude-opus-4-6",
            "claude-sonnet-4-6",
            "claude-haiku-4-5-20251001",
        };

        var r = SwarmDefaults.RankModels(models);

        Assert.NotNull(r);
        Assert.Equal("claude-opus-4-6", r!.Agent);
        Assert.Equal("claude-haiku-4-5-20251001", r.Light);
        Assert.Equal("claude-haiku-4-5-20251001", r.Compaction);
    }

    [Fact]
    public void RankModels_OpenAi_PicksGpt5ForAgent_MiniForLight()
    {
        var models = new List<string>
        {
            "gpt-5.2-2025-12-11",
            "gpt-4o",
            "gpt-5-mini-2025-08-07",
        };

        var r = SwarmDefaults.RankModels(models);

        Assert.NotNull(r);
        Assert.Equal("gpt-5.2-2025-12-11", r!.Agent);
        Assert.Equal("gpt-5-mini-2025-08-07", r.Light);
    }

    [Fact]
    public void RankModels_NeverPicksLlama3_ForClaudeList()
    {
        // Regression guard: the old URL-heuristic returned llama3 for the loopback cliproxy endpoint.
        var models = new List<string> { "claude-opus-4-6", "claude-haiku-4-5-20251001" };

        var r = SwarmDefaults.RankModels(models);

        Assert.NotNull(r);
        Assert.DoesNotContain("llama", r!.Agent);
        Assert.DoesNotContain("llama", r.Orchestrator);
        Assert.DoesNotContain("llama", r.Light);
    }

    [Fact]
    public void RankModels_SingleModel_UsesItForEveryRole()
    {
        var r = SwarmDefaults.RankModels(new List<string> { "gpt-4o" });

        Assert.NotNull(r);
        Assert.Equal("gpt-4o", r!.Agent);
        Assert.Equal("gpt-4o", r.Orchestrator);
        Assert.Equal("gpt-4o", r.Light);
        Assert.Equal("gpt-4o", r.Compaction);
    }

    [Fact]
    public void RankModels_Deduplicates()
    {
        var r = SwarmDefaults.RankModels(new List<string> { "gpt-4o", "gpt-4o", "gpt-4o-mini" });

        Assert.NotNull(r);
        Assert.Equal("gpt-4o", r!.Agent);
        Assert.Equal("gpt-4o-mini", r.Light);
    }
}
