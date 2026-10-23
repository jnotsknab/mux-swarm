namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// Forwards SWARM per-call usage (one UsageContent frame = one model call) to the telemetry sink and the
/// OTel token counters with agent attribution. Swarm/pswarm do not use <see cref="CostLedger"/> (it is the
/// single-agent /cost view), so this is their only usage path.
/// </summary>
public static class TelemetryUsageTracker
{
    /// <summary>
    /// True for the first usage frame of a model response. Some OpenAI-compatible proxies repeat the final
    /// usage chunk (same response id, same numbers), which must not count the call twice. Frames without a
    /// response id always count. <paramref name="lastId"/> is the caller's per-stream state.
    /// </summary>
    public static bool FirstForResponse(string? responseId, ref string? lastId)
    {
        if (string.IsNullOrEmpty(responseId)) return true;
        if (responseId == lastId) return false;
        lastId = responseId;
        return true;
    }

    /// <summary>Record the usage of one model call by <paramref name="agent"/>. Null or negative values count as 0.</summary>
    public static void RecordCall(string agent, string model,
        long? input, long? output, long? cached, long? reasoning, long? total)
    {
        long i = Math.Max(0, input ?? 0), o = Math.Max(0, output ?? 0), c = Math.Max(0, cached ?? 0),
             r = Math.Max(0, reasoning ?? 0), t = Math.Max(0, total ?? 0);
        TelemetrySink.RecordUsageDelta(model, i, o, c, r, agent, t);
        if (i > 0 || o > 0 || c > 0 || r > 0 || t > 0)
            OtelMetrics.RecordTokens(agent, model, i, o, c, r, t);
    }
}
