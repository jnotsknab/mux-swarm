namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// Command-execution gate for the native shell/REPL tools, mirroring Claude Code's Bash gating.
/// Governed by <see cref="ShellConfig.SecurityMode"/> (off/prompt/allowlist); default "off" keeps
/// today's run-anything behavior. On a blocked command the caller returns the deny message and the
/// process/exec never starts. Elevation + non-interactive auto-deny reuse <see cref="NativeToolSecurity"/>.
/// </summary>
internal static class NativeShellSecurity
{
    private static ShellConfig Cfg => App.Config.Shell;
    private static string Mode => (Cfg.SecurityMode ?? "off").Trim().ToLowerInvariant();

    /// <summary>
    /// Returns null when the command may run; otherwise a deny/block message the tool returns
    /// verbatim. <paramref name="label"/> describes the action for the elevation prompt.
    /// </summary>
    public static string? Gate(string command, string label)
    {
        switch (Mode)
        {
            case "prompt":
                return NativeToolSecurity.Elevate($"{label}: {Trim(command)}")
                    ? null
                    : NativeToolSecurity.DenyMessage($"{label}: {Trim(command)}");

            case "allowlist":
                if (IsAllowlisted(command)) return null;
                return NativeToolSecurity.Elevate($"{label} (not allowlisted): {Trim(command)}")
                    ? null
                    : NativeToolSecurity.DenyMessage($"{label}: {Trim(command)}");

            default: // "off"
                return null;
        }
    }

    private static bool IsAllowlisted(string command)
    {
        var allowed = Cfg.AllowedCommands;
        if (allowed is null || allowed.Count == 0) return false;
        string first = FirstToken(command);
        foreach (var a in allowed)
            if (!string.IsNullOrWhiteSpace(a) &&
                first.Equals(a.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string FirstToken(string command)
    {
        string c = command.TrimStart();
        int i = 0;
        while (i < c.Length && !char.IsWhiteSpace(c[i])) i++;
        // strip any path so "/usr/bin/git" -> "git", "C:\\foo\\python.exe" -> "python"
        string tok = c[..i];
        tok = Path.GetFileNameWithoutExtension(tok);
        return tok;
    }

    private static string Trim(string s) => s.Length <= 120 ? s : s[..117] + "...";
}
