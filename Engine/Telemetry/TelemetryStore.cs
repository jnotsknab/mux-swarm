using System.Text.Json;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// Read side of the JSONL telemetry sink: parses the monthly metric files and aggregates them
/// into the shapes the TelemetryServer dashboard renders - summary stat tiles, hourly/daily
/// time-series buckets, and per-model / per-agent breakdowns. Pure file reads on demand; a
/// small mtime-keyed cache keeps repeated dashboard refreshes cheap without a watcher.
/// </summary>
public static class TelemetryStore
{
    public sealed record Event(DateTime Ts, string Kind, string? Model,
        long In, long Out, long Cached, long Reason, double? Cost, string? From, string? To, long Ms);

    public sealed record Summary(long TotalIn, long TotalOut, long TotalCached, long TotalReason,
        double TotalCost, long ToolCalls, long Compactions, long Delegations, DateTime? FirstEvent);

    public sealed record SeriesPoint(string Bucket, long In, long Out, double Cost, long Tools);

    public sealed record ModelRow(string Model, long In, long Out, long Cached, double Cost, long Tools);

    public sealed record AgentRow(string Agent, long Delegations, long TotalMs);

    private static readonly object Gate = new();
    private static List<Event>? _cache;
    private static string _cacheKey = "";

    /// <summary>Load all events since <paramref name="sinceUtc"/> (null = all time), newest last.</summary>
    public static IReadOnlyList<Event> Load(DateTime? sinceUtc)
    {
        var all = LoadAll();
        if (sinceUtc is not { } cut) return all;
        return all.Where(e => e.Ts >= cut).ToList();
    }

    private static IReadOnlyList<Event> LoadAll()
    {
        var dir = TelemetrySink.Directory;
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<Event>();
        var files = Directory.GetFiles(dir, "metrics-*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        string key = string.Join("|", files.Select(f => f + ":" + new FileInfo(f).Length));
        lock (Gate)
        {
            if (_cache is not null && _cacheKey == key) return _cache;
        }
        var events = new List<Event>();
        foreach (var file in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("ts", out var tsEl) || !DateTime.TryParse(tsEl.GetString(),
                            null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                            out var when)) continue;
                    string kind = r.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                    events.Add(new Event(when, kind,
                        Str(r, "model"), Num(r, "in"), Num(r, "out"), Num(r, "cached"), Num(r, "reason"),
                        r.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
                        Str(r, "from"), Str(r, "to"), Num(r, "ms")));
                }
                catch { /* skip malformed line */ }
            }
        }
        lock (Gate) { _cache = events; _cacheKey = key; }
        return events;

        static string? Str(JsonElement r, string name)
            => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static long Num(JsonElement r, string name)
            => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }

    /// <summary>Headline totals for the stat tiles.</summary>
    public static Summary Summarize(IReadOnlyList<Event> events)
    {
        long tin = 0, tout = 0, tcache = 0, treason = 0, tools = 0, compactions = 0, delegations = 0;
        double cost = 0;
        DateTime? first = null;
        foreach (var e in events)
        {
            first ??= e.Ts;
            switch (e.Kind)
            {
                case "usage":
                    tin += e.In; tout += e.Out; tcache += e.Cached; treason += e.Reason;
                    cost += e.Cost ?? 0;
                    break;
                case "tool": tools++; break;
                case "compaction": compactions++; break;
                case "delegation": delegations++; break;
            }
        }
        return new Summary(tin, tout, tcache, treason, cost, tools, compactions, delegations, first);
    }

    /// <summary>Bucketed time series (hourly for ranges up to 3 days, else daily), oldest first.</summary>
    public static IReadOnlyList<SeriesPoint> Series(IReadOnlyList<Event> events, DateTime? sinceUtc)
    {
        bool hourly = sinceUtc is { } s && (DateTime.UtcNow - s) <= TimeSpan.FromDays(3);
        string Fmt(DateTime t) => hourly ? t.ToString("yyyy-MM-dd HH:00") : t.ToString("yyyy-MM-dd");
        var buckets = new SortedDictionary<string, (long In, long Out, double Cost, long Tools)>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            string b = Fmt(e.Ts);
            var cur = buckets.TryGetValue(b, out var v) ? v : (0, 0, 0.0, 0);
            if (e.Kind == "usage") { cur.Item1 += e.In; cur.Item2 += e.Out; cur.Item3 += e.Cost ?? 0; }
            else if (e.Kind == "tool") cur.Item4++;
            buckets[b] = cur;
        }
        return buckets.Select(kv => new SeriesPoint(kv.Key, kv.Value.In, kv.Value.Out, kv.Value.Cost, kv.Value.Tools)).ToList();
    }

    /// <summary>Per-model totals, largest total tokens first.</summary>
    public static IReadOnlyList<ModelRow> ByModel(IReadOnlyList<Event> events)
    {
        var map = new Dictionary<string, (long In, long Out, long Cached, double Cost, long Tools)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            string m = string.IsNullOrWhiteSpace(e.Model) ? "(unknown)" : e.Model!;
            var cur = map.TryGetValue(m, out var v) ? v : (0, 0, 0, 0.0, 0);
            if (e.Kind == "usage") { cur.Item1 += e.In; cur.Item2 += e.Out; cur.Item3 += e.Cached; cur.Item4 += e.Cost ?? 0; }
            else if (e.Kind == "tool") cur.Item5++;
            map[m] = cur;
        }
        return map.Select(kv => new ModelRow(kv.Key, kv.Value.In, kv.Value.Out, kv.Value.Cached, kv.Value.Cost, kv.Value.Tools))
            .OrderByDescending(r => r.In + r.Out).ToList();
    }

    /// <summary>Per-target delegation totals, most delegated-to first.</summary>
    public static IReadOnlyList<AgentRow> ByAgent(IReadOnlyList<Event> events)
    {
        var map = new Dictionary<string, (long Count, long Ms)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            if (e.Kind != "delegation" || string.IsNullOrWhiteSpace(e.To)) continue;
            var cur = map.TryGetValue(e.To!, out var v) ? v : (0, 0);
            map[e.To!] = (cur.Count + 1, cur.Ms + e.Ms);
        }
        return map.Select(kv => new AgentRow(kv.Key, kv.Value.Count, kv.Value.Ms))
            .OrderByDescending(r => r.Delegations).ToList();
    }
}
