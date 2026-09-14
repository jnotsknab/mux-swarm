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
