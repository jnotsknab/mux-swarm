using System.Runtime.CompilerServices;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// Delta deriver for SWARM usage checkpoints. Providers report session-cumulative token
/// snapshots; <see cref="CostLedger"/> derives deltas with a per-MODEL baseline, which is only
/// correct when a single session drives each model (the single-agent path). Swarm/pswarm runs
/// several concurrent sessions per model, so this tracker keys baselines by the SESSION object
/// (weakly held - baselines die with the session) and forwards positive per-checkpoint deltas
/// to <see cref="TelemetrySink.RecordUsageDelta"/> with agent attribution.
/// </summary>
public static class TelemetryUsageTracker
{
    private sealed class Baseline
    {
        public long In, Out, Cached, Reason, Total;
        public bool Has;
    }

    private static readonly ConditionalWeakTable<object, Dictionary<string, Baseline>> Scopes = new();

    /// <summary>
    /// Record a provider session-cumulative usage checkpoint for one agent session. The delta vs
    /// the last checkpoint seen on the same <paramref name="sessionScope"/> + model is persisted
    /// to the telemetry sink; a snapshot smaller than the baseline is treated as a provider reset
    /// (advance by the snapshot itself, mirroring <see cref="CostLedger"/> semantics).
    /// </summary>
    public static void RecordCumulative(object sessionScope, string agent, string model,
        long? input, long? output, long? cached, long? reasoning, long? total)
    {
        var baselines = Scopes.GetOrCreateValue(sessionScope);
        long dIn, dOut, dCache, dReason, dTot;
        lock (baselines)
        {
            string key = string.IsNullOrWhiteSpace(model) ? "(unknown)" : model;
            if (!baselines.TryGetValue(key, out var b))
            {
                b = new Baseline();
                baselines[key] = b;
            }

            long nIn = input ?? 0, nOut = output ?? 0, nCache = cached ?? 0,
                 nReason = reasoning ?? 0, nTot = total ?? 0;
            if (!b.Has)
            {
                dIn = Math.Max(0, nIn); dOut = Math.Max(0, nOut); dCache = Math.Max(0, nCache);
                dReason = Math.Max(0, nReason); dTot = Math.Max(0, nTot);
            }
            else
            {
                dIn = Delta(nIn, b.In); dOut = Delta(nOut, b.Out); dCache = Delta(nCache, b.Cached);
                dReason = Delta(nReason, b.Reason); dTot = Delta(nTot, b.Total);
            }
            b.In = nIn; b.Out = nOut; b.Cached = nCache; b.Reason = nReason; b.Total = nTot;
            b.Has = true;
        }
        TelemetrySink.RecordUsageDelta(model, dIn, dOut, dCache, dReason, agent, dTot);
        if (dIn > 0 || dOut > 0 || dCache > 0 || dReason > 0 || dTot > 0)
            OtelMetrics.RecordTokens(agent, model, dIn, dOut, dCache, dReason, dTot);
    }

    private static long Delta(long now, long baseline)
        => now >= baseline ? now - baseline : Math.Max(0, now);
}
