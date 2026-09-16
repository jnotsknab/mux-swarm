using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// Durable store for the trace-detail plane (spans with events, agent responses, log entries)
/// behind a minimal storage seam so the backend can be swapped later without touching
/// producers or the dashboard. Default implementation: per-day JSONL files
/// (<c>traces-yyyyMMdd.jsonl</c>) in the same <c>Telemetry/</c> directory as the metrics
/// sink, cumulative retention (nothing is pruned). Writes are pre-serialized on the hot path
/// and appended by a background batch writer over a bounded channel - producers never block
/// on disk; under sustained overflow the oldest queued lines are dropped.
/// </summary>
public static class TraceStore
{
    private const int QueueCapacity = 8192;
    /// <summary>Max chars persisted for a response/message body.</summary>
    public const int MaxMessageLength = 4096;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Gate = new();
    private static string? _dirOverride;
    private static bool _failed;
    private static Channel<string>? _channel;
    private static Task? _writer;
    private static bool _syncForTests;

    /// <summary>Directory holding the per-day trace files (shared with the metrics sink).</summary>
    public static string Directory =>
        _dirOverride ?? Path.Combine(PlatformContext.BaseDirectory, "Telemetry");

    // ---- record shapes (one JSON object per line) -----------------------------------------

    /// <summary>One captured span event (name + timestamp + selected tags).</summary>
    public sealed record SpanEvent(
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("ts")] DateTime TsUtc,
        [property: JsonPropertyName("tags")] Dictionary<string, string>? Tags);

    /// <summary>One persisted record: a completed span, an agent message, or a log entry.</summary>
    public sealed record Rec(
        [property: JsonPropertyName("k")] string Kind,          // span | msg | log
        [property: JsonPropertyName("ts")] DateTime TsUtc,
        [property: JsonPropertyName("trace")] string? TraceId,
        [property: JsonPropertyName("span")] string? SpanId,
        [property: JsonPropertyName("parent")] string? ParentSpanId = null,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("ms")] double? DurationMs = null,
        [property: JsonPropertyName("status")] string? Status = null,
        [property: JsonPropertyName("desc")] string? StatusDescription = null,
        [property: JsonPropertyName("tags")] Dictionary<string, string>? Tags = null,
        [property: JsonPropertyName("events")] List<SpanEvent>? Events = null,
        [property: JsonPropertyName("agent")] string? Agent = null,
        [property: JsonPropertyName("level")] string? Level = null,
        [property: JsonPropertyName("text")] string? Text = null);

    // ---- write path -----------------------------------------------------------------------

    /// <summary>Persist a completed span (with its captured events).</summary>
    public static void WriteSpan(Rec rec) => Enqueue(rec);

    /// <summary>Persist an agent response/message attributed to the active trace.</summary>
    public static void WriteMessage(string agent, string text, string? traceId, string? spanId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Enqueue(new Rec("msg", DateTime.UtcNow, traceId, spanId, Agent: agent,
            Text: Truncate(text, MaxMessageLength)));
    }

    /// <summary>Persist a log entry attributed to the active trace (when any).</summary>
    public static void WriteLog(string level, string text, string? traceId, string? spanId)
        => Enqueue(new Rec("log", DateTime.UtcNow, traceId, spanId, Level: level,
            Text: Truncate(text, MaxMessageLength)));

    private static void Enqueue(Rec rec)
    {
        if (_failed) return;
        string line;
        try { line = JsonSerializer.Serialize(rec, JsonOpts); }
        catch { return; }

        if (_syncForTests)
        {
            AppendLines(new List<string>(1) { line });
            return;
        }

        var ch = _channel;
        if (ch is null)
        {
            lock (Gate)
            {
                if (_channel is null)
                {
                    _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                    });
                    _writer = Task.Run(WriterLoopAsync);
                }
                ch = _channel;
            }
        }
        ch.Writer.TryWrite(line);
    }

    private static async Task WriterLoopAsync()
    {
        var reader = _channel!.Reader;
        var batch = new List<string>(256);
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 256 && reader.TryRead(out var line))
                batch.Add(line);
            if (batch.Count > 0)
                AppendLines(batch);
            if (batch.Count < 32)
                await Task.Delay(200).ConfigureAwait(false); // coalesce trickles into fewer appends
        }
    }

    private static void AppendLines(List<string> lines)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                string path = Path.Combine(Directory, $"traces-{DateTime.UtcNow:yyyyMMdd}.jsonl");
                File.AppendAllLines(path, lines);
            }
        }
        catch
        {
            _failed = true; // storage unavailable: telemetry must never take down the runtime
        }
    }

    /// <summary>Best-effort flush of queued lines on process exit (bounded wait).</summary>
    public static void Shutdown()
    {
        var ch = _channel;
        if (ch is null) return;
        ch.Writer.TryComplete();
        try { _writer?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _channel = null;
        _writer = null;
    }

    /// <summary>Redirect storage for tests; null restores the default. Also enables synchronous writes.</summary>
    internal static void OverrideDirectoryForTests(string? dir)
    {
        lock (Gate)
        {
            _dirOverride = dir;
            _syncForTests = dir is not null;
            _failed = false;
        }
    }

    private static string Truncate(string value, int max)
        => value.Length > max ? value[..max] + "..." : value;

    // ---- read path ------------------------------------------------------------------------

    /// <summary>Light per-trace projection for the dashboard list (no payloads).</summary>
    public sealed record TraceSummary(string TraceId, string Root, DateTime StartUtc,
        double DurationMs, int Spans, int Messages, IReadOnlyList<string> Agents, bool HasError);

    /// <summary>Full trace detail: spans, messages, and logs sharing one trace id.</summary>
    public sealed record TraceDetail(string TraceId, IReadOnlyList<Rec> Spans,
        IReadOnlyList<Rec> Messages, IReadOnlyList<Rec> Logs);

    private static IEnumerable<string> FilesNewestFirst()
    {
        if (!System.IO.Directory.Exists(Directory)) yield break;
        var files = System.IO.Directory.GetFiles(Directory, "traces-*.jsonl");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        for (int i = files.Length - 1; i >= 0; i--)
            yield return files[i];
    }

    private static IEnumerable<Rec> ReadFile(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { yield break; }
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            Rec? rec = null;
            try { rec = JsonSerializer.Deserialize<Rec>(line, JsonOpts); } catch { }
            if (rec is not null) yield return rec;
        }
    }

    /// <summary>
    /// List recent traces newest-first, scanning up to <paramref name="days"/> day-files back
    /// (retention itself is cumulative; the window only bounds one listing pass). Optional
    /// filters: <paramref name="name"/> matches any span name in the trace, <paramref name="agent"/>
    /// matches the agent tag on any span.
    /// </summary>
    public static IReadOnlyList<TraceSummary> ListTraces(int limit = 50, int days = 7,
        string? name = null, string? agent = null)
    {
        limit = Math.Clamp(limit, 1, 500);
        days = Math.Clamp(days, 1, 3650);

        // trace id -> accumulators (a trace's records can straddle a midnight file boundary,
        // so accumulate across files before projecting).
        var acc = new Dictionary<string, (List<Rec> Spans, int Msgs)>(StringComparer.Ordinal);
        int filesRead = 0;
        foreach (var file in FilesNewestFirst())
        {
            if (++filesRead > days) break;
            foreach (var rec in ReadFile(file))
            {
                if (rec.TraceId is null) continue;
                if (!acc.TryGetValue(rec.TraceId, out var a))
                {
                    a = (new List<Rec>(), 0);
                    acc[rec.TraceId] = a;
                }
                if (rec.Kind == "span") a.Spans.Add(rec);
                else if (rec.Kind == "msg") a = (a.Spans, a.Msgs + 1);
                acc[rec.TraceId] = a;
            }
        }

        var list = new List<TraceSummary>(acc.Count);
        foreach (var (traceId, (spans, msgs)) in acc)
        {
            if (spans.Count == 0) continue;
            if (name is { Length: > 0 } &&
                !spans.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (agent is { Length: > 0 } &&
                !spans.Any(s => s.Tags is not null && s.Tags.TryGetValue("agent", out var a) &&
                    a.Contains(agent, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Root = span with no parent (fall back to earliest start).
            var byStart = spans.OrderBy(s => StartOf(s)).ToList();
            var root = byStart.FirstOrDefault(s => s.ParentSpanId is null) ?? byStart[0];
            DateTime start = StartOf(byStart[0]);
            DateTime end = byStart.Max(s => StartOf(s).AddMilliseconds(s.DurationMs ?? 0));
            var agents = spans.Where(s => s.Tags is not null && s.Tags.ContainsKey("agent"))
                .Select(s => s.Tags!["agent"]).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            bool hasError = spans.Any(s => string.Equals(s.Status, "Error", StringComparison.OrdinalIgnoreCase));
            list.Add(new TraceSummary(traceId, root.Name ?? "(unnamed)", start,
                (end - start).TotalMilliseconds, spans.Count, msgs, agents, hasError));
        }

        return list.OrderByDescending(t => t.StartUtc).Take(limit).ToList();
    }

    // Span records carry their START time in TsUtc (writer contract with OtelIngest).
    private static DateTime StartOf(Rec s) => s.TsUtc;

    /// <summary>
    /// Load one trace in full (spans + messages + logs), scanning newest-first. The scan stops
    /// two files past the first hit (a trace can straddle a day boundary, never more).
    /// </summary>
    public static TraceDetail? GetTrace(string traceId)
    {
        if (string.IsNullOrWhiteSpace(traceId)) return null;
        var spans = new List<Rec>();
        var msgs = new List<Rec>();
        var logs = new List<Rec>();
        int filesPastHit = -1;
        foreach (var file in FilesNewestFirst())
        {
            bool hit = false;
            foreach (var rec in ReadFile(file))
            {
                if (!string.Equals(rec.TraceId, traceId, StringComparison.Ordinal)) continue;
                hit = true;
                switch (rec.Kind)
                {
                    case "span": spans.Add(rec); break;
                    case "msg": msgs.Add(rec); break;
                    case "log": logs.Add(rec); break;
                }
            }
            if (filesPastHit >= 0 || hit)
                filesPastHit = hit ? 0 : filesPastHit + 1;
            if (filesPastHit >= 2) break;
        }
        if (spans.Count == 0 && msgs.Count == 0 && logs.Count == 0) return null;
        return new TraceDetail(traceId,
            spans.OrderBy(StartOf).ToList(),
            msgs.OrderBy(m => m.TsUtc).ToList(),
            logs.OrderBy(l => l.TsUtc).ToList());
    }

    /// <summary>List recent log records newest-first, optionally filtered by level.</summary>
    public static IReadOnlyList<Rec> ListLogs(int limit = 200, string? level = null, int days = 7)
    {
        limit = Math.Clamp(limit, 1, 2000);
        days = Math.Clamp(days, 1, 3650);
        var outLogs = new List<Rec>();
        int filesRead = 0;
        foreach (var file in FilesNewestFirst())
        {
            if (++filesRead > days || outLogs.Count >= limit * 4) break;
            foreach (var rec in ReadFile(file))
            {
                if (rec.Kind != "log") continue;
                if (level is { Length: > 0 } &&
                    !string.Equals(rec.Level, level, StringComparison.OrdinalIgnoreCase)) continue;
                outLogs.Add(rec);
            }
        }
        return outLogs.OrderByDescending(l => l.TsUtc).Take(limit).ToList();
    }
}
