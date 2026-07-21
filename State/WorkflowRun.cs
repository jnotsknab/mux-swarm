using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MuxSwarm.Utils;

namespace MuxSwarm.State;

/// <summary>Lifecycle of one workflow run (static or dynamic).</summary>
public enum WorkflowRunState { Running, Done, Failed, Cancelled }

/// <summary>One task row inside a workflow section (a sub-agent unit of work).</summary>
public sealed class WorkflowTask
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "pending"; // pending|running|done|failed
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("secs")] public int? Secs { get; set; }
    [JsonPropertyName("tools")] public int? Tools { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
}

/// <summary>One section/phase of a workflow: a named panel grouping its tasks.</summary>
public sealed class WorkflowSection
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tasks")] public List<WorkflowTask> Tasks { get; set; } = new();
}

/// <summary>
/// The manifest a workflow run declares up front: its shape (sections/tasks), mode, and
/// identity. Dynamic runs write this from the generated script (via the SDK helper); static
/// runs synthesize it from the workflow file's steps. The viewer renders panels from it.
/// </summary>
public sealed class WorkflowRunManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "static"; // static|dynamic
    [JsonPropertyName("started")] public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("sections")] public List<WorkflowSection> Sections { get; set; } = new();
}

/// <summary>
/// One live (or finished) workflow run tracked by the registry. For dynamic runs the engine
/// tails the run directory's status.ndjson (appended by the driver script through the SDK) and
/// folds updates into the manifest snapshot; static runs update in-process. The viewer reads
/// snapshots only - it never touches the filesystem itself.
/// </summary>
public sealed class WorkflowRun
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Mode { get; init; }
    public WorkflowRunState State { get; set; } = WorkflowRunState.Running;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Finished { get; set; }
    public string? Error { get; set; }
    /// <summary>Run directory (manifest.json + status.ndjson + script for dynamic runs).</summary>
    public string? RunDir { get; init; }
    /// <summary>The dynamic driver process, when this run owns one.</summary>
    internal Process? Driver { get; set; }
    /// <summary>Mutable manifest snapshot (sections/tasks with live statuses).</summary>
    public WorkflowRunManifest Manifest { get; set; } = new();
    /// <summary>Bytes of status.ndjson already folded in (tail cursor).</summary>
    internal long StatusCursor;
    /// <summary>Rolling tail of recent status lines for the viewer's footer strip.</summary>
    public ConcurrentQueue<string> RecentEvents { get; } = new();
}

/// <summary>
/// Registry + status plane for workflow runs (v0.12.4). ONE registry serves both modes:
/// static runs report in-process; dynamic runs are child mux processes driven by a generated
/// SDK script, observed through a file-based journal (crash-safe, cross-process, no ports):
///   Runtime/workflows/runs/&lt;id&gt;/manifest.json   - declared shape (sections/tasks)
///   Runtime/workflows/runs/&lt;id&gt;/status.ndjson    - appended events: {"task":"t1","status":"running"|"done"|"failed","detail":...}
///                                                    or {"run":"done"|"failed","error":...}
/// The tailer polls lazily (on snapshot access, throttled) so idle costs nothing.
/// </summary>
public static class WorkflowRunRegistry
{
    private static readonly object _gate = new();
    private static readonly List<WorkflowRun> _runs = new();
    private static DateTime _lastPoll = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const int MaxRecentEvents = 8;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Register a run (already started). Returns the run for caller-side updates.</summary>
    public static WorkflowRun Register(WorkflowRun run)
    {
        lock (_gate) _runs.Add(run);
        return run;
    }

    /// <summary>Snapshot all runs (newest last), folding in any new dynamic status first.</summary>
    public static IReadOnlyList<WorkflowRun> Snapshot()
    {
        PollDynamic();
        lock (_gate) return _runs.ToList();
    }

