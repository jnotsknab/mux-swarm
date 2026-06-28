using System.Diagnostics;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// Cross-platform "open this URL in the system browser" for the OAuth login step. On Windows the URL is
/// handed to the shell (UseShellExecute) so the default browser opens it; on Linux xdg-open; on macOS
/// open. On any failure (headless / no DISPLAY / no browser) the caller is expected to show the URL for
/// manual paste - this returns false so the caller can fall back to a copy-paste prompt.
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>Try to open <paramref name="url"/> in the system browser. Returns false on failure (headless).</summary>
    public static bool TryOpen(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo { FileName = "xdg-open", Arguments = url, UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo { FileName = "open", Arguments = url, UseShellExecute = false });
            else
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
