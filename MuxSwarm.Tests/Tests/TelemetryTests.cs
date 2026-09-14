using MuxSwarm.Engine;
using MuxSwarm.Engine.Telemetry;

namespace MuxSwarm.Tests.Tests;

// Persistent telemetry pipeline: sink writes JSONL deltas, store aggregates summary/series/
// per-model/per-agent, server range parsing, plus the v0.14.0 tuning defaults
// (collapseToolLines=3) and the /context token-count parser.
public class TelemetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-telemetry-" + Guid.NewGuid().ToString("N"));

    public TelemetryTests()
    {
        Directory.CreateDirectory(_dir);
        TelemetrySink.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        TelemetrySink.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Sink_WritesDeltas_StoreAggregatesSummaryAndModels()
    {
        TelemetrySink.RecordUsageDelta("gpt-test", 1000, 200, 300, 50);
        TelemetrySink.RecordUsageDelta("gpt-test", 500, 100, 0, 0);
        TelemetrySink.RecordUsageDelta("claude-test", 2000, 400, 0, 0);
        TelemetrySink.RecordToolCall("gpt-test");
        TelemetrySink.RecordCompaction("gpt-test");
        TelemetrySink.RecordDelegation("Lead", "CodeAgent", 1234);

        var events = TelemetryStore.Load(null);
        Assert.True(events.Count >= 6);

        var summary = TelemetryStore.Summarize(events);
        Assert.Equal(3500, summary.TotalIn);
        Assert.Equal(700, summary.TotalOut);
        Assert.Equal(300, summary.TotalCached);
        Assert.Equal(1, summary.ToolCalls);
        Assert.Equal(1, summary.Compactions);
        Assert.Equal(1, summary.Delegations);

        var models = TelemetryStore.ByModel(events);
        Assert.Equal(2, models.Count(m => m.Model is "gpt-test" or "claude-test"));
        Assert.Equal(2400, models.First(m => m.Model == "claude-test").In + models.First(m => m.Model == "claude-test").Out);

        var agents = TelemetryStore.ByAgent(events);
        Assert.Single(agents);
        Assert.Equal("CodeAgent", agents[0].Agent);
        Assert.Equal(1234, agents[0].TotalMs);
    }

    [Fact]
    public void Sink_ZeroDelta_NotWritten_AndRangeFilters()
    {
        TelemetrySink.RecordUsageDelta("m", 0, 0, 0, 0);   // no-op
        Assert.Empty(TelemetryStore.Load(null).Where(e => e.Kind == "usage"));

        TelemetrySink.RecordUsageDelta("m", 10, 5, 0, 0);
        Assert.Single(TelemetryStore.Load(DateTime.UtcNow.AddMinutes(-5)).Where(e => e.Kind == "usage"));
        Assert.Empty(TelemetryStore.Load(DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public void Store_Series_BucketsHourlyForShortRanges_DailyForAll()
    {
        TelemetrySink.RecordUsageDelta("m", 100, 50, 0, 0);
        var events = TelemetryStore.Load(null);
        var daily = TelemetryStore.Series(events, null);
        Assert.Single(daily);
        Assert.DoesNotContain(":", daily[0].Bucket);         // yyyy-MM-dd
        var hourly = TelemetryStore.Series(events, DateTime.UtcNow.AddHours(-24));
        Assert.Single(hourly);
        Assert.Contains(":00", hourly[0].Bucket);            // yyyy-MM-dd HH:00
    }

    [Fact]
    public void Store_SkipsMalformedLines()
    {
        File.WriteAllText(Path.Combine(_dir, $"metrics-{DateTime.UtcNow:yyyyMM}.jsonl"),
            "not json at all\n{\"kind\":\"usage\",\"ts\":\"2026-01-01T00:00:00Z\",\"model\":\"ok\",\"in\":7,\"out\":3}\n{broken\n");
        var events = TelemetryStore.Load(null);
        Assert.Single(events);
        Assert.Equal("ok", events[0].Model);
    }

    [Fact]
    public void Server_ParseRange_MapsKnownRanges_NullForAll()
    {
        Assert.Null(TelemetryServer.ParseRange(null));
        Assert.Null(TelemetryServer.ParseRange("all"));
        Assert.True(TelemetryServer.ParseRange("24h") > DateTime.UtcNow.AddHours(-25));
        Assert.True(TelemetryServer.ParseRange("7d") > DateTime.UtcNow.AddDays(-8));
        Assert.True(TelemetryServer.ParseRange("30d") > DateTime.UtcNow.AddDays(-31));
    }

    [Fact]
    public void Sink_EnrichedEvents_RoundTrip_AgentToolTurnCompactionDelegation()
    {
        TelemetrySink.RecordUsageDelta("gpt-test", 100, 50, 20, 5, agent: "CodeAgent", total: 175);
        TelemetrySink.RecordToolCall("gpt-test", agent: "CodeAgent", tool: "repl_shell_exec", ms: 42, ok: false);
        TelemetrySink.RecordTurn("CodeAgent", "gpt-test", 2500);
        TelemetrySink.RecordCompaction("gpt-test", beforeTokens: 140000, afterTokens: 38000);
        TelemetrySink.RecordDelegation("Lead", "WebAgent", 900, ok: false);

        var events = TelemetryStore.Load(null);
        var usage = events.Single(e => e.Kind == "usage");
        Assert.Equal("CodeAgent", usage.Agent);
        Assert.Equal(175, usage.Total);
        var tool = events.Single(e => e.Kind == "tool");
        Assert.Equal("repl_shell_exec", tool.Tool);
        Assert.False(tool.Ok);
        var turn = events.Single(e => e.Kind == "turn");
        Assert.Equal(2500, turn.Ms);
        var compaction = events.Single(e => e.Kind == "compaction");
        Assert.Equal(140000, compaction.Before);
        Assert.Equal(38000, compaction.After);
        var delegation = events.Single(e => e.Kind == "delegation");
        Assert.False(delegation.Ok);
        Assert.All(events, e => Assert.Equal(TelemetrySink.RunId, e.Run));

        var summary = TelemetryStore.Summarize(events);
        Assert.Equal(1, summary.Turns);
        Assert.Equal(2500, summary.AvgTurnMs);
        Assert.Equal(1, summary.ToolErrors);
        Assert.Equal(102000, summary.TokensSaved);
    }

    [Fact]
    public void Store_LegacyLines_WithoutNewFields_StillParse()
    {
        // Pre-enrichment JSONL shape (v0.14.0 initial telemetry commit): no agent/tool/ok/tot/run.
        File.WriteAllText(Path.Combine(_dir, $"metrics-{DateTime.UtcNow:yyyyMM}.jsonl"),
            "{\"kind\":\"usage\",\"model\":\"m\",\"in\":10,\"out\":5,\"cached\":0,\"reason\":0,\"cost\":null,\"ts\":\"2026-01-01T00:00:00Z\"}\n"
            + "{\"kind\":\"tool\",\"model\":\"m\",\"ts\":\"2026-01-01T00:00:01Z\"}\n"
            + "{\"kind\":\"delegation\",\"from\":\"A\",\"to\":\"B\",\"ms\":7,\"ts\":\"2026-01-01T00:00:02Z\"}\n");
        var events = TelemetryStore.Load(null);
        Assert.Equal(3, events.Count);
        Assert.True(events.Single(e => e.Kind == "tool").Ok);          // absent ok => success
        Assert.Null(events.Single(e => e.Kind == "usage").Agent);     // absent agent tolerated
        var tools = TelemetryStore.ByTool(events);
        Assert.Equal("(unattributed)", tools.Single().Tool);
        Assert.Equal(0, tools.Single().Errors);
    }

    [Fact]
    public void Store_ByAgent_MergesDelegationsWithUsageAndTurns()
    {
        TelemetrySink.RecordUsageDelta("m", 1000, 200, 0, 0, agent: "CodeAgent");
        TelemetrySink.RecordTurn("CodeAgent", "m", 1500);
        TelemetrySink.RecordDelegation("Lead", "CodeAgent", 400);
        TelemetrySink.RecordDelegation("Lead", "WebAgent", 300);

        var agents = TelemetryStore.ByAgent(TelemetryStore.Load(null));
        var code = agents.Single(a => a.Agent == "CodeAgent");
        Assert.Equal(1200, code.In + code.Out);
        Assert.Equal(1, code.Turns);
        Assert.Equal(1, code.Delegations);
        Assert.Equal(400, code.TotalMs);
        Assert.Equal(0, agents.Single(a => a.Agent == "WebAgent").In);
        Assert.Equal("CodeAgent", agents[0].Agent); // token-weighted ordering
    }

    [Fact]
    public void Store_ByTool_CountsCallsAndErrors()
    {
        TelemetrySink.RecordToolCall("m", agent: "A", tool: "read_file", ok: true);
        TelemetrySink.RecordToolCall("m", agent: "A", tool: "read_file", ok: false);
        TelemetrySink.RecordToolCall("m", agent: "B", tool: "web_search", ok: true);

        var tools = TelemetryStore.ByTool(TelemetryStore.Load(null));
        Assert.Equal("read_file", tools[0].Tool);
        Assert.Equal(2, tools[0].Calls);
        Assert.Equal(1, tools[0].Errors);
        Assert.Equal(0, tools.Single(t => t.Tool == "web_search").Errors);
    }

    [Fact]
    public void UsageTracker_SessionScopedBaselines_EmitDeltas_NotCumulative()
    {
        var sessionA = new object();
        var sessionB = new object();
        // Same model, two concurrent sessions reporting cumulative snapshots.
        Telemetry_RecordCumulative(sessionA, "CodeAgent", "m", 100, 10);
        Telemetry_RecordCumulative(sessionB, "WebAgent", "m", 40, 4);
        Telemetry_RecordCumulative(sessionA, "CodeAgent", "m", 150, 15); // +50/+5
        Telemetry_RecordCumulative(sessionB, "WebAgent", "m", 90, 9);    // +50/+5

        var usage = TelemetryStore.Load(null).Where(e => e.Kind == "usage").ToList();
        Assert.Equal(4, usage.Count);
        Assert.Equal(100 + 40 + 50 + 50, usage.Sum(e => e.In));
        Assert.Equal(10 + 4 + 5 + 5, usage.Sum(e => e.Out));
        var agents = TelemetryStore.ByAgent(usage);
        Assert.Equal(150, agents.Single(a => a.Agent == "CodeAgent").In);
        Assert.Equal(90, agents.Single(a => a.Agent == "WebAgent").In);

        static void Telemetry_RecordCumulative(object scope, string agent, string model, long input, long output)
            => TelemetryUsageTracker.RecordCumulative(scope, agent, model, input, output, 0, 0, input + output);
    }

    [Fact]
    public void Store_PerFileCache_PicksUpAppends()
    {
        TelemetrySink.RecordUsageDelta("m", 10, 1, 0, 0);
        Assert.Single(TelemetryStore.Load(null).Where(e => e.Kind == "usage"));
        TelemetrySink.RecordUsageDelta("m", 20, 2, 0, 0);   // append -> length change -> re-parse
        Assert.Equal(2, TelemetryStore.Load(null).Count(e => e.Kind == "usage"));
    }

    [Fact]
    public void ModelPricing_EstimateFull_DiscountsCacheReads_AndMatchesEstimateWithoutCache()
    {
        double basePrice = Engine.ModelPricing.Estimate("claude-opus-4", 1_000_000, 0)!.Value;
        double fullNoCache = Engine.ModelPricing.EstimateFull("claude-opus-4", 1_000_000, 0, 0)!.Value;
        Assert.Equal(basePrice, fullNoCache, 10);

        double withCache = Engine.ModelPricing.EstimateFull("claude-opus-4", 0, 0, 1_000_000)!.Value;
        Assert.Equal(basePrice * Engine.ModelPricing.CacheReadInputRateFraction, withCache, 10);
        Assert.Null(Engine.ModelPricing.EstimateFull("totally-unknown-model-xyz", 1, 1, 1));
    }

    [Fact]
    public void CollapseToolLines_DefaultsToThree_AtAllLayers()
    {
        Assert.Equal(3, new AppConfig().Console.CollapseToolLines);
    }

    [Fact]
    public void TryParseTokenCount_HandlesRawKAndM()
    {
        Assert.True(Engine.Tui.TuiConfigCommands.TryParseTokenCount("120000", out var raw));
        Assert.Equal(120000, raw);
        Assert.True(Engine.Tui.TuiConfigCommands.TryParseTokenCount("64k", out var k));
        Assert.Equal(64000, k);
        Assert.True(Engine.Tui.TuiConfigCommands.TryParseTokenCount("1.5M", out var m));
        Assert.Equal(1500000, m);
        Assert.False(Engine.Tui.TuiConfigCommands.TryParseTokenCount("max", out _));
        Assert.False(Engine.Tui.TuiConfigCommands.TryParseTokenCount("", out _));
        Assert.False(Engine.Tui.TuiConfigCommands.TryParseTokenCount("-5k", out _));
    }
}
