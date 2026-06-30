using System.Runtime.InteropServices;

namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// Cross-platform security gate shared by the native Filesystem + shell/REPL tools. Because Mux now
/// owns these tools in-process (instead of shelling out to @modelcontextprotocol/server-filesystem
/// and a shared REPL MCP), it can enforce real constraints the MCP layer could not: path scoping
/// against <see cref="FilesystemConfig.AllowedPaths"/>, a sensitive-directory blocklist, and an
/// "elevate to the user" confirmation that hard-blocks an operation at the process level on deny.
///
/// Elevation routes through the SAME interactive confirm path as ask_user. In a NON-interactive
/// context (stdio/NDJSON, ACP, a captured sub-agent run, or the daemon) there is no human to ask,
/// so elevation AUTO-DENIES with a clear message rather than hanging or silently allowing.
/// </summary>
internal static class NativeToolSecurity
{
    private static StringComparison PathCmp =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// True only when there is a live human on the other end of the console who can answer an
    /// elevation prompt. False under stdio/serve (NDJSON), ACP, a captured sub-agent lane, or the
    /// daemon - all of which must AUTO-DENY rather than block on input that will never come.
    /// </summary>
    public static bool IsInteractive =>
        !MuxConsole.StdioMode
        && !MuxConsole.AcpActive
        && !MuxConsole.InSubAgentCapture
        && App.DaemonRunner is null;

    /// <summary>
    /// Canonicalize a user-supplied path to a comparable absolute form: full path + resolved
    /// symlink real-target (walking up to the nearest existing ancestor for not-yet-created
    /// paths so a write target cannot smuggle a symlink escape through a missing leaf).
    /// </summary>
    public static string Canonicalize(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return path; }

        // Resolve the deepest existing ancestor's real (symlink-followed) path, then re-attach the
        // remaining not-yet-existing segments. Defends against symlink-escape on both files+dirs.
        try
        {
            string probe = full;
            var tail = new Stack<string>();
            while (!string.IsNullOrEmpty(probe) && !File.Exists(probe) && !Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || parent == probe) break;
                tail.Push(Path.GetFileName(probe));
                probe = parent;
            }
            if (File.Exists(probe) || Directory.Exists(probe))
            {
                var info = Directory.Exists(probe)
                    ? (FileSystemInfo)new DirectoryInfo(probe)
                    : new FileInfo(probe);
                var real = info.ResolveLinkTarget(returnFinalTarget: true);
                string baseReal = real?.FullName ?? probe;
                full = tail.Count == 0 ? baseReal : Path.Combine(new[] { baseReal }.Concat(tail).ToArray());
                full = Path.GetFullPath(full);
            }
        }
        catch { /* best-effort; fall back to the lexical full path */ }

        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>True when <paramref name="path"/> resolves to a location at or under any allowed root.</summary>
    public static bool IsUnderAllowed(string path, IReadOnlyList<string> allowedRoots)
    {
        if (allowedRoots is null || allowedRoots.Count == 0) return false;
        string c = Canonicalize(path);
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string r = Canonicalize(root);
            if (c.Equals(r, PathCmp)) return true;
            if (c.StartsWith(r + Path.DirectorySeparatorChar, PathCmp)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the path falls inside a cross-platform system / sensitive directory that even
    /// "lax" mode must refuse (OS internals + credential stores). AllowedPaths always wins over
    /// this list (checked by the caller), so an explicitly-allowed subtree is never blocked here.
    /// </summary>
    public static bool IsSensitive(string path)
    {
        string c = Canonicalize(path);
        foreach (var bad in SensitiveRoots())
        {
            string b = Canonicalize(bad);
            if (string.IsNullOrEmpty(b)) continue;
            if (c.Equals(b, PathCmp)) return true;
            if (c.StartsWith(b + Path.DirectorySeparatorChar, PathCmp)) return true;
        }
        return false;
    }

    private static IEnumerable<string> SensitiveRoots()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(win)) yield return win;
            if (!string.IsNullOrEmpty(pf)) yield return pf;
            if (!string.IsNullOrEmpty(pf86)) yield return pf86;
            if (!string.IsNullOrEmpty(pd)) yield return pd;
            if (!string.IsNullOrEmpty(user))
            {
                yield return Path.Combine(user, ".ssh");
                yield return Path.Combine(user, ".aws");
                yield return Path.Combine(user, ".gnupg");
                yield return Path.Combine(user, "AppData", "Roaming", "Microsoft", "Crypto");
            }
        }
        else
        {
            foreach (var r in new[] { "/etc", "/sys", "/proc", "/boot", "/dev", "/root",
                                      "/bin", "/sbin", "/usr/bin", "/usr/sbin",
                                      "/System", "/Library/Keychains", "/private/etc" })
                yield return r;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".ssh");
                yield return Path.Combine(home, ".aws");
                yield return Path.Combine(home, ".gnupg");
                yield return Path.Combine(home, ".config", "gcloud");
                yield return Path.Combine(home, "Library", "Keychains");
            }
        }
    }

    /// <summary>
    /// Ask the user to approve an operation. Returns true on confirm. In a non-interactive context
    /// returns false WITHOUT prompting (auto-deny). Callers turn a false into a hard process-level
    /// block (the write never touches disk / the process never spawns).
    /// </summary>
    public static bool Elevate(string operation)
    {
        if (!IsInteractive) return false;
        try { return MuxConsole.Confirm($"[security] Allow: {operation}?", defaultValue: false); }
        catch { return false; }
    }

    /// <summary>The auto-deny message surfaced to the model when elevation is refused/unavailable.</summary>
    public static string DenyMessage(string operation) =>
        IsInteractive
            ? $"[BLOCKED] User declined: {operation}"
            : $"[BLOCKED] {operation} requires user confirmation, but no interactive user is available " +
              "(running headless/sub-agent/daemon). Operation refused by security policy.";
}
