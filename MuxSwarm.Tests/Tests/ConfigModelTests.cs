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
}
