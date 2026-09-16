using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>Lifecycle state of one detached background job.</summary>
public enum DetachedStatus { Running, Done, Failed, Cancelled }

/// <summary>
/// One detached (background) agent job: an agent running a single goal headless, off the main
/// input loop, so the user keeps typing to their foreground agent while this runs. Surfaces in the
/// `\` Agent View via the existing sub-agent capture path (its display name is tagged so the user
/// can tell it apart from foreground delegations).
/// </summary>
public sealed class DetachedJob
{
    public required string Id { get; init; }
    public required string Agent { get; init; }
    public required string Goal { get; init; }
    public DetachedStatus Status { get; set; } = DetachedStatus.Running;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Finished { get; set; }
    public string? Result { get; set; }
    /// <summary>DelegationStore handle (d:Agent#N) once the finished result is spilled, so the lead
    /// can pull detail on demand via read_delegation instead of carrying the blob in context.</summary>
    public string? Handle { get; set; }
    /// <summary>Concise live-activity text while running (e.g. "working", "calling: read_file").
    /// Mirrored from the capture lane so /background jobs shows real progress, not bare "Running".</summary>
    public string? LiveActivity { get; set; }
    /// <summary>Rolling ~120-char tail of the streamed output, retained after finish so a completed
    /// job's row still previews what it produced.</summary>
    public string? Tail { get; set; }
    internal CancellationTokenSource Cts { get; init; } = new();
    internal Task? Task { get; set; }

    /// <summary>The capture/display name shown in the `\` Agent View (bg-prefixed for clarity).</summary>
    public string DisplayName => $"bg:{Agent}";
}

/// <summary>
/// Launches and tracks detached background agent jobs. A detached job runs the chosen agent on a
/// goal through the existing headless sub-agent execution path (<see cref="MultiAgentOrchestrator.RunSubAgentAsync"/>),
/// which is already wrapped by <c>BeginSubAgentCapture</c> - so the job streams into a buffer (not
/// the foreground live region) and appears in the `\` Agent View for free, with zero new
/// render-region contention. Fire-and-forget: the main menu / session loop never awaits it.
///
/// This is the in-house "run an agent/team detached" primitive: it frees the user input loop while
/// keeping the work watchable. It deliberately reuses the sub-agent path rather than the interactive
/// <c>ChatAgentAsync</c> loop, which owns the screen and would fight the foreground.
/// </summary>
public static class DetachedRunner
{
    private static readonly object _gate = new();
    private static readonly List<DetachedJob> _jobs = new();
    private static int _seq;

    /// <summary>A point-in-time snapshot of all jobs (newest last), for /detach jobs + status.</summary>
    public static IReadOnlyList<DetachedJob> Jobs()
    {
        lock (_gate) return _jobs.ToList();
    }

    /// <summary>Count of jobs still running.</summary>
    public static int RunningCount()
    {
        lock (_gate) return _jobs.Count(j => j.Status == DetachedStatus.Running);
    }

