using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MuxSwarm.Utils;

namespace MuxSwarm.Setup;

/// <summary>
/// Handles locating binaries on PATH, in common install directories, and via bounded deep scans.
/// Extracted from Setup to keep binary-resolution logic self-contained.
/// </summary>
public static class BinaryResolver
{
    public static bool IsBinaryAvailable(string binary)
    {
        return TryFindBinaryPath(binary, out _);
    }

    public static (bool found, bool onPath, string? fullPath) FindBinary(string name)
    {
        bool onPath = TryFindBinaryOnPath(name, out var pathOnPath);

        if (onPath)
            return (true, true, pathOnPath);

        if (TryFindBinaryPath(name, out var deepPath))
            return (true, false, deepPath);

        return (false, false, null);
    }

    private static bool TryFindBinaryOnPath(string binary, out string? fullPath)
    {
        fullPath = null;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var exts = PlatformContext.IsWindows
            ? new[] { ".exe", ".cmd", ".bat", ".com", ".ps1", "" }
            : new[] { "" };

        foreach (var dir in pathDirs)
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, binary + ext);
                if (File.Exists(candidate))
                {
                    fullPath = Path.GetFullPath(candidate);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the binary exists either on PATH or in common install locations.
    /// If found, fullPath will contain the resolved absolute path.
    /// </summary>
    public static bool TryFindBinaryPath(string binary, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(binary)) return false;

        //Path check
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var exts = PlatformContext.IsWindows
            ? new[] { ".exe", ".cmd", ".bat", ".com", ".ps1", "" }
            : new[] { "" };

        foreach (var dir in pathDirs)
        {
            if (TryFindInDir(dir, binary, exts, out fullPath))
                return true;
        }


        foreach (var dir in GetCommonBinaryDirs())
        {
            if (TryFindInDir(dir, binary, exts, out fullPath))
                return true;
        }

        foreach (var root in GetDeepScanRoots())
        {
            if (TryFindInTree(root, binary, exts, maxDepth: 3, maxVisitedDirs: 4000, out fullPath))
                return true;
        }

        return false;
    }

    private static bool TryFindInDir(string dir, string binary, string[] exts, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(dir)) return false;

        try
        {
            if (!Directory.Exists(dir)) return false;

            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, binary + ext);
                if (File.Exists(candidate))
                {
                    fullPath = Path.GetFullPath(candidate);
                    return true;
                }
            }
        }
        catch
        {
            // ignore access/path errors
        }

        return false;
    }

    /// <summary>
    /// Searches for the binary under root with a max directory depth and a max visited directory cap.
    /// This avoids expensive full-disk recursion.
    /// </summary>
    private static bool TryFindInTree(
        string root,
        string binary,
        string[] exts,
        int maxDepth,
        int maxVisitedDirs,
        out string? fullPath)
    {
        fullPath = null;

        if (string.IsNullOrWhiteSpace(root)) return false;

        try
        {
            if (!Directory.Exists(root)) return false;

            var visited = 0;
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                if (visited++ > maxVisitedDirs) return false;

                if (TryFindInDir(dir, binary, exts, out fullPath))
                    return true;

                if (depth >= maxDepth) continue;

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(dir);
                }
                catch
                {
                    continue;
                }

                foreach (var child in children)
                    stack.Push((child, depth + 1));
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static IEnumerable<string> GetCommonBinaryDirs()
    {
        var dirs = new List<string>();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (PlatformContext.IsWindows)
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            if (!string.IsNullOrEmpty(home))
            {
                dirs.Add(Path.Combine(home, ".local", "bin"));
                dirs.Add(Path.Combine(home, "scoop", "shims"));
            }

            if (!string.IsNullOrEmpty(localApp))
            {
                dirs.Add(Path.Combine(localApp, "Programs"));
                dirs.Add(Path.Combine(localApp, "Microsoft", "WinGet", "Packages"));
                dirs.Add(Path.Combine(localApp, "uv"));
            }

            if (!string.IsNullOrEmpty(programFiles))
            {
                dirs.Add(programFiles);
                dirs.Add(Path.Combine(programFiles, "uv"));
            }

            if (!string.IsNullOrEmpty(programFilesX86))
            {
                dirs.Add(programFilesX86);
                dirs.Add(Path.Combine(programFilesX86, "uv"));
            }

            dirs.Add(@"C:\ProgramData\chocolatey\bin");
        }
        else
        {
            dirs.Add("/usr/local/bin");
            dirs.Add("/usr/bin");
            dirs.Add("/bin");
            dirs.Add("/opt/bin");

            if (!string.IsNullOrEmpty(home))
            {
                dirs.Add(Path.Combine(home, ".local", "bin"));
                dirs.Add(Path.Combine(home, ".cargo", "bin"));
                dirs.Add(Path.Combine(home, ".npm-global", "bin"));
            }

            if (PlatformContext.IsMac)
            {
                dirs.Add("/opt/homebrew/bin");
                dirs.Add("/usr/local/homebrew/bin");
            }

            dirs.Add("/home/linuxbrew/.linuxbrew/bin");
        }

        return dirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d =>
            {
                try { return Path.GetFullPath(d); } catch { return d; }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetDeepScanRoots()
    {
        var roots = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (PlatformContext.IsWindows)
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            if (!string.IsNullOrEmpty(localApp)) roots.Add(Path.Combine(localApp, "Programs"));
            if (!string.IsNullOrEmpty(programFiles)) roots.Add(programFiles);
            if (!string.IsNullOrEmpty(programFilesX86)) roots.Add(programFilesX86);
            if (!string.IsNullOrEmpty(home)) roots.Add(Path.Combine(home, "scoop"));
        }
        else
        {
            if (!string.IsNullOrEmpty(home)) roots.Add(Path.Combine(home, ".local"));
            if (PlatformContext.IsMac) roots.Add("/opt/homebrew");
            roots.Add("/usr/local");
            roots.Add("/opt");
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
