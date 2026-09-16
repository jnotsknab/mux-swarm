using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// Standalone Kestrel dashboard for the built-in telemetry stack (/telemetry, --telemetry).
/// Four tabs: Overview (persistent JSONL sink via <see cref="TelemetryStore"/> - stat tiles,
/// canvas time-series charts, per-model / per-agent / per-tool tables), plus Metrics, Traces,
/// and Logs served live from <see cref="OtelIngest"/> - the full in-process OTel surface
/// (every "MuxSwarm" meter instrument with tag breakdowns and rates, recent spans, recent
/// log entries) without needing an external OTLP collector. Themed to match the main web UI
/// (same Zinc palette, fonts, theme presets, and settings-panel conventions) so the two
/// surfaces feel like one product. Zero external dependencies: one static HTML document at
/// <c>Runtime/mux-telemetry/index.html</c> (no chart library, no npm), editable without a
/// rebuild like the web UI's <c>Runtime/mux-web-app</c>. Binds loopback by default via
/// <c>serve.address</c> discipline; runs on its own port (default 6725) so it can live
/// alongside --serve.
/// </summary>
public static class TelemetryServer
{
    /// <summary>Default dashboard port (web UI default + 2, leaving 6724 for test instances).</summary>
    public const int DefaultPort = 6725;

    private static WebApplication? _app;

    /// <summary>True while a dashboard instance is listening (used by /telemetry toggle text).</summary>
    public static bool IsRunning => _app is not null;

    /// <summary>Start the dashboard on <paramref name="port"/>; no-op when already running.</summary>
    public static async Task<string> StartAsync(int port = DefaultPort)
    {
        if (_app is not null) return $"Telemetry dashboard already running on port {port}.";
        var builder = WebApplication.CreateSlimBuilder();
        string address = App.Config?.ServeAddress ?? "127.0.0.1";
        builder.WebHost.UseUrls($"http://{address}:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/", (HttpContext ctx) =>
        {
            string? dir = ResolveDashboardRoot();
            string? index = dir is null ? null : Path.Combine(dir, "index.html");
            if (index is null || !File.Exists(index))
                return Results.Content("Telemetry dashboard asset missing: Runtime/mux-telemetry/index.html", "text/plain", statusCode: 500);
            // Never cache the shell: an install swap must repaint stale browsers on next refresh
            // (same discipline as the main web UI's SPA shell in ServeMode).
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            return Results.File(index, "text/html", enableRangeProcessing: false);
        });
        app.MapGet("/api/telemetry/summary", (string? range) =>
        {
            var events = TelemetryStore.Load(ParseRange(range));
            return Results.Json(TelemetryStore.Summarize(events));
        });
        app.MapGet("/api/telemetry/series", (string? range) =>
        {
            var since = ParseRange(range);
            var events = TelemetryStore.Load(since);
            return Results.Json(TelemetryStore.Series(events, since));
        });
        app.MapGet("/api/telemetry/models", (string? range) =>
        {
            var events = TelemetryStore.Load(ParseRange(range));
            return Results.Json(new
            {
                models = TelemetryStore.ByModel(events),
                agents = TelemetryStore.ByAgent(events),
            });
        });
        app.MapGet("/api/telemetry/tools", (string? range) =>
        {
            var events = TelemetryStore.Load(ParseRange(range));
            return Results.Json(TelemetryStore.ByTool(events));
        });
        // Live OTel plane: metric aggregates from in-proc ingestion; traces/logs from the
        // durable TraceStore (per-day JSONL, cumulative retention).
        app.MapGet("/api/telemetry/otel/metrics", () => Results.Json(OtelIngest.MetricsSnapshot()));
        app.MapGet("/api/telemetry/otel/traces", (int? limit, int? days, string? name, string? agent) =>
            Results.Json(TraceStore.ListTraces(limit ?? 50, days ?? 7, name, agent)));
        app.MapGet("/api/telemetry/otel/trace", (string? id) =>
        {
            var detail = string.IsNullOrWhiteSpace(id) ? null : TraceStore.GetTrace(id);
            return detail is null ? Results.NotFound() : Results.Json(detail);
        });
        app.MapGet("/api/telemetry/otel/logs", (int? limit, string? level, int? days) =>
            Results.Json(TraceStore.ListLogs(limit ?? 200, level, days ?? 7)));

        await app.StartAsync();
        _app = app;
        return $"Telemetry dashboard: http://{(address == "0.0.0.0" ? "localhost" : address)}:{port}/";
    }

    /// <summary>Stop the dashboard (used by /telemetry off and shutdown).</summary>
    public static async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is not null)
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await app.StopAsync(stopCts.Token); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Locate the dashboard asset directory <c>Runtime/mux-telemetry</c> (same install-relative
    /// resolution as the web UI's <c>Runtime/mux-web-app</c>): install dir first, then the
    /// process base dir for dev runs from the build output tree.
    /// </summary>
    internal static string? ResolveDashboardRoot()
    {
        var runtimeDir = Path.Combine(PlatformContext.BaseDirectory, "Runtime", "mux-telemetry");
        if (Directory.Exists(runtimeDir)) return Path.GetFullPath(runtimeDir);

        var binDir = Path.Combine(AppContext.BaseDirectory, "Runtime", "mux-telemetry");
        if (Directory.Exists(binDir)) return Path.GetFullPath(binDir);

        return null;
    }

    /// <summary>Parse the dashboard range parameter: 24h / 7d / 30d; null or "all" = all time.</summary>
    internal static DateTime? ParseRange(string? range) => (range ?? "").Trim().ToLowerInvariant() switch
    {
        "24h" => DateTime.UtcNow.AddHours(-24),
        "7d" => DateTime.UtcNow.AddDays(-7),
        "30d" => DateTime.UtcNow.AddDays(-30),
        _ => null,
    };
}
