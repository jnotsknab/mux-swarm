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

    // ---- progress-wait state (additive; drives the event-driven wait_seconds/cursor mode surfaced
    // via the session's WaitProgressAsync). All mutated under _lock alongside _out/_err/_status. ----
    private long _version;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private DateTime _lastOutputUtc = DateTime.UtcNow;
    private bool _stdoutEof;
    private bool _stderrEof;
    private bool _processExited;
    // Last stdout/stderr cursor this job already DELIVERED via a progress-wait call. A wait call
    // passing the -1 sentinel (agent supplied nothing) auto-resumes from here, so repeated bare
    // wait_job_progress(job_id) calls yield clean non-overlapping deltas with zero cursor threading.
    private int _lastDeliveredOut;
    private int _lastDeliveredErr;
    // Rotating completion source: a waiter captures .Task UNDER _lock (before any await); any writer
    // swaps + completes it UNDER _lock. Captured-before-await + swap-under-lock = lost-wakeup safe, and
    // it broadcasts to ALL waiters (unlike SemaphoreSlim, which wakes only one). Never await under _lock.
    private TaskCompletionSource<bool> _changeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Status => _status;

    public ShellJob(string id, string command) { _id = id; _command = command; }

    public void Start(string workDir, string? venvDir = null, SandboxSpec? spec = null, OciSandbox? oci = null)
    {
        string file;
        string args;
        if (spec is null)
        {
            // Host execution (current behavior).
            (file, args) = SplitShell(_command);
        }
        else if (spec.Kind == SandboxKind.Oci && oci is not null)
        {
            // Run the command inside the per-session container via `<binary> exec`.
            (file, args) = oci.ExecShell(_command);
        }
        else
        {
            // Wrapper (bwrap/firejail/sandbox-exec) or custom backend: re-wrap each command.
            (file, args) = SandboxBackend.WrapShellCommand(spec, _command, workDir);
        }
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

        Pump(_proc.StandardOutput, _out, $"ShellOut:{_id}", isStdout: true);
        Pump(_proc.StandardError, _err, $"ShellErr:{_id}", isStdout: false);

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
                    _processExited = true;
                    Signal_NoLock(output: false);
                }
            }
            catch { lock (_lock) { _status = "failed"; _processExited = true; Signal_NoLock(output: false); } }
        }) { IsBackground = true, Name = $"ShellWait:{_id}" }.Start();
    }

    private void Pump(StreamReader reader, StringBuilder sink, string name, bool isStdout)
    {
        new Thread(() =>
        {
            try
            {
                char[] buf = new char[4096];
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    lock (_lock) { sink.Append(buf, 0, n); Signal_NoLock(output: true); }
            }
            catch { /* stream closed */ }
            finally
            {
                // EOF: this pump has drained. OutputDrained becomes true only once BOTH pumps reach here
                // AND the process exited - lets a caller collect final tail output after a terminal status.
                lock (_lock) { if (isStdout) _stdoutEof = true; else _stderrEof = true; Signal_NoLock(output: false); }
            }
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
        // Failed before/without a live process: nothing will ever drain, so mark EOF/exited so
        // OutputDrained reports true and a waiter never blocks expecting tail output.
        lock (_lock) { _status = "failed"; _err.Append(reason).Append('\n'); _processExited = true; _stdoutEof = true; _stderrEof = true; Signal_NoLock(output: true); }
    }

    public void Kill()
    {
        try { _proc?.Kill(entireProcessTree: true); } catch { /* ignore */ }
    }

    /// <summary>Monotonic change counter (output append OR status transition). Read under _lock via the accessor.</summary>
    public long Version { get { lock (_lock) return _version; } }

    /// <summary>Bump the version and wake all current waiters. Caller MUST hold _lock. `output` marks a real
    /// stdout/stderr append (refreshes the idle timer); status/EOF bumps pass false so idle keeps counting.</summary>
    private void Signal_NoLock(bool output)
    {
        _version++;
        if (output) _lastOutputUtc = DateTime.UtcNow;
        var prev = _changeTcs;
        _changeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        prev.TrySetResult(true);
    }

    /// <summary>Immutable progress snapshot handed to the session layer for rendering (built under _lock,
    /// rendered outside it).</summary>
    internal readonly record struct ShellProgress(
        string Status, int? ExitCode, long Version,
        string StdoutDelta, string StderrDelta,
        int NextStdoutCursor, int NextStderrCursor,
        int TotalStdout, int TotalStderr,
        bool Truncated, int Dropped,
        int ElapsedSeconds, int IdleSeconds,
        bool ProcessExited, bool OutputDrained);

    /// <summary>
    /// Block until this job changes (new output, status transition, or terminal), the wait budget elapses,
    /// or the turn is cancelled, then return a delta-only snapshot from the caller's cursors. Rule: return
    /// immediately if the caller is behind on either stream (cursor &lt; total) or the job is terminal; else
    /// wait for the next version bump. Cancellation ends only the WAIT - the process is untouched.
    /// </summary>
    public async Task<ShellProgress> WaitProgressAsync(
        int stdoutCursor, int stderrCursor, int maxChars, int waitSeconds, CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                // Sentinel -1 = auto-resume from the last cursor this job delivered (agent passed
                // nothing). A non-negative value is honored verbatim so an explicit re-read still works.
                int so = stdoutCursor < 0 ? _lastDeliveredOut : stdoutCursor;
                int se = stderrCursor < 0 ? _lastDeliveredErr : stderrCursor;
                bool terminal = _status is "completed" or "failed";
                bool behind = so < _out.Length || se < _err.Length;
                if (terminal || behind)
                    return SnapshotAndRemember_NoLock(so, se, maxChars);
                changed = _changeTcs.Task; // capture UNDER lock before awaiting (no lost wakeup)
            }
            var timeout = Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
            var done = await Task.WhenAny(changed, timeout).ConfigureAwait(false);
            if (done == timeout)
            {
                ct.ThrowIfCancellationRequested(); // cancellation ends the wait, not the process
                lock (_lock)
                {
                    int so = stdoutCursor < 0 ? _lastDeliveredOut : stdoutCursor;
                    int se = stderrCursor < 0 ? _lastDeliveredErr : stderrCursor;
                    return SnapshotAndRemember_NoLock(so, se, maxChars); // Changed=false path
                }
            }
            // woke on a bump: re-check under lock (may be a status-only bump with no new bytes)
        }
    }

    /// <summary>Build a snapshot from the resolved cursors, then remember the NEW next-cursors so a
    /// subsequent sentinel(-1) call auto-continues from here. Caller holds _lock.</summary>
    private ShellProgress SnapshotAndRemember_NoLock(int stdoutCursor, int stderrCursor, int maxChars)
    {
        var snap = Snapshot_NoLock(stdoutCursor, stderrCursor, maxChars);
        _lastDeliveredOut = snap.NextStdoutCursor;
        _lastDeliveredErr = snap.NextStderrCursor;
        return snap;
    }

    private ShellProgress Snapshot_NoLock(int stdoutCursor, int stderrCursor, int maxChars)
    {
        int totalOut = _out.Length, totalErr = _err.Length;
        int so = Math.Clamp(stdoutCursor, 0, totalOut);
        int se = Math.Clamp(stderrCursor, 0, totalErr);
        string outDelta = so < totalOut ? _out.ToString(so, totalOut - so) : "";
        string errDelta = se < totalErr ? _err.ToString(se, totalErr - se) : "";

        // Tail-first cap across the COMBINED delta: trim stdout head first, then stderr head, keeping the
        // freshest bytes. Dropped = chars omitted; next cursors advance only by chars actually emitted.
        bool truncated = false; int dropped = 0;
        int combined = outDelta.Length + errDelta.Length;
        if (combined > maxChars)
        {
            truncated = true;
            int over = combined - maxChars;
            int cutOut = Math.Min(over, outDelta.Length);
            outDelta = outDelta.Substring(cutOut);
            dropped += cutOut; over -= cutOut;
            if (over > 0) { int cutErr = Math.Min(over, errDelta.Length); errDelta = errDelta.Substring(cutErr); dropped += cutErr; }
        }
        // Next cursor = END of the stream (caller has now consumed through totalOut). Any head chars
        // dropped by tail-first truncation are gone and reported via Dropped, never re-served. The
        // emitted slice STARTS at (nextOut - delta.Length), which the renderer shows as the "from" value.
        int nextOut = totalOut;
        int nextErr = totalErr;

        bool outputDrained = _processExited && _stdoutEof && _stderrEof;
        int elapsed = (int)Math.Max(0, (DateTime.UtcNow - _startedUtc).TotalSeconds);
        int idle = (int)Math.Max(0, (DateTime.UtcNow - _lastOutputUtc).TotalSeconds);
        return new ShellProgress(
            _status, _exitCode, _version, outDelta, errDelta, nextOut, nextErr,
            totalOut, totalErr, truncated, dropped, elapsed, idle,
            _processExited, outputDrained);
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
