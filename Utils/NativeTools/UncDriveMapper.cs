using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// Windows-only helper that makes a UNC share (e.g. <c>\\banknas\Public\Jb\...</c>) bind-mountable by
/// Docker Desktop. Docker for Windows cannot bind a raw UNC path to <c>-v</c>; it needs a drive-letter
/// path. This maps the share's ROOT to a free temporary drive letter via <c>net use</c>, rewrites a UNC
/// host path to its <c>&lt;Z&gt;:\subpath</c> equivalent, and tears the mapping down on dispose. On
/// non-Windows (or for non-UNC paths) every method is a pass-through no-op, so the cross-platform path
/// is unaffected.
///
/// One mapper instance is owned per <see cref="OciSandbox"/>; it caches one drive letter per distinct
/// share root so several allowed-paths on the same server reuse a single mapping, and releases them all
/// on <see cref="Dispose"/>.
/// </summary>
internal sealed class UncDriveMapper : IDisposable
{
    private readonly object _gate = new();
    // share-root (\\server\share, lowercased) -> assigned drive (e.g. "Z:")
    private readonly Dictionary<string, string> _mapped = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    internal static bool IsUnc(string p) => !string.IsNullOrEmpty(p) && (p.StartsWith(@"\\") || p.StartsWith("//"));

    /// <summary>
    /// Return a docker-mountable HOST path for <paramref name="hostPath"/>. For a Windows UNC path this
    /// maps the share to a free drive letter (once per share root) and returns the drive-letter form.
    /// For anything else it returns the path unchanged. Throws <see cref="SandboxException"/> if a UNC
    /// path cannot be mapped (no free letter / net use failed) so the caller can skip or surface it.
    /// </summary>
    public string ToMountable(string hostPath)
    {
        if (!IsWindows || !IsUnc(hostPath)) return hostPath;

        string norm = hostPath.Replace('/', '\\');
        var (shareRoot, sub) = SplitUnc(norm);
        if (shareRoot is null)
            throw new SandboxException($"could not parse UNC path '{hostPath}'.");

        lock (_gate)
        {
            if (_disposed) return hostPath;
            if (!_mapped.TryGetValue(shareRoot, out var drive))
            {
                drive = FreeDriveLetter()
                    ?? throw new SandboxException("no free drive letter available to map the NAS share for Docker.");
                var (ok, _, err) = RunNet($"use {drive} \"{shareRoot}\"");
                if (!ok)
                    throw new SandboxException($"failed to map '{shareRoot}' to {drive} for Docker: {err.Trim()}");
                _mapped[shareRoot] = drive;
            }
            // drive is like "Z:"; sub is "\Public\Jb\..." (already leading-backslash-trimmed below)
            return string.IsNullOrEmpty(sub) ? drive + "\\" : drive + "\\" + sub;
        }
    }

    /// <summary>Release every drive mapping this mapper created. Safe to call repeatedly.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!IsWindows) return;
            foreach (var drive in _mapped.Values)
                RunNet($"use {drive} /delete /y"); // best-effort
            _mapped.Clear();
        }
    }

    // ---- helpers ----

    // \\server\share\a\b -> ("\\server\share", "a\b")
    internal static (string? root, string sub) SplitUnc(string unc)
    {
        string body = unc.TrimStart('\\');           // server\share\a\b
        var parts = body.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (null, "");
        string root = @"\\" + parts[0] + @"\" + parts[1];
        string sub = parts.Length > 2 ? string.Join('\\', parts.Skip(2)) : "";
        return (root, sub);
    }

    // Pick a free drive letter from Z..H (high letters first to avoid common A-G assignments).
    private static string? FreeDriveLetter()
    {
        var used = new HashSet<char>(
            DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
        for (char c = 'Z'; c >= 'H'; c--)
            if (!used.Contains(c)) return c + ":";
        return null;
    }

    private static (bool ok, string outp, string err) RunNet(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "net", Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "", "could not start net.exe");
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } return (false, o, "net use timed out"); }
            return (p.ExitCode == 0, o, e);
        }
        catch (Exception ex) { return (false, "", ex.Message); }
    }
}