    /// <summary>
    /// Launch <paramref name="agent"/> on <paramref name="goal"/> as a detached background job.
    /// Builds the specialist registry if needed so the agent resolves, then fires a background Task
    /// running the headless sub-agent path. Returns the job (already Running), or null with a
    /// written error when the agent cannot be resolved. Non-blocking.
    /// </summary>
    public static async Task<DetachedJob?> LaunchAsync(
        string agent, string goal,
        Func<string, IChatClient> chatClientFactory,
        Dictionary<string, string> agentModels,
        CancellationToken sessionCt)
    {
        sessionCt.ThrowIfCancellationRequested();
        ExecutionCancellation.Current.ThrowIfCancellationRequested();
        var who = (agent ?? string.Empty).Trim();
        if (who.Length == 0) { MuxConsole.WriteWarning("[detach] No agent specified."); return null; }
        if (string.IsNullOrWhiteSpace(goal)) { MuxConsole.WriteWarning("[detach] No goal specified."); return null; }

        // Ensure the member registry is built so RunSubAgentAsync can resolve the agent.
        if (!MultiAgentOrchestrator.Specialists.ContainsKey(who))
        {
            try
            {
                await MultiAgentOrchestrator.BuildSpecialists(
                    agentModels, chatClientFactory,
                    (App.McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList());
            }
            catch (OperationCanceledException) when (sessionCt.IsCancellationRequested || ExecutionCancellation.Current.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"[detach] Failed to build agents: {ex.Message}");
                return null;
            }
        }

        if (!MultiAgentOrchestrator.Specialists.TryGetValue(who, out var specialist))
        {
            var available = string.Join(", ", MultiAgentOrchestrator.Specialists.Keys.Where(k => k != "Orchestrator"));
            MuxConsole.WriteWarning($"[detach] Unknown agent '{who}'. Available: {available}");
            return null;
        }

        sessionCt.ThrowIfCancellationRequested();
        ExecutionCancellation.Current.ThrowIfCancellationRequested();
        DetachedJob job;
        lock (_gate)
        {
            job = new DetachedJob
            {
                Id = $"bg{++_seq}",
                Agent = who,
                Goal = goal.Trim(),
                Cts = ExecutionCancellation.Link(sessionCt),
            };
            _jobs.Add(job);
        }

        int maxIters = ExecutionLimits.Current.MaxSubAgentIterations;
        if (maxIters <= 0) maxIters = 8;

        job.Task = Task.Run(async () =>
        {
            try
            {
                using var jobOwnership = ExecutionCancellation.Enter(job.Cts.Token);
                job.Cts.Token.ThrowIfCancellationRequested();
                // Tag every NDJSON frame this background job emits with a serve origin+lane so the web
                // app routes them into the job's own sub-agent card instead of interleaving them into
                // the main viewport (mirrors how DaemonRunner tags its lane via BeginServeOrigin). The
                // AsyncLocal flows into RunSubAgentAsync's child tasks. Absent under classic TUI (no-op
                // on the emit path when not serving); byte-identical legacy frames when untagged.
                using var _originScope = MuxConsole.BeginServeOrigin("subagent", $"sub:{job.Agent}");
                // Poll the capture lane's live activity onto the job each loop iteration so
                // /background jobs shows real progress (tool calls, current action) not bare
                // "Running". Best-effort: the lane may not exist outside TUI capture.
                try
                {
                    var live = MuxConsole.GetLiveSubAgentDetail(job.Agent);
                    if (live is { } d)
                    {
                        job.LiveActivity = string.IsNullOrWhiteSpace(d.LiveStatus) ? "working" : d.LiveStatus;
                        if (!string.IsNullOrWhiteSpace(d.Tail)) job.Tail = d.Tail;
                    }
                }
                catch { /* activity is best-effort */ }
                var (raw, status, _, _) = await MultiAgentOrchestrator.RunSubAgentAsync(
                    specialist, job.Goal, maxIters, job.Cts.Token, prodMode: false, hiddenCapture: true);
                job.Cts.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    job.Cts.Token.ThrowIfCancellationRequested();
                    job.Result = raw;
                    job.Status = status == "success" ? DetachedStatus.Done : DetachedStatus.Failed;
                    job.Finished = DateTimeOffset.UtcNow;
                    // Spill the finished result into the delegation store so the lead can read it
                    // surgically via read_delegation (d:Agent#N handle) instead of a giant inline dump.
                    job.Handle = DelegationStore.Persist(
                        DelegationStore.CurrentScope, job.Agent, raw ?? "", status, summary: null, artifacts: null)?.Handle;
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate) { job.Status = DetachedStatus.Cancelled; job.Finished = DateTimeOffset.UtcNow; }
            }
            catch (Exception ex)
            {
                lock (_gate) { job.Status = DetachedStatus.Failed; job.Result = ex.Message; job.Finished = DateTimeOffset.UtcNow; }
            }
            finally { job.Cts.Dispose(); }
        });

