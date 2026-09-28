using System.Collections.Generic;
using MuxSwarm.Engine;
using MuxSwarm.State;
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
    public void DaemonTrigger_PayloadLimit_DefaultsTo64K()
    {
        Assert.Equal(65536, new DaemonTrigger().PayloadLimit);
    }

    // --- v0.15.0 webhook fixes: response path, templating, truncation, HMAC, form bodies, cooldown ---

    [Fact]
    public void GoalTemplate_PlaceholdersInsidePayload_AreNotReexpanded()
    {
        var goal = DaemonRunner.SubstituteGoalTemplate("Handle {payload} ({ID} @ {timestamp}) {unknown}",
            new Dictionary<string, string>
            {
                ["{payload}"] = "{\"comment\":\"deploy {id} at {timestamp}\"}",
                ["{id}"] = "ghpr",
                ["{timestamp}"] = "T",
            });
        Assert.Equal("Handle {\"comment\":\"deploy {id} at {timestamp}\"} (ghpr @ T) {unknown}", goal);
    }

    [Fact]
    public void TruncatePayload_MarksTheCut()
    {
        Assert.Equal("abc", DaemonRunner.TruncatePayload("abc", 5));
        Assert.Equal("abcde\n[payload truncated: 5 of 8 chars]", DaemonRunner.TruncatePayload("abcdefgh", 5));
    }

    private static string Sig(byte[] body, string secret)
        => "sha256=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void Hmac_IsVerifiedOverRawBytes_IncludingBomAndNonUtf8()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}'];
        byte[] latin1 = [(byte)'{', 0xE9, (byte)'}'];   // not valid UTF-8
        Assert.True(ServeMode.VerifyHmacSignature(bom, "s3cret", Sig(bom, "s3cret")));
        Assert.True(ServeMode.VerifyHmacSignature(latin1, "s3cret", Sig(latin1, "s3cret")));
        Assert.False(ServeMode.VerifyHmacSignature(bom, "s3cret", Sig(bom, "other")));
        Assert.False(ServeMode.VerifyHmacSignature(bom, "s3cret", ""));
    }

    [Fact]
    public void Body_FormEncoded_UnwrapsPayloadField_JsonPassesThrough()
    {
        var form = System.Text.Encoding.UTF8.GetBytes("payload=%7B%22action%22%3A%22opened%22%2C%22n%22%3A1%7D&x=1");
        Assert.Equal("{\"action\":\"opened\",\"n\":1}",
            ServeMode.DecodeWebhookBody(form, "application/x-www-form-urlencoded; charset=utf-8"));
        byte[] json = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\"a\":1}")];
        Assert.Equal("{\"a\":1}", ServeMode.DecodeWebhookBody(json, "application/json"));
    }

    [Fact]
    public void Webhook_DefaultCooldownIsZero_OtherTriggersKeep30()
    {
        Assert.Equal(0u, new DaemonTrigger { Type = "webhook" }.EffectiveInterval);
        Assert.Equal(30u, new DaemonTrigger { Type = "watch" }.EffectiveInterval);
        Assert.Equal(7u, new DaemonTrigger { Type = "webhook", Interval = 7 }.EffectiveInterval);
        Assert.Equal(5u, new DaemonTrigger { Type = "webhook", Interval = 7, Cooldown = 5 }.EffectiveInterval);
    }

    [Fact]
    public async Task Enqueue_ReturnsDeliveryId_AndRefusesInsideCooldown()
    {
        var hook = new DaemonTrigger { Id = "ghpr", Type = "webhook", Cooldown = 60 };
        var free = new DaemonTrigger { Id = "free", Type = "webhook" };
        await using var runner = new DaemonRunner(new DaemonConfig { Triggers = [hook, free] });
        runner.Start(_ => throw new InvalidOperationException("no model in tests"), [], new Dictionary<string, string> { ["Orchestrator"] = "m" });
        for (int i = 0; i < 100 && !(runner.HasWebhook("ghpr") && runner.HasWebhook("free")); i++) await Task.Delay(10);

        Assert.Equal(DaemonRunner.WebhookEnqueue.Accepted, runner.EnqueueWebhook("ghpr", "{}", "t", out var d1, out _));
        Assert.Equal(32, d1.Length);
        Assert.Equal(DaemonRunner.WebhookEnqueue.Cooldown, runner.EnqueueWebhook("ghpr", "{}", "t", out var d2, out var retry));
        Assert.Equal("", d2);
        Assert.InRange(retry, 1, 60);

        Assert.Equal(DaemonRunner.WebhookEnqueue.Accepted, runner.EnqueueWebhook("free", "{}", "t", out var a, out _));
        Assert.Equal(DaemonRunner.WebhookEnqueue.Accepted, runner.EnqueueWebhook("free", "{}", "t", out var b, out _));
        Assert.NotEqual(a, b);
        Assert.Equal(DaemonRunner.WebhookEnqueue.Unknown, runner.EnqueueWebhook("nope", "{}", "t", out _, out _));
    }

    [Fact]
    public async Task Webhook_AddedAtRuntime_IsLiveImmediately_AndCancelStopsIt()
    {
        await using var runner = new DaemonRunner(new DaemonConfig());
        runner.Start(_ => throw new InvalidOperationException("no model in tests"), [], new Dictionary<string, string> { ["Orchestrator"] = "m" });
        Assert.Equal("rt-hook", runner.AddTriggerRuntime(new DaemonTrigger { Id = "rt-hook", Type = "webhook", Goal = "x" }));
        for (int i = 0; i < 100 && !runner.HasWebhook("rt-hook"); i++) await Task.Delay(10);
        Assert.True(runner.HasWebhook("rt-hook"));

        Assert.True(runner.CancelTrigger("rt-hook"));
        for (int i = 0; i < 100 && runner.HasWebhook("rt-hook"); i++) await Task.Delay(10);
        Assert.False(runner.HasWebhook("rt-hook"));   // route 404s once the loop is gone
    }

    [Fact]
    public void CallbackBody_CarriesCorrelationAndOutcome()
    {
        using var ok = System.Text.Json.JsonDocument.Parse(
            DaemonRunner.BuildCallbackBody("ghpr", "abc", new DaemonRunner.GoalOutcome(true, "LGTM", null)));
        Assert.Equal("ghpr", ok.RootElement.GetProperty("id").GetString());
        Assert.Equal("abc", ok.RootElement.GetProperty("deliveryId").GetString());
        Assert.Equal("ok", ok.RootElement.GetProperty("status").GetString());
        Assert.Equal("LGTM", ok.RootElement.GetProperty("result").GetString());
        using var err = System.Text.Json.JsonDocument.Parse(
            DaemonRunner.BuildCallbackBody("ghpr", "abc", new DaemonRunner.GoalOutcome(false, null, "boom")));
        Assert.Equal("error", err.RootElement.GetProperty("status").GetString());
        Assert.Equal("boom", err.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunResult_FlowsThroughAsyncAndIsInertOutsideAScope()
    {
        RunResult.Report("ignored");   // no scope: no-op
        using var scope = RunResult.Begin();
        await Task.Run(async () => { await Task.Yield(); RunResult.Report("first"); RunResult.Report("final"); });
        RunResult.Report("   ");        // blank never overwrites
        Assert.Equal("final", scope.Text);
    }

    [Fact]
    public async Task RunResult_ReportError_FirstWins_AndIsInertOutsideAScope()
    {
        RunResult.ReportError("ignored");   // no scope: no-op
        using var scope = RunResult.Begin();
        Assert.Null(scope.Error);
        await Task.Run(() => { RunResult.ReportError("model rejected"); RunResult.ReportError("later"); });
        Assert.Equal("model rejected", scope.Error);
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
