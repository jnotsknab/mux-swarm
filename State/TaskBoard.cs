using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuxSwarm.State;

/// <summary>Lifecycle state of a single team task.</summary>
public enum TeamTaskStatus
{
    Pending,
    InProgress,
    Blocked,
    Done,
    Failed,
}

/// <summary>
/// One task on a team's shared board. Carries a dependency graph (Blocks / BlockedBy) and an
/// owner once claimed. Persisted to disk as one JSON file per task so the board survives a
/// resume (Claude-Code task-graph parity). Mutable by design - the board owns synchronization.
/// </summary>
public sealed class TeamTask
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Owning agent name once claimed; null while unclaimed.</summary>
    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    [JsonPropertyName("status")]
    public TeamTaskStatus Status { get; set; } = TeamTaskStatus.Pending;

    /// <summary>Task ids this task blocks (its dependents).</summary>
    [JsonPropertyName("blocks")]
    public List<string> Blocks { get; set; } = [];

    /// <summary>Task ids that must reach Done before this task can be claimed.</summary>
    [JsonPropertyName("blockedBy")]
    public List<string> BlockedBy { get; set; } = [];

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("claimedAt")]
    public DateTimeOffset? ClaimedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Member designated to run this task (auto-run target). Distinct from
    /// <see cref="Owner"/>, which is set atomically when the task is actually claimed.</summary>
    [JsonPropertyName("assignee")]
    public string? Assignee { get; set; }

    /// <summary>Earliest time the auto-runner may start this task. Null = eligible immediately.</summary>
    [JsonPropertyName("startAt")]
    public DateTimeOffset? StartAt { get; set; }
}

