using System.Text.Json;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// Persistent, zero-dependency telemetry sink: append-only JSONL files (one per month) under
/// <c>&lt;install&gt;/Telemetry/</c>. Mirrors the same instrumentation points as
/// <see cref="OtelMetrics"/>/<see cref="CostLedger"/> but SURVIVES the process, giving the
/// telemetry dashboard true all-time queries without Loki/Prometheus. Events are tiny
/// (usage deltas, tool calls, compactions, delegations) and writes are lock-serialized
/// appends - O(1) per event, no reader ever blocks the hot path. Disabled automatically if
/// the directory cannot be created (telemetry must never break a session).
/// </summary>
public static class TelemetrySink
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static bool _failed;

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

    /// <summary>Record a per-checkpoint token DELTA (never cumulative) with an optional cost estimate.</summary>
    public static void RecordUsageDelta(string model, long input, long output, long cached, long reasoning)
    {
        if (input <= 0 && output <= 0 && cached <= 0 && reasoning <= 0) return;
        double? cost = ModelPricing.Estimate(model, Math.Max(0, input), Math.Max(0, output));
        Write(new Dictionary<string, object?>
        {
            ["kind"] = "usage", ["model"] = model,
            ["in"] = input, ["out"] = output, ["cached"] = cached, ["reason"] = reasoning,
            ["cost"] = cost,
        });
    }

    /// <summary>Record one tool invocation attributed to the active model.</summary>
    public static void RecordToolCall(string model)
        => Write(new Dictionary<string, object?> { ["kind"] = "tool", ["model"] = model });

    /// <summary>Record one context compaction run for the active model.</summary>
    public static void RecordCompaction(string model)
        => Write(new Dictionary<string, object?> { ["kind"] = "compaction", ["model"] = model });

    /// <summary>Record one delegation (lead -> specialist) with its wall-clock duration.</summary>
    public static void RecordDelegation(string from, string to, long durationMs)
        => Write(new Dictionary<string, object?>
        { ["kind"] = "delegation", ["from"] = from, ["to"] = to, ["ms"] = durationMs });

    private static void Write(Dictionary<string, object?> fields)
    {
        var dir = Directory;
        if (dir is null) return;
        try
        {
            fields["ts"] = DateTime.UtcNow.ToString("o");
            string line = JsonSerializer.Serialize(fields) + "\n";
            string file = Path.Combine(dir, $"metrics-{DateTime.UtcNow:yyyyMM}.jsonl");
            lock (Gate) File.AppendAllText(file, line);
        }
        catch { /* telemetry must never break a session */ }
    }
}
