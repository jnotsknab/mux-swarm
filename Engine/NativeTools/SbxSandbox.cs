using System.Text;
using System.Text.Json;

namespace MuxSwarm.Engine.NativeTools;

/// <summary>
/// One per-session Docker Sandboxes microVM (the <c>docker</c> backend), driven through the <c>sbx</c> CLI.
/// Same all-or-nothing model as <see cref="OciSandbox"/>: shell jobs and the Python REPL worker both
/// <c>sbx exec</c> into the one VM, which has its own kernel and Docker engine. Created lazily on first
/// use, removed on <see cref="Dispose"/>.
///
/// sbx mounts each workspace at its host path (on Windows <c>C:\x</c> becomes <c>/c/x</c>), so after
/// create the session work dir is linked to <c>/work</c> and each allowed path to <c>/host/&lt;leaf&gt;</c>,
/// keeping the same guest layout as the container backend.
///
/// Network: <c>network: false</c> denies all egress (<c>--deny-network "**"</c>); <c>network: true</c>
/// adds a per-sandbox allow-all rule on top of the global sbx policy. <c>allowedDomains</c> is rejected
/// at resolve time: sbx deny rules outrank allow rules, so a strict allowlist is not expressible.
/// </summary>
internal sealed class SbxSandbox : ISessionSandbox
{
    private readonly SandboxSpec _spec;
    private readonly string _hostWorkDir;
    private readonly string _key;
    private readonly object _gate = new();
    private bool _started;
    private bool _disposed;
    private int _buildAttempts;
    private string _lastBuildError = "";
    private const int MaxBuildAttempts = 3;
    private string _name = "";

    public SbxSandbox(SandboxSpec spec, string hostWorkDir, string key)
    {
        _spec = spec;
        _hostWorkDir = hostWorkDir;
        _key = key;
    }

