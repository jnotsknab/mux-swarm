using MuxSwarm.Engine;
using MuxSwarm.Engine.Telemetry;

namespace MuxSwarm.Tests.Tests;

// In-proc OTel ingestion: meter measurements aggregate into instrument snapshots (in-memory,
// fixed-size), while completed "MuxSwarm" spans (with events), turn responses, and OtelLogger
// entries persist through TraceStore (per-day JSONL, synchronous under the test override).
// Metric assertions are tolerant (>=, Contains) because ingest state is process-global.
[Collection("TelemetryState")]
public class OtelIngestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-otel-ingest-" + Guid.NewGuid().ToString("N"));

    public OtelIngestTests()
    {
        Directory.CreateDirectory(_dir);
        TraceStore.OverrideDirectoryForTests(_dir);
        OtelIngest.Start();
        OtelIngest.ResetForTests();
    }

    public void Dispose()
    {
        TraceStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
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
    public void Spans_PersistWithEventsAndPayloadTags()
    {
        string traceId;
        using (var a = OtelTracer.GetSource().StartActivity("tool_call"))
        {
            Assert.NotNull(a);
            traceId = a!.TraceId.ToString();
            a.SetTag("agent", "IngestTraceAgent");
            a.SetTag("tool", "trace_probe");
            a.SetTag("args", new string('x', 3000)); // payload key: 2048 cap, not 256
            a.AddEvent(new System.Diagnostics.ActivityEvent("message",
                tags: new System.Diagnostics.ActivityTagsCollection { { "content", "hello from event" } }));
        }

        var detail = TraceStore.GetTrace(traceId);
        Assert.NotNull(detail);
        var span = detail!.Spans.Single();
        Assert.Equal("tool_call", span.Name);
        Assert.Equal("IngestTraceAgent", span.Tags!["agent"]);
        Assert.True(span.Tags["args"].Length > 300, "payload tag should use the 2048 cap");
        var ev = Assert.Single(span.Events!);
        Assert.Equal("message", ev.Name);
        Assert.Equal("hello from event", ev.Tags!["content"]);
    }

    [Fact]
    public void Responses_TeeIntoStore_AttributedToActiveTrace()
    {
        string traceId;
        using (var a = OtelTracer.GetSource().StartActivity("agent_turn"))
        {
            Assert.NotNull(a);
            traceId = a!.TraceId.ToString();
            OtelIngest.RecordResponse("IngestRespAgent", "the final answer");
        }

        var detail = TraceStore.GetTrace(traceId);
        Assert.NotNull(detail);
        var msg = Assert.Single(detail!.Messages);
        Assert.Equal("IngestRespAgent", msg.Agent);
        Assert.Equal("the final answer", msg.Text);
    }

    [Fact]
    public void Logs_TeeFromOtelLogger_PersistWithLevelFilter()
    {
        OtelLogger.Info("ingest info probe");
        OtelLogger.Warn("ingest warn probe");
        OtelLogger.Error("ingest error probe");

        var all = TraceStore.ListLogs(50);
        Assert.Contains(all, e => e.Level == "info" && e.Text!.Contains("ingest info probe"));

        var errs = TraceStore.ListLogs(50, level: "error");
        Assert.Contains(errs, e => e.Text!.Contains("ingest error probe"));
        Assert.All(errs, e => Assert.Equal("error", e.Level));
    }

    [Fact]
    public void ListTraces_GroupsSpansPerTrace_WithRootAndAgents()
    {
        string traceId;
        using (var root = OtelTracer.GetSource().StartActivity("agent_session"))
        {
            Assert.NotNull(root);
            traceId = root!.TraceId.ToString();
            root.SetTag("agent", "ListAgent");
            using (var child = OtelTracer.GetSource().StartActivity("tool_call"))
                child?.SetTag("agent", "ListAgent");
        }

        var traces = TraceStore.ListTraces(limit: 20, days: 2);
        var mine = traces.Single(t => t.TraceId == traceId);
        Assert.Equal("agent_session", mine.Root);
        Assert.Equal(2, mine.Spans);
        Assert.Contains("ListAgent", mine.Agents);
        Assert.False(mine.HasError);
    }
}
