using System.Text.Json;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

public class SwarmConfigScalarTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void ExecutionLimitsScalars_RoundTrip()
    {
        var swarm = new SwarmConfig();
        swarm.ExecutionLimits.MaxOrchestratorIterations = 42;
        swarm.ExecutionLimits.MaxSubAgentIterations = 11;
        swarm.ExecutionLimits.MaxSubTaskRetries = 7;

        var json = JsonSerializer.Serialize(swarm, Opts);
        var back = JsonSerializer.Deserialize<SwarmConfig>(json, Opts)!;

        Assert.Equal(42, back.ExecutionLimits.MaxOrchestratorIterations);
        Assert.Equal(11, back.ExecutionLimits.MaxSubAgentIterations);
        Assert.Equal(7, back.ExecutionLimits.MaxSubTaskRetries);
        // The json uses the camelCase property names the /set keys target.
        Assert.Contains("maxOrchestratorIterations", json);
    }

    [Fact]
    public void MidTurnCompaction_DefaultsFalse_And_RoundTrips()
    {
        Assert.False(new ExecutionLimits().MidTurnCompaction);

        var swarm = new SwarmConfig();
        swarm.ExecutionLimits.MidTurnCompaction = true;

        var json = JsonSerializer.Serialize(swarm, Opts);
        var back = JsonSerializer.Deserialize<SwarmConfig>(json, Opts)!;

        Assert.True(back.ExecutionLimits.MidTurnCompaction);
        Assert.Contains("midTurnCompaction", json);
    }

    [Fact]
    public void AgentCrud_AddEditRemove_RoundTrips()
    {
        var swarm = new SwarmConfig();
        swarm.Agents.Add(new AgentConfig { Name = "TestA", Description = "d", Model = "m1", CanDelegate = false });
        Assert.Single(swarm.Agents);

        // edit
        swarm.Agents[0].Model = "m2";
        swarm.Agents[0].CanDelegate = true;

        var json = JsonSerializer.Serialize(swarm, Opts);
        var back = JsonSerializer.Deserialize<SwarmConfig>(json, Opts)!;
        Assert.Equal("m2", back.Agents[0].Model);
        Assert.True(back.Agents[0].CanDelegate);

        // remove
        back.Agents.RemoveAll(a => a.Name == "TestA");
        Assert.Empty(back.Agents);
    }
}
