namespace MuxSwarm.Utils;

/// <summary>
/// In-process, per-model token/cost ledger backing the <c>/cost all</c> and <c>/tokens all</c>
/// matrixed breakdown. The OpenTelemetry counters in <see cref="OtelMetrics"/> are write-only
/// (pushed to exporters; not readable back in-process), so this small accumulator mirrors the
/// same instrumentation points to keep a queryable view.
///
/// Token semantics: providers report SESSION-CUMULATIVE snapshots on each UsageContent frame
/// (assignment, not a per-frame delta). We therefore keep, per model id:
///   - <b>session</b> totals = the latest cumulative snapshot (cleared on <c>/wipe</c>);
///   - <b>rolling</b> totals = accumulated deltas across the whole process run (survive <c>/wipe</c>);
/// plus static per-session estimates for the system prompt and tool schemas (a subset of input,
/// shown for the proportional breakdown) and counts for tool calls + compaction runs.
///
/// Inert by default: nothing reads the ledger unless the user types <c>/cost all</c> /
/// <c>/tokens all</c>; the recording calls are O(1) dictionary updates.
/// </summary>
public static class CostLedger
{
    private sealed class ModelStat
    {
        // Session-cumulative (latest snapshot); reset on /wipe.
        public long SessInput, SessOutput, SessCached, SessReasoning, SessTotal;
        // Rolling process-lifetime totals (accumulated by delta); survive /wipe.
        public long RollInput, RollOutput, RollCached, RollReasoning, RollTotal;
        // Baseline snapshot used to derive deltas for the rolling totals this session.
        public long BaseInput, BaseOutput, BaseCached, BaseReasoning, BaseTotal;
        public bool HasBase;
        // Counts.
        public long SessToolCalls, RollToolCalls;
        public long SessCompactions, RollCompactions;
        // Static per-session estimates (subset of input): system prompt + serialized tool schemas.
        public long SysPromptTok, ToolsTok;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ModelStat> Stats =
        new(StringComparer.OrdinalIgnoreCase);

    private static ModelStat Get(string model)
    {
        string key = string.IsNullOrWhiteSpace(model) ? "(unknown)" : model;
        if (!Stats.TryGetValue(key, out var s))
        {
            s = new ModelStat();
            Stats[key] = s;
        }
        return s;
    }

    /// <summary>True when nothing has been recorded yet (render a friendly empty message).</summary>
    public static bool IsEmpty
    {
        get { lock (Gate) return Stats.Count == 0; }
    }

    /// <summary>
    /// Record a provider usage checkpoint. The token counts are the provider's session-cumulative
    /// running totals (the same numbers fed to <see cref="OtelMetrics.RecordTokens"/>). Session
    /// totals snap to the snapshot; rolling totals advance by the positive delta vs the last
    /// snapshot seen this session.
    /// </summary>
    public static void RecordUsage(string model, long input, long output, long cached,
        long reasoning, long total)
    {
        lock (Gate)
        {
            var s = Get(model);

            // Session = latest cumulative snapshot.
            s.SessInput = input;
            s.SessOutput = output;
            s.SessCached = cached;
            s.SessReasoning = reasoning;
            s.SessTotal = total;

            // Rolling += positive delta vs baseline (handles provider resets gracefully: a snapshot
            // smaller than the baseline => treat as a fresh run, advance by the snapshot itself).
            if (!s.HasBase)
            {
                s.RollInput += Math.Max(0, input);
                s.RollOutput += Math.Max(0, output);
                s.RollCached += Math.Max(0, cached);
                s.RollReasoning += Math.Max(0, reasoning);
                s.RollTotal += Math.Max(0, total);
            }
            else
            {
                s.RollInput += Delta(input, s.BaseInput);
                s.RollOutput += Delta(output, s.BaseOutput);
                s.RollCached += Delta(cached, s.BaseCached);
                s.RollReasoning += Delta(reasoning, s.BaseReasoning);
                s.RollTotal += Delta(total, s.BaseTotal);
            }

            s.BaseInput = input;
            s.BaseOutput = output;
            s.BaseCached = cached;
            s.BaseReasoning = reasoning;
            s.BaseTotal = total;
            s.HasBase = true;
        }
    }

    private static long Delta(long now, long baseline)
        => now >= baseline ? now - baseline : Math.Max(0, now);

    /// <summary>Increment the tool-call count for a model (session + rolling).</summary>
    public static void RecordToolCall(string model)
    {
        lock (Gate)
        {
            var s = Get(model);
            s.SessToolCalls++;
            s.RollToolCalls++;
        }
    }

    /// <summary>Increment the compaction-run count for a model (session + rolling).</summary>
    public static void RecordCompaction(string model)
    {
        lock (Gate)
        {
            var s = Get(model);
            s.SessCompactions++;
            s.RollCompactions++;
        }
    }

    /// <summary>
    /// Set the static per-session estimates (system prompt + serialized tool schemas) for a model.
    /// These are a subset of the input tokens, surfaced for the proportional breakdown.
    /// </summary>
    public static void SetStatic(string model, long sysPromptTok, long toolsTok)
    {
        lock (Gate)
        {
            var s = Get(model);
            s.SysPromptTok = sysPromptTok;
            s.ToolsTok = toolsTok;
        }
    }

    /// <summary>
    /// Clear SESSION token totals + the delta baseline + per-session counts/estimates, on <c>/wipe</c>.
    /// Rolling process-lifetime totals are preserved.
    /// </summary>
    public static void ResetSession()
    {
        lock (Gate)
        {
            foreach (var s in Stats.Values)
            {
                s.SessInput = s.SessOutput = s.SessCached = s.SessReasoning = s.SessTotal = 0;
                s.HasBase = false;
                s.BaseInput = s.BaseOutput = s.BaseCached = s.BaseReasoning = s.BaseTotal = 0;
                s.SessToolCalls = 0;
                s.SessCompactions = 0;
                s.SysPromptTok = 0;
                s.ToolsTok = 0;
            }
        }
    }

    /// <summary>One model's snapshot for rendering. All token figures are absolute counts.</summary>
    public readonly record struct Row(
        string Model,
        long SessInput, long SessOutput, long SessCached, long SessReasoning, long SessTotal,
        long RollInput, long RollOutput, long RollCached, long RollReasoning, long RollTotal,
        long SessToolCalls, long RollToolCalls,
        long SessCompactions, long RollCompactions,
        long SysPromptTok, long ToolsTok);

    /// <summary>Immutable snapshot of every tracked model, for the breakdown renderer.</summary>
    public static IReadOnlyList<Row> Snapshot()
    {
        lock (Gate)
        {
            var rows = new List<Row>(Stats.Count);
            foreach (var (model, s) in Stats)
            {
                rows.Add(new Row(
                    model,
                    s.SessInput, s.SessOutput, s.SessCached, s.SessReasoning, s.SessTotal,
                    s.RollInput, s.RollOutput, s.RollCached, s.RollReasoning, s.RollTotal,
                    s.SessToolCalls, s.RollToolCalls,
                    s.SessCompactions, s.RollCompactions,
                    s.SysPromptTok, s.ToolsTok));
            }
            return rows;
        }
    }
}
