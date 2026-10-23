namespace MuxSwarm.Engine;

/// <summary>
/// In-process, per-model token/cost ledger backing the <c>/cost all</c> and <c>/tokens all</c>
/// matrixed breakdown. The OpenTelemetry counters in <see cref="OtelMetrics"/> are write-only
/// (pushed to exporters; not readable back in-process), so this small accumulator mirrors the
/// same instrumentation points to keep a queryable view.
///
/// Token semantics: each UsageContent frame is the usage of ONE model call (the chat-completions path
/// reports per-call usage; a tool turn emits one frame per round-trip). We therefore keep, per model id:
///   - <b>session</b> totals = the sum of this session's calls (cleared on <c>/wipe</c>);
///   - <b>rolling</b> totals = the sum across the whole process run (survive <c>/wipe</c>);
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
        // Session sums of per-call usage; reset on /wipe.
        public long SessInput, SessOutput, SessCached, SessReasoning, SessTotal;
        // Rolling process-lifetime sums; survive /wipe.
        public long RollInput, RollOutput, RollCached, RollReasoning, RollTotal;
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
    /// Record the usage of one model call (one UsageContent frame). Session and rolling totals add it; the
    /// same per-call figures go to the durable telemetry sink and the OTel token counters.
    /// <paramref name="agent"/> attributes the persisted record to the emitting agent (the in-memory ledger
    /// stays keyed by model). Negative values are treated as 0.
    /// </summary>
    public static void RecordUsage(string model, long input, long output, long cached,
        long reasoning, long total, string? agent = null)
    {
        input = Math.Max(0, input); output = Math.Max(0, output); cached = Math.Max(0, cached);
        reasoning = Math.Max(0, reasoning); total = Math.Max(0, total);
        lock (Gate)
        {
            var s = Get(model);
            s.SessInput += input; s.SessOutput += output; s.SessCached += cached;
            s.SessReasoning += reasoning; s.SessTotal += total;
            s.RollInput += input; s.RollOutput += output; s.RollCached += cached;
            s.RollReasoning += reasoning; s.RollTotal += total;
        }
        Telemetry.TelemetrySink.RecordUsageDelta(model, input, output, cached, reasoning, agent, total);
        if (input > 0 || output > 0 || cached > 0 || reasoning > 0 || total > 0)
            OtelMetrics.RecordTokens(agent ?? "(unknown)", model, input, output, cached, reasoning, total);
    }

    /// <summary>
    /// Increment the tool-call count for a model (session + rolling). <paramref name="agent"/>
    /// and <paramref name="tool"/> enrich the persisted telemetry event only.
    /// </summary>
    public static void RecordToolCall(string model, string? agent = null, string? tool = null)
    {
        lock (Gate)
        {
            var s = Get(model);
            s.SessToolCalls++;
            s.RollToolCalls++;
        }
        Telemetry.TelemetrySink.RecordToolCall(model, agent, tool);
    }

    /// <summary>
    /// Increment the compaction-run count for a model (session + rolling). Before/after token
    /// counts (when known) enrich the persisted telemetry event with the context savings.
    /// </summary>
    public static void RecordCompaction(string model, long beforeTokens = 0, long afterTokens = 0)
    {
        lock (Gate)
        {
            var s = Get(model);
            s.SessCompactions++;
            s.RollCompactions++;
        }
        Telemetry.TelemetrySink.RecordCompaction(model, beforeTokens, afterTokens);
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
    /// Clear SESSION token totals + per-session counts/estimates, on <c>/wipe</c>.
    /// Rolling process-lifetime totals are preserved.
    /// </summary>
    public static void ResetSession()
    {
        lock (Gate)
        {
            foreach (var s in Stats.Values)
            {
                s.SessInput = s.SessOutput = s.SessCached = s.SessReasoning = s.SessTotal = 0;
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
