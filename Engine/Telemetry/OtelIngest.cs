using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// In-process ingestion of the built-in OTel stack (Meter + ActivitySource "MuxSwarm",
/// <see cref="OtelLogger"/> entries, and turn-final agent responses) so the telemetry
/// dashboard can serve live metrics, traces, and logs without an external OTLP collector.
/// Metric aggregates stay in fixed-size memory (capped tag cardinality, fixed histogram
/// reservoirs); spans (with their events), responses, and logs are persisted through
/// <see cref="TraceStore"/> (per-day JSONL, cumulative retention) so trace step-through
/// survives restarts and memory stays flat. Composes with the OTLP exporters: listeners
/// observe the same instruments/spans the exporters push, and ingestion runs regardless of
/// <c>telemetry.enabled</c>.
/// </summary>
public static class OtelIngest
{
    private const int HistReservoirSize = 256;
    private const int MaxTagCombos = 64;
    private const int MaxTagValueLength = 256;
    private const int MaxPayloadTagLength = 2048;
    private const int MaxSpanTags = 16;
    private const int MaxSpanEvents = 64;

    /// <summary>Tag/event keys that carry payloads (args, results, content) and get the larger cap.</summary>
    private static readonly HashSet<string> PayloadKeys = new(StringComparer.OrdinalIgnoreCase)
        { "args", "result", "content", "message", "task", "summary" };

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

    private static readonly Dictionary<string, InstrumentState> Instruments = new(StringComparer.Ordinal);

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

    private static string CapTag(string key, string? value)
        => Truncate(value ?? "", PayloadKeys.Contains(key) ? MaxPayloadTagLength : MaxTagValueLength);

    private static void OnActivityStopped(Activity activity)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in activity.TagObjects)
        {
            if (tags.Count >= MaxSpanTags) break;
            tags[kv.Key] = CapTag(kv.Key, kv.Value?.ToString());
        }

        List<TraceStore.SpanEvent>? events = null;
        foreach (var ev in activity.Events)
        {
            events ??= new List<TraceStore.SpanEvent>();
            if (events.Count >= MaxSpanEvents) break;
            Dictionary<string, string>? evTags = null;
            foreach (var kv in ev.Tags)
            {
                evTags ??= new Dictionary<string, string>(StringComparer.Ordinal);
                if (evTags.Count >= MaxSpanTags) break;
                evTags[kv.Key] = CapTag(kv.Key, kv.Value?.ToString());
            }
            events.Add(new TraceStore.SpanEvent(ev.Name, ev.Timestamp.UtcDateTime, evTags));
        }

        TraceStore.WriteSpan(new TraceStore.Rec("span", activity.StartTimeUtc,
            activity.TraceId.ToString(), activity.SpanId.ToString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToString(),
            activity.OperationName, activity.Duration.TotalMilliseconds,
            activity.Status.ToString(),
            string.IsNullOrEmpty(activity.StatusDescription) ? null : Truncate(activity.StatusDescription, MaxTagValueLength),
            tags.Count > 0 ? tags : null, events));
    }

    /// <summary>Tee an <see cref="OtelLogger"/> entry to durable trace storage (always captured, even with no active span).</summary>
    public static void RecordLog(string level, string message)
    {
        var current = Activity.Current;
        TraceStore.WriteLog(level, message, current?.TraceId.ToString(), current?.SpanId.ToString());
    }

    /// <summary>
    /// Record a turn-final agent response for trace step-through, attributed to the active
    /// span/trace. Always captured to local storage (unlike the verbosity-gated OTLP span
    /// event in <see cref="OtelMetrics.RecordAgentMessage"/>); never exported.
    /// </summary>
    public static void RecordResponse(string agent, string text)
    {
        var current = Activity.Current;
        TraceStore.WriteMessage(agent, text, current?.TraceId.ToString(), current?.SpanId.ToString());
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
}
