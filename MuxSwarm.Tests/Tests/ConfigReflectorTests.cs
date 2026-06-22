using System.Collections.Generic;
using System.Linq;
using MuxSwarm.Utils;
using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

public class ConfigReflectorTests
{
    [Fact]
    public void Walk_AppConfig_CoversNestedScalarLeaves()
    {
        var cfg = new AppConfig();
        var leaves = ConfigReflector.Walk(cfg, "").ToList();
        var paths = leaves.Select(l => l.Path).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        // Root + nested scalars are all reachable by dotted JsonPropertyName path.
        Assert.Contains("isUsingDockerForExec", paths);
        Assert.Contains("serveAddress", paths);
        Assert.Contains("console.collapseToolLines", paths);
        Assert.Contains("console.renderMode", paths);
        Assert.Contains("ultra.thinkingBudget", paths);
        Assert.Contains("serve.editable", paths);
        Assert.Contains("serve.auth.enabled", paths);
        Assert.Contains("contextLimits.memoryMdCharLimit", paths);
    }

    [Fact]
    public void Walk_Set_RoundTrips_Bool_Int_Enum()
    {
        var cfg = new AppConfig();
        var leaves = ConfigReflector.Walk(cfg, "").ToDictionary(l => l.Path, l => l, System.StringComparer.OrdinalIgnoreCase);

        var (ok1, _) = leaves["console.collapseToolLines"].Set("17");
        Assert.True(ok1);
        Assert.Equal(17, cfg.Console.CollapseToolLines);

        var (ok2, _) = leaves["serve.auth.enabled"].Set("on");
        Assert.True(ok2);
        Assert.True(cfg.Serve.Auth.Enabled);

        // Re-reading the value reflects the change.
        Assert.Equal("17", leaves["console.collapseToolLines"].Get());
        Assert.Equal("true", leaves["serve.auth.enabled"].Get());
    }

    [Fact]
    public void Walk_Set_RejectsBadValues()
    {
        var cfg = new AppConfig();
        var leaf = ConfigReflector.Walk(cfg, "").First(l => l.Path == "console.collapseToolLines");
        var (ok, msg) = leaf.Set("not-a-number");
        Assert.False(ok);
        Assert.Contains("integer", msg);
    }

    [Fact]
    public void Walk_Swarm_CoversExecutionLimitsAndPerAgentScalars()
    {
        var swarm = new SwarmConfig();
        swarm.Agents.Add(new AgentConfig { Name = "CodeAgent", Model = "m1" });
        var leaves = ConfigReflector.Walk(swarm, "swarm").ToList();
        var paths = leaves.Select(l => l.Path).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        Assert.Contains("swarm.executionLimits.maxSubAgentIterations", paths);
        // Per-agent scalars are addressable by agent name.
        Assert.Contains("swarm.agents.CodeAgent.model", paths);

        var modelLeaf = leaves.First(l => l.Path == "swarm.agents.CodeAgent.model");
        var (ok, _) = modelLeaf.Set("m2");
        Assert.True(ok);
        Assert.Equal("m2", swarm.Agents[0].Model);
    }

    [Fact]
    public void Walk_NullableScalar_AcceptsNull()
    {
        var swarm = new SwarmConfig();
        swarm.Agents.Add(new AgentConfig { Name = "A", Model = "m1" });
        var leaf = ConfigReflector.Walk(swarm, "swarm").First(l => l.Path == "swarm.agents.A.model");
        var (ok, _) = leaf.Set("null");
        Assert.True(ok);
        Assert.Null(swarm.Agents[0].Model);
    }
}
