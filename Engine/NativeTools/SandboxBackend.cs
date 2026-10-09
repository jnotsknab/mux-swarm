using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MuxSwarm.Engine.NativeTools;

/// <summary>
/// What family of wrapping a backend uses. Drives how <see cref="SandboxBackend.WrapShell"/> and the
/// per-session container lifecycle behave.
/// </summary>
internal enum SandboxKind
{
    Host,     // no wrap (current behavior)
    Oci,      // docker-container/podman/nerdctl/gvisor/kata - persistent container per session, exec into it
    Sbx,      // docker - Docker Sandboxes microVM per session (sbx CLI), exec into it
    Wrapper,  // bwrap/firejail/sandbox-exec - re-wrap each command, no image, no persistent instance
    Custom    // user template string
}

/// <summary>
/// Resolved, validated sandbox configuration. Built once from <see cref="SandboxConfig"/> via
/// <see cref="SandboxBackend.Resolve"/>; carries everything the session lifecycle + command wrapping
/// need. A null result from Resolve means "host" (no sandbox); a thrown <see cref="SandboxException"/>
/// means the config was invalid or the backend is unavailable (surfaced to the user, never a silent
/// host fallback).
/// </summary>
internal sealed class SandboxSpec
{
    public required SandboxKind Kind { get; init; }
    public required string Backend { get; init; }     // canonical lowered name
    public required string Binary { get; init; }       // docker|podman|nerdctl|bwrap|firejail|sandbox-exec|""
    public required string Image { get; init; }
    public bool NetworkOpen { get; init; }
    public IReadOnlyList<string> AllowedDomains { get; init; } = Array.Empty<string>();
    public string CustomTemplate { get; init; } = "";

    /// <summary>Custom backend only: the template can host the stdio Python REPL worker (sandbox.replStdio).</summary>
    public bool CustomReplStdio { get; init; }
    public string? Runtime { get; init; }              // e.g. "runsc" for gvisor

    /// <summary>
    /// Host allowed-paths to bind into the sandbox, with per-path read-only/read-write derived from the
    /// filesystem security posture (see <see cref="SandboxBackend.ResolveMounts"/>). Empty = only the
    /// Mux-internal work dir is mounted (today's behavior). Container and microVM backends only.
    /// </summary>
    public IReadOnlyList<SandboxMount> Mounts { get; init; } = Array.Empty<SandboxMount>();

    public bool UsesAllowlist => AllowedDomains.Count > 0;

    /// <summary>True for backends with a persistent per-session instance that tools exec into.</summary>
    public bool IsSession => Kind is SandboxKind.Oci or SandboxKind.Sbx;
}

/// <summary>One host->guest bind for the sandbox. <paramref name="ReadOnly"/> derived from fs security mode.</summary>
internal readonly record struct SandboxMount(string HostPath, string GuestPath, bool ReadOnly);

/// <summary>Raised when sandbox config is invalid or the chosen backend is not usable. Message is user-facing.</summary>
internal sealed class SandboxException : Exception
{
    public SandboxException(string message) : base(message) { }
}

/// <summary>
/// Resolves + validates the configured sandbox backend and renders command wrappings. This is the one
/// seam the native exec tools call instead of spawning a process directly. Pure/stateless apart from a
/// cached availability probe; per-session container lifecycle lives in <see cref="OciSandbox"/>.
/// </summary>
internal static class SandboxBackend
{
    private static readonly HashSet<string> OciBackends =
        new(StringComparer.OrdinalIgnoreCase) { "docker-container", "podman", "nerdctl", "gvisor", "kata" };

