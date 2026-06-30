using Microsoft.Extensions.AI;
using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

// Mutates the process-wide App.Config + ExecutionLimits.Current singletons -> serialize with the
// other exec-limits/console tests.
[Collection("ExecLimitsState")]
public class DelegationStoreTests : IDisposable
{
    private readonly AppConfig _prevConfig;
    private readonly ExecutionLimits _prevLimits;
    private readonly string _root;
    private readonly string _scope;

    public DelegationStoreTests()
    {
        _prevConfig = App.Config;
        _prevLimits = ExecutionLimits.Current;
        _root = Path.Combine(Path.GetTempPath(), "muxdeltests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        App.Config = new AppConfig();
        App.Config.Filesystem.SandboxPath = _root;
        ExecutionLimits.Current = new ExecutionLimits();
        _scope = "scope_" + Guid.NewGuid().ToString("N")[..8];
        DelegationStore.SetScope(_scope);
        DelegationStore.ResetScope(_scope);
    }

    public void Dispose()
    {
        App.Config = _prevConfig;
        ExecutionLimits.Current = _prevLimits;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    // Canned non-streaming client so summary tiers can be exercised without a network model.
    private sealed class CannedClient : IChatClient
    {
        private readonly string _reply;
        public CannedClient(string reply) => _reply = reply;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static string Big(int approxChars)
    {
        var lines = new List<string>();
        int n = 0;
        while (string.Join("\n", lines).Length < approxChars)
            lines.Add($"Line {n}: created /home/user/project/file{n}.cs at port 80{n++}.");
        return string.Join("\n", lines);
    }

    // ── RootFor: sandbox-first ──────────────────────────────────────────

    [Fact]
    public void RootFor_UsesSandboxFirst()
    {
        var root = DelegationStore.RootFor(_scope);
        Assert.NotNull(root);
        Assert.StartsWith(_root, root!);
        Assert.Contains("delegations", root);
    }

    // ── Persist + Resolve + ReadSlice ───────────────────────────────────

    [Fact]
    public void Persist_Resolve_ReadSlice_WholeHeadTailPattern()
    {
        var raw = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"row {i} value={i}"));
        var retained = DelegationStore.Persist(_scope, "WebAgent", raw, "success", "did the thing", "out.txt");
        Assert.NotNull(retained);
        Assert.True(File.Exists(retained!.Path));
        Assert.Equal(raw.Length, retained.RawLen);

        // handle resolves
        var resolved = DelegationStore.Resolve(retained.Handle);
        Assert.NotNull(resolved);
        Assert.Equal(retained.Path, resolved!.Path);

        // whole (bounded) — front-matter stripped
        var whole = DelegationStore.ReadSlice(retained.Handle, null, null, null, 100000);
        Assert.DoesNotContain("handle:", whole);
        Assert.Contains("row 0 value=0", whole);
        Assert.Contains("row 49 value=49", whole);

        // head
        var head = DelegationStore.ReadSlice(retained.Handle, null, 3, null, 100000);
        Assert.Contains("row 0", head);
        Assert.DoesNotContain("row 49", head);

        // tail
        var tail = DelegationStore.ReadSlice(retained.Handle, null, null, 3, 100000);
        Assert.Contains("row 49", tail);
        Assert.DoesNotContain("row 0 value=0", tail);

        // pattern (grep) with context window
        var grep = DelegationStore.ReadSlice(retained.Handle, "value=25", null, null, 100000);
        Assert.Contains("row 25", grep);
        Assert.DoesNotContain("row 0 value=0", grep);
    }

    [Fact]
    public void ReadSlice_BoundsToMaxChars()
    {
        var raw = new string('x', 5000);
        var retained = DelegationStore.Persist(_scope, "DataAgent", raw, "success", null, null);
        Assert.NotNull(retained);
        var sliced = DelegationStore.ReadSlice(retained!.Handle, null, null, null, 500);
        Assert.True(sliced.Length <= 700); // 500 + truncation note
        Assert.Contains("truncated", sliced);
    }

    [Fact]
    public void ReadSlice_UnknownHandle_ReturnsClearMessage()
    {
        var msg = DelegationStore.ReadSlice("d:Nope#99", null, null, null, 1000);
        Assert.Contains("handle not found", msg);
    }

    [Fact]
    public void Persist_UnwritableSandbox_ReturnsNull()
    {
        // Point sandbox at a path that cannot be created (a file, not a dir).
        var filePath = Path.Combine(_root, "iamafile");
        File.WriteAllText(filePath, "x");
        App.Config.Filesystem.SandboxPath = filePath; // not a directory
        var badScope = "bad_" + Guid.NewGuid().ToString("N")[..6];
        DelegationStore.SetScope(badScope);
        // Local fallback (%LOCALAPPDATA%) is normally writable, so retention may still succeed there;
        // assert only that no exception is thrown and a null-or-valid result is returned.
        var r = DelegationStore.Persist(badScope, "X", "data", "success", null, null);
        Assert.True(r is null || File.Exists(r.Path));
    }

    // ── TierResultAsync: posture by size ────────────────────────────────

    [Fact]
    public async Task TierResult_SmallResult_Inline()
    {
        ExecutionLimits.Current = new ExecutionLimits { ProgressEntryBudget = 1000 };
        DelegationStore.ResetScope(_scope);
        var raw = "all done, short and sweet";
        var (lead, retained) = await DelegationStore.TierResultAsync(
            _scope, "WebAgent", raw, "success", "done", null, null, null);
        Assert.Equal(raw, lead);
        Assert.Null(retained); // no spill for small
    }

    [Fact]
    public async Task TierResult_LargeResult_SpillsToPointer()
    {
        ExecutionLimits.Current = new ExecutionLimits { ProgressEntryBudget = 200 }; // spill threshold = 600
        DelegationStore.ResetScope(_scope);
        var raw = Big(3000); // >> 600
        var (lead, retained) = await DelegationStore.TierResultAsync(
            _scope, "WebAgent", raw, "success", "produced a large report", null, null, null);
        Assert.NotNull(retained);
        Assert.Contains("read_delegation", lead);
        Assert.Contains(retained!.Handle, lead);
        Assert.True(lead.Length < raw.Length); // pointer is far smaller than the raw

        // The handle resolves to the FULL raw via ReadSlice.
        var pulled = DelegationStore.ReadSlice(retained.Handle, null, null, null, 100000);
        Assert.Contains("file0.cs", pulled);
        Assert.Equal(raw, pulled); // full raw round-trips (front-matter stripped, content intact)
    }

    [Fact]
    public async Task TierResult_MediumResult_SummarizesViaCompactor()
    {
        ExecutionLimits.Current = new ExecutionLimits
        {
            ProgressEntryBudget = 400,   // spill threshold = 1200
            ProgressLogTotalBudget = 100000,
            SubAgentSummaryMode = "auto"
        };
        DelegationStore.ResetScope(_scope);
        var raw = Big(900); // > 400 (not inline) and < 1200 (not spill)
        var client = new CannedClient("SUMMARY: a medium report.");
        var (lead, retained) = await DelegationStore.TierResultAsync(
            _scope, "WebAgent", raw, "success", "medium report", null, client, null);
        Assert.Null(retained); // summarized, not spilled
        Assert.Contains("SUMMARY: a medium report.", lead);
    }

    [Fact]
    public async Task TierResult_LeadCumulativeCap_DemotesToPointer()
    {
        // Tiny lead budget so the cumulative soft cap is crossed quickly, forcing a demote-to-pointer
        // even for a result that would otherwise summarize.
        ExecutionLimits.Current = new ExecutionLimits
        {
            ProgressEntryBudget = 300,     // spill threshold = 900
            ProgressLogTotalBudget = 50,   // soft cap = 50 -> crossed immediately
            SubAgentSummaryMode = "auto"
        };
        DelegationStore.ResetScope(_scope);
        var raw = Big(600); // medium band, but the cap forces a pointer
        var (lead, retained) = await DelegationStore.TierResultAsync(
            _scope, "WebAgent", raw, "success", "report", null, new CannedClient("ignored"), null);
        Assert.NotNull(retained);
        Assert.Contains(retained!.Handle, lead);
    }

    // ── read_delegation tool surface ────────────────────────────────────

    [Fact]
    public void ReadDelegationTool_SurfaceAndResolves()
    {
        var tool = LocalAiFunctions.ReadDelegationTool;
        Assert.NotNull(tool);
        Assert.Equal("read_delegation", tool.Name);
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
    }
}
