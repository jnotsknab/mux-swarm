using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// What family of wrapping a backend uses. Drives how <see cref="SandboxBackend.WrapShell"/> and the
/// per-session container lifecycle behave.
/// </summary>
internal enum SandboxKind
{
    Host,     // no wrap (current behavior)
    Oci,      // docker/podman/nerdctl/gvisor - persistent container per session, exec into it
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
    public string? Runtime { get; init; }              // e.g. "runsc" for gvisor

    public bool UsesAllowlist => AllowedDomains.Count > 0;
    public bool IsOci => Kind == SandboxKind.Oci;
}

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
        new(StringComparer.OrdinalIgnoreCase) { "docker", "podman", "nerdctl", "gvisor" };

    private static readonly HashSet<string> WrapperBackends =
        new(StringComparer.OrdinalIgnoreCase) { "bwrap", "firejail", "sandbox-exec" };

    /// <summary>
    /// Resolve the active sandbox spec from config. Returns null for "host" (no sandbox). Throws
    /// <see cref="SandboxException"/> (user-facing) for an unknown backend, an impossible network combo,
    /// a custom backend without a template, or a backend whose binary/runtime is not installed/ready.
    /// </summary>
    public static SandboxSpec? Resolve(SandboxConfig cfg)
    {
        string backend = (cfg.Backend ?? "host").Trim().ToLowerInvariant();
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
                throw new SandboxException("sandbox.allowedDomains is only enforceable on OCI backends " +
                    "(docker/podman/nerdctl/gvisor). A 'custom' backend manages its own network; remove allowedDomains.");
            return new SandboxSpec
            {
                Kind = SandboxKind.Custom, Backend = backend, Binary = "",
                Image = cfg.Image ?? "", NetworkOpen = cfg.Network, CustomTemplate = cfg.Command,
            };
        }

        // ----- OCI family -----
        if (OciBackends.Contains(backend))
        {
            // gvisor is docker + --runtime=runsc.
            string binary = backend == "gvisor" ? "docker" : backend;
            string? runtime = backend == "gvisor" ? "runsc" : null;
            EnsureBinaryReady(binary, ociDaemonCheck: true);
            if (string.IsNullOrWhiteSpace(cfg.Image))
                throw new SandboxException($"sandbox.backend '{backend}' requires sandbox.image to be set.");
            return new SandboxSpec
            {
                Kind = SandboxKind.Oci, Backend = backend, Binary = binary, Image = cfg.Image,
                NetworkOpen = cfg.Network, AllowedDomains = allow, Runtime = runtime,
            };
        }

        // ----- wrapper family -----
        if (WrapperBackends.Contains(backend))
        {
            if (allow.Count > 0)
                throw new SandboxException($"sandbox.allowedDomains is not enforceable on the '{backend}' wrapper backend " +
                    "(namespace network isolation is all-or-nothing without privileged plumbing). Use an OCI backend " +
                    "(docker/podman/nerdctl) for a domain allowlist, or set sandbox.network true/false.");
            if (backend == "sandbox-exec" && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                throw new SandboxException("sandbox.backend 'sandbox-exec' is macOS-only.");
            if ((backend is "bwrap" or "firejail") && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                throw new SandboxException($"sandbox.backend '{backend}' is Linux-only.");
            EnsureBinaryReady(backend, ociDaemonCheck: false);
            return new SandboxSpec
            {
                Kind = SandboxKind.Wrapper, Backend = backend, Binary = backend,
                Image = "", NetworkOpen = cfg.Network,
            };
        }

        throw new SandboxException($"Unknown sandbox.backend '{cfg.Backend}'. Valid: host, docker, podman, nerdctl, " +
            "gvisor, bwrap, firejail, sandbox-exec, custom.");
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
        if (!BinaryOnPath(binary))
            throw new SandboxException($"sandbox backend needs '{binary}' but it was not found on PATH. Install it (or pick another backend).");
        if (ociDaemonCheck && !OciDaemonReady(binary))
            throw new SandboxException($"'{binary}' is installed but its daemon/runtime is not reachable ('{binary} info' failed). Start it and retry.");
    }

    private static bool BinaryOnPath(string binary)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary, Arguments = "--version",
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
    public static (string File, string Args) WrapShellCommand(SandboxSpec spec, string innerCommand, string workDir)
    {
        // The inner command is always run through a POSIX shell inside the sandbox (wrapper backends are
        // Linux/macOS only; custom is user-defined). Quote the whole command as one -c argument.
        string shell = "/bin/sh";
        string shArgs = "-c " + ShQuote(innerCommand);

        switch (spec.Kind)
        {
            case SandboxKind.Wrapper:
                return spec.Backend switch
                {
                    "bwrap" => ("bwrap", string.Join(' ',
                        "--ro-bind / /",
                        "--bind", Q(workDir), Q(workDir),
                        "--chdir", Q(workDir),
                        "--proc /proc --dev /dev --tmpfs /tmp",
                        "--unshare-all",
                        spec.NetworkOpen ? "--share-net" : "",
                        "--die-with-parent",
                        shell, shArgs)),
                    "firejail" => ("firejail", string.Join(' ',
                        "--quiet",
                        spec.NetworkOpen ? "" : "--net=none",
                        "--whitelist=" + Q(workDir),
                        "--caps.drop=all --nonewprivs --seccomp",
                        shell, shArgs)),
                    "sandbox-exec" => ("sandbox-exec", string.Join(' ',
                        "-p", SeatbeltProfile(workDir, spec.NetworkOpen),
                        shell, shArgs)),
                    _ => throw new SandboxException($"unhandled wrapper backend '{spec.Backend}'"),
                };

            case SandboxKind.Custom:
                // Render the user template. {cmd} = the shell-quoted inner command, {workdir}, {image}.
                string rendered = spec.CustomTemplate
                    .Replace("{cmd}", innerCommand)
                    .Replace("{workdir}", workDir)
                    .Replace("{image}", spec.Image);
                // Run the rendered template via the host shell so users can write a full pipeline.
                if (OperatingSystem.IsWindows())
                    return ("cmd.exe", "/c " + rendered);
                return ("/bin/sh", "-c " + ShQuote(rendered));

            default:
                throw new SandboxException("WrapShellCommand is only valid for Wrapper/Custom backends.");
        }
    }

    private static string SeatbeltProfile(string workDir, bool net)
    {
        // Minimal Seatbelt SBPL: deny by default, allow exec + read of system, rw on the workdir, network optional.
        string p = "(version 1)(deny default)(allow process-fork)(allow process-exec)" +
                   "(allow file-read* (subpath \\\"/usr\\\")(subpath \\\"/System\\\")(subpath \\\"/Library\\\")(subpath \\\"/bin\\\")(subpath \\\"/sbin\\\")(subpath \\\"/private/var\\\")(subpath \\\"/etc\\\"))" +
                   "(allow file-read* file-write* (subpath \\\"" + workDir + "\\\")(subpath \\\"/tmp\\\")(subpath \\\"/private/tmp\\\"))" +
                   (net ? "(allow network*)" : "");
        return "'" + p.Replace("\\\"", "\"") + "'";
    }

    // POSIX single-quote escaping for a whole command passed to `sh -c`.
    private static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
    // Quote a path arg that may contain spaces (double-quote form for argv tokens).
    private static string Q(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;
}
