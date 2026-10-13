using System.Diagnostics;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Opening extensions for the <c>/extensions</c> panel: <see cref="Open"/> opens a manifest or
/// folder in the user's editor or the OS default app.
/// </summary>
internal static class ExtensionScaffold
{
    /// <summary>
    /// Open <paramref name="path"/> for the user. With <paramref name="edit"/> and <c>$VISUAL</c> /
    /// <c>$EDITOR</c> set, runs that editor in this console and waits (returns true = edited);
    /// otherwise hands the path to the OS default app without waiting (returns false). Throws when
    /// nothing can open it.
    /// </summary>
    internal static bool Open(string path, bool edit)
    {
        var editor = edit ? Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR") : null;
        if (!string.IsNullOrWhiteSpace(editor))
        {
            // The editor string is the user's own (it may carry flags, e.g. "code -w"), so a shell runs it.
            // cmd /s /c strips the outer quotes and runs: editor "path" (Windows paths cannot contain quotes).
            var psi = PlatformContext.IsWindows
                ? new ProcessStartInfo("cmd.exe") { Arguments = $"/s /c \"{editor} \"{path}\"\"" }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", editor + " \"$1\"", "sh", path } };
            psi.UseShellExecute = false;
            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start '{editor}'.");
            p.WaitForExit();
            return true;
        }
        if (PlatformContext.IsWindows)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) when (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();   // no app registered for .json
            }
            return false;
        }
        var open = new ProcessStartInfo(PlatformContext.IsMac ? "open" : "xdg-open")
        {
            ArgumentList = { path }, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using (var p = Process.Start(open) ?? throw new InvalidOperationException("could not start the system opener."))
            p.StandardInput.Close();
        return false;
    }
}
