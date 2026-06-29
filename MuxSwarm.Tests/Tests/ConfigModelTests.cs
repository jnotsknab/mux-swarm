using System.Text.Json;
using System.Text.Json.Serialization;
using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

public class ConfigModelTests
{
    // ── AppConfig defaults ─────────────────────────────────────────────

    [Fact]
    public void AppConfig_Defaults_SetupCompletedIsFalse()
    {
        var config = new AppConfig();
        Assert.False(config.SetupCompleted);
    }

    [Fact]
    public void AppConfig_Defaults_DockerExecIsFalse()
    {
        var config = new AppConfig();
        Assert.False(config.IsUsingDockerForExec);
    }

    [Fact]
    public void AppConfig_Defaults_ServeAddressIsAllInterfaces()
    {
        var config = new AppConfig();
        Assert.Equal("0.0.0.0", config.ServeAddress);
    }

    [Fact]
    public void AppConfig_Defaults_McpServersIsEmpty()
    {
        var config = new AppConfig();
        Assert.Empty(config.McpServers);
    }

    [Fact]
    public void AppConfig_Defaults_ProvidersIsEmpty()
    {
        var config = new AppConfig();
        Assert.Empty(config.LlmProviders);
    }

    [Fact]
    public void AppConfig_Serialization_RoundTripsCorrectly()
    {
        var config = new AppConfig
        {
            SetupCompleted = true,
            IsUsingDockerForExec = true,
            ServeAddress = "127.0.0.1"
        };

        var json = JsonSerializer.Serialize(config);
        var deserialized = JsonSerializer.Deserialize<AppConfig>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.SetupCompleted);
        Assert.True(deserialized.IsUsingDockerForExec);
        Assert.Equal("127.0.0.1", deserialized.ServeAddress);
    }

    // ── ExecutionLimits defaults ───────────────────────────────────────

    [Fact]
    public void ExecutionLimits_Defaults_TurnContinuationKnobs()
    {
        var limits = new ExecutionLimits();
        // A is high by default so a normal tool chain never trips it.
        Assert.True(limits.MaxToolIterationsPerTurn >= 100);
        // B defaults to a small bounded number of silent continues.
        Assert.Equal(3, limits.MaxAutoContinuesPerTurn);
    }

    [Fact]
    public void ExecutionLimits_MissingKeys_InheritDefaults()
    {
        // A swarm.json that predates these knobs (no keys present) must deserialize to the
        // C# defaults rather than 0 - otherwise auto-continue would silently turn off and the
        // tool cap would collapse to zero iterations.
        var limits = JsonSerializer.Deserialize<ExecutionLimits>("{}");
        Assert.NotNull(limits);
        Assert.Equal(1000, limits!.MaxToolIterationsPerTurn);
        Assert.Equal(3, limits.MaxAutoContinuesPerTurn);
    }

    [Fact]
    public void ExecutionLimits_TurnKnobs_RoundTrip()
    {
        var limits = new ExecutionLimits { MaxToolIterationsPerTurn = 0, MaxAutoContinuesPerTurn = 7 };
        var json = JsonSerializer.Serialize(limits);
        var back = JsonSerializer.Deserialize<ExecutionLimits>(json);
        Assert.NotNull(back);
        Assert.Equal(0, back!.MaxToolIterationsPerTurn);  // 0 = unlimited sentinel preserved
        Assert.Equal(7, back.MaxAutoContinuesPerTurn);
        Assert.Contains("maxToolIterationsPerTurn", json);
        Assert.Contains("maxAutoContinuesPerTurn", json);
    }

    [Fact]
    public void ExecutionLimits_Defaults_AreSensible()
    {
        var limits = new ExecutionLimits();
        Assert.True(limits.ProgressEntryBudget > 0);
        Assert.True(limits.CrossAgentContextBudget > 0);
        Assert.True(limits.MaxOrchestratorIterations > 0);
        Assert.True(limits.MaxSubAgentIterations > 0);
        Assert.True(limits.MaxStuckCount > 0);
        Assert.True(limits.ActivityTimeoutSeconds > 0);
        Assert.True(limits.CompactionCharBudget > 0);
    }

    [Fact]
    public void ExecutionLimits_Defaults_ActivityTimeoutIsTwelveHours()
    {
        // QOL default: a generous stall tolerance (1h) so long tool-running turns and slow
        // providers are not torn down mid-turn. Pin it so a future edit doesn't silently shrink it.
        Assert.Equal(3600, new ExecutionLimits().ActivityTimeoutSeconds);
    }

    [Fact]
    public void AppConfig_Defaults_McpConnectTimeoutIsNinetySeconds()
    {
        Assert.Equal(90, new AppConfig().McpConnectTimeoutSeconds);
    }

    [Fact]
    public void AppConfig_McpConnectTimeout_RoundTrips()
    {
        var cfg = new AppConfig { McpConnectTimeoutSeconds = 120 };
        var json = JsonSerializer.Serialize(cfg);
        var back = JsonSerializer.Deserialize<AppConfig>(json);
        Assert.NotNull(back);
        Assert.Equal(120, back!.McpConnectTimeoutSeconds);
    }

    [Fact]
    public void ExecutionLimits_Defaults_OrchestratorMoreThanSubAgent()
    {
        var limits = new ExecutionLimits();
        Assert.True(limits.MaxOrchestratorIterations > limits.MaxSubAgentIterations);
    }

    [Fact]
    public void ExecutionLimits_Defaults_CrossAgentBudgetLessThanProgressLog()
    {
        var limits = new ExecutionLimits();
        Assert.True(limits.CrossAgentContextBudget < limits.ProgressLogTotalBudget);
    }

    [Fact]
    public void ExecutionLimits_Serialization_RoundTripsCorrectly()
    {
        var limits = new ExecutionLimits
        {
            MaxOrchestratorIterations = 20,
            MaxSubAgentIterations = 10,
            ActivityTimeoutSeconds = 300
        };

        var json = JsonSerializer.Serialize(limits);
        var deserialized = JsonSerializer.Deserialize<ExecutionLimits>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(20, deserialized.MaxOrchestratorIterations);
        Assert.Equal(10, deserialized.MaxSubAgentIterations);
        Assert.Equal(300, deserialized.ActivityTimeoutSeconds);
    }

    // ── showReasoning default + round-trip ───────────────────────────

    [Fact]
    public void AppConfig_Defaults_ShowReasoningIsSummary()
    {
        var config = new AppConfig();
        Assert.Equal("summary", config.ShowReasoning);
    }

    [Fact]
    public void AppConfig_ShowReasoning_RoundTrips()
    {
        var config = new AppConfig { ShowReasoning = "none" };
        var json = JsonSerializer.Serialize(config);
        Assert.Contains("showReasoning", json);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal("none", back.ShowReasoning);
    }
}
