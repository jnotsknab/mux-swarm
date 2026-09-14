using System.Text.Json;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// Persistent, zero-dependency telemetry sink: append-only JSONL files (one per month) under
/// <c>&lt;install&gt;/Telemetry/</c>. Mirrors the same instrumentation points as
/// <see cref="OtelMetrics"/>/<see cref="CostLedger"/> but SURVIVES the process, giving the
/// telemetry dashboard true all-time queries without Loki/Prometheus. Events are tiny
/// (usage deltas, tool calls, turns, compactions, delegations) and writes are lock-serialized
/// appends - O(1) per event, no reader ever blocks the hot path. Every event carries a
/// process-scoped run id so the dashboard can group work into sessions. Disabled automatically
/// if the directory cannot be created (telemetry must never break a session).
/// </summary>
public static class TelemetrySink
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static bool _failed;

    /// <summary>
    /// Process-scoped run id stamped onto every event (start time + random suffix). Groups
    /// events into "sessions" on the dashboard without threading a session key through
    /// every orchestrator.
    /// </summary>
    public static readonly string RunId =
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];

    /// <summary>Telemetry directory (created on first write). Null once marked failed.</summary>
    public static string? Directory
    {
        get
        {
            if (_failed) return null;
            if (_dir is not null) return _dir;
            lock (Gate)
            {
                if (_failed || _dir is not null) return _dir;
                try
                {
                    var d = Path.Combine(PlatformContext.BaseDirectory, "Telemetry");
                    System.IO.Directory.CreateDirectory(d);
                    _dir = d;
                }
                catch { _failed = true; }
                return _dir;
            }
        }
    }

    /// <summary>Test hook: point the sink at a scratch directory (null resets to default).</summary>
    internal static void OverrideDirectoryForTests(string? dir)
    {
        lock (Gate) { _dir = dir; _failed = false; }
    }

    /// <summary>
    /// Record a per-checkpoint token DELTA (never cumulative) with an optional cost estimate.
    /// <paramref name="agent"/> attributes the tokens to the emitting agent; <paramref name="total"/>
    /// is the provider-reported total delta (may exceed the sum of the split buckets).
    /// </summary>
    public static void RecordUsageDelta(string model, long input, long output, long cached, long reasoning,
        string? agent = null, long total = 0)
    {
        if (input <= 0 && output <= 0 && cached <= 0 && reasoning <= 0) return;
        double? cost = ModelPricing.EstimateFull(model, Math.Max(0, input), Math.Max(0, output), Math.Max(0, cached));
        Write(new Dictionary<string, object?>
        {
            ["kind"] = "usage", ["model"] = model,
            ["in"] = input, ["out"] = output, ["cached"] = cached, ["reason"] = reasoning,
            ["cost"] = cost, ["agent"] = agent, ["tot"] = total,
        });
    }

    /// <summary>
    /// Record one completed tool invocation: the active model, the invoking agent, the tool
    /// name, wall-clock duration, and whether it succeeded.
    /// </summary>
    public static void RecordToolCall(string model, string? agent = null, string? tool = null,
        long ms = 0, bool ok = true)
        => Write(new Dictionary<string, object?>
        {
            ["kind"] = "tool", ["model"] = model, ["agent"] = agent, ["tool"] = tool,
            ["ms"] = ms, ["ok"] = ok,
        });

    /// <summary>Record one completed agent turn with its wall-clock duration.</summary>
    public static void RecordTurn(string agent, string model, long ms)
        => Write(new Dictionary<string, object?>
        { ["kind"] = "turn", ["agent"] = agent, ["model"] = model, ["ms"] = ms });

    /// <summary>Record one context compaction run with before/after token counts when known.</summary>
    public static void RecordCompaction(string model, long beforeTokens = 0, long afterTokens = 0)
        => Write(new Dictionary<string, object?>
        {
            ["kind"] = "compaction", ["model"] = model,
            ["before"] = beforeTokens, ["after"] = afterTokens,
        });

    /// <summary>
    /// Record one delegation (lead -> specialist) with its wall-clock duration and whether the
    /// specialist reported success.
    /// </summary>
    public static void RecordDelegation(string from, string to, long durationMs, bool ok = true)
        => Write(new Dictionary<string, object?>
        { ["kind"] = "delegation", ["from"] = from, ["to"] = to, ["ms"] = durationMs, ["ok"] = ok });

    private static void Write(Dictionary<string, object?> fields)
    {
        var dir = Directory;
        if (dir is null) return;
        try
        {
            fields["ts"] = DateTime.UtcNow.ToString("o");
            fields["run"] = RunId;
            string line = JsonSerializer.Serialize(fields) + "\n";
            string file = Path.Combine(dir, $"metrics-{DateTime.UtcNow:yyyyMM}.jsonl");
            lock (Gate) File.AppendAllText(file, line);
        }
        catch { /* telemetry must never break a session */ }
    }
}
