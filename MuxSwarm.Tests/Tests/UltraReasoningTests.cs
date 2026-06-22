using Microsoft.Extensions.AI;
using MuxSwarm;
using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

public class UltraReasoningTests
{
    // ── UltraConfig defaults ───────────────────────────────────────────

    [Fact]
    public void UltraConfig_Defaults_ThinkingBudgetMatchesCcCeiling()
    {
        var cfg = new UltraConfig();
        Assert.Equal(31999, cfg.ThinkingBudget);
    }

    [Fact]
    public void UltraConfig_Defaults_IncludeSubAgentsIsTrue()
    {
        var cfg = new UltraConfig();
        Assert.True(cfg.IncludeSubAgents);
    }

    [Fact]
    public void UltraConfig_Defaults_AutoSubAgentsIsTrue()
    {
        var cfg = new UltraConfig();
        Assert.True(cfg.AutoSubAgents);
    }

    [Fact]
    public void AppConfig_Defaults_UltraIsPresent()
    {
        var config = new AppConfig();
        Assert.NotNull(config.Ultra);
        Assert.Equal(31999, config.Ultra.ThinkingBudget);
    }

    // ── UltraReasoning.Apply ───────────────────────────────────────────

    [Fact]
    public void Apply_InjectsNumericThinkingBudget()
    {
        App.Config = new AppConfig();
        var opts = new ChatOptions();

        UltraReasoning.Apply(opts);

        Assert.NotNull(opts.AdditionalProperties);
        Assert.True(opts.AdditionalProperties!.ContainsKey("thinking"));
        var thinking = Assert.IsType<Dictionary<string, object>>(opts.AdditionalProperties["thinking"]);
        Assert.Equal("enabled", thinking["type"]);
        Assert.Equal(31999, thinking["budget_tokens"]);
    }

    [Fact]
    public void Apply_RaisesEffortTierToHigh()
    {
        App.Config = new AppConfig();
        var opts = new ChatOptions();

        UltraReasoning.Apply(opts);

        Assert.NotNull(opts.Reasoning);
        Assert.Equal(ReasoningEffort.High, opts.Reasoning!.Effort);
    }

    [Fact]
    public void Apply_HonorsConfiguredBudget()
    {
        App.Config = new AppConfig { Ultra = new UltraConfig { ThinkingBudget = 12345 } };
        var opts = new ChatOptions();

        UltraReasoning.Apply(opts);

        var thinking = Assert.IsType<Dictionary<string, object>>(opts.AdditionalProperties!["thinking"]);
        Assert.Equal(12345, thinking["budget_tokens"]);
    }

    [Fact]
    public void Apply_ZeroBudget_SkipsThinkingButStillSetsEffort()
    {
        App.Config = new AppConfig { Ultra = new UltraConfig { ThinkingBudget = 0 } };
        var opts = new ChatOptions();

        UltraReasoning.Apply(opts);

        Assert.True(opts.AdditionalProperties is null || !opts.AdditionalProperties.ContainsKey("thinking"));
        Assert.Equal(ReasoningEffort.High, opts.Reasoning!.Effort);
    }
}
