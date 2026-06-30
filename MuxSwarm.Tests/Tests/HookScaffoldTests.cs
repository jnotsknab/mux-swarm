using System;
using System.Linq;
using System.Text.Json;
using MuxSwarm.State;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Hook scaffolding + toggle coverage: HookConfig serializes round-trip into a SwarmConfig hooks[]
/// entry (the shape /createhook persists), and the HookWorker session enable gate flips.
/// </summary>
public class HookScaffoldTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void HookConfig_RoundTrips_InSwarmHooksArray()
    {
        var swarm = new SwarmConfig();
        swarm.Hooks.Add(new HookConfig
        {
            Id = "notify-done",
            Command = "bash /hooks/notify.sh",
            Mode = HookMode.Blocking,
            TimeoutSeconds = 45,
            When = new HookClause { Event = "task_complete", Agent = "CodeAgent", Tool = null },
        });

        var json = JsonSerializer.Serialize(swarm, Opts);
        var back = JsonSerializer.Deserialize<SwarmConfig>(json, Opts)!;

        var h = Assert.Single(back.Hooks);
        Assert.Equal("notify-done", h.Id);
        Assert.Equal("bash /hooks/notify.sh", h.Command);
        Assert.Equal(HookMode.Blocking, h.Mode);
        Assert.Equal(45, h.TimeoutSeconds);
        Assert.Equal("task_complete", h.When.Event);
        Assert.Equal("CodeAgent", h.When.Agent);
        Assert.Null(h.When.Tool);
    }

    [Fact]
    public void HookConfig_DefaultsAreAsync30s()
    {
        var h = new HookConfig();
        Assert.Equal(HookMode.Async, h.Mode);
        Assert.Equal(30, h.TimeoutSeconds);
        Assert.False(h.Persistent);
    }

    [Fact]
    public void HookWorker_EnableGate_Toggles()
    {
        var prior = HookWorker.Enabled;
        try
        {
            HookWorker.Enabled = false;
            Assert.False(HookWorker.Enabled);
            HookWorker.Enabled = true;
            Assert.True(HookWorker.Enabled);
        }
        finally { HookWorker.Enabled = prior; }
    }
}