/// <summary>
/// A team's shared task board: a persisted, dependency-gated, file-locked task graph. Mirrors
/// Claude-Code's team task list. Claiming is atomic under a per-board lock so two members can
/// never own the same task; a task whose <see cref="TeamTask.BlockedBy"/> are not all Done
/// cannot be claimed; completing a blocker auto-unblocks its dependents. Every mutation is
/// flushed to <c>{root}/tasks/{id}.json</c> so the board reloads intact after a resume.
///
/// The in-process lock guarantees claim-safety within one runtime; the on-disk JSON is the
/// durable record. (Cross-process locking via OS file locks is an M-later concern - a single
/// runtime owns a team session today.)
/// </summary>
public sealed class TaskBoard
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, TeamTask> _tasks = new(StringComparer.Ordinal);
    private readonly string _tasksDir;
    private int _seq;

    /// <summary>The team this board belongs to (display name, may carry a giga: prefix).</summary>
    public string Team { get; }

    /// <summary>The on-disk tasks directory ({teamRoot}/tasks).</summary>
    public string TasksDirectory => _tasksDir;

    private TaskBoard(string team, string teamRoot)
    {
        Team = team;
        _tasksDir = Path.Combine(teamRoot, "tasks");
    }

    /// <summary>
    /// Open (or create) the board rooted at <paramref name="teamRoot"/>, loading any persisted
    /// tasks from <c>{teamRoot}/tasks/*.json</c> so a resumed team picks up exactly where it
    /// left off. Unparseable task files are skipped rather than aborting the load.
    /// </summary>
    public static TaskBoard Open(string team, string teamRoot)
    {
        var board = new TaskBoard(team, teamRoot);
        Directory.CreateDirectory(board._tasksDir);
        foreach (var file in Directory.EnumerateFiles(board._tasksDir, "*.json"))
        {
            try
            {
                var t = JsonSerializer.Deserialize<TeamTask>(File.ReadAllText(file), JsonOpts);
                if (t is not null && !string.IsNullOrEmpty(t.Id))
                {
                    board._tasks[t.Id] = t;
                    if (int.TryParse(t.Id.TrimStart('t', 'T', '#'), out var n) && n > board._seq)
                        board._seq = n;
                }
            }
            catch { /* skip a corrupt task file rather than fail the whole board */ }
        }
        return board;
    }

    /// <summary>A point-in-time copy of every task, ordered by creation time.</summary>
    public IReadOnlyList<TeamTask> Snapshot()
    {
        lock (_gate)
            return _tasks.Values
                .OrderBy(t => t.Created)
                .Select(Clone)
                .ToList();
    }

    /// <summary>Count of tasks in each terminal/active bucket, for the strip summary.</summary>
    public (int Total, int Done, int InProgress, int Blocked, int Failed) Tally()
    {
        lock (_gate)
        {
            int total = _tasks.Count, done = 0, prog = 0, blocked = 0, failed = 0;
            foreach (var t in _tasks.Values)
                switch (t.Status)
                {
                    case TeamTaskStatus.Done: done++; break;
                    case TeamTaskStatus.InProgress: prog++; break;
                    case TeamTaskStatus.Blocked: blocked++; break;
                    case TeamTaskStatus.Failed: failed++; break;
                }
            return (total, done, prog, blocked, failed);
        }
    }

    /// <summary>
    /// Create a task. <paramref name="blockedBy"/> ids that exist are wired both ways (the
    /// blocker's Blocks list gains this task) and the new task starts Blocked if any blocker is
    /// not yet Done, else Pending. Returns the assigned id.
    /// </summary>
    public TeamTask Create(string subject, string description, IEnumerable<string>? blockedBy = null,
        string? assignee = null, DateTimeOffset? startAt = null)
    {
        lock (_gate)
        {
            var id = $"t{++_seq}";
            var deps = (blockedBy ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var task = new TeamTask
            {
                Id = id,
                Subject = subject ?? string.Empty,
                Description = description ?? string.Empty,
                BlockedBy = deps,
                Status = TeamTaskStatus.Pending,
                Assignee = string.IsNullOrWhiteSpace(assignee) ? null : assignee!.Trim(),
                StartAt = startAt,
            };

            foreach (var depId in deps)
                if (_tasks.TryGetValue(depId, out var dep) && !dep.Blocks.Contains(id))
                {
                    dep.Blocks.Add(id);
                    Persist(dep);
                }

            // A dependency that is not Done - INCLUDING an unknown/stale id that resolves to no
            // task - leaves this task Blocked. A dangling dep id must never be treated as
            // satisfied (that would silently bypass gating); it blocks until the id is created +
            // completed or the dep is removed. Mirrors DepsSatisfied_NoLock / IsClaimable_NoLock.
            if (deps.Any(d => !_tasks.TryGetValue(d, out var dep) || dep.Status != TeamTaskStatus.Done))
                task.Status = TeamTaskStatus.Blocked;

            _tasks[id] = task;
            Persist(task);
            return Clone(task);
        }
    }

    /// <summary>True when a task with this id exists on the board.</summary>
    public bool Exists(string id)
    {
        lock (_gate) return _tasks.ContainsKey((id ?? string.Empty).Trim());
    }

    /// <summary>
    /// The subset of <paramref name="depIds"/> that do NOT resolve to a real task on the board.
    /// Used to reject a create/block with stale or typo'd dependency ids LOUDLY rather than
    /// silently treating an unknown dep as satisfied (which would bypass dependency gating).
    /// </summary>
    public IReadOnlyList<string> UnknownDeps(IEnumerable<string>? depIds)
    {
        lock (_gate)
            return (depIds ?? Enumerable.Empty<string>())
                .Select(d => (d ?? string.Empty).Trim())
                .Where(d => d.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Where(d => !_tasks.ContainsKey(d))
                .ToList();
    }

    /// <summary>True when a task exists, is unclaimed, and all its blockers are Done.</summary>
    public bool IsClaimable(string id)
    {
        lock (_gate)
            return _tasks.TryGetValue(id, out var t) && IsClaimable_NoLock(t);
    }

    private bool IsClaimable_NoLock(TeamTask t)
        => t.Owner is null
           && (t.Status == TeamTaskStatus.Pending || t.Status == TeamTaskStatus.Blocked)
           && t.BlockedBy.All(d => _tasks.TryGetValue(d, out var dep) && dep.Status == TeamTaskStatus.Done);

    /// <summary>
    /// Atomically claim a task for <paramref name="owner"/>. Returns true only if the task was
    /// unclaimed AND unblocked at claim time - so two callers racing for the same task can never
    /// both win. On success the task flips to InProgress and is flushed to disk.
    /// </summary>
    public bool TryClaim(string id, string owner, out string reason)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var t)) { reason = $"no such task '{id}'"; return false; }
            if (t.Owner is not null) { reason = $"already owned by {t.Owner}"; return false; }
            if (!IsClaimable_NoLock(t)) { reason = "blocked by unfinished dependencies"; return false; }

            t.Owner = owner;
            t.Status = TeamTaskStatus.InProgress;
            t.ClaimedAt = DateTimeOffset.UtcNow;
            Persist(t);
            reason = "claimed";
            return true;
        }
    }

    /// <summary>
    /// Update a task's status. Completing a task (Done) auto-unblocks any dependent whose
    /// blockers are now all Done (Blocked -> Pending). All touched tasks are flushed.
    /// </summary>
    public bool SetStatus(string id, TeamTaskStatus status, out string reason)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var t)) { reason = $"no such task '{id}'"; return false; }

            t.Status = status;
            if (status == TeamTaskStatus.Done) t.CompletedAt = DateTimeOffset.UtcNow;
            Persist(t);

            if (status == TeamTaskStatus.Done)
                foreach (var depId in t.Blocks)
                    if (_tasks.TryGetValue(depId, out var dep)
                        && dep.Status == TeamTaskStatus.Blocked
                        && dep.BlockedBy.All(d => _tasks.TryGetValue(d, out var b) && b.Status == TeamTaskStatus.Done))
                    {
                        dep.Status = TeamTaskStatus.Pending;
                        Persist(dep);
                    }

            reason = "updated";
            return true;
        }
    }

    /// <summary>
    /// Reassign a task to a different member: clears the current owner + InProgress claim, sets
    /// the new Assignee, and returns it to Pending/Blocked (re-derived from its deps). Use to move
    /// in-flight or stuck work to another member. Returns false for an unknown task.
    /// </summary>
    public bool Reassign(string id, string? assignee, out string reason)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var t)) { reason = $"no such task '{id}'"; return false; }
            t.Owner = null;
            t.ClaimedAt = null;
            t.Assignee = string.IsNullOrWhiteSpace(assignee) ? null : assignee!.Trim();
            t.Status = DepsSatisfied_NoLock(t) ? TeamTaskStatus.Pending : TeamTaskStatus.Blocked;
            Persist(t);
            reason = t.Assignee is null ? "unassigned" : $"reassigned to {t.Assignee}";
            return true;
        }
    }

    /// <summary>Clear a task's owner/claim and return it to Pending/Blocked, keeping its Assignee.
    /// (Reassign with a null assignee also drops the designation.)</summary>
    public bool Unassign(string id, out string reason)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var t)) { reason = $"no such task '{id}'"; return false; }
            t.Owner = null;
            t.ClaimedAt = null;
            t.Status = DepsSatisfied_NoLock(t) ? TeamTaskStatus.Pending : TeamTaskStatus.Blocked;
            Persist(t);
            reason = "unassigned";
            return true;
        }
    }

    /// <summary>
    /// Reopen a Done/Failed task: clears owner + completion and returns it to Pending/Blocked. Any
    /// dependent that had auto-unblocked off this task is re-blocked (it is no longer Done), so the
    /// dependency graph stays consistent. Returns false for an unknown task.
    /// </summary>
    public bool Reopen(string id, out string reason)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var t)) { reason = $"no such task '{id}'"; return false; }
            t.Owner = null;
            t.ClaimedAt = null;
            t.CompletedAt = null;
            t.Status = DepsSatisfied_NoLock(t) ? TeamTaskStatus.Pending : TeamTaskStatus.Blocked;
            Persist(t);

            // This task is no longer Done, so re-block any dependent that relied on it.
            foreach (var depId in t.Blocks)
                if (_tasks.TryGetValue(depId, out var dep)
                    && (dep.Status == TeamTaskStatus.Pending || dep.Status == TeamTaskStatus.InProgress)
                    && !DepsSatisfied_NoLock(dep))
                {
                    dep.Owner = null;
                    dep.ClaimedAt = null;
                    dep.Status = TeamTaskStatus.Blocked;
                    Persist(dep);
                }

            reason = "reopened";
            return true;
        }
    }

    /// <summary>Remove a single task from the board + disk, unwiring it from any dependency links.
    /// Returns false for an unknown task.</summary>
    public bool Remove(string id, out string reason)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var t)) { reason = $"no such task '{id}'"; return false; }
            // Unwire links: drop this id from every other task's Blocks/BlockedBy.
            foreach (var other in _tasks.Values)
            {
                bool touched = other.Blocks.Remove(id) | other.BlockedBy.Remove(id);
                if (touched)
                {
                    // A dependent that just lost its last blocker becomes claimable again.
                    if (other.Status == TeamTaskStatus.Blocked && DepsSatisfied_NoLock(other))
                        other.Status = TeamTaskStatus.Pending;
                    Persist(other);
                }
            }
            _tasks.Remove(id);
            TryDeleteFile(id);
            reason = "removed";
            return true;
        }
    }

    /// <summary>Remove every task from the board + disk. Returns the number cleared.</summary>
    public int RemoveAll()
    {
        lock (_gate)
        {
            int n = _tasks.Count;
            foreach (var id in _tasks.Keys.ToList()) TryDeleteFile(id);
            _tasks.Clear();
            return n;
        }
    }

    /// <summary>One unassigned task to display in info: a deep snapshot of a single task, or null.</summary>
    public TeamTask? Get(string id)
    {
        lock (_gate)
            return _tasks.TryGetValue(id, out var t) ? Clone(t) : null;
    }

    /// <summary>
    /// The next task the auto-runner should start: the earliest-created task that is unowned,
    /// dependency-satisfied, has a designated <see cref="TeamTask.Assignee"/>, and is past its
    /// <see cref="TeamTask.StartAt"/> time - EXCLUDING any whose assignee already appears in
    /// <paramref name="busyAssignees"/> (one in-flight task per member). Null when nothing is
    /// runnable right now. Returned as a deep clone.
    /// </summary>
    public TeamTask? NextRunnable(DateTimeOffset now, IReadOnlySet<string> busyAssignees)
    {
        lock (_gate)
        {
            var pick = _tasks.Values
                .Where(t => t.Owner is null
                            && t.Assignee is not null
                            && (t.Status == TeamTaskStatus.Pending || t.Status == TeamTaskStatus.Blocked)
                            && DepsSatisfied_NoLock(t)
                            && (t.StartAt is null || t.StartAt <= now)
                            && !busyAssignees.Contains(t.Assignee!))
                .OrderBy(t => t.Created)
                .FirstOrDefault();
            return pick is null ? null : Clone(pick);
        }
    }

    /// <summary>
    /// The next task a specific <paramref name="member"/> may claim under its own self-claim loop:
    /// the earliest-created task that is unowned, dependency-satisfied, and past its
    /// <see cref="TeamTask.StartAt"/> time, AND eligible for this member under the pickup policy.
    /// When <paramref name="openPool"/> is false the task's <see cref="TeamTask.Assignee"/> must be
    /// this member (assigned-only); when true the member may also take any UNASSIGNED ready task
    /// (a self-organizing pool). Returns a deep clone, or null when nothing is claimable now. The
    /// actual claim still goes through <see cref="TryClaim"/>, so two members racing for the same
    /// open task can never both win.
    /// </summary>
    public TeamTask? NextClaimableFor(string member, bool openPool, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(member)) return null;
        lock (_gate)
        {
            var pick = _tasks.Values
                .Where(t => t.Owner is null
                            && (t.Status == TeamTaskStatus.Pending || t.Status == TeamTaskStatus.Blocked)
                            && DepsSatisfied_NoLock(t)
                            && (t.StartAt is null || t.StartAt <= now)
                            && (string.Equals(t.Assignee, member, StringComparison.OrdinalIgnoreCase)
                                || (openPool && t.Assignee is null)))
                .OrderBy(t => t.Created)
                .FirstOrDefault();
            return pick is null ? null : Clone(pick);
        }
    }

    /// <summary>True when every blocker of <paramref name="t"/> is Done.</summary>
    private bool DepsSatisfied_NoLock(TeamTask t)
        => t.BlockedBy.All(d => _tasks.TryGetValue(d, out var dep) && dep.Status == TeamTaskStatus.Done);

    private void TryDeleteFile(string id)
    {
        try { var f = Path.Combine(_tasksDir, id + ".json"); if (File.Exists(f)) File.Delete(f); }
        catch { /* best-effort; the in-memory removal is authoritative this session */ }
    }

    private void Persist(TeamTask t)
    {
        try
        {
            Directory.CreateDirectory(_tasksDir);
            var tmp = Path.Combine(_tasksDir, t.Id + ".json.tmp");
            var dst = Path.Combine(_tasksDir, t.Id + ".json");
            File.WriteAllText(tmp, JsonSerializer.Serialize(t, JsonOpts));
            File.Move(tmp, dst, overwrite: true);
        }
        catch { /* best-effort durability; in-memory board stays authoritative this session */ }
    }

    private static TeamTask Clone(TeamTask t) => new()
    {
        Id = t.Id, Subject = t.Subject, Description = t.Description, Owner = t.Owner,
        Status = t.Status, Blocks = new List<string>(t.Blocks), BlockedBy = new List<string>(t.BlockedBy),
        Created = t.Created, ClaimedAt = t.ClaimedAt, CompletedAt = t.CompletedAt,
        Assignee = t.Assignee, StartAt = t.StartAt,
    };
}
