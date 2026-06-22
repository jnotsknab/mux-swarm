using System.Runtime.InteropServices;

namespace MuxSwarm.Utils;

public static class PlatformContext
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static readonly string BaseDirectory = AppContext.BaseDirectory;

    // The root the interactive "@" file picker indexes. Defaults to the process CWD, but when
    // mux is launched via an alias that resolves CWD to the install dir, the @ picker would index
    // mux's own files - so this is overridable via "--workspace <path>" to point at the real
    // project. Set once at startup by App.ParseArgs.
    private static string? _workspaceRoot;
    public static string WorkspaceRoot
    {
        get => _workspaceRoot ?? Directory.GetCurrentDirectory();
        set => _workspaceRoot = string.IsNullOrWhiteSpace(value) ? null : System.IO.Path.GetFullPath(value);
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