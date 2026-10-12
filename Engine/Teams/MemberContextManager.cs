using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine.Teams;

/// <summary>
/// Manages how a team member's session context is carried across task pickups.
///
/// In <c>"fresh"</c> mode every task runs in a clean session (the pre-g12.16 one-shot behavior):
/// no carry-over, no growth. In <c>"persistent"</c> mode each member keeps a warm session so
/// context accumulates between tasks (the Claude-Code "each teammate has its own context window"
/// model) - and, exactly like the single-agent loop's auto-compaction, when a member's session is
/// estimated to exceed <see cref="MemberContextManager"/>'s token threshold its history is
/// summarized and reseeded so it can never grow without bound. Members get their own threshold
/// (<see cref="CompacterConfig.MemberAutoCompactTokenThreshold"/>) so they can be held tighter
/// than the lead.
///
/// The compaction mechanism is the same one the main agent uses
/// (<see cref="ResultCompactor.CompactConversationAsync"/> -> reseed the session with the summary).
/// When no compaction client is configured we fall back to a bounded hard-reset (drop history)
/// rather than leak, with a muted notice. Per-member state (activity, completed count, token
/// estimate, compaction count) is persisted via <see cref="MemberState"/> for resume + the
/// Agent View / kanban.
/// </summary>
public sealed class MemberContextManager
{
    /// <summary>Fallback per-member context ceiling when <c>memberAutoCompactTokenThreshold</c> is unset or 0:
    /// a full 200k model window, so a persistent member keeps its context until it is actually needed.
    /// Set the key to hold members tighter.</summary>
    public const int DefaultThresholdTokens = 200_000;

    private readonly string _teamName;
    private readonly string _mode;            // "persistent" | "fresh"
    private readonly int _thresholdTokens;
    private readonly IChatClient? _compactionClient;
    private readonly ChatOptions? _compactionOptions;

    private readonly object _gate = new();
    private readonly Dictionary<string, MemberState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _memberLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Insight.ContextDrop> _drops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// /insight max: the member's context_map/context_drop over its warm session, or null for fresh members (their
    /// session is clean every task, so there is nothing to drop). One instance per member; drops it queues are
    /// applied after the member's task (see <see cref="RunAsync"/>).
    /// </summary>
    internal Insight.ContextDrop? DropFor(string member)
    {
        if (!IsPersistent) return null;
        lock (_gate)
        {
            if (!_drops.TryGetValue(member, out var drop))
                _drops[member] = drop = new Insight.ContextDrop(() =>
                    MultiAgentOrchestrator.Specialists.TryGetValue(member, out var s)
                    && s.Session.TryGetInMemoryChatHistory(out var h) && h is not null ? h.ToList() : null);
            return drop;
        }
    }

    public string Mode => _mode;
    public int ThresholdTokens => _thresholdTokens;
    public bool IsPersistent => _mode == "persistent";

    public MemberContextManager(
        string teamName, string memberContext, int configuredThreshold,
        IChatClient? compactionClient, ChatOptions? compactionOptions)
    {
        _teamName = teamName ?? string.Empty;
        _mode = NormalizeContext(memberContext);
        _thresholdTokens = EffectiveThreshold(configuredThreshold);
        _compactionClient = compactionClient;
        _compactionOptions = compactionOptions;
    }

    /// <summary>Normalize the member-context mode; unknown values fall back to "persistent".</summary>
    public static string NormalizeContext(string? mode)
        => (mode ?? "persistent").Trim().ToLowerInvariant() == "fresh" ? "fresh" : "persistent";

    /// <summary>0 (or negative) configured threshold falls back to <see cref="DefaultThresholdTokens"/>.</summary>
    public static int EffectiveThreshold(int configured)
        => configured > 0 ? configured : DefaultThresholdTokens;

    /// <summary>Pure decision: should a session estimated at <paramref name="estTokens"/> compact?</summary>
    public static bool ShouldCompact(int estTokens, int thresholdTokens)
        => thresholdTokens > 0 && estTokens > thresholdTokens;

    /// <summary>A point-in-time snapshot of every tracked member's state.</summary>
    public IReadOnlyList<MemberState> Snapshot()
    {
        lock (_gate) return _states.Values.Select(Copy).ToList();
    }

