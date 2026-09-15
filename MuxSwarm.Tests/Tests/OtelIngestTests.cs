using MuxSwarm.Engine;
using MuxSwarm.Engine.Telemetry;

namespace MuxSwarm.Tests.Tests;

// In-proc OTel ingestion for the dashboard tabs: meter measurements aggregate into instrument
// snapshots (totals, tags, histogram stats), completed "MuxSwarm" spans land in the trace ring,
// OtelLogger entries tee into the log ring, and snapshot filters behave. Assertions are
// tolerant (>=, Contains with unique markers) because the ingest state is process-global and
// other test classes may record measurements concurrently.
[Collection("TelemetryState")]
public class OtelIngestTests
{
    public OtelIngestTests()
    {
        OtelIngest.Start();
        OtelIngest.ResetForTests();
    }

    [Fact]
    public void Metrics_CounterMeasurements_AggregateTotalsAndTags()
    {
        OtelMetrics.ToolCalls.Add(3,
            new KeyValuePair<string, object?>("agent", "IngestTestAgent"),
            new KeyValuePair<string, object?>("tool", "ingest_probe"));

        var snap = OtelIngest.MetricsSnapshot();
        var inst = snap.Instruments.Single(i => i.Name == "mux.tool.calls");
        Assert.Equal("counter", inst.Kind);
        Assert.True(inst.Recorded);
        Assert.True(inst.Total >= 3);
        Assert.Contains(inst.Tags, t => t.Key.Contains("IngestTestAgent") && t.Key.Contains("ingest_probe"));
    }

    [Fact]
    public void Metrics_FullCatalogPublished_IncludingUnrecordedInstruments()
    {
        var snap = OtelIngest.MetricsSnapshot();
        Assert.True(snap.Instruments.Count >= 31);
        // Declared-but-never-recorded instruments still appear (dashboard greys them out).
        Assert.Contains(snap.Instruments, i => i.Name == "mux.memory.reads");
        Assert.Contains(snap.Instruments, i => i.Name == "mux.daemon.triggers_fired");
        Assert.Equal("histogram", snap.Instruments.Single(i => i.Name == "mux.agent.turn_duration_ms").Kind);
    }

    [Fact]
    public void Metrics_HistogramStats_CountMinMaxPercentiles()
    {
        for (int i = 1; i <= 100; i++)
            OtelMetrics.CompactionDuration.Record(i);

        var snap = OtelIngest.MetricsSnapshot();
        var inst = snap.Instruments.Single(i => i.Name == "mux.compaction.duration_ms");
        Assert.Equal("histogram", inst.Kind);
        Assert.True(inst.Count >= 100);
        Assert.True(inst.Max >= 100);
        Assert.True(inst.P50 > 0);
        Assert.True(inst.P95 >= inst.P50);
    }

    [Fact]
    public void Traces_CompletedSpans_Captured_WithNameAndAgentFilters()
    {
        using (var a = OtelTracer.GetSource().StartActivity("tool_call"))
        {
            a?.SetTag("agent", "IngestTraceAgent");
            a?.SetTag("tool", "trace_probe");
        }

        var snap = OtelIngest.TracesSnapshot(limit: 50, name: "tool_call", agent: "IngestTraceAgent");
        Assert.True(snap.Total >= 1);
        var span = snap.Spans.First();
        Assert.Equal("tool_call", span.Name);
        Assert.Equal("IngestTraceAgent", span.Tags["agent"]);
        Assert.Equal("trace_probe", span.Tags["tool"]);
        Assert.False(string.IsNullOrEmpty(span.TraceId));
    }

    [Fact]
    public void Logs_TeeFromOtelLogger_CapturesLevels_AndLevelFilter()
    {
        OtelLogger.Info("ingest info probe");
        OtelLogger.Warn("ingest warn probe");
        OtelLogger.Error("ingest error probe");

        var all = OtelIngest.LogsSnapshot(50);
        Assert.True(all.Total >= 3);
        Assert.Contains(all.Entries, e => e.Level == "info" && e.Message.Contains("ingest info probe"));

        var errs = OtelIngest.LogsSnapshot(50, level: "error");
        Assert.Contains(errs.Entries, e => e.Message.Contains("ingest error probe"));
        Assert.All(errs.Entries, e => Assert.Equal("error", e.Level));
    }
}
