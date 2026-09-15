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
        using (var a = OtelTracer.GetSource().StartActivity("tool_call"))
            a?.SetTag("agent", "HttpProbeAgent");
        OtelLogger.Warn("http probe warn");

        using var metrics = System.Text.Json.JsonDocument.Parse(await _client.GetStringAsync("/api/telemetry/otel/metrics"));
        var instruments = metrics.RootElement.GetProperty("instruments");
        Assert.True(instruments.GetArrayLength() >= 31);
        Assert.Contains(instruments.EnumerateArray(),
            i => i.GetProperty("name").GetString() == "mux.tool.calls" && i.GetProperty("recorded").GetBoolean());

        using var traces = System.Text.Json.JsonDocument.Parse(
            await _client.GetStringAsync("/api/telemetry/otel/traces?name=tool_call&agent=HttpProbeAgent"));
        Assert.True(traces.RootElement.GetProperty("total").GetInt32() >= 1);
        Assert.Contains(traces.RootElement.GetProperty("spans").EnumerateArray(),
            s => s.GetProperty("name").GetString() == "tool_call");

        using var logs = System.Text.Json.JsonDocument.Parse(
            await _client.GetStringAsync("/api/telemetry/otel/logs?level=warn"));
        Assert.Contains(logs.RootElement.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("message").GetString()!.Contains("http probe warn"));
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