    public static WorkflowRun? Find(string id)
    {
        lock (_gate) return _runs.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static int RunningCount()
    {
        PollDynamic();
        lock (_gate) return _runs.Count(r => r.State == WorkflowRunState.Running);
    }

    /// <summary>Cancel a running dynamic run by killing its driver tree. Static runs cancel
    /// through their own turn token; this flags them for the viewer either way.</summary>
    public static bool Cancel(string id)
    {
        var run = Find(id);
        if (run is null || run.State != WorkflowRunState.Running) return false;
        try { if (run.Driver is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
        run.State = WorkflowRunState.Cancelled;
        run.Finished = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>Fold new status.ndjson lines + driver exits into every running dynamic run.
    /// Throttled; safe to call from render paths.</summary>
    public static void PollDynamic()
    {
        List<WorkflowRun> dyn;
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastPoll < PollInterval) return;
            _lastPoll = DateTime.UtcNow;
            dyn = _runs.Where(r => r.Mode == "dynamic" && r.State == WorkflowRunState.Running).ToList();
        }
        foreach (var run in dyn)
        {
            try { TailStatus(run); } catch { /* journal is best-effort */ }
            // Driver exit with no terminal status line = the script died (or finished silently).
            if (run.State == WorkflowRunState.Running && run.Driver is { HasExited: true } p)
            {
                run.State = p.ExitCode == 0 ? WorkflowRunState.Done : WorkflowRunState.Failed;
                if (p.ExitCode != 0) run.Error ??= $"driver exited {p.ExitCode}";
                run.Finished = DateTimeOffset.UtcNow;
            }
        }
    }

    private static void TailStatus(WorkflowRun run)
    {
        if (run.RunDir is null) return;
        // Manifest may be (re)written by the script after registration - pick it up once present.
        if (run.Manifest.Sections.Count == 0)
        {
            var mp = Path.Combine(run.RunDir, "manifest.json");
            if (File.Exists(mp))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<WorkflowRunManifest>(File.ReadAllText(mp), JsonOpts);
                    if (m is not null && m.Sections.Count > 0) run.Manifest = m;
                }
                catch { /* partial write - retry next poll */ }
            }
        }
        var sp = Path.Combine(run.RunDir, "status.ndjson");
        if (!File.Exists(sp)) return;
        using var fs = new FileStream(sp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length <= run.StatusCursor) return;
        fs.Seek(run.StatusCursor, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        string? line;
        long consumed = run.StatusCursor;
        while ((line = sr.ReadLine()) is not null)
        {
            consumed += System.Text.Encoding.UTF8.GetByteCount(line) + 1; // approx incl newline
            if (string.IsNullOrWhiteSpace(line)) continue;
            ApplyStatusLine(run, line);
        }
        run.StatusCursor = fs.Length; // snap to real length (approx-consumed guards partial lines)
        _ = consumed;
    }

    private static void ApplyStatusLine(WorkflowRun run, string line)
    {
        JsonElement el;
        try { el = JsonSerializer.Deserialize<JsonElement>(line); }
        catch { return; }

        run.RecentEvents.Enqueue(line.Length > 160 ? line[..160] : line);
        while (run.RecentEvents.Count > MaxRecentEvents) run.RecentEvents.TryDequeue(out _);

        if (el.TryGetProperty("run", out var rs))
        {
            var s = rs.GetString();
            run.State = s switch
            {
                "done" => WorkflowRunState.Done,
                "failed" => WorkflowRunState.Failed,
                _ => run.State,
            };
            if (run.State != WorkflowRunState.Running)
            {
                run.Finished = DateTimeOffset.UtcNow;
                if (el.TryGetProperty("error", out var er)) run.Error = er.GetString();
            }
            return;
        }
        if (!el.TryGetProperty("task", out var tid)) return;
        var id = tid.GetString() ?? "";
        string? status = el.TryGetProperty("status", out var st) ? st.GetString() : null;
        string? detail = el.TryGetProperty("detail", out var dt) ? dt.GetString() : null;
        int? secs = el.TryGetProperty("secs", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : null;
        int? toolsN = el.TryGetProperty("tools", out var tl) && tl.ValueKind == JsonValueKind.Number ? tl.GetInt32() : null;
        string? model = el.TryGetProperty("model", out var md) ? md.GetString() : null;
        foreach (var sec in run.Manifest.Sections)
            foreach (var t in sec.Tasks)
                if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    if (status is not null) t.Status = status;
                    if (detail is not null) t.Detail = detail;
                    if (secs is not null) t.Secs = secs;
                    if (toolsN is not null) t.Tools = toolsN;
                    if (model is not null) t.Model = model;
                    return;
                }
    }

    /// <summary>Allocate a fresh run directory + id under the workflow-runs root.</summary>
    public static (string Id, string Dir) NewRunDir(string name)
    {
        var safe = name;
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safe}";
        var dir = Path.Combine(PlatformContext.WorkflowRunsDirectory, id);
        Directory.CreateDirectory(dir);
        return (id, dir);
    }

    /// <summary>TEST ONLY: clear the registry.</summary>
    internal static void ResetForTests()
    {
        lock (_gate) { _runs.Clear(); _lastPoll = DateTime.MinValue; }
    }

    /// <summary>TEST ONLY: force the next PollDynamic to run regardless of throttle.</summary>
    internal static void ForceNextPollForTests()
    {
        lock (_gate) _lastPoll = DateTime.MinValue;
    }
}
