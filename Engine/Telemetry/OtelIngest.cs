using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// In-process ingestion of the built-in OTel stack (Meter + ActivitySource "MuxSwarm" and
/// <see cref="OtelLogger"/> entries) into bounded in-memory rings, so the telemetry dashboard
/// can serve live metrics, traces, and logs without an external OTLP collector. Composes with
/// the OTLP exporters: listeners observe the same instruments/spans the exporters push, and
/// ingestion runs regardless of <c>telemetry.enabled</c>. Memory is bounded (span/log rings,
/// capped tag cardinality, fixed histogram reservoirs).
/// </summary>
public static class OtelIngest
{
    private const int SpanRingSize = 2000;
    private const int LogRingSize = 1000;
    private const int HistReservoirSize = 256;
    private const int MaxTagCombos = 64;
    private const int MaxTagValueLength = 256;
    private const int MaxSpanTags = 16;
    private const int MaxLogMessageLength = 4096;

    private static readonly object Gate = new();
    private static MeterListener? _meterListener;
    private static ActivityListener? _activityListener;

    /// <summary>UTC time ingestion started (dashboard "since process start" subtitle).</summary>
    public static DateTime StartedUtc { get; private set; }

    // ---- storage ------------------------------------------------------------------------

    private sealed class InstrumentState
    {
        public required string Name;
        public required string Kind; // "counter" | "histogram"
        public bool Recorded;
        public double Total;                 // counters: running sum of measurements
        public long Count;                   // histograms: number of recordings
        public double Sum, Min = double.MaxValue, Max = double.MinValue;
        public readonly double[] Reservoir = new double[HistReservoirSize];
        public long ReservoirNext;
        public readonly Dictionary<string, double> TagCombos = new(StringComparer.Ordinal);
        // Per-minute ring for rates (unix-minute stamped).
        public readonly double[] MinuteValues = new double[60];
        public readonly long[] MinuteStamps = new long[60];
    }

    private sealed record SpanRec(string Name, string TraceId, string SpanId, string? ParentSpanId,
        DateTime StartUtc, double DurationMs, string Status, string? StatusDescription,
        Dictionary<string, string> Tags);

    private sealed record LogRec(DateTime TsUtc, string Level, string Message, string? TraceId, string? SpanId);

    private static readonly Dictionary<string, InstrumentState> Instruments = new(StringComparer.Ordinal);
    private static readonly SpanRec?[] SpanRing = new SpanRec?[SpanRingSize];
    private static long _spanNext;
    private static readonly LogRec?[] LogRing = new LogRec?[LogRingSize];
    private static long _logNext;

    // ---- lifecycle ----------------------------------------------------------------------

    /// <summary>
    /// Register the meter/activity listeners and begin ingesting. Idempotent; called once at
    /// startup. Forces <see cref="OtelMetrics"/> static initialization so the full instrument
    /// catalog is published (never-recorded instruments still appear, greyed, in the dashboard).
    /// </summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_meterListener is not null) return;
            StartedUtc = DateTime.UtcNow;

            // Materialize the instrument catalog before the listener starts.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(OtelMetrics).TypeHandle);

