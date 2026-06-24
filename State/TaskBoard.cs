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
    public TeamTask Create(string subject, string description, IEnumerable<string>? blockedBy = null)
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
            };

            foreach (var depId in deps)
                if (_tasks.TryGetValue(depId, out var dep) && !dep.Blocks.Contains(id))
                {
                    dep.Blocks.Add(id);
                    Persist(dep);
                }

            if (deps.Any(d => _tasks.TryGetValue(d, out var dep) && dep.Status != TeamTaskStatus.Done))
                task.Status = TeamTaskStatus.Blocked;

            _tasks[id] = task;
            Persist(task);
            return Clone(task);
        }
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
    };
}
