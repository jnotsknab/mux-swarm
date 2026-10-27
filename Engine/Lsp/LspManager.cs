using System.Collections.Concurrent;

namespace MuxSwarm.Engine.Lsp;

/// <summary>
/// Owns the language servers. A server starts the first time a file it covers is used and is kept per
/// (server, project root). A server that fails to start is not retried until <see cref="Reset"/>
/// (<c>/refresh</c>). Idle servers stop after <see cref="LspConfig.IdleMinutes"/>. Inert when
/// <see cref="LspConfig.Enabled"/> is off.
/// </summary>
internal static class LspManager
{
    private static readonly ConcurrentDictionary<(string Server, string Root), Lazy<Task<LspClient>>> Clients = new();
    private static readonly ConcurrentDictionary<(string Server, string Root), string> Failed = new();
    private static Timer? _idleTimer;

    /// <summary>Config in effect (test seam; defaults to the live config).</summary>
    internal static Func<LspConfig> Config = () => App.Config.Lsp;

    /// <summary>Workspace root used when no root marker is found (test seam).</summary>
    internal static Func<string> WorkspaceRoot = () => PlatformContext.WorkspaceRoot;

    /// <summary>True when LSP is on and at least one server is configured.</summary>
    internal static bool Active => Config() is { Enabled: true } c && c.Servers.Any(s => !s.Value.Disabled);

    /// <summary>The configured server for <paramref name="path"/> by extension (first by name), or null.</summary>
    internal static (string Name, LspServerConfig Server)? ServerFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        foreach (var (name, s) in Config().Servers.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (!s.Disabled && s.Extensions.Any(e => string.Equals(Dot(e), ext, StringComparison.OrdinalIgnoreCase)))
                return (name, s);
        return null;
    }

