using System.Runtime.InteropServices;

namespace MuxSwarm.Utils;

public static class PlatformContext
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static readonly string BaseDirectory = AppContext.BaseDirectory;

    // The root the interactive "@" file picker indexes. Precedence:
    //   1. Explicit override ("--workspace <path>" / "/workspace") - always wins.
    //   2. RESOLVED launch dir - recovers the directory the user actually launched FROM when the install
    //      shim/alias cd'd us into the install dir first (see ResolveLaunchCwd). Without this, @ would index
    //      mux's OWN install files instead of the user's project on the primary (shim) launch path.
    //   3. The process CWD (Directory.GetCurrentDirectory()) - correct for a direct binary launch.
    private static string? _workspaceRoot;
    public static string WorkspaceRoot
    {
        get => _workspaceRoot ?? ResolveLaunchCwd();
        set => _workspaceRoot = string.IsNullOrWhiteSpace(value) ? null : System.IO.Path.GetFullPath(value);
    }

    /// <summary>
    /// Resolves the directory the user launched Mux FROM. The install shim (PowerShell profile function on
    /// Windows; bash script on Linux/macOS) cd's into the install dir before invoking the exe, so the raw
    /// process CWD is the install dir, not the user's project. We recover the real launch dir from, in order:
    ///   - MUX_LAUNCH_CWD: an explicit hand-off the shim can export (universal; the only signal available on
    ///     Windows, where PowerShell's Set-Location exports nothing).
    ///   - OLDPWD: bash's `cd` sets+exports this to the pre-cd dir, so on Linux/macOS the fix needs NO shim
    ///     change at all.
    /// A recovered dir is trusted ONLY when the process CWD is actually the install dir (i.e. a shim really
    /// did cd us there) AND the candidate exists and is not itself the install dir - so a direct binary
    /// launch (cwd != install) always uses the real cwd and a stale shell OLDPWD can never hijack it.
    /// </summary>
    private static string ResolveLaunchCwd() =>
        ResolveLaunchCwd(
            Directory.GetCurrentDirectory(),
            BaseDirectory,
            Environment.GetEnvironmentVariable,
            System.IO.Directory.Exists);

    /// <summary>
    /// Pure resolution used by <see cref="ResolveLaunchCwd()"/> (seams injected for testing). Returns the
    /// user's real launch dir per the precedence documented on that method.
    /// </summary>
    internal static string ResolveLaunchCwd(
        string cwd, string baseDirectory, Func<string, string?> envLookup, Func<string, bool> dirExists)
    {
        string installDir = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(baseDirectory));

        // Direct launch (not cd'd into the install dir): the process CWD is already correct.
        if (!string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(cwd)),
                installDir, StringComparison.OrdinalIgnoreCase))
            return cwd;

        foreach (var name in new[] { "MUX_LAUNCH_CWD", "OLDPWD" })
        {
            var candidate = envLookup(name);
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string full;
            try { full = System.IO.Path.GetFullPath(candidate); } catch { continue; }
            if (!dirExists(full)) continue;
            if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(full), installDir,
                    StringComparison.OrdinalIgnoreCase))
                continue; // launched from the install dir itself; nothing to recover
            return full;
        }

        return cwd;
    }

    /// <summary>True when the resolved workspace root is the mux install/base dir (so the @ file
    /// picker would index mux's own files rather than a real project) - used to surface a hint.</summary>
    public static bool WorkspaceIsInstallDir =>
        string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(WorkspaceRoot)),
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(BaseDirectory)),
            StringComparison.OrdinalIgnoreCase);
    public static readonly string ConfigDirectory = Path.Combine(BaseDirectory, "Configs");
    public static readonly string PromptsDirectory = Path.Combine(BaseDirectory, "Prompts", "Agents");
    public static readonly string ContextDirectory = Path.Combine(BaseDirectory, "Context");
    public static readonly string SessionsDirectory = Path.Combine(BaseDirectory, "Sessions");
    public static readonly string TeamsDirectory = Path.Combine(BaseDirectory, "Teams");
    public static readonly string SkillsDirectory = Path.Combine(BaseDirectory, "Skills", "bundled");
    public static readonly string MascotPath = Path.Combine(BaseDirectory, "assets", "mascot.png");


    private static string? _configPathOverride;
    private static string? _swarmPathOverride;

    public static string ConfigPath => _configPathOverride ?? Path.Combine(ConfigDirectory, "Config.json");
    public static string SwarmPath => _swarmPathOverride ?? Path.Combine(ConfigDirectory, "Swarm.json");

    public static void ApplyOverrides(string? configPath = null, string? swarmPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            if (!File.Exists(configPath))
                throw new FileNotFoundException($"--cfg path does not exist: {configPath}", configPath);
            _configPathOverride = Path.GetFullPath(configPath);
        }

        if (!string.IsNullOrWhiteSpace(swarmPath))
        {
            if (!File.Exists(swarmPath))
                throw new FileNotFoundException($"--swarmcfg path does not exist: {swarmPath}", swarmPath);
            _swarmPathOverride = Path.GetFullPath(swarmPath);
        }
    }

    public static string PathSeparator => IsWindows ? "\\" : "/";
    public static string ExecutableExtension => IsWindows ? ".exe" : "";
    public static string Which => IsWindows ? "where" : "which";
    public static string Shell => IsWindows ? "powershell" : "bash";
    public static string ShellFlag => IsWindows ? "-Command" : "-c";
}