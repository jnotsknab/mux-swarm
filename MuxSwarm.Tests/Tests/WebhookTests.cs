using System.Collections.Generic;
using MuxSwarm.State;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Bidirectional webhook coverage (v0.12.1). Outbound = WebhookSink arming/inert semantics; inbound
/// = DaemonRunner webhook queue registry (EnqueueWebhook/HasWebhook). The HTTP route + HMAC verify
/// and the goal-fire path drive I/O + orchestrators so they are exercised via live probe, not here.
/// </summary>
public class WebhookTests
{
    [Fact]
    public void WebhookSink_NoSinks_IsInert()
    {
        WebhookSink.Start(null);
        Assert.False(WebhookSink.IsActive);
        // Notify must be a safe no-op when inert.
        WebhookSink.Notify("task_complete", new Dictionary<string, object?> { ["x"] = 1 });
    }

    [Fact]
    public void WebhookSink_EntryMissingUrlOrEvents_IsFilteredOut()
    {
        WebhookSink.Start(new List<WebhookConfig>
        {
            new() { Url = "", Events = { "task_complete" } },      // no url
            new() { Url = "https://example.com", Events = { } },   // no events
        });
        Assert.False(WebhookSink.IsActive);
        WebhookSink.Start(null); // reset shared state for other tests
    }

    [Fact]
    public void WebhookSink_ValidSink_Arms()
    {
        WebhookSink.Start(new List<WebhookConfig>
        {
            new() { Url = "https://example.com/hook", Events = { "task_complete", "error" } },
        });
        Assert.True(WebhookSink.IsActive);
        WebhookSink.Start(null); // reset shared state
        Assert.False(WebhookSink.IsActive);
    }

    [Fact]
    public void DaemonRunner_UnknownWebhookId_NotEnqueuable()
    {
        var runner = new DaemonRunner(new DaemonConfig());
        Assert.False(runner.HasWebhook("ghpr"));
        Assert.False(runner.EnqueueWebhook("ghpr", "{}", "127.0.0.1"));
    }

    [Fact]
    public void WebhookConfig_RoundTrips_InSwarmWebhooksArray()
    {
        var swarm = new SwarmConfig();
        swarm.Webhooks.Add(new WebhookConfig
        {
            Url = "https://example.com/hook",
            Events = { "task_complete" },
            Secret = "s3cr3t",
            Headers = new Dictionary<string, string> { ["X-Env"] = "prod" },
        });

        var json = System.Text.Json.JsonSerializer.Serialize(swarm);
        var back = System.Text.Json.JsonSerializer.Deserialize<SwarmConfig>(json)!;

        var wh = Assert.Single(back.Webhooks);
        Assert.Equal("https://example.com/hook", wh.Url);
        Assert.Contains("task_complete", wh.Events);
        Assert.Equal("s3cr3t", wh.Secret);
        Assert.Equal("prod", wh.Headers!["X-Env"]);
    }

    [Fact]
    public void DaemonTrigger_WebhookFields_RoundTrip()
    {
        var t = new DaemonTrigger { Id = "ghpr", Type = "webhook", Secret = "abc", PayloadLimit = 2048 };
        var json = System.Text.Json.JsonSerializer.Serialize(t);
        var back = System.Text.Json.JsonSerializer.Deserialize<DaemonTrigger>(json)!;
        Assert.Equal("webhook", back.Type);
        Assert.Equal("abc", back.Secret);
        Assert.Equal(2048, back.PayloadLimit);
    }

    [Fact]
    public void DaemonTrigger_PayloadLimit_DefaultsTo8K()
    {
        Assert.Equal(8192, new DaemonTrigger().PayloadLimit);
    }

    // --- alias map: outbound events[] accepts the lifecycle/hook names users already know ---

    [Theory]
    [InlineData("text_chunk", "stream", true)]           // lifecycle name -> render-stream alias
    [InlineData("thinking_chunk", "thinking_start", true)]
    [InlineData("thinking_chunk", "thinking_update", true)]
    [InlineData("thinking_chunk", "thinking_end", true)]
    [InlineData("turn_end", "agent_turn_end", true)]     // the trap: hook name -> emit name
    [InlineData("task_complete", "task_complete", true)] // exact twin, no alias needed
    [InlineData("error", "error", true)]
    [InlineData("turn_end", "agent_turn_start", false)]  // must NOT match a different moment
    [InlineData("text_chunk", "tool_call", false)]
    public void AllowlistMatches_ResolvesLifecycleAliases(string allowEntry, string emittedType, bool expected)
    {
        Assert.Equal(expected, WebhookSink.AllowlistMatches(new[] { allowEntry }, emittedType));
    }

    [Fact]
    public void AllowlistMatches_Wildcard_MatchesAnything()
    {
        Assert.True(WebhookSink.AllowlistMatches(new[] { "*" }, "anything_at_all"));
    }

    [Fact]
    public void AllowlistMatches_NoMatch_ReturnsFalse()
    {
        Assert.False(WebhookSink.AllowlistMatches(new[] { "task_complete", "error" }, "stream"));
    }
}
