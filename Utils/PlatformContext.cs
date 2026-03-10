using System.Runtime.InteropServices;

namespace MuxSwarm.Utils;

public static class PlatformContext
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static readonly string BaseDirectory = AppContext.BaseDirectory;
    public static readonly string ConfigDirectory = Path.Combine(BaseDirectory, "Configs");
    public static readonly string PromptsDirectory = Path.Combine(BaseDirectory, "Prompts", "Agents");
    public static readonly string SessionsDirectory = Path.Combine(BaseDirectory, "Sessions");
    public static readonly string SkillsDirectory = Path.Combine(BaseDirectory, "Skills", "bundled");

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