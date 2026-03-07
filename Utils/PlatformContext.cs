using System.Runtime.InteropServices;

namespace MuxSwarm.Utils;

public static class PlatformContext
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static readonly string BaseDirectory = AppContext.BaseDirectory;
    public static readonly string ConfigDirectory = Path.Combine(BaseDirectory, "Configs"); 
    public static readonly string ConfigPath = Path.Combine(ConfigDirectory, "Config.json");
    public static readonly string SwarmPath = Path.Combine(ConfigDirectory, "Swarm.json");
    public static readonly string PromptsDirectory = Path.Combine(BaseDirectory, "Prompts", "Agents");
    public static readonly string SessionsDirectory = Path.Combine(BaseDirectory, "Sessions");
    public static readonly string SkillsDirectory = Path.Combine(BaseDirectory, "Skills", "bundled");
    
    public static string PathSeparator => IsWindows ? "\\" : "/";
    public static string ExecutableExtension => IsWindows ? ".exe" : "";
    public static string Which => IsWindows ? "where" : "which";
    public static string Shell => IsWindows ? "powershell" : "bash";
    public static string ShellFlag => IsWindows ? "-Command" : "-c";
    
}