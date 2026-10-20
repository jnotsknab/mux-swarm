using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MuxSwarm.Engine;

/// <summary>
/// Reports files created, changed, renamed or deleted while a tool call runs, whatever the tool
/// (file tools, REPL, shell, MCP servers): watchers on the workspace, sandbox and allowed paths are
/// armed only while at least one tool call is in flight (plus a short tail for late notifications),
/// so the user's own edits between tool calls are not reported. Changes become <c>file_changed</c>
/// events on the serve/stdio stream only; they never reach the model. Off with
/// <c>serve.fileEvents: false</c>.
/// </summary>
internal sealed class FileChangeTracker
{
    /// <summary>Directory names whose contents are never reported (build output, VCS, caches, deps).</summary>
    internal static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "bin", "obj", "node_modules", ".venv", "venv", "__pycache__",
        ".pytest_cache", ".mypy_cache", ".idea", ".vs", "target", "dist", "build", ".next",
    };

    /// <summary>Upper bound on events emitted per tool call, so a bulk operation cannot flood the stream.</summary>
    internal const int MaxEventsPerCall = 50;

    /// <summary>Notifications arriving this long after the last tool call ended are still reported.</summary>
    internal static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(600);

    /// <summary>Same-path notifications within this window collapse into one event (one save = several notifications).</summary>
    internal static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(250);

    /// <summary>Engine-wide tracker, started by serve/stdio when <c>serve.fileEvents</c> is on.</summary>
    internal static FileChangeTracker? Shared;

    /// <summary>User ignore patterns (<c>serve.fileEventsIgnore</c>); defaults to logs and lock files.</summary>
    private List<Glob> _ignore = Globs(new ServeConfig().FileEventsIgnore);

    private readonly Action<string, string, string?> _emit;   // (path, change, tool)
    private readonly List<string> _excluded = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _inFlight;
    private DateTime _windowEnd = DateTime.MinValue;
    private string? _tool;
    private int _emitted;

    internal FileChangeTracker(Action<string, string, string?> emit) => _emit = emit;

    /// <summary>Replace the ignore patterns (<c>serve.fileEventsIgnore</c>). Null keeps the defaults.</summary>
    internal void Ignore(IEnumerable<string>? patterns) { if (patterns != null) _ignore = Globs(patterns); }

    /// <summary>A compiled ignore pattern; <see cref="WholePath"/> = match the path's tail, else the file name.</summary>
    internal sealed record Glob(Regex Rx, bool WholePath);

    /// <summary>
    /// Compile ignore globs. A pattern without <c>/</c> matches the file name (<c>*.log</c>); one with
    /// <c>/</c> matches the end of the path (<c>docs/generated/**</c>). <c>*</c> and <c>?</c> stay inside
    /// one path segment, <c>**</c> crosses them. Case-insensitive on Windows and macOS.
    /// </summary>
    internal static List<Glob> Globs(IEnumerable<string> patterns)
    {
        var opts = RegexOptions.CultureInvariant | (OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase);
        var list = new List<Glob>();
        foreach (var raw in patterns)
        {
            var p = (raw ?? "").Trim().Replace('\\', '/').TrimStart('/');
            if (p.Length == 0) continue;
            var body = Regex.Escape(p).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]").Replace("\u0001", ".*");
            bool whole = p.Contains('/');
            list.Add(new Glob(new Regex((whole ? "(^|/)" : "^") + body + "$", opts), whole));
        }
        return list;
    }

    /// <summary>True when <paramref name="path"/> matches one of <paramref name="globs"/> (see <see cref="Globs"/>).</summary>
    internal static bool MatchesAny(string path, IReadOnlyList<Glob> globs)
    {
        var norm = path.Replace('\\', '/');
        var name = norm[(norm.LastIndexOf('/') + 1)..];
        foreach (var g in globs)
            if (g.Rx.IsMatch(g.WholePath ? norm : name)) return true;
        return false;
    }

    /// <summary>Directories actually watched (existing, de-duplicated, nested roots dropped).</summary>
    internal IReadOnlyList<string> Roots => _watchers.Select(w => w.Path).ToList();

    /// <summary>
    /// Never report anything under <paramref name="dirs"/>: Mux's own state (telemetry, REPL worker,
    /// browser profile, ...), which changes during tool calls without the agent writing it.
    /// </summary>
    internal void Exclude(IEnumerable<string?> paths)
    {
        foreach (var d in paths)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(d));
                // Folders match as a prefix; a path with an extension (the graph file) matches exactly.
                _excluded.Add(Path.HasExtension(full) && !Directory.Exists(full) ? full : full + Path.DirectorySeparatorChar);
            }
            catch { }
        }
    }

    /// <summary>
    /// Mux's own state folders: telemetry, REPL workers, delegation journals, the built-in
    /// browser (binary + profile), downloaded runtimes, the memory stores. Under the install folder
    /// and the per-user data folder (they differ for builds run from another directory).
    /// </summary>
    internal static IEnumerable<string> OwnStateDirs(string baseDir, string? sessionsDir, string? kgPath, string? chromaPath)
    {
        var local = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        foreach (var root in new[] { baseDir, Path.Combine(local, "Mux-Swarm") })
        {
            yield return Path.Combine(root, "Telemetry");
            yield return Path.Combine(root, "repl");
            yield return Path.Combine(root, "delegations");
            yield return Path.Combine(root, "Runtime", "browser");
            yield return Path.Combine(root, "Runtime", "whisper");
            yield return Path.Combine(root, "cliproxy");
            yield return Path.Combine(root, "Backups");
        }
        foreach (var s in new[] { sessionsDir, Path.Combine(baseDir, "Sessions") })
            if (!string.IsNullOrWhiteSpace(s))
            {
                yield return Path.Combine(s, "BrowserProfile");
                yield return Path.Combine(s, "PlaywrightChromeProfile");
            }
        if (!string.IsNullOrWhiteSpace(chromaPath)) yield return chromaPath;
        // The graph file only (its folder may be the user's sandbox, the setup default).
        if (!string.IsNullOrWhiteSpace(kgPath)) yield return kgPath;
    }

    /// <summary>True when <paramref name="fullPath"/> is inside an excluded directory.</summary>
    internal bool IsExcluded(string fullPath)
    {
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var d in _excluded)
        {
            bool dir = d.EndsWith(Path.DirectorySeparatorChar);
            if (dir ? fullPath.StartsWith(d, cmp) || (fullPath + Path.DirectorySeparatorChar).Equals(d, cmp) : fullPath.Equals(d, cmp)) return true;
        }
        return false;
    }

    /// <summary>Watch <paramref name="roots"/> recursively. Missing roots and roots the OS refuses are skipped.</summary>
    internal void Start(IEnumerable<string?> roots)
    {
        foreach (var root in NormalizeRoots(roots))
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => On(e.FullPath, "created");
                w.Changed += (_, e) => On(e.FullPath, "modified");
                w.Deleted += (_, e) => On(e.FullPath, "deleted");
                w.Renamed += (_, e) => On(e.FullPath, "renamed");
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                MuxConsole.WriteWarning($"[file events] not watching {root}: {ex.Message}");
            }
        }
    }

    /// <summary>Mark a tool call as running; changes are reported until every running call has ended (plus <see cref="Tail"/>).</summary>
    internal IDisposable BeginToolCall(string? tool)
    {
        lock (_gate)
        {
            if (_inFlight++ == 0 && DateTime.UtcNow > _windowEnd) _emitted = 0;
            _tool = tool;
        }
        return new Scope(this);
    }

    private void EndToolCall()
    {
        lock (_gate)
        {
            if (--_inFlight == 0) _windowEnd = DateTime.UtcNow + Tail;
        }
    }

    private void On(string fullPath, string change)
    {
        string? tool;
        lock (_gate)
        {
            if (_inFlight == 0 && DateTime.UtcNow > _windowEnd) return;
            if (_emitted >= MaxEventsPerCall) return;
            tool = _tool;
        }
        if (IsIgnored(fullPath) || MatchesAny(fullPath, _ignore) || IsExcluded(fullPath)) return;
        if (change is "created" or "modified" && Directory.Exists(fullPath)) return;   // folder metadata churn

        var now = DateTime.UtcNow;
        var key = fullPath + "|" + (change == "deleted" ? "d" : "w");
        if (_recent.TryGetValue(key, out var last) && now - last < Coalesce) return;
        _recent[key] = now;
        if (_recent.Count > 512)
            foreach (var old in _recent.Where(kv => now - kv.Value > Coalesce).Select(kv => kv.Key).ToList())
                _recent.TryRemove(old, out _);

        lock (_gate) _emitted++;
        try { _emit(fullPath, change, tool); } catch { /* never let a sink break the watcher */ }
    }

    /// <summary>True when the path sits under an ignored directory or is an editor/OS temp file.</summary>
    internal static bool IsIgnored(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
            if (IgnoredDirs.Contains(parts[i])) return true;
        var name = parts[^1];
        return name.Length == 0 || name.StartsWith("~$", StringComparison.Ordinal)
               || name.EndsWith('~') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase)
               || name.Contains(".mux-tmp-", StringComparison.Ordinal)   // Mux's own atomic-write temp
               || name.Equals(".DS_Store", StringComparison.Ordinal);
    }

    /// <summary>Existing directories only, full paths, duplicates and roots nested in another root removed.</summary>
    internal static List<string> NormalizeRoots(IEnumerable<string?> roots)
    {
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = roots.Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(r!)); } catch { return null; } })
            .Where(r => r != null && Directory.Exists(r))
            .Select(r => r!)
            .Distinct(cmp == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Length)
            .ToList();
        var kept = new List<string>();
        foreach (var r in full)
            if (!kept.Any(k => r.StartsWith(k.EndsWith(Path.DirectorySeparatorChar) ? k : k + Path.DirectorySeparatorChar, cmp)))
                kept.Add(r);
        return kept;
    }

    /// <summary>Stop and release every watcher.</summary>
    internal void Stop()
    {
        foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _watchers.Clear();
    }

    private sealed class Scope(FileChangeTracker owner) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) owner.EndToolCall(); }
    }
}
