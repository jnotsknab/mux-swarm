using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Clipboard sink for NAV-mode yank. Two paths, used together for reliability (v0.11.0):
///   1. OSC 52 escape - the terminal copies to the user's LOCAL clipboard, which works even
///      over SSH (e.g. windows-lite). Requires the terminal to allow it; Windows Terminal,
///      iTerm2, kitty, wezterm and recent xterm do. Emitted through the same ITuiTerminal the
///      renderer writes to, so it is captured headlessly in tests.
///   2. A platform shell-out fallback (clip.exe / pbcopy / xclip|wl-copy) for terminals that
///      block OSC 52. Over SSH this targets the REMOTE clipboard, hence it is only a fallback.
/// </summary>
internal static class TuiClipboard
{
    /// <summary>Build the OSC 52 sequence that sets the clipboard to <paramref name="text"/>.
    /// Pure (no I/O) so it can be asserted in tests. Format: ESC ] 52 ; c ; &lt;base64&gt; BEL.</summary>
    public static string Osc52(string text)
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        return $"{Ansi.ESC}]52;c;{b64}{Ansi.BEL}";
    }

    /// <summary>Emit the OSC 52 copy sequence through the terminal sink.</summary>
    public static void CopyViaTerminal(ITuiTerminal term, string text)
    {
        term.Write(Osc52(text));
        term.Flush();
    }

    /// <summary>Best-effort copy to the local OS clipboard by shelling out to the platform tool.
    /// Returns true on a clean exit. Never throws.</summary>
    public static bool CopyViaShell(string text)
    {
        try
        {
            string file; string args;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { file = "clip"; args = ""; }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { file = "pbcopy"; args = ""; }
            else
            {
                // Linux: prefer wl-copy (Wayland), else xclip, else xsel. Try in order.
                foreach (var (f, a) in new[] { ("wl-copy", ""), ("xclip", "-selection clipboard"), ("xsel", "--clipboard --input") })
                {
                    if (TryPipe(f, a, text)) return true;
                }
                return false;
            }
            return TryPipe(file, args, text);
        }
        catch { return false; }
    }

    private static bool TryPipe(string file, string args, string text)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit(2000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }
}
