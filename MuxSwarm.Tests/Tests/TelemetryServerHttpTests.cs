using MuxSwarm.Engine;
using MuxSwarm.Engine.Telemetry;

namespace MuxSwarm.Tests.Tests;

// End-to-end over the real Kestrel host: the dashboard HTML carries the four tabs and the
// OTel routes serve ingested metrics/traces/logs JSON alongside the persistent-sink routes.
[Collection("TelemetryState")]
public class TelemetryServerHttpTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-telemetry-http-" + Guid.NewGuid().ToString("N"));
    private int _port;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        TelemetrySink.OverrideDirectoryForTests(_dir);
        TraceStore.OverrideDirectoryForTests(_dir);
        OtelIngest.Start();
        OtelIngest.ResetForTests();

        // Random high port with retries to dodge collisions on busy dev boxes.
        var rng = new Random();
        for (int attempt = 0; ; attempt++)
        {
            _port = rng.Next(36000, 46000);
            try { await TelemetryServer.StartAsync(_port); break; }
            catch when (attempt < 3) { }
        }
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await TelemetryServer.StopAsync();
        TelemetrySink.OverrideDirectoryForTests(null);
        TraceStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Dashboard_ServesTabbedHtml()
    {
        string html = await _client.GetStringAsync("/");
        Assert.Contains("tab-overview", html);
        Assert.Contains("tab-metrics", html);
        Assert.Contains("tab-traces", html);
        Assert.Contains("tab-logs", html);
        Assert.Contains("renderMetrics", html);
        Assert.Contains("renderTraces", html);
        Assert.Contains("renderLogs", html);
        Assert.Contains("/api/telemetry/otel/metrics", html);
    }

    [Fact]
    public async Task OtelRoutes_ServeIngestedData()
    {
        OtelMetrics.ToolCalls.Add(1,
            new KeyValuePair<string, object?>("agent", "HttpProbeAgent"),
            new KeyValuePair<string, object?>("tool", "http_probe"));
        string traceId;
        using (var a = OtelTracer.GetSource().StartActivity("tool_call"))
        {
            Assert.NotNull(a);
            traceId = a!.TraceId.ToString();
            a.SetTag("agent", "HttpProbeAgent");
            a.SetTag("args", "{\"q\":42}");
            OtelIngest.RecordResponse("HttpProbeAgent", "probe response body");
        }
        OtelLogger.Warn("http probe warn");

        using var metrics = System.Text.Json.JsonDocument.Parse(await _client.GetStringAsync("/api/telemetry/otel/metrics"));
        var instruments = metrics.RootElement.GetProperty("instruments");
        Assert.True(instruments.GetArrayLength() >= 31);
        Assert.Contains(instruments.EnumerateArray(),
            i => i.GetProperty("name").GetString() == "mux.tool.calls" && i.GetProperty("recorded").GetBoolean());

        using var traces = System.Text.Json.JsonDocument.Parse(
            await _client.GetStringAsync("/api/telemetry/otel/traces?name=tool_call&agent=HttpProbeAgent"));
        Assert.Contains(traces.RootElement.EnumerateArray(),
            t => t.GetProperty("traceId").GetString() == traceId && t.GetProperty("spans").GetInt32() >= 1);

        using var detail = System.Text.Json.JsonDocument.Parse(
            await _client.GetStringAsync("/api/telemetry/otel/trace?id=" + traceId));
        Assert.Contains(detail.RootElement.GetProperty("spans").EnumerateArray(),
            s => s.GetProperty("name").GetString() == "tool_call"
                 && s.GetProperty("tags").GetProperty("args").GetString() == "{\"q\":42}");
        Assert.Contains(detail.RootElement.GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("text").GetString() == "probe response body");

        var missing = await _client.GetAsync("/api/telemetry/otel/trace?id=00000000000000000000000000000000");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        using var logs = System.Text.Json.JsonDocument.Parse(
            await _client.GetStringAsync("/api/telemetry/otel/logs?level=warn"));
        Assert.Contains(logs.RootElement.EnumerateArray(),
            e => e.GetProperty("text").GetString()!.Contains("http probe warn"));
    }

    [Fact]
    public async Task SinkRoutes_StillServeAlongsideOtelRoutes()
    {
        TelemetrySink.RecordUsageDelta("http-model", 100, 20, 0, 0, agent: "HttpProbeAgent");
        using var summary = System.Text.Json.JsonDocument.Parse(
            await _client.GetStringAsync("/api/telemetry/summary"));
        Assert.True(summary.RootElement.GetProperty("totalIn").GetInt64() >= 100);
    }
}
