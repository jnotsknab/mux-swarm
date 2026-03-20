using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MuxSwarm.Utils;

namespace MuxSwarm.State;

/// <summary>
/// Registers/removes mux-swarm as an OS-level service that survives reboots.
///   Windows — Task Scheduler via XML (supports WorkingDirectory, restart-on-failure)
///   Linux   — systemd user service with linger (starts before login)
///   macOS   — launchd LaunchAgent with KeepAlive
///
/// Usage:
///   ms --register --serve --daemon --watchdog
///   ms --remove
/// </summary>
public static class ServiceRegistration
{
    private const string ServiceName = "MuxSwarm";
    private const string LinuxServiceName = "mux-swarm";
    private const string MacLabel = "com.mux-swarm";

    /// <summary>Flags stripped from the service definition to avoid re-registration loops.</summary>
    private static readonly HashSet<string> StripFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--register", "--remove"
    };
    
    /// <summary>
    /// Register mux-swarm as an OS service. All CLI args (minus --register/--remove)
    /// are forwarded as the service launch args.
    /// </summary>
    public static void Register(string[] originalArgs)
    {
        var (exePath, workDir) = GetExecutableInfo();
        var serviceArgs = BuildServiceArgs(originalArgs);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            RegisterWindows(exePath, workDir, serviceArgs);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            RegisterMac(exePath, workDir, serviceArgs);
        else
            RegisterLinux(exePath, workDir, serviceArgs);
    }

    /// <summary>
    /// Remove the mux-swarm OS service registration.
    /// </summary>
    public static void Remove()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            RemoveWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            RemoveMac();
        else
            RemoveLinux();
    }
    
    /// <summary>
    /// Resolves the actual binary path and install directory.
    /// Process.MainModule.FileName may point to a shim (ms.exe alias),
    /// so we resolve from PlatformContext.BaseDirectory instead.
    /// </summary>
    private static (string ExePath, string WorkingDir) GetExecutableInfo()
    {
        var installDir = PlatformContext.BaseDirectory;

        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "MuxSwarm.exe"
            : "MuxSwarm";

        var exePath = Path.Combine(installDir, binaryName);

        if (!File.Exists(exePath))
            throw new FileNotFoundException(
                $"Cannot find binary at {exePath}. Ensure mux-swarm is installed correctly.");

        return (exePath, installDir);
    }
    
    private static void RegisterWindows(string exePath, string workDir, string serviceArgs)
    {
        // XML import gives us WorkingDirectory, RestartOnFailure,
        // DisallowStartIfOnBatteries=false, and unlimited execution time
        // that schtasks /Create flags cannot express.
        var taskXml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <BootTrigger>
                  <Delay>PT30S</Delay>
                  <Enabled>true</Enabled>
                </BootTrigger>
              </Triggers>
              <Principals>
                <Principal>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <RestartOnFailure>
                  <Interval>PT60S</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
              </Settings>
              <Actions>
                <Exec>
                  <Command>{SecurityEscape(exePath)}</Command>
                  <Arguments>{SecurityEscape(serviceArgs)}</Arguments>
                  <WorkingDirectory>{SecurityEscape(workDir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var tempXml = Path.Combine(Path.GetTempPath(), "muxswarm-task.xml");

        try
        {
            File.WriteAllText(tempXml, taskXml, Encoding.Unicode);

            var result = RunProcess("schtasks",
                $"/Create /TN \"{ServiceName}\" /XML \"{tempXml}\" /F");

            if (result.ExitCode == 0)
            {
                MuxConsole.WriteSuccess($"Registered as Windows scheduled task: {ServiceName}");
                MuxConsole.WriteMuted($"  Binary:     {exePath}");
                MuxConsole.WriteMuted($"  Args:       {serviceArgs}");
                MuxConsole.WriteMuted($"  WorkingDir: {workDir}");
                MuxConsole.WriteMuted("  Trigger:    System startup (30s delay, auto-restart on failure)");
                MuxConsole.WriteMuted("  Manage:     taskschd.msc or schtasks /Query /TN MuxSwarm");
            }
            else
            {
                MuxConsole.WriteError($"Failed to register task. Exit code: {result.ExitCode}");
                if (!string.IsNullOrWhiteSpace(result.StdErr))
                    MuxConsole.WriteError(result.StdErr.Trim());
                MuxConsole.WriteMuted("Try running from an elevated (Administrator) terminal.");
            }
        }
        finally
        {
            try { File.Delete(tempXml); } catch { /* cleanup best-effort */ }
        }
    }

    private static void RemoveWindows()
    {
        var result = RunProcess("schtasks", $"/Delete /TN \"{ServiceName}\" /F");

        if (result.ExitCode == 0)
            MuxConsole.WriteSuccess($"Removed Windows scheduled task: {ServiceName}");
        else
        {
            MuxConsole.WriteWarning("Could not remove task. It may not exist.");
            if (!string.IsNullOrWhiteSpace(result.StdErr))
                MuxConsole.WriteMuted(result.StdErr.Trim());
        }
    }
    
    private static string BuildServiceArgs(string[] originalArgs)
    {
        var filtered = new List<string>();
        bool stripWatchdog = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        for (int i = 0; i < originalArgs.Length; i++)
        {
            if (StripFlags.Contains(originalArgs[i]))
                continue;

            if (stripWatchdog && originalArgs[i].Equals("--watchdog", StringComparison.OrdinalIgnoreCase))
                continue;

            filtered.Add(originalArgs[i]);
        }

        return string.Join(" ", filtered);
    }

    private static StringBuilder BuildEnvironmentLines()
    {
        var envLines = new StringBuilder();

        // Capture PATH so bare commands (uvx, npx) resolve in the service
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
            envLines.AppendLine($"Environment=PATH={path}");

        envLines.AppendLine("Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1");

        try
        {
            var config = Setup.Setup.LoadConfig(PlatformContext.ConfigPath);

            // Forward API key env vars from current environment
            foreach (var provider in config.LlmProviders.Where(p =>
                         p.Enabled && !string.IsNullOrEmpty(p.ApiKeyEnvVar)))
            {
                var value = Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar);
                if (!string.IsNullOrEmpty(value))
                    envLines.AppendLine($"Environment={provider.ApiKeyEnvVar}={value}");
            }

            // Forward MCP server env vars
            foreach (var (_, server) in config.McpServers)
            {
                if (server.Env == null) continue;
                foreach (var (key, _) in server.Env)
                {
                    var value = Environment.GetEnvironmentVariable(key);
                    if (!string.IsNullOrEmpty(value))
                        envLines.AppendLine($"Environment={key}={value}");
                }
            }
        }
        catch { /* best effort */ }

        return envLines;
    }

    private static Dictionary<string, string> BuildEnvironmentDict()
    {
        var env = new Dictionary<string, string>();

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
            env["PATH"] = path;

        env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";

        try
        {
            var config = Setup.Setup.LoadConfig(PlatformContext.ConfigPath);

            foreach (var provider in config.LlmProviders.Where(p =>
                         p.Enabled && !string.IsNullOrEmpty(p.ApiKeyEnvVar)))
            {
                var value = Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar);
                if (!string.IsNullOrEmpty(value))
                    env[provider.ApiKeyEnvVar] = value;
            }

            foreach (var (_, server) in config.McpServers)
            {
                if (server.Env == null) continue;
                foreach (var (key, _) in server.Env)
                {
                    var value = Environment.GetEnvironmentVariable(key);
                    if (!string.IsNullOrEmpty(value))
                        env[key] = value;
                }
            }
        }
        catch { /* best effort */ }

        return env;
    }

    private static void RegisterLinux(string exePath, string workDir, string serviceArgs)
    {
        var serviceDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user");

        Directory.CreateDirectory(serviceDir);

        var servicePath = Path.Combine(serviceDir, $"{LinuxServiceName}.service");

        var execStart = string.IsNullOrEmpty(serviceArgs)
            ? exePath
            : $"{exePath} {serviceArgs}";

        var envLines = BuildEnvironmentLines();

        var unit = $"""
            [Unit]
            Description=Mux-Swarm Daemon
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=simple
            WorkingDirectory={workDir}
            ExecStart={execStart}
            Restart=always
            RestartSec=10
            {envLines.ToString().TrimEnd()}

            [Install]
            WantedBy=default.target
            """;

        File.WriteAllText(servicePath, unit);

        RunProcess("systemctl", "--user daemon-reload");
        var enableResult = RunProcess("systemctl", $"--user enable {LinuxServiceName}");
        var startResult = RunProcess("systemctl", $"--user start {LinuxServiceName}");

        // Enable linger so the service starts before login on headless systems
        var user = Environment.UserName;
        RunProcess("loginctl", $"enable-linger {user}");

        if (enableResult.ExitCode == 0)
        {
            MuxConsole.WriteSuccess($"Registered as systemd user service: {LinuxServiceName}");
            MuxConsole.WriteMuted($"  Unit file:  {servicePath}");
            MuxConsole.WriteMuted($"  Command:    {execStart}");
            MuxConsole.WriteMuted($"  WorkingDir: {workDir}");
            MuxConsole.WriteMuted($"  Linger:     enabled for {user}");
            MuxConsole.WriteMuted($"  Manage:     systemctl --user status {LinuxServiceName}");
            MuxConsole.WriteMuted($"  Logs:       journalctl --user -u {LinuxServiceName} -f");

            if (startResult.ExitCode == 0)
                MuxConsole.WriteSuccess("Service started.");
            else
                MuxConsole.WriteWarning("Service enabled but failed to start immediately. Check logs.");
        }
        else
        {
            MuxConsole.WriteError("Failed to enable systemd service.");
            if (!string.IsNullOrWhiteSpace(enableResult.StdErr))
                MuxConsole.WriteError(enableResult.StdErr.Trim());
        }
    }

    private static void RemoveLinux()
    {
        RunProcess("systemctl", $"--user stop {LinuxServiceName}");
        RunProcess("systemctl", $"--user disable {LinuxServiceName}");

        var servicePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user", $"{LinuxServiceName}.service");

        if (File.Exists(servicePath))
        {
            File.Delete(servicePath);
            RunProcess("systemctl", "--user daemon-reload");
            MuxConsole.WriteSuccess($"Removed systemd service: {LinuxServiceName}");
            MuxConsole.WriteMuted($"  Deleted: {servicePath}");
        }
        else
        {
            MuxConsole.WriteWarning($"Service file not found: {servicePath}");
        }
    }

    private static void RegisterMac(string exePath, string workDir, string serviceArgs)
    {
        var agentsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");

        Directory.CreateDirectory(agentsDir);

        var plistPath = Path.Combine(agentsDir, $"{MacLabel}.plist");

        var argsXml = new StringBuilder();
        argsXml.AppendLine($"        <string>{SecurityEscape(exePath)}</string>");

        if (!string.IsNullOrEmpty(serviceArgs))
        {
            foreach (var arg in serviceArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                argsXml.AppendLine($"        <string>{SecurityEscape(arg)}</string>");
        }

        // Build environment variables dict for plist
        var envDict = BuildEnvironmentDict();
        var envXml = new StringBuilder();
        if (envDict.Count > 0)
        {
            envXml.AppendLine("        <key>EnvironmentVariables</key>");
            envXml.AppendLine("        <dict>");
            foreach (var (key, value) in envDict)
            {
                envXml.AppendLine($"            <key>{SecurityEscape(key)}</key>");
                envXml.AppendLine($"            <string>{SecurityEscape(value)}</string>");
            }
            envXml.AppendLine("        </dict>");
        }

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "Mux-Swarm", "Logs");

        Directory.CreateDirectory(logDir);

        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{MacLabel}</string>
                <key>ProgramArguments</key>
                <array>
            {argsXml.ToString().TrimEnd()}
                </array>
                <key>WorkingDirectory</key>
                <string>{SecurityEscape(workDir)}</string>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
            {envXml.ToString().TrimEnd()}
                <key>StandardOutPath</key>
                <string>{Path.Combine(logDir, "mux-swarm.stdout.log")}</string>
                <key>StandardErrorPath</key>
                <string>{Path.Combine(logDir, "mux-swarm.stderr.log")}</string>
            </dict>
            </plist>
            """;

        File.WriteAllText(plistPath, plist);

        var loadResult = RunProcess("launchctl", $"load {plistPath}");

        if (loadResult.ExitCode == 0)
        {
            MuxConsole.WriteSuccess($"Registered as macOS LaunchAgent: {MacLabel}");
            MuxConsole.WriteMuted($"  Plist:      {plistPath}");
            MuxConsole.WriteMuted($"  WorkingDir: {workDir}");
            MuxConsole.WriteMuted($"  Manage:     launchctl list | grep mux-swarm");
            MuxConsole.WriteMuted($"  Logs:       {logDir}");
        }
        else
        {
            MuxConsole.WriteError("Failed to load LaunchAgent.");
            if (!string.IsNullOrWhiteSpace(loadResult.StdErr))
                MuxConsole.WriteError(loadResult.StdErr.Trim());
        }
    }

    private static void RemoveMac()
    {
        var plistPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", $"{MacLabel}.plist");

        if (File.Exists(plistPath))
        {
            RunProcess("launchctl", $"unload {plistPath}");
            File.Delete(plistPath);
            MuxConsole.WriteSuccess($"Removed macOS LaunchAgent: {MacLabel}");
            MuxConsole.WriteMuted($"  Deleted: {plistPath}");
        }
        else
        {
            MuxConsole.WriteWarning($"LaunchAgent not found: {plistPath}");
        }
    }
    
    /// <summary>
    /// Escape special XML characters in values embedded in XML templates.
    /// Prevents injection via crafted paths or arguments.
    /// </summary>
    private static string SecurityEscape(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private record ProcessResult(int ExitCode, string StdOut, string StdErr);

    private static ProcessResult RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return new ProcessResult(-1, "", "Failed to start process.");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15_000);

            return new ProcessResult(proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }
}