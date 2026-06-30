using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

// CostLedger backs /cost all + /tokens all. Tests run sequentially because the ledger is a
// process-static accumulator; each test resets the session and uses a unique model id to avoid
// cross-test bleed on the rolling (process-lifetime) totals.
public class CostLedgerTests
{
    [Fact]
    public void RecordUsage_SessionSnapsToLatestCumulative()
    {
        string m = "test-model-snap-" + Guid.NewGuid().ToString("N");
        CostLedger.RecordUsage(m, input: 100, output: 20, cached: 10, reasoning: 5, total: 120);
        CostLedger.RecordUsage(m, input: 250, output: 60, cached: 40, reasoning: 15, total: 310);

        var row = Find(m);
        Assert.Equal(250, row.SessInput);
        Assert.Equal(60, row.SessOutput);
        Assert.Equal(40, row.SessCached);
        Assert.Equal(15, row.SessReasoning);
        Assert.Equal(310, row.SessTotal);
    }

    [Fact]
    public void RecordUsage_RollingAccumulatesByDelta()
    {
        string m = "test-model-roll-" + Guid.NewGuid().ToString("N");
        // Two cumulative snapshots: rolling should equal the latest snapshot (single contiguous run).
        CostLedger.RecordUsage(m, 100, 20, 0, 0, 120);
        CostLedger.RecordUsage(m, 250, 60, 0, 0, 310);

        var row = Find(m);
        Assert.Equal(250, row.RollInput);
        Assert.Equal(60, row.RollOutput);
        Assert.Equal(310, row.RollTotal);
    }

    [Fact]
    public void ResetSession_ClearsSessionButKeepsRolling()
    {
        string m = "test-model-reset-" + Guid.NewGuid().ToString("N");
        CostLedger.RecordUsage(m, 200, 50, 0, 0, 250);

        CostLedger.ResetSession();

        var row = Find(m);
        Assert.Equal(0, row.SessInput);
        Assert.Equal(0, row.SessTotal);
        // Rolling survives the wipe.
        Assert.Equal(200, row.RollInput);
        Assert.Equal(250, row.RollTotal);

        // After reset, a new snapshot is treated as a fresh baseline (advances rolling by full value).
        CostLedger.RecordUsage(m, 80, 10, 0, 0, 90);
        row = Find(m);
        Assert.Equal(80, row.SessInput);
        Assert.Equal(280, row.RollInput);  // 200 + 80
    }

    [Fact]
    public void RecordToolCall_And_Compaction_Increment()
    {
        string m = "test-model-counts-" + Guid.NewGuid().ToString("N");
        CostLedger.RecordToolCall(m);
        CostLedger.RecordToolCall(m);
        CostLedger.RecordCompaction(m);

        var row = Find(m);
        Assert.Equal(2, row.SessToolCalls);
        Assert.Equal(2, row.RollToolCalls);
        Assert.Equal(1, row.SessCompactions);
    }

    [Fact]
    public void SetStatic_StoresSystemPromptAndToolEstimates()
    {
        string m = "test-model-static-" + Guid.NewGuid().ToString("N");
        CostLedger.SetStatic(m, sysPromptTok: 1234, toolsTok: 5678);

        var row = Find(m);
        Assert.Equal(1234, row.SysPromptTok);
        Assert.Equal(5678, row.ToolsTok);
    }

    [Fact]
    public void Snapshot_IsImmutableCopy()
    {
        string m = "test-model-snapimm-" + Guid.NewGuid().ToString("N");
        CostLedger.RecordUsage(m, 10, 1, 0, 0, 11);
        var first = Find(m);
        CostLedger.RecordUsage(m, 99, 9, 0, 0, 108);
        // The earlier struct copy is unaffected by the later record.
        Assert.Equal(10, first.SessInput);
    }

    private static CostLedger.Row Find(string model)
    {
        var snap = CostLedger.Snapshot();
        var row = snap.FirstOrDefault(r => r.Model == model);
        Assert.False(string.IsNullOrEmpty(row.Model), $"model {model} not found in ledger snapshot");
        return row;
    }
}
