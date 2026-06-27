using System.Diagnostics;
using System.Text;

namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// A single background shell command: a native <see cref="Process"/> with redirected stdio, pumped
/// on DEDICATED reader threads (never the thread pool - under sub-agent fan-out a pooled pump would
/// starve and freeze output, the same starvation class as the ACP/Esc fixes). Output is exposed in
/// the Claude-Code-friendly --- STDOUT --- / --- STDERR --- framing for clean diffs/log surfacing.
/// </summary>
internal sealed class ShellJob
{
    private readonly string _id;
    private readonly string _command;
    private readonly object _lock = new();
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _err = new();
    private Process? _proc;
    private volatile string _status = "starting"; // starting|running|completed|failed
    private int? _exitCode;

    public string Status => _status;

    public ShellJob(string id, string command) { _id = id; _command = command; }

    public void Start(string workDir, string? venvDir = null)
    {
        var (file, args) = SplitShell(_command);
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Run shell jobs INSIDE the session venv so a bare `python`/`pip` resolves to the same
        // interpreter the REPL worker uses and install_package_async targets. Mirrors venv
        // activation: prepend the venv bin dir to PATH, set VIRTUAL_ENV, clear PYTHONHOME. Without
        // this, cmd.exe/sh inherit the parent PATH and `python` hits the system interpreter, so
        // tool-installed packages are invisible to shell jobs (the inconsistent-venv bug).
        if (venvDir is not null)
        {
            string bin = OperatingSystem.IsWindows()
                ? Path.Combine(venvDir, "Scripts")
                : Path.Combine(venvDir, "bin");
            string existingPath = psi.Environment.TryGetValue("PATH", out var pv) && pv is not null
                ? pv
                : (Environment.GetEnvironmentVariable("PATH") ?? "");
            psi.Environment["PATH"] = bin + Path.PathSeparator + existingPath;
            psi.Environment["VIRTUAL_ENV"] = venvDir;
            psi.Environment.Remove("PYTHONHOME");
        }
        _proc = Process.Start(psi);
        if (_proc is null) { MarkFailed("failed to start process"); return; }
        _status = "running";

        Pump(_proc.StandardOutput, _out, $"ShellOut:{_id}");
        Pump(_proc.StandardError, _err, $"ShellErr:{_id}");

        var proc = _proc;
        new Thread(() =>
        {
            try
            {
                proc.WaitForExit();
                lock (_lock)
                {
                    _exitCode = proc.ExitCode;
                    _status = proc.ExitCode == 0 ? "completed" : "failed";
                }
            }
            catch { lock (_lock) _status = "failed"; }
        }) { IsBackground = true, Name = $"ShellWait:{_id}" }.Start();
    }

    private void Pump(StreamReader reader, StringBuilder sink, string name)
    {
        new Thread(() =>
        {
            try
            {
                char[] buf = new char[4096];
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    lock (_lock) sink.Append(buf, 0, n);
            }
            catch { /* stream closed */ }
        }) { IsBackground = true, Name = name }.Start();
    }

    public string SendInput(string text)
    {
        var p = _proc;
        if (p is null || p.HasExited) return $"Job {_id} is not running (status: {_status}).";
        try { p.StandardInput.Write(text); p.StandardInput.Flush(); return $"Input sent to job {_id}."; }
        catch (Exception ex) { return $"Failed to send input to job {_id}: {ex.Message}"; }
    }

    public string Render()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.Append("Job ID: ").Append(_id).Append('\n');
            sb.Append("Status: ").Append(_status);
            if (_exitCode is { } ec) sb.Append(" (exit ").Append(ec).Append(')');
            sb.Append('\n');
            sb.Append("Command: ").Append(_command);
            if (_out.Length > 0) sb.Append("\n\n--- STDOUT ---\n").Append(_out);
            if (_err.Length > 0) sb.Append("\n\n--- STDERR ---\n").Append(_err);
            return sb.ToString().TrimEnd();
        }
    }

    public void MarkFailed(string reason)
    {
        lock (_lock) { _status = "failed"; _err.Append(reason).Append('\n'); }
    }

    public void Kill()
    {
        try { _proc?.Kill(entireProcessTree: true); } catch { /* ignore */ }
    }

    /// <summary>Pick the platform shell to run a free-form command string.</summary>
    private static (string File, string Args) SplitShell(string command)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", "/c " + command);
        return ("/bin/sh", "-c " + Quote(command));
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