        return job;
    }

    /// <summary>Cancel one job by id. Returns false when no running job has that id.</summary>
    public static bool Cancel(string id)
    {
        DetachedJob? job;
        lock (_gate) job = _jobs.FirstOrDefault(j => j.Id.Equals((id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (job is null || job.Status != DetachedStatus.Running) return false;
        try { job.Cts.Cancel(); } catch { /* already disposed */ }
        return true;
    }

    /// <summary>Cancel every running job (called on session/app teardown).</summary>
    public static void CancelAll()
    {
        lock (_gate)
            foreach (var j in _jobs.Where(j => j.Status == DetachedStatus.Running))
                try { j.Cts.Cancel(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Handle a "/background" (alias "/bg") command line from the in-session meta-loop. Subcommands:
    ///   /background jobs                 list background jobs
    ///   /background cancel &lt;id&gt;          cancel a running job
    ///   /background &lt;agent&gt; &lt;goal...&gt;     launch agent on goal as a background job
    /// All output goes through MuxConsole. Non-blocking - a launched job runs in the background and
    /// surfaces in the `\` Agent View tagged bg:&lt;agent&gt;. (The `/detach` name is reserved for the
    /// future "detach the current LIVE session" feature, which is a different mechanism.)
    /// </summary>
    public static async Task RunCommand(
        string raw, Func<string, IChatClient>? chatClientFactory,
        Dictionary<string, string> agentModels, CancellationToken sessionCt)
    {
        var line = (raw ?? string.Empty).Trim();
        foreach (var pfx in new[] { "/background", "/bg" })
            if (line.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
            { line = line.Substring(pfx.Length).Trim(); break; }

        if (line.Length == 0 || line.Equals("jobs", StringComparison.OrdinalIgnoreCase)
            || line.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            // TUI: open the interactive JobView (running AND finished; o reopen / c cancel), so a
            // finished bg agent's output stays reachable after its lane leaves the backslash view.
            // Offline (serve / stdio / non-TUI) the driver is inactive and this returns false, so
            // we fall back to the static text panel.
            if (!MuxConsole.TuiEnterJobView())
                MuxConsole.WritePanel("Detached jobs", Render());
            return;
        }

        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();

        if (verb is "cancel" or "kill" or "stop")
        {
            var id = parts.Length > 1 ? parts[1].Trim() : "";
            if (id.Length == 0) { MuxConsole.WriteWarning("Usage: /background cancel <id>"); return; }
            MuxConsole.WriteMuted(Cancel(id) ? $"[detach] Cancelling {id}." : $"[detach] No running job '{id}'.");
            return;
        }

        if (verb is "help" or "?")
        {
            MuxConsole.WriteMuted("/background <agent> <goal>   run an agent on a goal in the background (watch with \\)");
            MuxConsole.WriteMuted("/background jobs             list background jobs");
            MuxConsole.WriteMuted("/background cancel <id>      cancel a running background job");
            return;
        }

        // Otherwise: /detach <agent> <goal...>
        if (parts.Length < 2)
        {
            MuxConsole.WriteWarning("Usage: /background <agent> <goal>  (or /background jobs | /background cancel <id>)");
            return;
        }
        if (chatClientFactory is null)
        {
            MuxConsole.WriteWarning("[detach] No chat client available in this context.");
            return;
        }
        var agent = parts[0].Trim();
        var goal = parts[1].Trim();
        var job = await LaunchAsync(agent, goal, chatClientFactory, agentModels, sessionCt);
        if (job is not null)
            MuxConsole.WriteSuccess($"[bg] {job.Id} launched: {job.Agent} (running in background; press \\ to view).");
    }

    /// <summary>Render the job list for /detach jobs. Running jobs show their live activity;
    /// finished show a short tail preview, so the text listing carries real status too.</summary>
    public static string Render()
    {
        var jobs = Jobs();
        if (jobs.Count == 0) return "No detached jobs.";
        var sb = new System.Text.StringBuilder();
        foreach (var j in jobs)
        {
            var dur = (j.Finished ?? DateTimeOffset.UtcNow) - j.Started;
            string activity = j.Status == DetachedStatus.Running
                ? (string.IsNullOrWhiteSpace(j.LiveActivity) ? "working" : j.LiveActivity!)
                : (string.IsNullOrWhiteSpace(j.Tail) ? "" : j.Tail!);
            if (activity.Length > 48) activity = activity[..47] + "...";
            sb.Append(j.Id).Append("  [").Append(j.Status).Append("]  ")
              .Append(j.Agent).Append("  ").Append((int)dur.TotalSeconds).Append("s");
            if (activity.Length > 0) sb.Append("  ").Append(activity);
            sb.AppendLine();
            sb.Append("     ").AppendLine(j.Goal.Length > 72 ? j.Goal[..71] + "..." : j.Goal);
        }
        sb.Append("open the interactive viewer: /background jobs  (o reopen \u00b7 c cancel)");
        return sb.ToString();
    }

    // ---- progress waiting (wait_job_progress semantics for delegations) -------------------

    /// <summary>Per-job read cursor: what the lead last saw. A job has "new progress" when its
    /// live tool-call count, activity line, output tail, or status differs from this snapshot.
    /// Kept per PROCESS (one lead per process owns the delegation loop), reset with the jobs.</summary>
    private sealed record ReadCursor(int ToolCalls, string LiveStatus, string Tail, DetachedStatus Status, bool ResultReported);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReadCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);

    private static (int ToolCalls, string LiveStatus, string Tail) LiveOf(DetachedJob j)
    {
        try
        {
            var live = MuxConsole.GetLiveSubAgentDetail(j.Agent);
            if (live is { } d)
                return (d.ToolCalls, d.LiveStatus ?? "", d.Tail ?? "");
        }
        catch { /* live detail is best-effort */ }
        return (0, j.LiveActivity ?? "", j.Tail ?? "");
    }

    private static bool HasNewProgress(DetachedJob j)
    {
        var (tc, status, tail) = LiveOf(j);
        if (!_cursors.TryGetValue(j.Id, out var c))
            return true;   // never read -> everything is new
        if (j.Status != c.Status) return true;
        if (j.Status != DetachedStatus.Running) return !c.ResultReported;
        return tc != c.ToolCalls || status != c.LiveStatus || tail != c.Tail;
    }

    private static void CommitCursor(DetachedJob j)
    {
        var (tc, status, tail) = LiveOf(j);
        bool reported = j.Status != DetachedStatus.Running;
        _cursors[j.Id] = new ReadCursor(tc, status, tail, j.Status, reported);
    }

    /// <summary>
    /// Block up to <paramref name="waitSeconds"/> for NEW progress on the watched jobs (all
    /// running jobs, or one by id): a live tool call landing, the activity line or output tail
    /// changing, or a job reaching a terminal state. Returns the ids that changed (empty on
    /// timeout). Polls every 250ms; returns immediately when something is already unread.
    /// Cancellation (Esc / turn cancel) propagates.
    /// </summary>
    internal static async Task<List<string>> WaitForProgressAsync(string? jobId, int waitSeconds, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, 600));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var jobs = Jobs();
            if (!string.IsNullOrWhiteSpace(jobId))
                jobs = jobs.Where(j => j.Id.Equals(jobId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

            var changed = jobs.Where(HasNewProgress).Select(j => j.Id).ToList();
            if (changed.Count > 0) return changed;
            if (jobs.All(j => j.Status != DetachedStatus.Running)) return changed;   // nothing left to wait on
            if (DateTimeOffset.UtcNow >= deadline) return changed;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reset jobs + read cursors (test isolation).</summary>
    internal static void ResetForTests()
    {
        lock (_gate) { _jobs.Clear(); _seq = 0; }
        _cursors.Clear();
    }

    /// <summary>Register a fabricated job (test seam; no task attached).</summary>
    internal static DetachedJob InjectJobForTests(string agent, DetachedStatus status, string? result = null)
    {
        DetachedJob job;
        lock (_gate)
        {
            job = new DetachedJob { Id = $"bg{++_seq}", Agent = agent, Goal = "test goal", Status = status, Result = result };
            _jobs.Add(job);
        }
        return job;
    }

    /// <summary>
    /// Render the check_delegations report for the given jobs. <paramref name="deltaOnly"/>
    /// (wait mode) reports just the jobs in <paramref name="changedIds"/> so a waiting lead
    /// reads only what is NEW since its last look; snapshot mode keeps the full listing.
    /// Commits read cursors for everything reported.
    /// </summary>
    internal static string RenderCheckReport(string? jobId, IReadOnlyList<string>? changedIds, bool waited, int waitedSeconds)
    {
        var jobs = Jobs();
        if (!string.IsNullOrWhiteSpace(jobId))
            jobs = jobs.Where(j => j.Id.Equals(jobId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        if (jobs.Count == 0)
            return string.IsNullOrWhiteSpace(jobId)
                ? "[check_delegations] No background jobs. Launch some with delegate_parallel(background:true)."
                : $"[check_delegations] No background job with id '{jobId}'.";

        bool deltaOnly = waited && changedIds is { Count: > 0 };
        var report = deltaOnly ? jobs.Where(j => changedIds!.Contains(j.Id, StringComparer.OrdinalIgnoreCase)).ToList() : jobs;

        var sb = new System.Text.StringBuilder();
        int running = jobs.Count(j => j.Status == DetachedStatus.Running);
        if (waited)
            sb.AppendLine(changedIds is { Count: > 0 }
                ? $"[check_delegations] NEW progress on {changedIds.Count} job(s) (waited; {running} running total)."
                : $"[check_delegations] No new progress within {waitedSeconds}s ({running} running). Wait again or keep working.");
        else
            sb.AppendLine($"[check_delegations] {jobs.Count} job(s), {running} still running.");

        foreach (var j in report)
        {
            bool alreadyCollected = _cursors.TryGetValue(j.Id, out var cur)
                && j.Status != DetachedStatus.Running && cur.ResultReported && cur.Status == j.Status;
            if (j.Status == DetachedStatus.Running)
            {
                var elapsed = DateTimeOffset.UtcNow - j.Started;
                var (tc, liveStatus, tail) = LiveOf(j);
                string detail = liveStatus.Length > 0 ? $" \u2014 {liveStatus} (tools: {tc})" : "";
                string tailTxt = tail.Length > 0 ? $"\n    tail: {tail}" : "";
                sb.AppendLine($"- {j.Id} [{j.Agent}] Running ({(int)elapsed.TotalSeconds}s){detail}{tailTxt}");
            }
            else if (alreadyCollected)
            {
                // Result already delivered on a prior read: one line, no re-dump.
                sb.AppendLine($"- {j.Id} [{j.Agent}] {j.Status} (result already collected{(string.IsNullOrWhiteSpace(j.Handle) ? "" : $"; re-read via {j.Handle}")})");
            }
            else
            {
                sb.AppendLine($"- {j.Id} [{j.Agent}] {j.Status}");
                if (!string.IsNullOrWhiteSpace(j.Result))
                {
                    if (!string.IsNullOrWhiteSpace(j.Handle))
                    {
                        var tailTxt = j.Result!.Length > 240 ? "\u2026" + j.Result[^240..] : j.Result;
                        sb.AppendLine($"  result: {j.Result.Length} chars spilled as {j.Handle} (read with read_delegation). tail: {tailTxt}");
                    }
                    else
                    {
                        var r = j.Result!.Length > 4000 ? j.Result[..4000] + "\n... (truncated)" : j.Result;
                        sb.AppendLine($"  result:\n{r}");
                    }
                }
            }
            CommitCursor(j);
        }

        if (running > 0)
            sb.AppendLine("Jobs still running: call check_delegations with waitSeconds (e.g. 30) to BLOCK until real progress instead of sleeping/polling.");
        return sb.ToString();
    }

    /// <summary>
    /// The shared check_delegations tool (single-agent leads, giga leads, and the swarm
    /// orchestrator all grant this same instance's factory output). waitSeconds=0 preserves the
    /// classic instant snapshot; &gt;0 blocks until new progress / a terminal state / timeout,
    /// reporting only what changed since the lead's last read - wait_job_progress semantics for
    /// sub-agents, so leads never burn turns on system_sleep + re-poll loops.
    /// </summary>
    public static Microsoft.Extensions.AI.AIFunction CreateCheckDelegationsTool()
        => Microsoft.Extensions.AI.AIFunctionFactory.Create(
            method: async (
                [System.ComponentModel.Description("Optional job id (e.g. bg3) to check/wait on just one; omit for ALL background jobs.")]
                string? jobId = null,
                [System.ComponentModel.Description("Seconds to BLOCK waiting for new progress (default 0 = instant snapshot). When >0, returns EARLY the moment any watched job produces a new tool call / activity / output or finishes - strictly better than system_sleep + re-poll. Use 15-60.")]
                int waitSeconds = 0
            ) =>
            {
                if (waitSeconds <= 0)
                    return RenderCheckReport(jobId, changedIds: null, waited: false, waitedSeconds: 0);
                var changed = await WaitForProgressAsync(jobId, waitSeconds, ExecutionCancellation.Current).ConfigureAwait(false);
                return RenderCheckReport(jobId, changed, waited: true, waitedSeconds: waitSeconds);
            },
            name: "check_delegations",
            description: "Poll OR WAIT ON background delegations launched via delegate_parallel(background:true) (and /background jobs). " +
                         "Pass a job id to target one, or omit for all. Default (waitSeconds=0) returns an instant snapshot: elapsed, live activity, " +
                         "tool-call count, and a short output tail per running job; finished jobs return results inline when small or as a d:Agent#N " +
                         "handle for read_delegation when large. PREFER waitSeconds>0 while waiting on sub-agents: it BLOCKS until real progress " +
                         "(new tool call, new output, or completion) and returns only what is NEW since your last read - never use system_sleep to wait on delegations.");
}
