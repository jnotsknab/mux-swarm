using System.Collections.Generic;
using MuxSwarm.Engine;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// /api/health <c>daemon</c> field (v0.15.2): with <c>--serve --daemon</c> Kestrel answers before the
/// daemon registers its triggers, so a webhook POST can 404 while health says the server is up. The
/// field lets a client wait for <c>ready</c>. <see cref="DaemonRunner.TriggersReady"/> (not
/// <see cref="DaemonRunner.IsStarted"/>) drives it.
/// </summary>
public class HealthDaemonTests
{
    [Fact]
    public void DaemonHealth_IsOff_WhenNoDaemonRequested()
        => Assert.Equal("off", ServeMode.DaemonHealth(null, pending: false));

    [Fact]
    public void DaemonHealth_IsStarting_BeforeTheRunnerExists()
        => Assert.Equal("starting", ServeMode.DaemonHealth(null, pending: true));

    [Fact]
    public async Task DaemonHealth_IsStarting_UntilStartRegistersTriggers_ThenReady()
    {
        var hook = new DaemonTrigger { Id = "ghpr", Type = "webhook" };
        await using var runner = new DaemonRunner(new DaemonConfig { Triggers = [hook] });

        // Created but not started (the MCP-init window): webhooks are not routable yet.
        Assert.False(runner.TriggersReady);
        Assert.False(runner.HasWebhook("ghpr"));
        Assert.Equal("starting", ServeMode.DaemonHealth(runner, pending: false));

        runner.Start(_ => throw new InvalidOperationException("no model in tests"), [],
            new Dictionary<string, string> { ["Orchestrator"] = "m" });

        // "ready" must mean the webhook route resolves now, with no polling.
        Assert.True(runner.TriggersReady);
        Assert.True(runner.HasWebhook("ghpr"));
        Assert.Equal("ready", ServeMode.DaemonHealth(runner, pending: false));
    }

    [Fact]
    public async Task DaemonHealth_IsReady_ForADaemonWithNoTriggers()
    {
        await using var runner = new DaemonRunner(new DaemonConfig());
        runner.Start(_ => throw new InvalidOperationException("no model in tests"), [],
            new Dictionary<string, string> { ["Orchestrator"] = "m" });
        Assert.Equal("ready", ServeMode.DaemonHealth(runner, pending: true));
    }
}
