using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MuxSwarm.Utils;

public static class DepResolver
{
    private static bool _installOffered;
    private static bool _installAll;

    // Requirements you can tune
    private const int MinNodeMajor = 20; // Brave MCP needs newer Node; 20+ is a safe baseline

    public record Dep(string Name, string Reason, bool Required = true);

    public record DepResult(
        Dep Dep,
        bool FoundBefore,
        bool FoundAfter,
        bool WasOnPath,
        string? FoundPath,
        bool AddedToPathForProcess,
        bool PersistedToUserPath,
        bool PersistedToSystemPath
    );

    public static List<DepResult> EnsureDepsInteractive(
        IEnumerable<Dep> deps,
        Func<string, bool> isBinaryAvailable,
        Func<string, (bool found, bool onPath, string? fullPath)> findBinary,
        bool verbose = false)
    {
        var results = new List<DepResult>();
        _installOffered = false;
        _installAll = false;

        foreach (var dep in deps)
        {
            var before = FindWithAliases(dep.Name, findBinary);
            var foundBefore = before.found;
            var onPathBefore = before.onPath;
            var foundPath = before.fullPath;

            bool addedProc = false, persistedUser = false, persistedSystem = false;

            if (foundBefore && !onPathBefore && !string.IsNullOrEmpty(foundPath))
            {
                var dir = Path.GetDirectoryName(foundPath)!;

                AddDirToProcessPath(dir);
                addedProc = true;

                if (PlatformContext.IsWindows)
                {
                    var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
                    var systemPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
                    bool alreadyPersistedUser = PathListContains(userPath, dir);
                    bool alreadyPersistedSystem = PathListContains(systemPath, dir);

                    if (alreadyPersistedUser || alreadyPersistedSystem)
                    {
                        MuxConsole.WriteSuccess($"Found '{dep.Name}' at '{foundPath}', added to PATH for this session (already persisted).");
                    }
                    else
                    {
                        MuxConsole.WriteWarning($"Found '{dep.Name}' at '{foundPath}', added to PATH for this session.");

                        if (MuxConsole.Confirm("Persist to User PATH (recommended)?", defaultValue: false))
                            persistedUser = TryPersistToUserPath(dir, verbose);

                        if (MuxConsole.Confirm("Persist to System PATH (admin)?", defaultValue: false))
                            persistedSystem = TryPersistToSystemPathWindows(dir, verbose);
                    }
                }
                else
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var localBin = Path.Combine(home, ".local", "bin");
                    var linkPath = Path.Combine(localBin, Path.GetFileName(foundPath));

                    if (Path.Exists(linkPath))
                    {
                        MuxConsole.WriteSuccess($"Found '{dep.Name}' at '{foundPath}', added to PATH for this session (already symlinked).");
                        AddDirToProcessPath(localBin);
                    }
                    else
                    {
                        MuxConsole.WriteWarning($"Found '{dep.Name}' at '{foundPath}', added to PATH for this session.");

                        if (MuxConsole.Confirm($"Symlink '{dep.Name}' into ~/.local/bin (recommended)?", defaultValue: false))
                            persistedUser = TryPersistUnixSymlink(foundPath, verbose);
                    }
                }
            }

            if (!foundBefore)
            {
                MuxConsole.WriteError($"'{dep.Name}' not found anywhere on this system.");

                if (!_installOffered)
                {
                    _installAll = MuxConsole.Confirm("Install all missing dependencies automatically?", defaultValue: false);
                    _installOffered = true;
                }

                if (_installAll)
                {
                    var installed = TryInstallDep(dep.Name, findBinary, verbose);
                    if (installed)
                    {
                        var postInstall = FindWithAliases(dep.Name, findBinary);
                        if (postInstall.found)
                        {
                            foundBefore = true;
                            foundPath = postInstall.fullPath;

                            if (!postInstall.onPath && !string.IsNullOrEmpty(postInstall.fullPath))
                            {
                                AddDirToProcessPath(Path.GetDirectoryName(postInstall.fullPath)!);
                                addedProc = true;
                            }
                        }
                    }
                }
            }

            var after = FindWithAliases(dep.Name, findBinary);

            results.Add(new DepResult(
                Dep: dep,
                FoundBefore: foundBefore,
                FoundAfter: after.found,
                WasOnPath: after.onPath,
                FoundPath: after.fullPath ?? foundPath,
                AddedToPathForProcess: addedProc,
                PersistedToUserPath: persistedUser,
                PersistedToSystemPath: persistedSystem
            ));

            if (after.found)
                MuxConsole.WriteSuccess(dep.Name);
            else
                MuxConsole.WriteWarning($"{dep.Name} — {dep.Reason}");
        }

