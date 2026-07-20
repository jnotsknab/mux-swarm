using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers single-agent modelOpts resolution (SingleAgentOrchestrator.ResolveSingleAgentModelOpts).
/// The selected named agent's own modelOpts must win over the singleAgent block so per-agent
/// sampling params (e.g. a provider that only accepts temperature 1) are honored in single-agent
/// mode, not just swarm mode. Falls back to the singleAgent block when there is no match.
/// Pure-function tests; no file IO.
/// </summary>
public class SingleAgentModelOptsResolutionTests
{
    private static SwarmConfig BuildConfig() => new()
    {
        SingleAgent = new AgentConfig
        {
            Name = "singleAgent",
            Model = "claude-opus-4-8",
            ModelOpts = new ModelOpts { Temperature = 0.6f },
        },
        Agents =
        [
            new AgentConfig
            {
                Name = "CodeAgent",
                Model = "kimi-k3",
                ModelOpts = new ModelOpts { Temperature = 1f },
            },
            new AgentConfig
            {
                Name = "WebAgent",
                Model = "claude-opus-4-8",
                // No modelOpts on purpose: must fall back to the singleAgent block.
            },
        ],
    };

    [Fact]
    public void NamedAgent_WithModelOpts_PrefersPerAgentOverSingleAgentBlock()
    {
        var opts = SingleAgentOrchestrator.ResolveSingleAgentModelOpts(BuildConfig(), "CodeAgent");
        Assert.NotNull(opts);
        Assert.Equal(1f, opts!.Temperature);
    }

    [Fact]
    public void NamedAgent_IsCaseInsensitive()
    {
        var opts = SingleAgentOrchestrator.ResolveSingleAgentModelOpts(BuildConfig(), "codeagent");
        Assert.NotNull(opts);
        Assert.Equal(1f, opts!.Temperature);
    }

    [Fact]
    public void NamedAgent_WithoutModelOpts_FallsBackToSingleAgentBlock()
    {
        var opts = SingleAgentOrchestrator.ResolveSingleAgentModelOpts(BuildConfig(), "WebAgent");
        Assert.NotNull(opts);
        Assert.Equal(0.6f, opts!.Temperature); // singleAgent block value
    }

    [Fact]
    public void UnknownAgent_FallsBackToSingleAgentBlock()
    {
        var opts = SingleAgentOrchestrator.ResolveSingleAgentModelOpts(BuildConfig(), "NoSuchAgent");
        Assert.NotNull(opts);
        Assert.Equal(0.6f, opts!.Temperature);
    }

    [Fact]
    public void NullOrEmptyAgentName_UsesSingleAgentBlock()
    {
        var cfg = BuildConfig();
        Assert.Equal(0.6f, SingleAgentOrchestrator.ResolveSingleAgentModelOpts(cfg, null)!.Temperature);
        Assert.Equal(0.6f, SingleAgentOrchestrator.ResolveSingleAgentModelOpts(cfg, "")!.Temperature);
    }

    [Fact]
    public void NullConfig_ReturnsNull()
    {
        Assert.Null(SingleAgentOrchestrator.ResolveSingleAgentModelOpts(null, "CodeAgent"));
    }
}