    private MemberState GetOrLoad(string member)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(member, out var st)) return st;
            st = MemberState.Load(_teamName, member) ?? new MemberState { Name = member };
            st.Name = member;
            st.Context = _mode;
            _states[member] = st;
            return st;
        }
    }

    private SemaphoreSlim LockFor(string member)
    {
        lock (_gate)
        {
            if (!_memberLocks.TryGetValue(member, out var s))
            {
                s = new SemaphoreSlim(1, 1);
                _memberLocks[member] = s;
            }
            return s;
        }
    }

    /// <summary>
    /// Run one task for <paramref name="member"/> through <paramref name="runWorker"/>, passing the
    /// correct <c>cleanSession</c> flag for the configured mode (fresh =&gt; clean every time;
    /// persistent =&gt; warm/accumulating). For persistent members, after the task the member's
    /// session is estimated and compacted if it exceeds the threshold. Member state is updated and
    /// persisted throughout.
    /// </summary>
    public async Task<string> RunAsync(
        string member, Func<bool, Task<string>> runWorker, CancellationToken ct)
    {
        var st = GetOrLoad(member);
        lock (_gate) { st.Status = "running"; st.LastActive = DateTimeOffset.UtcNow; }
        st.Save(_teamName);

        // fresh => clean session each task; persistent => keep the warm session.
        bool cleanSession = !IsPersistent;
        string result;
        bool completed = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            result = await runWorker(cleanSession);
            ct.ThrowIfCancellationRequested();
            completed = true;
        }
        finally
        {
            lock (_gate)
            {
                if (completed) st.CompletedTasks++;
                st.Status = "idle";
                st.CurrentTask = null;
                st.LastActive = DateTimeOffset.UtcNow;
            }
            st.Save(_teamName);
        }

        if (IsPersistent)
        {
            await ApplyDropsAsync(member, ct);   // /insight max; separate step, compaction below is unchanged
            await MaybeCompactAsync(member, st, ct);
            lock (_gate) st.TasksSinceCompaction++;
        }

        st.Save(_teamName);
        return result;
    }

    /// <summary>
    /// /insight max: apply the member's queued context_drop requests to its warm session now that its task has
    /// finished (the session is idle), under the per-member lock. A recovery snapshot is saved first (same writer
    /// as /prune); without one nothing is dropped. No-op when nothing is queued.
    /// </summary>
    private async Task ApplyDropsAsync(string member, CancellationToken ct)
    {
        Insight.ContextDrop? drop;
        lock (_gate) _drops.TryGetValue(member, out drop);
        if (drop is not { PendingCount: > 0 } || !MultiAgentOrchestrator.Specialists.TryGetValue(member, out var specialist)) return;
        var sem = LockFor(member);
        await sem.WaitAsync(ct);
        try
        {
            if (!specialist.Session.TryGetInMemoryChatHistory(out var live) || live is null) { drop.Clear(); return; }
            var originals = live.ToList();
            var fs = App.Config.Filesystem;
            var serialized = await specialist.Agent.SerializeSessionAsync(specialist.Session, cancellationToken: ct);
            string recovery = ContextPruneSession.SaveSnapshot(serialized, fs?.SandboxPath ?? "", fs?.AllowedPaths ?? [],
                $"pre-drop-{ContextPruneSession.SessionTag(_teamName + "-" + member)}", SessionSnapshotText.FullText(originals));
            var (messages, dropped, saved) = drop.Apply(originals, recovery);
            if (dropped == 0) return;
            specialist.Session.SetInMemoryChatHistory(messages);
            MuxConsole.WriteMuted($"  [teams] {member} dropped {dropped} context block(s) (~{saved / 2.5:N0} tokens); recovery: {recovery}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            drop.Clear();
            MuxConsole.WriteMuted($"  [teams] {member} context drop skipped (no recovery snapshot: {ex.Message}).");
        }
        finally { sem.Release(); }
    }

    /// <summary>
    /// Estimate the member's warm session and, if over threshold, summarize + reseed it (the main
    /// agent's compaction mechanism). Serialized per member so a concurrent same-member dispatch can
    /// never corrupt the read-modify-write of the shared session history.
    /// </summary>
    private async Task MaybeCompactAsync(string member, MemberState st, CancellationToken ct)
    {
        if (!MultiAgentOrchestrator.Specialists.TryGetValue(member, out var specialist))
            return;
        var session = specialist.Session;

        var sem = LockFor(member);
        await sem.WaitAsync(ct);
        try
        {
            if (!session.TryGetInMemoryChatHistory(out var history) || history is null || history.Count == 0)
            {
                lock (_gate) st.SessionTokens = 0;
                return;
            }

            int estTokens = Common.EstimateTokenCount(history);
            lock (_gate) st.SessionTokens = estTokens;
            if (!ShouldCompact(estTokens, _thresholdTokens)) return;

            if (_compactionClient is null)
            {
                // No compaction model -> bounded hard reset rather than an unbounded leak.
                session.SetInMemoryChatHistory(new List<ChatMessage>());
                lock (_gate)
                {
                    st.SessionTokens = 0;
                    st.TasksSinceCompaction = 0;
                    st.Compactions++;
                }
                MuxConsole.WriteMuted($"  [teams] {member} context reset (~{estTokens:N0} tokens; no compaction model configured).");
                return;
            }

            try
            {
                var summary = await ResultCompactor.CompactConversationAsync(history, _compactionClient, _compactionOptions);
                var reseeded = new List<ChatMessage> { new(ChatRole.User, summary.Text) };
                session.SetInMemoryChatHistory(reseeded);
                int after = Common.EstimateTokenCount(reseeded);
                lock (_gate)
                {
                    st.SessionTokens = after;
                    st.TasksSinceCompaction = 0;
                    st.Compactions++;
                }
                MuxConsole.WriteMuted($"  [teams] {member} context compacted: {estTokens:N0} -> {after:N0} tokens.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                MuxConsole.WriteMuted($"  [teams] {member} compaction failed ({ex.Message}); keeping warm session.");
            }
        }
        finally { sem.Release(); }
    }

    private static MemberState Copy(MemberState s) => new()
    {
        Name = s.Name, Status = s.Status, Context = s.Context, CurrentTask = s.CurrentTask,
        CompletedTasks = s.CompletedTasks, TasksSinceCompaction = s.TasksSinceCompaction,
        Compactions = s.Compactions, SessionTokens = s.SessionTokens, LastActive = s.LastActive,
    };
}