    /// <summary>The LSP language id for <paramref name="path"/>: the server's <c>languageIds</c> entry, else the extension without its dot.</summary>
    internal static string LanguageId(LspServerConfig server, string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var (k, v) in server.LanguageIds)
            if (string.Equals(Dot(k), ext, StringComparison.OrdinalIgnoreCase)) return v;
        return ext.TrimStart('.').ToLowerInvariant();
    }

    /// <summary>
    /// The project root for <paramref name="path"/>: the nearest folder upward holding one of the server's
    /// <c>rootMarkers</c> (a name, or a <c>*.ext</c> pattern), else the workspace root.
    /// </summary>
    internal static string RootFor(LspServerConfig server, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var marker in server.RootMarkers)
            {
                if (string.IsNullOrWhiteSpace(marker)) continue;
                bool hit = marker.Contains('*')
                    ? SafeAny(() => Directory.EnumerateFileSystemEntries(dir, marker).Any())
                    : File.Exists(Path.Combine(dir, marker)) || Directory.Exists(Path.Combine(dir, marker));
                if (hit) return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Path.GetFullPath(WorkspaceRoot());
    }

    /// <summary>
    /// The running client for <paramref name="path"/>, starting it on first use. Null (with <paramref name="why"/>)
    /// when LSP is off, no server covers the file, or the server cannot start.
    /// </summary>
    internal static async Task<(LspClient? Client, string LanguageId, string? Why)> ClientForAsync(string path, CancellationToken ct)
    {
        if (Config() is not { Enabled: true }) return (null, "", "LSP is off (set lsp.enabled and configure lsp.servers)");
        if (ServerFor(path) is not { } match) return (null, "", $"no configured language server handles '{Path.GetExtension(path)}' files");
        var (name, server) = match;
        var root = RootFor(server, path);
        var key = (name, root);
        if (Failed.TryGetValue(key, out var reason)) return (null, "", $"{name} failed to start ({reason}); fix lsp.servers and /refresh");
        var lazy = Clients.GetOrAdd(key, _ => new Lazy<Task<LspClient>>(() => StartAsync(name, root, server)));
        try
        {
            var client = await lazy.Value.WaitAsync(ct);
            if (!client.IsRunning)
            {
                Clients.TryRemove(new KeyValuePair<(string, string), Lazy<Task<LspClient>>>(key, lazy));
                return await ClientForAsync(path, ct);   // exited since: start a fresh one
            }
            EnsureIdleTimer();
            return (client, LanguageId(server, path), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Clients.TryRemove(key, out _);
            Failed[key] = ex.Message;
            return (null, "", $"{name} failed to start ({ex.Message})");
        }
    }

    private static Task<LspClient> StartAsync(string name, string root, LspServerConfig server)
    {
        var exe = ResolveCommand(server.Command)
                  ?? throw new InvalidOperationException($"'{server.Command}' was not found on PATH");
        return LspClient.StartAsync(name, root, server, exe, CancellationToken.None);
    }

    /// <summary>
    /// The executable for <paramref name="command"/>: an existing path, or the first PATH match. On Windows a
    /// bare name also tries the PATHEXT extensions (.exe, .cmd, .bat), so npm-installed servers resolve.
    /// </summary>
    internal static string? ResolveCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return null;
        if (command.Contains('/') || command.Contains('\\'))
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        var exts = OperatingSystem.IsWindows() && !Path.HasExtension(command)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir.Trim('"'), command + ext);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>One line per configured server: resolved command, running roots, last failure.</summary>
    internal static IReadOnlyList<string> Status()
    {
        var lines = new List<string>();
        foreach (var (name, s) in Config().Servers.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (s.Disabled) { lines.Add($"{name}: disabled"); continue; }
            var exe = ResolveCommand(s.Command);
            var running = Clients.Where(c => c.Key.Server == name && c.Value.IsValueCreated && c.Value.Value.IsCompletedSuccessfully && c.Value.Value.Result.IsRunning)
                .Select(c => c.Key.Root).ToList();
            var failed = Failed.Where(f => f.Key.Server == name).Select(f => $"failed in {f.Key.Root}: {f.Value}");
            lines.Add($"{name} ({string.Join(" ", s.Extensions)}): " + (exe is null ? $"'{s.Command}' not found on PATH" : exe)
                + (running.Count > 0 ? $"; running for {string.Join(", ", running)}" : "; not started")
                + string.Concat(failed.Select(f => "; " + f)));
        }
        return lines.Count > 0 ? lines : ["no servers configured (lsp.servers)"];
    }

    /// <summary>Running clients (for diagnostics after writes).</summary>
    internal static IEnumerable<LspClient> Running =>
        Clients.Values.Where(l => l.IsValueCreated && l.Value.IsCompletedSuccessfully && l.Value.Result.IsRunning).Select(l => l.Value.Result);

    /// <summary>Stop every server Mux started and forget failed starts (<c>/refresh</c>, exit).</summary>
    internal static void Reset()
    {
        foreach (var (key, lazy) in Clients.ToArray())
        {
            Clients.TryRemove(key, out _);
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) lazy.Value.Result.Dispose();
        }
        Failed.Clear();
    }

    private static void EnsureIdleTimer()
    {
        if (_idleTimer is not null) return;
        _idleTimer = new Timer(_ =>
        {
            var idle = Config().IdleMinutes;
            if (idle <= 0) return;
            foreach (var (key, lazy) in Clients.ToArray())
                if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully
                    && DateTime.UtcNow - lazy.Value.Result.LastUsed > TimeSpan.FromMinutes(idle)
                    && Clients.TryRemove(new KeyValuePair<(string, string), Lazy<Task<LspClient>>>(key, lazy)))
                    lazy.Value.Result.Dispose();
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Path relative to the workspace when inside it, else absolute (what results show).</summary>
    internal static string Rel(string path)
    {
        var rel = Path.GetRelativePath(WorkspaceRoot(), path);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel;
    }

    private static string Dot(string ext) => ext.StartsWith('.') ? ext : "." + ext;

    private static bool SafeAny(Func<bool> f)
    {
        try { return f(); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
