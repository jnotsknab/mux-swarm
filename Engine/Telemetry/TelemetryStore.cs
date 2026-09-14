using System.Text.Json;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// Read side of the JSONL telemetry sink: parses the monthly metric files and aggregates them
/// into the shapes the TelemetryServer dashboard renders - summary stat tiles, hourly/daily
/// time-series buckets, and per-model / per-agent / per-tool breakdowns. Pure file reads on
/// demand with a per-file length-keyed cache: only files that actually grew are re-parsed
/// (in practice just the current month), so dashboard refreshes never re-read history.
/// </summary>
public static class TelemetryStore
{
    /// <summary>
    /// One parsed telemetry event. Fields absent from older JSONL lines parse to their
    /// defaults (null / 0 / true), so historical files remain fully readable.
    /// </summary>
    public sealed record Event(DateTime Ts, string Kind, string? Model,
        long In, long Out, long Cached, long Reason, double? Cost, string? From, string? To, long Ms,
        string? Agent = null, string? Tool = null, bool Ok = true, long Total = 0,
        long Before = 0, long After = 0, string? Run = null);

    /// <summary>Headline totals for the dashboard stat tiles.</summary>
    public sealed record Summary(long TotalIn, long TotalOut, long TotalCached, long TotalReason,
        double TotalCost, long ToolCalls, long Compactions, long Delegations, DateTime? FirstEvent,
        long Turns = 0, double AvgTurnMs = 0, long ToolErrors = 0, long TokensSaved = 0);

    public sealed record SeriesPoint(string Bucket, long In, long Out, double Cost, long Tools);

    public sealed record ModelRow(string Model, long In, long Out, long Cached, double Cost, long Tools);

    /// <summary>
    /// Per-agent rollup: delegation counts/time (from delegation events) merged with token,
    /// cost, and turn attribution (from usage/turn events that carry an agent name).
    /// </summary>
    public sealed record AgentRow(string Agent, long Delegations, long TotalMs,
        long In = 0, long Out = 0, double Cost = 0, long Turns = 0);

    /// <summary>Per-tool invocation rollup. Events without a tool name group under "(unattributed)".</summary>
    public sealed record ToolRow(string Tool, long Calls, long Errors);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Length, List<Event> Events)> FileCache =
        new(StringComparer.OrdinalIgnoreCase);

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
        lock (Gate)
        {
            // Drop cache entries for files that vanished (external cleanup).
            foreach (var stale in FileCache.Keys.Except(files, StringComparer.OrdinalIgnoreCase).ToList())
                FileCache.Remove(stale);

            var events = new List<Event>();
            foreach (var file in files)
            {
                long length;
                try { length = new FileInfo(file).Length; }
                catch { continue; }
                if (!FileCache.TryGetValue(file, out var cached) || cached.Length != length)
                {
                    cached = (length, ParseFile(file));
                    FileCache[file] = cached;
                }
                events.AddRange(cached.Events);
            }
            return events;
        }
    }

    private static List<Event> ParseFile(string file)
    {
        var events = new List<Event>();
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch { return events; }
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
                    Str(r, "from"), Str(r, "to"), Num(r, "ms"),
                    Agent: Str(r, "agent"), Tool: Str(r, "tool"),
                    Ok: !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False,
                    Total: Num(r, "tot"), Before: Num(r, "before"), After: Num(r, "after"),
                    Run: Str(r, "run")));
            }
            catch { /* skip malformed line */ }
        }
        return events;

        static string? Str(JsonElement r, string name)
            => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static long Num(JsonElement r, string name)
            => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }

    /// <summary>Headline totals for the stat tiles.</summary>
    public static Summary Summarize(IReadOnlyList<Event> events)
    {
        long tin = 0, tout = 0, tcache = 0, treason = 0, tools = 0, toolErrors = 0,
             compactions = 0, delegations = 0, turns = 0, turnMs = 0, saved = 0;
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
                case "tool":
                    tools++;
                    if (!e.Ok) toolErrors++;
                    break;
                case "compaction":
                    compactions++;
                    if (e.Before > e.After) saved += e.Before - e.After;
                    break;
                case "delegation": delegations++; break;
                case "turn": turns++; turnMs += e.Ms; break;
            }
        }
        return new Summary(tin, tout, tcache, treason, cost, tools, compactions, delegations, first,
            Turns: turns, AvgTurnMs: turns > 0 ? (double)turnMs / turns : 0,
            ToolErrors: toolErrors, TokensSaved: saved);
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

    /// <summary>
    /// Per-agent rollup: delegation events contribute counts/time keyed on the target agent;
    /// usage and turn events with agent attribution contribute tokens, cost, and turn counts.
    /// Busiest agents (by tokens, then delegations) first.
    /// </summary>
    public static IReadOnlyList<AgentRow> ByAgent(IReadOnlyList<Event> events)
    {
        var map = new Dictionary<string, (long Count, long Ms, long In, long Out, double Cost, long Turns)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case "delegation" when !string.IsNullOrWhiteSpace(e.To):
                {
                    var cur = map.TryGetValue(e.To!, out var v) ? v : default;
                    cur.Count++; cur.Ms += e.Ms;
                    map[e.To!] = cur;
                    break;
                }
                case "usage" when !string.IsNullOrWhiteSpace(e.Agent):
                {
                    var cur = map.TryGetValue(e.Agent!, out var v) ? v : default;
                    cur.In += e.In; cur.Out += e.Out; cur.Cost += e.Cost ?? 0;
                    map[e.Agent!] = cur;
                    break;
                }
                case "turn" when !string.IsNullOrWhiteSpace(e.Agent):
                {
                    var cur = map.TryGetValue(e.Agent!, out var v) ? v : default;
                    cur.Turns++;
                    map[e.Agent!] = cur;
                    break;
                }
            }
        }
        return map.Select(kv => new AgentRow(kv.Key, kv.Value.Count, kv.Value.Ms,
                In: kv.Value.In, Out: kv.Value.Out, Cost: kv.Value.Cost, Turns: kv.Value.Turns))
            .OrderByDescending(r => r.In + r.Out).ThenByDescending(r => r.Delegations).ToList();
    }

    /// <summary>Per-tool invocation totals, most called first.</summary>
    public static IReadOnlyList<ToolRow> ByTool(IReadOnlyList<Event> events)
    {
        var map = new Dictionary<string, (long Calls, long Errors)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            if (e.Kind != "tool") continue;
            string t = string.IsNullOrWhiteSpace(e.Tool) ? "(unattributed)" : e.Tool!;
            var cur = map.TryGetValue(t, out var v) ? v : (0, 0);
            map[t] = (cur.Calls + 1, cur.Errors + (e.Ok ? 0 : 1));
        }
        return map.Select(kv => new ToolRow(kv.Key, kv.Value.Calls, kv.Value.Errors))
            .OrderByDescending(r => r.Calls).ToList();
    }
}
