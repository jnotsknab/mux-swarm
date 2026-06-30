using System.Diagnostics;
using System.Text;
using MuxSwarm.Utils.NativeTools;

namespace MuxSwarm.Utils;

/// <summary>
/// Bounded, synchronous one-shot shell capture for interactive session commands (/diff, !cmd).
/// Runs a single command on the host via the platform shell with redirected stdio, a hard timeout,
/// and an output cap. When a WRAPPER/custom execution sandbox is active it wraps the command the
/// same way background shell jobs do; OCI/host run on the host working tree (which is the point for
/// /diff - it inspects the user's real repo). Output uses the --- STDOUT --- / --- STDERR --- framing.
/// </summary>
public static class ShellCapture
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut)
    {
        public string Combined
        {
            get
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(Stdout)) sb.Append(Stdout);
                if (!string.IsNullOrEmpty(Stderr))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("--- STDERR ---\n").Append(Stderr);
                }
                if (TimedOut)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("[timed out]");
                }
                return sb.ToString().TrimEnd();
            }
        }
    }

    /// <summary>
    /// Run <paramref name="command"/> in <paramref name="workDir"/> (defaults to the workspace root),
    /// bounded by <paramref name="timeoutSeconds"/> and <paramref name="maxOutputChars"/>.
    /// </summary>
    public static async Task<Result> RunAsync(
        string command,
        string? workDir = null,
        int timeoutSeconds = 30,
        int maxOutputChars = 60_000,
        CancellationToken ct = default)
    {
        string dir = string.IsNullOrWhiteSpace(workDir) ? PlatformContext.WorkspaceRoot : workDir!;

        string file, args;
        var spec = SandboxRuntime.Active;
        if (spec is not null && (spec.Kind == SandboxKind.Wrapper || spec.Kind == SandboxKind.Custom))
        {
            (file, args) = SandboxBackend.WrapShellCommand(spec, command, dir);
        }
        else if (OperatingSystem.IsWindows())
        {
            file = "cmd.exe";
            args = "/c " + command;
        }
        else
        {
            file = "/bin/sh";
            args = "-c " + Quote(command);
        }

        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return new Result(-1, string.Empty, ex.Message, false); }
        if (proc is null) return new Result(-1, string.Empty, "failed to start process", false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }

        string stdout = string.Empty, stderr = string.Empty;
        try { stdout = await outTask; } catch { /* ignore */ }
        try { stderr = await errTask; } catch { /* ignore */ }

        stdout = Cap(stdout, maxOutputChars);
        stderr = Cap(stderr, maxOutputChars);

        int code = timedOut ? -1 : SafeExitCode(proc);
        return new Result(code, stdout, stderr, timedOut);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private static string Cap(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
        return s[..max] + "\n... [output truncated]";
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