    /// <inheritdoc/>
    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_started)
            {
                if (!ShouldRebuild(Exists())) return;   // sbx exec restarts a stopped sandbox by itself
                OciSandbox.Run(_spec.Binary, $"rm --force {_name}", allowFail: true);
                _started = false;
                _name = "";
            }
            if (_buildAttempts >= MaxBuildAttempts)
                throw new SandboxException(
                    $"sandbox microVM failed to start {MaxBuildAttempts}x and will not be retried this session. " +
                    $"Last error: {(_lastBuildError.Length > 0 ? _lastBuildError : "unknown")}. " +
                    "Check `sbx diagnose`, or use /sandbox docker-container.");
            _buildAttempts++;

            Directory.CreateDirectory(_hostWorkDir);
            _name = SandboxName(_key, Guid.NewGuid().ToString("N")[..6]);

            var mounts = _spec.Mounts;   // network shares + missing paths were already dropped (SandboxBackend.SbxMounts)
            var skipped = (App.Config.Filesystem?.AllowedPaths ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p) && !mounts.Any(m => m.HostPath == p)).Distinct().ToList();
            if (skipped.Count > 0)
                MuxConsole.WriteWarning($"[sandbox] not mounted in the microVM ({skipped.Count}: network shares or " +
                    $"missing paths): {string.Join("; ", skipped)}");

            var (ok, _, err) = OciSandbox.Run(_spec.Binary, CreateArgs(_name, _spec.Image, _spec.NetworkOpen, _hostWorkDir, mounts),
                allowFail: true, timeoutMs: 600_000);
            if (ok && _spec.NetworkOpen)
                (ok, _, err) = OciSandbox.Run(_spec.Binary, $"policy allow network --sandbox {_name} \"**\"", allowFail: true);
            if (ok)
            {
                string setup = LayoutScript(GuestPath(_hostWorkDir, OperatingSystem.IsWindows()),
                    mounts.Select(m => (GuestPath(m.HostPath, OperatingSystem.IsWindows()), m.GuestPath)));
                (ok, _, err) = OciSandbox.Run(_spec.Binary, $"exec -u root {_name} sh -c {OciSandbox.ShQuoteForArgv(setup)}", allowFail: true);
            }
            if (!ok)
            {
                _lastBuildError = err.Trim();
                OciSandbox.Run(_spec.Binary, $"rm --force {_name}", allowFail: true);
                _name = "";
                throw new SandboxException($"failed to start sandbox microVM (docker / sbx): {_lastBuildError}");
            }
            _started = true;
            _buildAttempts = 0;
            _lastBuildError = "";
        }
    }

    /// <inheritdoc/>
    public (string File, string Args) ExecShell(string innerCommand)
    {
        if (!_started) EnsureStarted();   // callers health-check first; skip a second `sbx ls` per job
        return (_spec.Binary, $"exec -w {OciSandbox.GuestWorkDir} {_name} sh -c {OciSandbox.ShQuoteForArgv(innerCommand)}");
    }

    /// <inheritdoc/>
    public (string File, string Args) ExecPythonWorker(string guestWorkerPath)
    {
        if (!_started) EnsureStarted();
        return (_spec.Binary, $"exec -i -w {OciSandbox.GuestWorkDir} {_name} python {guestWorkerPath}");
    }

    /// <summary>
    /// Rebuild only when sbx confirmed the VM is gone (<paramref name="exists"/> false). Unknown (a failed
    /// <c>sbx ls</c>) keeps the current VM: dropping it would leak it and split the session between the
    /// Python worker (old VM) and new shell jobs (new VM).
    /// </summary>
    internal static bool ShouldRebuild(bool? exists) => exists == false;

    /// <summary>Whether the VM still exists: null when <c>sbx ls</c> itself failed (unknown).</summary>
    private bool? Exists()
    {
        var (ok, outp, _) = OciSandbox.Run(_spec.Binary, "ls --json", allowFail: true, timeoutMs: 30_000);
        return ok ? ListContains(outp, _name) : null;
    }

    /// <summary>True when <c>sbx ls --json</c> output lists a sandbox named <paramref name="name"/>.</summary>
    internal static bool ListContains(string lsJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(lsJson);
            if (!doc.RootElement.TryGetProperty("sandboxes", out var arr)) return false;
            foreach (var s in arr.EnumerateArray())
                if (s.TryGetProperty("name", out var n) && n.GetString() == name) return true;
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>
    /// Sandbox name: lowercase letters, digits and hyphens only (sbx rejects underscores), prefixed
    /// <c>mux-</c> so leftovers are recognizable in <c>sbx ls</c>.
    /// </summary>
    internal static string SandboxName(string key, string suffix)
    {
        var sb = new StringBuilder();
        foreach (char c in key.ToLowerInvariant())
            sb.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-');
        string k = sb.ToString().Trim('-');
        if (k.Length > 24) k = k[..24].TrimEnd('-');
        return "mux-" + (k.Length == 0 ? "s" : k) + "-" + suffix;
    }

    /// <summary>
    /// <c>sbx create</c> arguments: the <c>shell</c> agent on <paramref name="image"/>, the work dir as
    /// the read-write workspace, each mount as an extra workspace (<c>:ro</c> when read-only), and all
    /// egress denied unless <paramref name="networkOpen"/>.
    /// </summary>
    internal static string CreateArgs(string name, string image, bool networkOpen, string workDir, IEnumerable<SandboxMount> mounts)
    {
        var sb = new StringBuilder($"create --name {name} -q --pull missing -t {image}");
        if (!networkOpen) sb.Append(" --deny-network \"**\"");
        sb.Append(" shell ").Append(Q(workDir));
        foreach (var m in mounts) sb.Append(' ').Append(Q(m.HostPath + (m.ReadOnly ? ":ro" : "")));
        return sb.ToString();
    }

    /// <summary>
    /// Where sbx mounts a host path inside the VM: the same path on Linux/macOS; on Windows the drive
    /// becomes a lowercase root segment (<c>C:\Users\x</c> -> <c>/c/Users/x</c>).
    /// </summary>
    internal static string GuestPath(string hostPath, bool windows)
    {
        if (!windows) return hostPath;
        string p = hostPath.Replace('\\', '/').TrimEnd('/');   // pure string op: same result on every host OS
        return p.Length >= 2 && p[1] == ':' ? "/" + char.ToLowerInvariant(p[0]) + p[2..] : p;
    }

    /// <summary>
    /// Root shell script that links the work dir to /work and each mount to its /host/&lt;leaf&gt;. It never
    /// deletes anything: sbx mounts workspaces at their host paths, so on Linux/macOS a real directory at a
    /// link path can be host data. A link path that exists and is not a symlink fails the setup instead.
    /// No <c>$</c> in the script: the argv quoting escapes it.
    /// </summary>
    internal static string LayoutScript(string guestWorkDir, IEnumerable<(string Source, string Link)> links)
    {
        var sb = new StringBuilder("mkdir -p /host");
        foreach (var (source, link) in links.Prepend((guestWorkDir, OciSandbox.GuestWorkDir)))
        {
            string l = Sq(link);
            sb.Append(" && { [ -L ").Append(l).Append(" ] || [ ! -e ").Append(l).Append(" ] || { echo ")
              .Append(Sq("mux: " + link + " already exists in the sandbox")).Append(" >&2; false; }; } && ln -sfn ")
              .Append(Sq(source)).Append(' ').Append(l);
        }
        return sb.ToString();
    }

    private static string Q(string s) => "\"" + s + "\"";
    private static string Sq(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_name.Length > 0) OciSandbox.Run(_spec.Binary, $"rm --force {_name}", allowFail: true);
        }
    }
}