        return results;
    }

    // ---------------------------
    // Installer logic (prints output)
    // ---------------------------

    private static bool TryInstallDep(
        string name,
        Func<string, (bool found, bool onPath, string? fullPath)> findBinary,
        bool verbose)
    {
        try
        {
            // Special case: we may "have node" but it's too old
            if (name is "node" or "npm" or "npx")
            {
                var nodeOk = EnsureNodeVersionAtLeast(MinNodeMajor, verbose);
                if (nodeOk)
                    return true;
                // If not OK, proceed with installers that upgrade node
            }
            else
            {
                // If already satisfied (including aliases), don't do anything.
                if (FindWithAliases(name, findBinary).found)
                {
                    MuxConsole.WriteSuccess($"'{name}' already present.");
                    return true;
                }
            }

            var installers = GetInstallCommands(name);
            if (installers.Count == 0)
            {
                MuxConsole.WriteWarning($"No automatic installer available for '{name}'.");
                return false;
            }

            // Run ALL commands in order; do NOT claim success until verification passes.
            for (int i = 0; i < installers.Count; i++)
            {
                var inst = installers[i];
                if (inst.cmd == null) continue;

                MuxConsole.WriteInfo($"Installing '{name}' via: {inst.cmd} {inst.args}");

                var (ok, exitCode, timedOut) = RunProcessInheritConsole(
                    fileName: inst.cmd,
                    arguments: inst.args,
                    timeout: TimeSpan.FromMinutes(12)
                );

                if (timedOut)
                {
                    MuxConsole.WriteError($"Install step timed out for '{name}': {inst.cmd} {inst.args}");
                    continue;
                }

                if (!ok || exitCode != 0)
                {
                    MuxConsole.WriteError($"Install step failed for '{name}' (exit code {exitCode}).");
                    // Keep going: later commands may repair
                }

                RefreshProcessPath();

                // Verification
                if (name is "node" or "npm" or "npx")
                {
                    if (EnsureNodeVersionAtLeast(MinNodeMajor, verbose))
                    {
                        MuxConsole.WriteSuccess($"Successfully installed '{name}'.");
                        return true;
                    }
                }
                else
                {
                    if (FindWithAliases(name, findBinary).found)
                    {
                        MuxConsole.WriteSuccess($"Successfully installed '{name}'.");
                        return true;
                    }
                }
            }

            // Final verification
            if (name is "node" or "npm" or "npx")
            {
                var okFinal = EnsureNodeVersionAtLeast(MinNodeMajor, verbose);
                if (!okFinal)
                    MuxConsole.WriteError($"Installation did not provide Node {MinNodeMajor}+.");
                return okFinal;
            }

            var final = FindWithAliases(name, findBinary).found;
            if (!final)
                MuxConsole.WriteError($"Installation of '{name}' did not make '{name}' available.");
            return final;
        }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"Failed to install '{name}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns true if Node is present and >= requiredMajor.
    /// If Node isn't present or is older, returns false.
    /// Also handles Debian edge case: nodejs exists but node missing.
    /// </summary>
    private static bool EnsureNodeVersionAtLeast(int requiredMajor, bool verbose)
    {
        // Ensure `node` exists if `nodejs` exists (Debian-ish)
        if (!IsBinaryOnPath("node") && IsBinaryOnPath("nodejs"))
            TryEnsureNodeAlias(verbose);

        var major = GetNodeMajorVersion();
        if (major == null) return false;

        if (major.Value >= requiredMajor) return true;

        MuxConsole.WriteWarning($"Node.js v{major.Value} detected, but v{requiredMajor}+ is required. Upgrading...");
        return false;
    }

    private static int? GetNodeMajorVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "-v",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            proc.WaitForExit(4000);
            if (proc.ExitCode != 0) return null;

            var v = (proc.StandardOutput.ReadToEnd() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(v)) return null;

            // v20.11.0 => 20
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                v = v[1..];

            var dot = v.IndexOf('.');
            var majorStr = dot >= 0 ? v[..dot] : v;

            return int.TryParse(majorStr, out var major) ? major : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryEnsureNodeAlias(bool verbose)
    {
        try
        {
            if (IsBinaryOnPath("node")) return;
            if (!IsBinaryOnPath("nodejs")) return;

            // Try /usr/bin/nodejs -> /usr/local/bin/node (safe user-managed location)
            var nodejsPath = "/usr/bin/nodejs";
            var linkPath = "/usr/local/bin/node";

            var (cmd, args) = WrapUnix($"ln -sf {nodejsPath} {linkPath}");
            RunProcessInheritConsole(cmd, args, TimeSpan.FromSeconds(15));
            RefreshProcessPath();
        }
        catch
        {
            // non-fatal
        }
    }

    /// <summary>
    /// Run process with inherited stdio so user sees output (apt, brew, etc).
    /// </summary>
    private static (bool ok, int exitCode, bool timedOut) RunProcessInheritConsole(
        string fileName,
        string arguments,
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false
        };

        using var proc = Process.Start(psi);
        if (proc == null) return (false, -1, false);

        var exited = proc.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            try
            {
#if NET8_0_OR_GREATER
                proc.Kill(entireProcessTree: true);
#else
                proc.Kill();
#endif
            }
            catch { /* ignore */ }

            return (false, -1, true);
        }

        return (true, proc.ExitCode, false);
    }
    
    //Install CMDS

    private static List<(string? cmd, string args)> GetInstallCommands(string depName)
    {
        depName = depName.Trim();
        var cmds = new List<(string? cmd, string args)>();

        // uv / uvx
        if (depName is "uv" or "uvx")
        {
            if (PlatformContext.IsWindows)
                cmds.Add(("powershell", "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://astral.sh/uv/install.ps1 | iex\""));
            else
                cmds.Add(("sh", "-c \"curl -LsSf https://astral.sh/uv/install.sh | sh\""));
            return cmds;
        }

        // Node toolchain (node/npm/npx)
        if (depName is "node" or "npm" or "npx")
        {
            if (PlatformContext.IsWindows)
            {
                if (IsBinaryOnPath("winget"))
                    cmds.Add(("winget", "install -e --id OpenJS.NodeJS.LTS --accept-package-agreements --accept-source-agreements"));
                if (IsBinaryOnPath("choco"))
                    cmds.Add(("choco", "install nodejs-lts -y"));
                return cmds;
            }

            if (PlatformContext.IsMac)
            {
                if (IsBinaryOnPath("brew"))
                    cmds.Add(("brew", "install node"));
                return cmds;
            }

            // Linux: prefer "modern Node" sources.
            // - apt: NodeSource 20.x
            // - dnf: nodejs:20 module if available, otherwise standard
            // - pacman: repo is modern enough
            // - apk: repo is usually modern enough
            if (IsBinaryOnPath("apt"))
            {
                cmds.Add(WrapUnix("apt-get update -y || apt update"));

                // Remove potentially-old distro packages first (optional but helps)
                cmds.Add(WrapUnix("DEBIAN_FRONTEND=noninteractive apt-get remove -y nodejs npm || true"));
                cmds.Add(WrapUnix("DEBIAN_FRONTEND=noninteractive apt-get autoremove -y || true"));

                // Ensure curl/ca-certificates/gnupg exist for NodeSource
                cmds.Add(WrapUnix("DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl gnupg || apt install -y ca-certificates curl gnupg"));

                // NodeSource setup for Node 20.x (LTS)
                cmds.Add(WrapUnix("curl -fsSL https://deb.nodesource.com/setup_20.x | bash -"));

                // Install nodejs (includes npm)
                cmds.Add(WrapUnix("DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs || apt install -y nodejs"));

                // Repair if needed
                cmds.Add(WrapUnix("DEBIAN_FRONTEND=noninteractive apt-get -f install -y || true"));
                return cmds;
            }

            if (IsBinaryOnPath("dnf"))
            {
                // Try to enable Node 20 module (Fedora/RHEL derivatives). If module isn't available, fallback to standard install.
                cmds.Add(WrapUnix("dnf -y install nodejs npm || true"));
                cmds.Add(WrapUnix("dnf -y module list nodejs || true"));
                cmds.Add(WrapUnix("dnf -y module enable nodejs:20 || true"));
                cmds.Add(WrapUnix("dnf -y install nodejs npm"));
                return cmds;
            }

            if (IsBinaryOnPath("pacman"))
            {
                cmds.Add(WrapUnix("pacman -Syu --noconfirm"));
                cmds.Add(WrapUnix("pacman -S --noconfirm nodejs npm"));
                return cmds;
            }

            if (IsBinaryOnPath("apk"))
            {
                cmds.Add(WrapUnix("apk add --no-cache nodejs npm"));
                return cmds;
            }

            return cmds;
        }

        // Python
        if (depName == "python")
        {
            if (PlatformContext.IsWindows)
            {
                if (IsBinaryOnPath("winget"))
                    cmds.Add(("winget", "install -e --id Python.Python.3.12 --accept-package-agreements --accept-source-agreements"));
                if (IsBinaryOnPath("choco"))
                    cmds.Add(("choco", "install python --version=3.12 -y"));
                return cmds;
            }

            if (PlatformContext.IsMac)
            {
                if (IsBinaryOnPath("brew"))
                    cmds.Add(("brew", "install python@3.12"));
                return cmds;
            }

            if (IsBinaryOnPath("apt"))
            {
                cmds.Add(WrapUnix("apt-get update -y || apt update"));
                cmds.Add(WrapUnix("DEBIAN_FRONTEND=noninteractive apt-get install -y python3 python3-pip || apt install -y python3 python3-pip"));
                return cmds;
            }

            if (IsBinaryOnPath("dnf"))
            {
                cmds.Add(WrapUnix("dnf -y install python3 python3-pip"));
                return cmds;
            }

            if (IsBinaryOnPath("pacman"))
            {
                cmds.Add(WrapUnix("pacman -Syu --noconfirm"));
                cmds.Add(WrapUnix("pacman -S --noconfirm python python-pip"));
                return cmds;
            }

            if (IsBinaryOnPath("apk"))
            {
                cmds.Add(WrapUnix("apk add --no-cache python3 py3-pip"));
                return cmds;
            }

            return cmds;
        }

        return cmds;
    }

    private static (string cmd, string args) WrapUnix(string command)
    {
        if (IsBinaryOnPath("sudo"))
            return ("sudo", $"sh -c \"{command}\"");
        return ("sh", $"-c \"{command}\"");
    }

    private static (bool found, bool onPath, string? fullPath) FindWithAliases(
        string depName,
        Func<string, (bool found, bool onPath, string? fullPath)> findBinary)
    {
        var r = findBinary(depName);
        if (r.found) return r;

        if (!PlatformContext.IsWindows)
        {
            if (depName == "python")
            {
                var r2 = findBinary("python3");
                if (r2.found) return r2;
            }

            if (depName == "node")
            {
                var r2 = findBinary("nodejs");
                if (r2.found) return r2;
            }
        }

        return r;
    }

    private static bool IsBinaryOnPath(string name)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var exts = PlatformContext.IsWindows
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        return pathDirs.Any(dir =>
            exts.Any(ext => File.Exists(Path.Combine(dir, name + ext))));
    }

    private static void RefreshProcessPath()
    {
        if (PlatformContext.IsWindows)
        {
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var processPath = Environment.GetEnvironmentVariable("PATH") ?? "";

            var processDirs = new HashSet<string>(
                processPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var newDirs = new List<string>();

            foreach (var dir in userPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (processDirs.Add(dir)) newDirs.Add(dir);

            foreach (var dir in machinePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (processDirs.Add(dir)) newDirs.Add(dir);

            if (newDirs.Count > 0)
            {
                var merged = processPath + Path.PathSeparator + string.Join(Path.PathSeparator, newDirs);
                Environment.SetEnvironmentVariable("PATH", merged);
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var commonDirs = new[]
            {
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, ".cargo", "bin"),
                "/usr/local/bin",
                "/usr/bin",
                "/bin"
            };

            foreach (var dir in commonDirs)
                if (Directory.Exists(dir)) AddDirToProcessPath(dir);
        }
    }

    private static void AddDirToProcessPath(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        var cur = Environment.GetEnvironmentVariable("PATH") ?? "";
        var parts = cur.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var comp = PlatformContext.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parts.Any(p => string.Equals(p, dir, comp))) return;

        var next = string.Join(Path.PathSeparator, new[] { dir }.Concat(parts));
        Environment.SetEnvironmentVariable("PATH", next);
    }

    private static bool TryPersistToUserPath(string dir, bool verbose)
    {
        try
        {
            var cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            if (!PathListContains(cur, dir))
            {
                var next = AppendToPathList(cur, dir);
                Environment.SetEnvironmentVariable("PATH", next, EnvironmentVariableTarget.User);
                SendSettingChange();
            }

            AddDirToProcessPath(dir);
            return true;
        }
        catch (Exception ex)
        {
            if (verbose) MuxConsole.WriteWarning($"Failed to persist User PATH: {ex.Message}");
            return false;
        }
    }

    private static bool TryPersistToSystemPathWindows(string dir, bool verbose)
    {
        try
        {
            var cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            if (!PathListContains(cur, dir))
            {
                var next = AppendToPathList(cur, dir);
                Environment.SetEnvironmentVariable("PATH", next, EnvironmentVariableTarget.Machine);
                SendSettingChange();
            }

            AddDirToProcessPath(dir);
            return true;
        }
        catch (Exception ex)
        {
            if (verbose) MuxConsole.WriteWarning($"Failed to persist System PATH (need admin?): {ex.Message}");
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    private static void SendSettingChange()
    {
        if (!PlatformContext.IsWindows) return;

        var HWND_BROADCAST = (IntPtr)0xffff;
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero,
            "Environment", SMTO_ABORTIFHUNG, 5000, out _);
    }

    private static bool TryPersistUnixSymlink(string foundPath, bool verbose)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localBin = Path.Combine(home, ".local", "bin");
            Directory.CreateDirectory(localBin);

            var binaryName = Path.GetFileName(foundPath);
            var linkPath = Path.Combine(localBin, binaryName);

            if (Path.Exists(linkPath))
            {
                if (verbose) MuxConsole.WriteMuted($"'{linkPath}' already exists, skipping symlink.");
                AddDirToProcessPath(localBin);
                return true;
            }

            File.CreateSymbolicLink(linkPath, foundPath);
            MuxConsole.WriteSuccess($"Symlinked '{binaryName}' -> {linkPath}");

            AddDirToProcessPath(localBin);

            if (!IsOnDefaultUnixPath(localBin))
            {
                MuxConsole.WriteWarning("~/.local/bin may not be on your default PATH.");
                MuxConsole.WriteMuted($"  export PATH=\"{localBin}:$PATH\"");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (verbose) MuxConsole.WriteWarning($"Symlink failed: {ex.Message}");
            PrintUnixManualInstructions(foundPath);
            return false;
        }
    }

    private static bool IsOnDefaultUnixPath(string dir)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return pathDirs.Any(p => string.Equals(p, dir, StringComparison.Ordinal));
    }

    private static void PrintUnixManualInstructions(string foundPath)
    {
        var dir = Path.GetDirectoryName(foundPath) ?? "";
        MuxConsole.WriteWarning($"To make '{Path.GetFileName(foundPath)}' available permanently, add to your shell profile:");
        MuxConsole.WriteMuted($"  export PATH=\"{dir}:$PATH\"");
    }

    private static bool PathListContains(string pathList, string dir)
        => pathList.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));

    private static string AppendToPathList(string cur, string dir)
    {
        if (string.IsNullOrWhiteSpace(cur)) return dir;
        return cur.EndsWith(Path.PathSeparator) ? cur + dir : cur + Path.PathSeparator + dir;
    }
}