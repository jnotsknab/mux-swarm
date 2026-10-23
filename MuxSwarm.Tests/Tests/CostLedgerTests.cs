using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

// CostLedger backs /cost all + /tokens all. Tests run sequentially because the ledger is a
// process-static accumulator; each test resets the session and uses a unique model id to avoid
// cross-test bleed on the rolling (process-lifetime) totals.
[Collection("TelemetryState")]
public class CostLedgerTests
{
    [Fact]
    public void RecordUsage_SumsPerCallUsage_SessionAndRolling()
    {
        // Each UsageContent frame is ONE call's usage (harbor trial, claude via the chat path): 3 calls.
        string m = "test-model-sum-" + Guid.NewGuid().ToString("N");
        CostLedger.RecordUsage(m, input: 10402, output: 300, cached: 10000, reasoning: 5, total: 10702);
        CostLedger.RecordUsage(m, input: 10621, output: 250, cached: 10400, reasoning: 0, total: 10871);
        CostLedger.RecordUsage(m, input: 10987, output: 410, cached: 10600, reasoning: 7, total: 11397);

        var row = Find(m);
        Assert.Equal(32010, row.SessInput);
        Assert.Equal(960, row.SessOutput);
        Assert.Equal(31000, row.SessCached);
        Assert.Equal(12, row.SessReasoning);
        Assert.Equal(32970, row.SessTotal);
        Assert.Equal(32010, row.RollInput);
        Assert.Equal(32970, row.RollTotal);
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
        Assert.Equal(200, row.RollInput);   // rolling survives the wipe
        Assert.Equal(250, row.RollTotal);

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