    /// <summary>
    /// The Docker Sandboxes CLI. On Windows the per-user install adds its bin dir to the user PATH,
    /// which an already-running process may not have picked up, so the default install path is the fallback.
    /// </summary>
    internal static string SbxBinary()
    {
        if (OperatingSystem.IsWindows())
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DockerSandboxes", "bin", "sbx.exe");
            if (!BinaryOnPath("sbx", "version") && File.Exists(local)) return local;
        }
        return "sbx";
    }

    /// <summary>
    /// Mounts for the <c>docker</c> microVM: the usual allowed-path mounts minus network shares (UNC),
    /// which sbx cannot mount, and paths that do not exist (sbx prompts to create a missing workspace, and
    /// Mux must not create directories from config). Dropped at resolve time so the preamble never lists
    /// a path that is not mounted.
    /// </summary>
    internal static IReadOnlyList<SandboxMount> SbxMounts(FilesystemConfig fs) =>
        ResolveMounts(fs).Where(m => !UncDriveMapper.IsUnc(m.HostPath) && Directory.Exists(m.HostPath)).ToList();

    /// <summary>Canonical backend name: <c>docker-legacy</c> is an alias of <c>docker-container</c>.</summary>
    internal static string Canonical(string? backend)
    {
        string b = (backend ?? "host").Trim().ToLowerInvariant();
        return b == "docker-legacy" ? "docker-container" : b;
    }

    private static readonly HashSet<string> WrapperBackends =
        new(StringComparer.OrdinalIgnoreCase) { "bwrap", "firejail", "sandbox-exec" };

    /// <summary>
    /// Resolve the active sandbox spec from config. Returns null for "host" (no sandbox). Throws
    /// <see cref="SandboxException"/> (user-facing) for an unknown backend, an impossible network combo,
    /// a custom backend without a template, or a backend whose binary/runtime is not installed/ready.
    /// </summary>
    public static SandboxSpec? Resolve(SandboxConfig cfg)
    {
        string backend = Canonical(cfg.Backend);
        if (backend is "host" or "" or "none") return null;

        var allow = (cfg.AllowedDomains ?? new List<string>())
            .Select(d => d?.Trim() ?? "")
            .Where(d => d.Length > 0)
            .ToList();

        // ----- custom -----
        if (backend == "custom")
        {
            if (string.IsNullOrWhiteSpace(cfg.Command))
                throw new SandboxException("sandbox.backend is 'custom' but sandbox.command (the template) is empty. " +
                    "Provide a template using {cmd}, {workdir}, {image} placeholders.");
            if (allow.Count > 0)
                throw new SandboxException("sandbox.allowedDomains is only enforceable on container backends " +
                    "(docker-container/podman/nerdctl/gvisor). A 'custom' backend manages its own network; remove allowedDomains.");
            return new SandboxSpec
            {
                Kind = SandboxKind.Custom, Backend = backend, Binary = "",
                Image = cfg.Image ?? "", NetworkOpen = cfg.Network, CustomTemplate = cfg.Command,
                CustomReplStdio = cfg.ReplStdio,
            };
        }

        // ----- docker: Docker Sandboxes microVM (sbx) -----
        if (backend == "docker")
        {
            if (allow.Count > 0)
                throw new SandboxException("sandbox.allowedDomains is not supported on the 'docker' microVM backend: Docker " +
                    "Sandboxes deny rules outrank allow rules, so a strict allowlist cannot be expressed. Use /sandbox " +
                    "docker-container for a strict allowlist, or remove allowedDomains (network false = no egress, " +
                    "true = your sbx policy).");
            if (string.IsNullOrWhiteSpace(cfg.Image))
                throw new SandboxException("sandbox.backend 'docker' requires sandbox.image to be set.");
            string sbx = SbxBinary();
            if (!BinaryOnPath(sbx, "version"))
                throw new SandboxException("sandbox.backend 'docker' runs a Docker Sandboxes microVM and needs the 'sbx' CLI, " +
                    "which was not found. Install it (https://docs.docker.com/ai/sandboxes/install/), run `sbx login`, " +
                    "or use /sandbox docker-container for a plain container.");
            var (ok, _, err) = OciSandbox.Run(sbx, "ls --json", allowFail: true, timeoutMs: 30_000);
            if (!ok)
                throw new SandboxException($"'sbx' is installed but not ready ({err.Trim()}). Run `sbx login` " +
                    "(and `sbx policy init balanced` on first use), or use /sandbox docker-container.");
            return new SandboxSpec
            {
                Kind = SandboxKind.Sbx, Backend = backend, Binary = sbx, Image = cfg.Image,
                NetworkOpen = cfg.Network, Mounts = SbxMounts(App.Config.Filesystem),
            };
        }

        // ----- container family -----
        if (OciBackends.Contains(backend))
        {
            // gvisor + kata are runtimes layered on a base OCI engine (docker by default): gvisor =>
            // docker --runtime=runsc (user-space kernel); kata => docker --runtime=kata-runtime (true
            // microVM with its own guest kernel). Both reuse the persistent-container + `exec` lifecycle
            // (kata-agent over vsock services `exec`, like runsc) so the OciSandbox model is unchanged.
            string binary = backend is "gvisor" or "kata" or "docker-container" ? "docker" : backend;
            // Runtime resolution: an explicit sandbox.runtime wins (lets you layer any runtime onto any
            // base engine, e.g. podman + kata-runtime); otherwise the microVM/sandboxed-kernel backends
            // imply their canonical runtime, and plain docker/podman/nerdctl default to the engine default.
            string? runtime = !string.IsNullOrWhiteSpace(cfg.Runtime) ? cfg.Runtime.Trim()
                : backend switch { "gvisor" => "runsc", "kata" => "kata-runtime", _ => null };
            // Kata is a hardware-virtualization microVM: it needs Linux + KVM. Fail loud (never a silent
            // host or weaker-isolation fallback) when the platform can't actually provide a microVM.
            if (backend == "kata")
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    throw new SandboxException("sandbox.backend 'kata' is a microVM runtime that requires Linux + KVM; " +
                        "it is not available on this OS. Use docker (Docker Sandboxes microVM), docker-container/podman/nerdctl (containers) or gvisor (user-space kernel) instead.");
                if (!File.Exists("/dev/kvm"))
                    throw new SandboxException("sandbox.backend 'kata' needs hardware virtualization (/dev/kvm) but it was not found. " +
                        "Enable KVM (and nested virtualization if inside a VM), install kata-containers, then retry.");
            }
            EnsureBinaryReady(binary, ociDaemonCheck: true);
            if (string.IsNullOrWhiteSpace(cfg.Image))
                throw new SandboxException($"sandbox.backend '{backend}' requires sandbox.image to be set.");
            return new SandboxSpec
            {
                Kind = SandboxKind.Oci, Backend = backend, Binary = binary, Image = cfg.Image,
                NetworkOpen = cfg.Network, AllowedDomains = allow, Runtime = runtime,
                Mounts = ResolveMounts(App.Config.Filesystem),
            };
        }

        // ----- wrapper family -----
        if (WrapperBackends.Contains(backend))
        {
            if (allow.Count > 0)
                throw new SandboxException($"sandbox.allowedDomains is not enforceable on the '{backend}' wrapper backend " +
                    "(namespace network isolation is all-or-nothing without privileged plumbing). Use an OCI backend " +
                    "(docker-container/podman/nerdctl) for a domain allowlist, or set sandbox.network true/false.");
            if (backend == "sandbox-exec" && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                throw new SandboxException("sandbox.backend 'sandbox-exec' is macOS-only.");
            if ((backend is "bwrap" or "firejail") && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                throw new SandboxException($"sandbox.backend '{backend}' is Linux-only.");
            EnsureBinaryReady(backend, ociDaemonCheck: false);
            // firejail does NOT fail when it can't sandbox: nested in another sandbox (WSL, a container) it
            // warns "an existing sandbox was detected", runs the program unconfined and exits 0 - and our
            // --quiet hides the warning. Probe once, without --quiet, so that case is a hard error.
            if (backend == "firejail" && FirejailDegraded(ProbeOutput("firejail", "--noprofile /bin/true", stderr: true)))
                throw new SandboxException("firejail detected an existing sandbox (e.g. WSL or a container) and would run " +
                    "commands WITHOUT confinement. Use bwrap, a container backend, or run Mux outside the nested environment.");
            return new SandboxSpec
            {
                Kind = SandboxKind.Wrapper, Backend = backend, Binary = backend,
                Image = "", NetworkOpen = cfg.Network,
            };
        }

        throw new SandboxException($"Unknown sandbox.backend '{cfg.Backend}'. Valid: host, docker (microVM), " +
            "docker-container (alias docker-legacy), podman, nerdctl, gvisor, kata, bwrap, firejail, sandbox-exec, custom.");
    }

    /// <summary>Validate WITHOUT throwing - returns the error string (or null if ok). For /sandbox + startup.</summary>
    public static string? Validate(SandboxConfig cfg)
    {
        try { Resolve(cfg); return null; }
        catch (SandboxException ex) { return ex.Message; }
    }

    // ---- detection ----------------------------------------------------------------------------

    private static void EnsureBinaryReady(string binary, bool ociDaemonCheck)
    {
        if (!BinaryOnPath(binary, "--version"))
            throw new SandboxException($"sandbox backend needs '{binary}' but it was not found on PATH. Install it (or pick another backend).");
        if (ociDaemonCheck && !OciDaemonReady(binary))
            throw new SandboxException($"'{binary}' is installed but its daemon/runtime is not reachable ('{binary} info' failed). Start it and retry.");
    }

    private static bool BinaryOnPath(string binary, string probeArgs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary, Arguments = probeArgs,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>True when firejail's output says it fell back to running the program without a sandbox.</summary>
    internal static bool FirejailDegraded(string output) =>
        output.Contains("existing sandbox was detected", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Combined stdout(+stderr) of a short read-only probe, stdin closed. A probe that cannot start or does not
    /// finish within <paramref name="timeoutMs"/> throws <see cref="SandboxException"/> with the reason: callers
    /// gate confinement on the output, so a failed probe must never read as "nothing to worry about".
    /// </summary>
    internal static string ProbeOutput(string binary, string args, bool stderr = false, int timeoutMs = 5000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary, Arguments = args,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SandboxException($"could not run '{binary} {args}' to check the sandbox: {ex.Message}");
        }
        using (p)
        {
            if (p is null) throw new SandboxException($"could not run '{binary} {args}' to check the sandbox.");
            p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                // Best-effort cleanup of a hung probe; the timeout itself is what we report.
                try { p.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited or not killable; the timeout is reported below */ }
                throw new SandboxException($"'{binary} {args}' did not finish within {timeoutMs / 1000}s while checking the sandbox.");
            }
            return stderr ? outTask.Result + errTask.Result : outTask.Result;
        }
    }

    private static bool OciDaemonReady(string binary)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary, Arguments = "info",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(10000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ---- wrapper-family command rendering -----------------------------------------------------
    //
    // OCI backends are NOT rendered here - they run a persistent container via OciSandbox and use
    // `docker exec`. Wrapper + custom backends re-wrap each command; this builds the (file,args) to run.

    /// <summary>
    /// Render the (file, argv-string) to execute <paramref name="innerCommand"/> via a shell, wrapped by
    /// the wrapper/custom backend, confined to <paramref name="workDir"/>. Only valid for Wrapper/Custom.
    /// </summary>
    /// <param name="readOnlyPaths">Wrapper backends only: extra host paths the command must read (see <see cref="WrapProcess"/>).</param>
    public static (string File, string Args) WrapShellCommand(SandboxSpec spec, string innerCommand, string workDir,
        IReadOnlyList<string>? readOnlyPaths = null)
    {
        switch (spec.Kind)
        {
            case SandboxKind.Wrapper:
                // Wrapper backends are Linux/macOS only: run the command through a POSIX shell in the sandbox.
                return WrapProcess(spec, new[] { "/bin/sh", "-c", innerCommand }, workDir, readOnlyPaths);

            case SandboxKind.Custom:
                // Render the user template. {cmd} = the raw inner command, {workdir}, {image}.
                string rendered = spec.CustomTemplate
                    .Replace("{cmd}", innerCommand)
                    .Replace("{workdir}", workDir)
                    .Replace("{image}", spec.Image);
                // Run the rendered template via the host shell so users can write a full pipeline.
                if (OperatingSystem.IsWindows())
                    return ("cmd.exe", "/c " + rendered);
                return ("/bin/sh", "-c " + ArgvToken(rendered));

            default:
                throw new SandboxException("WrapShellCommand is only valid for Wrapper/Custom backends.");
        }
    }

    /// <summary>
    /// Render the (file, argv-string) that runs the process <paramref name="innerArgv"/> (program + args)
    /// inside a wrapper backend (bwrap/firejail/sandbox-exec), confined to <paramref name="workDir"/>.
    /// Every token is quoted for <see cref="ProcessStartInfo.Arguments"/>, so the wrapper receives exactly
    /// <paramref name="innerArgv"/>, byte-for-byte. Only valid for <see cref="SandboxKind.Wrapper"/>.
    /// </summary>
    /// <param name="readOnlyPaths">Extra host paths the process must be able to read (e.g. the Python
    /// installation behind a venv). bwrap already binds / read-only, so it needs none.</param>
    public static (string File, string Args) WrapProcess(SandboxSpec spec, IReadOnlyList<string> innerArgv, string workDir,
        IReadOnlyList<string>? readOnlyPaths = null)
    {
        readOnlyPaths ??= Array.Empty<string>();
        if (spec.Kind != SandboxKind.Wrapper)
            throw new SandboxException("WrapProcess is only valid for wrapper backends.");
        // Paths are spliced into firejail flags and the Seatbelt (SBPL) string literal: refuse anything that
        // could break out of them instead of trying to escape it.
        if (!IsSafeSandboxPath(workDir))
            throw new SandboxException($"sandbox work dir contains characters that cannot be confined safely: {workDir}");
        readOnlyPaths = readOnlyPaths.Where(IsSafeSandboxPath).ToList();
        var argv = new List<string>();
        switch (spec.Backend)
        {
            case "bwrap":
                argv.AddRange(new[] { "--ro-bind", "/", "/", "--bind", workDir, workDir, "--chdir", workDir,
                    "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--unshare-all" });
                if (spec.NetworkOpen) argv.Add("--share-net");
                // No --die-with-parent: it is PR_SET_PDEATHSIG, which fires when the spawning THREAD exits.
                // .NET starts processes from short-lived pool threads, so it SIGKILLed (137) any job that
                // ran longer than about a second. Mux kills its jobs/worker itself on dispose.
                break;
            case "firejail":
                argv.Add("--quiet");
                if (!spec.NetworkOpen) argv.Add("--net=none");
                argv.AddRange(new[] { "--whitelist=" + workDir, "--caps.drop=all", "--nonewprivs", "--seccomp" });
                // A whitelist hides the rest of $HOME, so expose extra paths that live there, read-only.
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var p in readOnlyPaths)
                    if (home.Length > 0 && p.StartsWith(home.TrimEnd('/') + "/", StringComparison.Ordinal))
                        argv.AddRange(new[] { "--whitelist=" + p, "--read-only=" + p });
                break;
            case "sandbox-exec":
                argv.Add("-p");
                argv.Add(SeatbeltProfile(workDir, spec.NetworkOpen, readOnlyPaths));
                break;
            default:
                throw new SandboxException($"unhandled wrapper backend '{spec.Backend}'");
        }
        argv.AddRange(innerArgv);
        var args = new List<string>(argv.Count);
        foreach (var a in argv) args.Add(ArgvToken(a));
        return (spec.Backend, string.Join(' ', args));
    }

    /// <summary>True when a host path can be placed in a wrapper flag or an SBPL string literal as-is: no
    /// <c>"</c>, <c>\</c> or control characters (the SBPL string escapes), not empty.</summary>
    internal static bool IsSafeSandboxPath(string path) =>
        !string.IsNullOrEmpty(path) && !path.Any(c => c is '"' or '\\' || char.IsControl(c));

    private static string SeatbeltProfile(string workDir, bool net, IReadOnlyList<string>? readOnlyPaths = null)
    {
        // Minimal Seatbelt SBPL: deny by default, allow exec + read of system, rw on the workdir, network optional.
        string extra = "";
        foreach (var p in readOnlyPaths ?? Array.Empty<string>()) extra += "(subpath \"" + p + "\")";
        return "(version 1)(deny default)(allow process-fork)(allow process-exec)" +
               "(allow file-read* (subpath \"/usr\")(subpath \"/System\")(subpath \"/Library\")(subpath \"/bin\")(subpath \"/sbin\")(subpath \"/private/var\")(subpath \"/etc\")" + extra + ")" +
               "(allow file-read* file-write* (subpath \"" + workDir + "\")(subpath \"/tmp\")(subpath \"/private/tmp\"))" +
               (net ? "(allow network*)" : "");
    }

    // One argv token for ProcessStartInfo.Arguments, which .NET parses with Windows/MSVCRT rules on EVERY
    // OS (not POSIX shell rules). Plain tokens pass through unchanged; anything else is quoted exactly.
    internal static string ArgvToken(string s) =>
        s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"', '\'', '\\' }) < 0
            ? s : OciSandbox.ShQuoteForArgv(s);

    // ---- allowed-path mounts mapped from the filesystem security posture --------------------
    //
    // The sandbox is where shell + Python execute; binding the host AllowedPaths in (read-only or
    // read-write per posture) lets sandboxed code work on the same project files the native Filesystem
    // tools see, instead of being copy-in/out isolated. The RW/RO split mirrors the fs SecurityMode so
    // there is ONE mental model (no separate mount config):
    //   "none"            -> all allowed paths RW
    //   "lax" / "yolo"    -> all allowed paths RW
    //   "standard"(deflt) -> FIRST allowed path (the workspace) RW, the rest RO  (Codex workspace-write)
    //   "secure"          -> ALL allowed paths RO  (matches "writes elevate to the user" - no silent writes)
    // The Mux-internal session work dir (/work) is always RW separately (scratch + venv, not host data).
    // Each host path is mounted at /host/<sanitized-leaf> to avoid colliding with the image's own dirs.
    internal static IReadOnlyList<SandboxMount> ResolveMounts(FilesystemConfig fs)
    {
        var paths = (fs?.AllowedPaths ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0) return Array.Empty<SandboxMount>();

        string mode = (fs!.SecurityMode ?? "standard").Trim().ToLowerInvariant();
        var mounts = new List<SandboxMount>(paths.Count);
        var usedLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paths.Count; i++)
        {
            bool ro = mode switch
            {
                "none" or "lax" or "yolo" => false,
                "secure" => true,
                _ => i != 0,   // "standard": first (workspace) RW, rest RO
            };
            string leaf = SanitizeLeaf(paths[i]);
            string guest = "/host/" + leaf;
            // de-dup guest leaf names (two paths with same leaf) by suffixing an index
            int n = 1; string g = guest;
            while (!usedLeaves.Add(g)) { g = guest + "_" + (++n); }
            mounts.Add(new SandboxMount(paths[i], g, ro));
        }
        return mounts;
    }

    private static string SanitizeLeaf(string hostPath)
    {
        string leaf;
        try { leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(hostPath)); }
        catch { leaf = hostPath; }
        if (string.IsNullOrWhiteSpace(leaf)) leaf = "root";
        var sb = new System.Text.StringBuilder(leaf.Length);
        foreach (char c in leaf) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.Length == 0 ? "p" : sb.ToString();
    }
}
