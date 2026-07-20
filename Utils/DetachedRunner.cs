using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

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

        DetachedJob job;
        lock (_gate)
        {
            job = new DetachedJob
            {
                Id = $"bg{++_seq}",
                Agent = who,
                Goal = goal.Trim(),
                Cts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt),
            };
            _jobs.Add(job);
        }

        int maxIters = ExecutionLimits.Current.MaxSubAgentIterations;
        if (maxIters <= 0) maxIters = 8;

        job.Task = Task.Run(async () =>
        {
            try
            {
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
                lock (_gate)
                {
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
}