            var meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name != "MuxSwarm") return;
                    RegisterInstrument(instrument);
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            meterListener.SetMeasurementEventCallback<long>(static (inst, value, tags, _) => OnMeasurement(inst, value, tags));
            meterListener.SetMeasurementEventCallback<double>(static (inst, value, tags, _) => OnMeasurement(inst, value, tags));
            meterListener.Start();
            _meterListener = meterListener;

            var activityListener = new ActivityListener
            {
                ShouldListenTo = static src => src.Name == "MuxSwarm",
                // AllData (not Recorded): span objects carry tags/events for ingestion, while the
                // OTLP pipeline's own sampler still decides what gets exported.
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = OnActivityStopped
            };
            ActivitySource.AddActivityListener(activityListener);
            _activityListener = activityListener;
        }
    }

    /// <summary>Clear all ingested data (test isolation). Listeners stay registered.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            foreach (var s in Instruments.Values)
            {
                s.Recorded = false; s.Total = 0; s.Count = 0; s.Sum = 0;
                s.Min = double.MaxValue; s.Max = double.MinValue; s.ReservoirNext = 0;
                s.TagCombos.Clear();
                Array.Clear(s.MinuteValues); Array.Clear(s.MinuteStamps);
            }
            Array.Clear(SpanRing); _spanNext = 0;
            Array.Clear(LogRing); _logNext = 0;
        }
    }

    // ---- ingestion ----------------------------------------------------------------------

    private static void RegisterInstrument(Instrument instrument)
    {
        string kind = instrument.GetType().Name.StartsWith("Histogram", StringComparison.Ordinal)
            ? "histogram" : "counter";
        lock (Gate)
        {
            if (!Instruments.ContainsKey(instrument.Name))
                Instruments[instrument.Name] = new InstrumentState { Name = instrument.Name, Kind = kind };
        }
    }

    private static void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string combo = ComboKey(tags);
        long minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        lock (Gate)
        {
            if (!Instruments.TryGetValue(instrument.Name, out var s)) return;
            s.Recorded = true;
            if (s.Kind == "histogram")
            {
                s.Count++;
                s.Sum += value;
                if (value < s.Min) s.Min = value;
                if (value > s.Max) s.Max = value;
                s.Reservoir[s.ReservoirNext++ % HistReservoirSize] = value;
                AddCombo(s, combo, 1);
            }
            else
            {
                s.Total += value;
                AddCombo(s, combo, value);
                int idx = (int)(minute % 60);
                if (s.MinuteStamps[idx] != minute) { s.MinuteStamps[idx] = minute; s.MinuteValues[idx] = 0; }
                s.MinuteValues[idx] += value;
            }
        }
    }

    private static void AddCombo(InstrumentState s, string combo, double value)
    {
        if (combo.Length == 0) return;
        if (s.TagCombos.TryGetValue(combo, out var cur)) s.TagCombos[combo] = cur + value;
        else if (s.TagCombos.Count < MaxTagCombos) s.TagCombos[combo] = value;
        else s.TagCombos["(other)"] = s.TagCombos.GetValueOrDefault("(other)") + value;
    }

    private static string ComboKey(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return "";
        Span<int> order = stackalloc int[tags.Length];
        for (int i = 0; i < tags.Length; i++) order[i] = i;
        // Insertion sort by key for a stable, allocation-light canonical ordering.
        for (int i = 1; i < order.Length; i++)
        {
            int cur = order[i], j = i - 1;
            while (j >= 0 && string.CompareOrdinal(tags[order[j]].Key, tags[cur].Key) > 0)
            { order[j + 1] = order[j]; j--; }
            order[j + 1] = cur;
        }
        var sb = new System.Text.StringBuilder(64);
        for (int i = 0; i < order.Length; i++)
        {
            if (i > 0) sb.Append('|');
            var kv = tags[order[i]];
            sb.Append(kv.Key).Append('=').Append(Truncate(kv.Value?.ToString() ?? "", 64));
        }
        return sb.ToString();
    }

    private static void OnActivityStopped(Activity activity)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in activity.TagObjects)
        {
            if (tags.Count >= MaxSpanTags) break;
            tags[kv.Key] = Truncate(kv.Value?.ToString() ?? "", MaxTagValueLength);
        }
        var rec = new SpanRec(
            activity.OperationName,
            activity.TraceId.ToString(),
            activity.SpanId.ToString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToString(),
            activity.StartTimeUtc,
            activity.Duration.TotalMilliseconds,
            activity.Status.ToString(),
            string.IsNullOrEmpty(activity.StatusDescription) ? null : Truncate(activity.StatusDescription, MaxTagValueLength),
            tags);
        lock (Gate)
        {
            SpanRing[_spanNext++ % SpanRingSize] = rec;
        }
    }

    /// <summary>Tee an <see cref="OtelLogger"/> entry into the log ring (always captured, even with no active span).</summary>
    public static void RecordLog(string level, string message)
    {
        var current = Activity.Current;
        var rec = new LogRec(DateTime.UtcNow, level, Truncate(message, MaxLogMessageLength),
            current?.TraceId.ToString(), current?.SpanId.ToString());
        lock (Gate)
        {
            LogRing[_logNext++ % LogRingSize] = rec;
        }
    }

    private static string Truncate(string value, int max)
        => value.Length > max ? value[..max] + "..." : value;

    // ---- snapshots (dashboard JSON) -------------------------------------------------------

    /// <summary>One tag combination and its accumulated value (counters) or count (histograms).</summary>
    public sealed record TagCount(string Key, double Value);

    /// <summary>Point-in-time view of one instrument: totals, histogram stats, and recent rates.</summary>
    public sealed record InstrumentSnap(string Name, string Kind, bool Recorded, double Total,
        long Count, double Sum, double Min, double Max, double P50, double P95,
        double Rate1m, double Rate5m, IReadOnlyList<TagCount> Tags);

    /// <summary>Full metrics snapshot: ingestion start time plus every published instrument.</summary>
    public sealed record MetricsSnap(DateTime StartedUtc, IReadOnlyList<InstrumentSnap> Instruments);

    /// <summary>One completed span captured from the "MuxSwarm" ActivitySource.</summary>
    public sealed record SpanSnap(string Name, string TraceId, string SpanId, string? ParentSpanId,
        DateTime StartUtc, double DurationMs, string Status, string? StatusDescription,
        IReadOnlyDictionary<string, string> Tags);

    /// <summary>Recent-spans snapshot: total captured plus the filtered page, newest first.</summary>
    public sealed record TraceSnap(int Total, IReadOnlyList<SpanSnap> Spans);

    /// <summary>One captured log entry with its originating span/trace ids when available.</summary>
    public sealed record LogSnap(DateTime TsUtc, string Level, string Message, string? TraceId, string? SpanId);

    /// <summary>Recent-logs snapshot: total captured plus the filtered page, newest first.</summary>
    public sealed record LogsSnap(int Total, IReadOnlyList<LogSnap> Entries);

    /// <summary>Snapshot every published instrument (declaration order preserved per registration).</summary>
    public static MetricsSnap MetricsSnapshot()
    {
        lock (Gate)
        {
            var list = new List<InstrumentSnap>(Instruments.Count);
            long nowMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            foreach (var s in Instruments.Values)
            {
                double p50 = 0, p95 = 0;
                if (s.Kind == "histogram" && s.Count > 0)
                {
                    int valid = (int)Math.Min(s.Count, HistReservoirSize);
                    var tmp = new double[valid];
                    Array.Copy(s.Reservoir, tmp, valid);
                    Array.Sort(tmp);
                    p50 = tmp[(int)(valid * 0.50) < valid ? (int)(valid * 0.50) : valid - 1];
                    p95 = tmp[(int)(valid * 0.95) < valid ? (int)(valid * 0.95) : valid - 1];
                }
                double r1 = 0, r5 = 0;
                for (int i = 0; i < 60; i++)
                {
                    long age = nowMinute - s.MinuteStamps[i];
                    if (age is >= 0 and < 1) r1 += s.MinuteValues[i];
                    if (age is >= 0 and < 5) r5 += s.MinuteValues[i];
                }
                var tags = s.TagCombos
                    .OrderByDescending(kv => kv.Value)
                    .Take(10)
                    .Select(kv => new TagCount(kv.Key, kv.Value))
                    .ToList();
                list.Add(new InstrumentSnap(s.Name, s.Kind, s.Recorded, s.Total, s.Count, s.Sum,
                    s.Count > 0 || s.Kind == "counter" ? (s.Min == double.MaxValue ? 0 : s.Min) : 0,
                    s.Max == double.MinValue ? 0 : s.Max, p50, p95, r1, r5, tags));
            }
            list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new MetricsSnap(StartedUtc, list);
        }
    }

    /// <summary>Snapshot recent spans, newest first, optionally filtered by span name and/or agent tag.</summary>
    public static TraceSnap TracesSnapshot(int limit = 200, string? name = null, string? agent = null)
    {
        limit = Math.Clamp(limit, 1, SpanRingSize);
        lock (Gate)
        {
            int total = (int)Math.Min(_spanNext, SpanRingSize);
            var spans = new List<SpanSnap>(Math.Min(limit, total));
            for (long i = _spanNext - 1; i >= 0 && i >= _spanNext - SpanRingSize && spans.Count < limit; i--)
            {
                var rec = SpanRing[i % SpanRingSize];
                if (rec is null) continue;
                if (name is { Length: > 0 } && !rec.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (agent is { Length: > 0 } &&
                    !(rec.Tags.TryGetValue("agent", out var a) && a.Contains(agent, StringComparison.OrdinalIgnoreCase))) continue;
                spans.Add(new SpanSnap(rec.Name, rec.TraceId, rec.SpanId, rec.ParentSpanId,
                    rec.StartUtc, rec.DurationMs, rec.Status, rec.StatusDescription, rec.Tags));
            }
            return new TraceSnap(total, spans);
        }
    }

    /// <summary>Snapshot recent log entries, newest first, optionally filtered by level.</summary>
    public static LogsSnap LogsSnapshot(int limit = 200, string? level = null)
    {
        limit = Math.Clamp(limit, 1, LogRingSize);
        lock (Gate)
        {
            int total = (int)Math.Min(_logNext, LogRingSize);
            var entries = new List<LogSnap>(Math.Min(limit, total));
            for (long i = _logNext - 1; i >= 0 && i >= _logNext - LogRingSize && entries.Count < limit; i--)
            {
                var rec = LogRing[i % LogRingSize];
                if (rec is null) continue;
                if (level is { Length: > 0 } && !rec.Level.Equals(level, StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new LogSnap(rec.TsUtc, rec.Level, rec.Message, rec.TraceId, rec.SpanId));
            }
            return new LogsSnap(total, entries);
        }
    }
}
