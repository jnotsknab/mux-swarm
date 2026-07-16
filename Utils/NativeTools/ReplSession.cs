using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// One agent session's isolated execution world: a persistent Python worker subprocess (REPL state
/// that survives across calls) plus a table of background shell jobs. Created lazily per session
/// key by <see cref="ReplShellTools"/>; disposed (worker killed, jobs reaped, venv left in place)
/// when the session scope ends. NOTHING here is shared between sessions, which is the entire point.
/// </summary>
internal sealed class ReplSession : IDisposable
{
    private readonly string _key;
    private readonly object _lock = new();
    private bool _disposed;

    // Per-session local working dir + venv (NEVER on NAS - venv binaries cannot execute from NAS).
    private readonly string _workDir;
    private string VenvDir => Path.Combine(_workDir, ".venv");
    private string VenvPython => OperatingSystem.IsWindows()
        ? Path.Combine(VenvDir, "Scripts", "python.exe")
        : Path.Combine(VenvDir, "bin", "python");

    // ---- python worker state ----
    private Process? _worker;
    private Thread? _readerThread;
    private string _workerFile = "";
    private volatile string _jobStatus = "idle"; // idle|running|completed|error|dead|waiting_input
    private string? _currentJobId;
    private string _currentCode = "";  // last code submitted; shown to the USER in the tool card (display side)
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _err = new();

    /// <summary>The full source of the most recent code submitted to this session (display side
    /// channel for the TUI card - shows the USER exactly what ran). Empty when nothing has run.</summary>
    internal string CurrentCode { get { lock (_lock) return _currentCode; } }
    private string _inputPrompt = "";
    private TaskCompletionSource<bool>? _doneTcs;
    private TaskCompletionSource<List<string>>? _varsTcs;

    // ---- python progress-wait state (additive; drives wait_python_progress). Rotating broadcast TCS
    // bumped in ReadLoop on every stream/input_request/done + on job start, all under _lock. Same
    // capture-under-lock-before-await + swap-under-lock discipline as ShellJob (lost-wakeup safe). ----
    private long _pyVersion;
    private DateTime _pyStartedUtc = DateTime.UtcNow;
    private DateTime _pyLastOutputUtc = DateTime.UtcNow;
    private TaskCompletionSource<bool> _pyChangeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Last python stdout/stderr cursor delivered via wait_python_progress; -1 sentinel auto-resumes.
    // Reset to 0 on each new job start (ExecutePythonAsync clears _out/_err).
    private int _pyLastDeliveredOut;
    private int _pyLastDeliveredErr;

    private void SignalPy_NoLock(bool output)
    {
        _pyVersion++;
        if (output) _pyLastOutputUtc = DateTime.UtcNow;
        var prev = _pyChangeTcs;
        _pyChangeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        prev.TrySetResult(true);
    }
    private bool _venvReady;

    // ---- shell jobs ----
    private readonly ConcurrentDictionary<string, ShellJob> _shellJobs = new(StringComparer.Ordinal);
    private int _shellSeq;

    // ---- sandbox (null = host execution; non-null = shell jobs + python worker run inside it) ----
    private readonly SandboxSpec? _spec;
    private readonly string? _sandboxError;
    private OciSandbox? _oci;
    private bool Sandboxed => _spec is not null;
    private bool OciSandboxed => _spec is { Kind: SandboxKind.Oci };

    public ReplSession(string key)
    {
        _key = key;
        string sub = "repl_" + Sanitize(key);
        _workDir = Path.Combine(LocalDataRoot(), "repl", sub);
        Directory.CreateDirectory(_workDir);

        // Resolve the configured sandbox backend ONCE. A SandboxException (unknown backend, missing
        // binary, bad network combo) is captured and surfaced on first tool use rather than thrown
        // from the ctor - so an invalid sandbox config fails loud at the tool, never silently to host.
        try { _spec = SandboxBackend.Resolve(App.Config.Sandbox); }
        catch (SandboxException ex) { _spec = null; _sandboxError = ex.Message; }
        if (OciSandboxed) _oci = new OciSandbox(_spec!, _workDir, _key);
    }

    /// <summary>Non-null when the configured sandbox is unusable; tools return it instead of running on host.</summary>
    private string? SandboxGuard() => _sandboxError is null ? null
        : $"[SANDBOX ERROR] {_sandboxError}\nExecution refused: fix sandbox config or set sandbox.backend host.";

    /// <summary>Local (never-NAS) data root for venvs + worker temp files.</summary>
    private static string LocalDataRoot()
    {
        string root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "Mux-Swarm");
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "primary" : sb.ToString();
    }

    // ===== Python REPL =====

    public async Task<string> ExecutePythonAsync(string code, CancellationToken ct)
    {
        if (SandboxGuard() is { } guard) return guard;
        try { await EnsureWorkerAsync(ct); }
        catch (SandboxException ex) { return $"[SANDBOX ERROR] {ex.Message}"; }
        lock (_lock)
        {
            if (_jobStatus == "running")
                return $"Error: Python worker is busy running job {_currentJobId}. Wait for it to finish, or call restart_python_worker if it is hung.";
            if (_jobStatus == "waiting_input")
                return $"Error: Python worker is waiting for input (prompt: {_inputPrompt}). Use send_python_input, or restart_python_worker to abort.";
        }

        TaskCompletionSource<bool> done;
        lock (_lock)
        {
            _currentJobId = Guid.NewGuid().ToString("N")[..12];
            _currentCode = code ?? "";
            _jobStatus = "running";
            _out.Clear();
            _err.Clear();
            _inputPrompt = "";
            _pyStartedUtc = DateTime.UtcNow;
            _pyLastOutputUtc = DateTime.UtcNow;
            _pyLastDeliveredOut = 0;
            _pyLastDeliveredErr = 0;
            SignalPy_NoLock(output: false);
            _doneTcs = done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Worker exec() runs arbitrary model code - normalize to LF so multi-line constructs tokenize
        // identically regardless of how the host delivered the string.
        WriteWorker(new Dictionary<string, object?> { ["cmd"] = "execute", ["code"] = code.Replace("\r\n", "\n").Replace("\r", "\n") });

        // Wait up to 2s for a quick finish (mirrors mcp-async-repl's UX).
        var quick = await Task.WhenAny(done.Task, Task.Delay(2000, ct));
        lock (_lock)
        {
            if (quick == done.Task)
                return RenderResult(prefix: null);
            if (_jobStatus == "waiting_input")
                return $"Job ID: {_currentJobId}\nStatus: waiting_input\nPrompt: {_inputPrompt}\n\nThe code called input(). Use send_python_input to provide the response.";
            return $"Job ID: {_currentJobId}\nStatus: running (in background)\n\nUse check_python_status to see intermediary output or check completion.";
        }
    }

    public async Task<string> SendPythonInputAsync(string text, CancellationToken ct)
    {
        TaskCompletionSource<bool>? done;
        lock (_lock)
        {
            if (_jobStatus != "waiting_input")
                return $"Error: worker is not waiting for input (status: {_jobStatus}).";
            _jobStatus = "running";
            _inputPrompt = "";
            done = _doneTcs;
        }
        WriteWorker(new Dictionary<string, object?> { ["cmd"] = "input", ["text"] = text });
        if (done is not null)
        {
            var quick = await Task.WhenAny(done.Task, Task.Delay(2000, ct));
            lock (_lock)
            {
                if (quick == done.Task) return RenderResult(prefix: "Input delivered. ");
                if (_jobStatus == "waiting_input")
                    return $"Input delivered. Code called input() again. Prompt: {_inputPrompt}\nUse send_python_input again.";
            }
        }
        return "Input delivered. Status: running (in background). Use check_python_status to poll.";
    }

    public string CheckPythonStatus()
    {
        lock (_lock)
        {
            if (_worker is null) return "Status: idle (no Python worker started yet).";
            return RenderResult(prefix: null);
        }
    }

    /// <summary>Event-driven progress wait for the persistent Python worker: blocks up to waitSeconds but
    /// returns early on new output, a state transition (including waiting_input), completion, or worker death.
    /// Emits only the delta since the caller's cursors. The legacy CheckPythonStatus full-snapshot is untouched.</summary>
    public async Task<string> WaitPythonProgressAsync(
        int waitSeconds, int stdoutCursor, int stderrCursor, int maxChars, CancellationToken ct)
    {
        lock (_lock) { if (_worker is null) return "Status: idle (no Python worker started yet)."; }
        int wait = Clamp(waitSeconds, 1, 120);
        int cap = Clamp(maxChars, 512, 60000);
        // Negative = auto-resume from the last delivered python cursor (agent supplied nothing).
        bool autoOut = stdoutCursor < 0, autoErr = stderrCursor < 0;
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                int so = autoOut ? _pyLastDeliveredOut : stdoutCursor;
                int se = autoErr ? _pyLastDeliveredErr : stderrCursor;
                bool terminal = _jobStatus is "completed" or "error" or "dead" or "waiting_input";
                bool behind = so < _out.Length || se < _err.Length;
                if (terminal || behind) return RenderPyProgressAndRemember(so, se, cap);
                changed = _pyChangeTcs.Task; // capture UNDER lock before await
            }
            var timeout = Task.Delay(TimeSpan.FromSeconds(wait), ct);
            var done = await Task.WhenAny(changed, timeout).ConfigureAwait(false);
            if (done == timeout)
            {
                ct.ThrowIfCancellationRequested();
                lock (_lock)
                {
                    int so = autoOut ? _pyLastDeliveredOut : stdoutCursor;
                    int se = autoErr ? _pyLastDeliveredErr : stderrCursor;
                    return RenderPyProgressAndRemember(so, se, cap); // Changed=false
                }
            }
            // woke on a bump: re-check under lock
        }
    }

    /// <summary>Caller holds _lock. Renders the python delta then remembers the advanced cursors so a
    /// subsequent sentinel(-1) call auto-continues. Parses the next cursors back from the rendered frame.</summary>
    private string RenderPyProgressAndRemember(int stdoutCursor, int stderrCursor, int maxChars)
    {
        // RenderPyProgress consumes each stream through its current end, so the next auto-resume
        // cursor is simply the stream length at this instant.
        _pyLastDeliveredOut = _out.Length;
        _pyLastDeliveredErr = _err.Length;
        return RenderPyProgress(stdoutCursor, stderrCursor, maxChars);
    }

    /// <summary>Caller holds _lock. Renders a delta-only python progress frame from the given cursors.</summary>
    private string RenderPyProgress(int stdoutCursor, int stderrCursor, int maxChars)
    {
        int totalOut = _out.Length, totalErr = _err.Length;
        int so = Math.Clamp(stdoutCursor, 0, totalOut);
        int se = Math.Clamp(stderrCursor, 0, totalErr);
        string outDelta = so < totalOut ? _out.ToString(so, totalOut - so) : "";
        string errDelta = se < totalErr ? _err.ToString(se, totalErr - se) : "";
        bool truncated = false; int dropped = 0;
        int combined = outDelta.Length + errDelta.Length;
        if (combined > maxChars)
        {
            truncated = true;
            int over = combined - maxChars;
            int cutOut = Math.Min(over, outDelta.Length);
            outDelta = outDelta.Substring(cutOut); dropped += cutOut; over -= cutOut;
            if (over > 0) { int cutErr = Math.Min(over, errDelta.Length); errDelta = errDelta.Substring(cutErr); dropped += cutErr; }
        }
        int nextOut = totalOut;
        int nextErr = totalErr;
        bool terminal = _jobStatus is "completed" or "error" or "dead";
        bool changed = outDelta.Length > 0 || errDelta.Length > 0 || terminal || _jobStatus == "waiting_input";
        int elapsed = (int)Math.Max(0, (DateTime.UtcNow - _pyStartedUtc).TotalSeconds);
        int idle = (int)Math.Max(0, (DateTime.UtcNow - _pyLastOutputUtc).TotalSeconds);

        var sb = new StringBuilder();
        sb.Append("Status: ").Append(_jobStatus).Append('\n');
        sb.Append("Changed: ").Append(changed ? "true" : "false").Append('\n');
        sb.Append("Elapsed: ").Append(elapsed).Append("s   Idle: ").Append(idle).Append("s\n");
        if (_jobStatus == "waiting_input") sb.Append("Prompt: ").Append(_inputPrompt).Append('\n');
        sb.Append("StdoutCursor: ").Append(nextOut - outDelta.Length).Append(" -> ").Append(nextOut).Append("   (total ").Append(totalOut).Append(")\n");
        sb.Append("StderrCursor: ").Append(nextErr - errDelta.Length).Append(" -> ").Append(nextErr).Append("   (total ").Append(totalErr).Append(")\n");
        sb.Append("Truncated: ").Append(truncated ? "true" : "false").Append("   Dropped: ").Append(dropped).Append('\n');
        sb.Append("SuggestedPollSeconds: ").Append(SuggestedPoll(_jobStatus, changed, idle));
        if (outDelta.Length > 0) sb.Append("\n\n--- STDOUT (new) ---\n").Append(outDelta);
        if (errDelta.Length > 0) sb.Append("\n\n--- STDERR (new) ---\n").Append(errDelta);
        return sb.ToString();
    }

    /// <summary>Deterministic next-poll hint (seconds). Terminal/waiting_input = 0; fresh output = 1;
    /// else scales with idle time so a quiet long job backs off.</summary>
    private static int SuggestedPoll(string status, bool changed, int idleSeconds)
    {
        if (status is "completed" or "failed" or "error" or "dead" or "waiting_input") return 0;
        if (changed) return 1;
        if (idleSeconds < 10) return 3;
        if (idleSeconds < 60) return Math.Min(10, 5 + idleSeconds / 12);
        return Math.Min(30, 15 + idleSeconds / 30);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    public async Task<string> ListVariablesAsync(CancellationToken ct)
    {
        await EnsureWorkerAsync(ct);
        TaskCompletionSource<List<string>> tcs;
        lock (_lock) { _varsTcs = tcs = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously); }
        WriteWorker(new Dictionary<string, object?> { ["cmd"] = "list_vars" });
        var got = await Task.WhenAny(tcs.Task, Task.Delay(2000, ct));
        if (got != tcs.Task) return "Timed out listing variables (worker may be busy running code).";
        var vars = tcs.Task.Result;
        return vars.Count == 0 ? "No variables defined in the persistent session." : "Variables: " + string.Join(", ", vars);
    }

    public string RestartPythonWorker()
    {
        lock (_lock) { KillWorker_NoLock(); }
        return "Python worker restarted. All in-memory variables cleared.";
    }

    private string RenderResult(string? prefix)
    {
        // Caller holds _lock.
        var sb = new StringBuilder();
        if (prefix is not null) sb.Append(prefix);
        sb.Append("Status: ").Append(_jobStatus).Append('\n');
        // NOTE: the code the user ran is shown in the TUI card from the display side channel
        // (ReplShellTools.CurrentReplCode -> CurrentCode), NOT echoed here - the model generated
        // the code, so repeating it in the result it ingests is pure dead-weight tokens.
        if (_out.Length > 0) sb.Append("\n--- STDOUT ---\n").Append(_out);
        if (_err.Length > 0) sb.Append("\n--- STDERR ---\n").Append(_err);
        return sb.ToString().TrimEnd();
    }

    private async Task EnsureWorkerAsync(CancellationToken ct)
    {
        bool needStart;
        lock (_lock) needStart = _worker is null || _worker.HasExited;
        if (!needStart) return;

        if (OciSandboxed)
            _oci!.EnsureStarted();   // python lives in the container; no host venv needed
        else
            await EnsureVenvAsync(ct);

        lock (_lock)
        {
            if (_worker is not null && !_worker.HasExited) return;
            _workerFile = Path.Combine(_workDir, "worker.py");
            // Write the worker as LF (Python) bytes; never let the host EOL leak in. The work dir is
            // bind-mounted into the OCI sandbox at /work, so the same file is visible inside the container.
            File.WriteAllText(_workerFile, ReplShellTools.WorkerCodeLf, new UTF8Encoding(false));

            ProcessStartInfo psi;
            if (OciSandboxed)
            {
                // Run the persistent Python worker INSIDE the session container via `exec -i`, so its
                // JSON line protocol flows across the container boundary. The worker file lives in the
                // mounted work dir, visible at /work/worker.py inside the sandbox.
                var (file, args) = _oci!.ExecPythonWorker(OciSandbox.GuestWorkDir + "/worker.py");
                psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _workDir,
                    // Same BOM-less stdio rule as the host path - a BOM on the first stdin write would
                    // corrupt the worker's first json.loads (the g12.52 bug), now across `docker exec -i`.
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardInputEncoding = new UTF8Encoding(false),
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = File.Exists(VenvPython) ? VenvPython : (OperatingSystem.IsWindows() ? "python" : "python3"),
                    Arguments = QuoteArg(_workerFile),
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _workDir,
                    // CRITICAL: a BOM-emitting encoder on stdin (Encoding.UTF8 emits a BOM on its first
                    // write on Windows) prepends \xEF\xBB\xBF to the first line, so the worker's
                    // json.loads on `{...}` fails and the execute is silently skipped (worker hangs in
                    // "running" forever). Use a BOM-LESS UTF-8 encoder for BOTH directions.
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardInputEncoding = new UTF8Encoding(false),
                };
            }
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            _worker = Process.Start(psi);
            _jobStatus = "idle";
            _currentJobId = null;
            _inputPrompt = "";

            // Dedicated reader thread (NOT the thread pool): under sub-agent fan-out the pool is
            // saturated and a pooled reader would starve, stalling status updates. (Project reflex.)
            var proc = _worker!;
            _readerThread = new Thread(() => ReadLoop(proc)) { IsBackground = true, Name = $"ReplWorker:{_key}" };
            _readerThread.Start();
            // Drain stderr (real process-level errors) on its own background thread so it never blocks.
            var errProc = proc;
            new Thread(() =>
            {
                try { string? l; while ((l = errProc.StandardError.ReadLine()) is not null) lock (_lock) _err.Append(l).Append('\n'); }
                catch { /* worker gone */ }
            }) { IsBackground = true, Name = $"ReplWorkerErr:{_key}" }.Start();
        }
    }

    private void ReadLoop(Process proc)
    {
        try
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                JsonElement msg;
                try { using var doc = JsonDocument.Parse(line); msg = doc.RootElement.Clone(); }
                catch { lock (_lock) _err.Append(line).Append('\n'); continue; }

                string mtype = msg.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString()! : "";
                lock (_lock)
                {
                    switch (mtype)
                    {
                        case "stream":
                            string data = msg.TryGetProperty("data", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString()! : "";
                            bool isErr = msg.TryGetProperty("stream", out var sEl) && sEl.GetString() == "stderr";
                            (isErr ? _err : _out).Append(data);
                            SignalPy_NoLock(output: true);
                            break;
                        case "done":
                            bool ok = msg.TryGetProperty("status", out var stEl) && stEl.GetString() == "ok";
                            _jobStatus = ok ? "completed" : "error";
                            if (!ok && msg.TryGetProperty("error", out var eEl) && eEl.ValueKind == JsonValueKind.String) _err.Append(eEl.GetString());
                            SignalPy_NoLock(output: false);
                            _doneTcs?.TrySetResult(true);
                            break;
                        case "vars":
                            var list = new List<string>();
                            if (msg.TryGetProperty("vars", out var vEl) && vEl.ValueKind == JsonValueKind.Array)
                                foreach (var v in vEl.EnumerateArray()) if (v.ValueKind == JsonValueKind.String) list.Add(v.GetString()!);
                            _varsTcs?.TrySetResult(list);
                            break;
                        case "input_request":
                            _jobStatus = "waiting_input";
                            _inputPrompt = msg.TryGetProperty("prompt", out var pEl) && pEl.ValueKind == JsonValueKind.String ? pEl.GetString()! : "";
                            SignalPy_NoLock(output: false);
                            break;
                    }
                }
            }
        }
        catch { /* worker exited */ }
        lock (_lock)
        {
            // Only the reader for the CURRENT worker may mutate session state. When restart_python_worker
            // (or Dispose) kills this worker and a fresh one is spawned, THIS thread is still blocked in
            // ReadLine on the dead pipe; its EOF can land AFTER the new worker started a job. Without this
            // identity guard the stale reader would flip the NEW worker's live "running" job to "dead" and
            // trip its _doneTcs early (the CI-exposed Python_RunsPersistsAndRestarts flake). If we've been
            // superseded, do nothing - the current worker owns its own status + TCS.
            if (!ReferenceEquals(_worker, proc)) return;
            if (_jobStatus is "running" or "waiting_input") _jobStatus = "dead";
            SignalPy_NoLock(output: false);
            _doneTcs?.TrySetResult(true);
            _varsTcs?.TrySetResult(new List<string>());
        }
    }

    private void WriteWorker(Dictionary<string, object?> msg)
    {
        Process? p; lock (_lock) p = _worker;
        if (p is null || p.HasExited) return;
        string line = JsonSerializer.Serialize(msg) + "\n";
        try { p.StandardInput.Write(line); p.StandardInput.Flush(); } catch { /* worker gone */ }
    }

    private void KillWorker_NoLock()
    {
        try { _worker?.Kill(entireProcessTree: true); } catch { /* ignore */ }
        _worker = null;
        _jobStatus = "idle";
        _currentJobId = null;
        _inputPrompt = "";
        SignalPy_NoLock(output: false);
        _doneTcs?.TrySetResult(true);
        _varsTcs?.TrySetResult(new List<string>());
    }

    private async Task EnsureVenvAsync(CancellationToken ct)
    {
        if (_venvReady) return;
        if (File.Exists(VenvPython)) { _venvReady = true; return; }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "uv",
                Arguments = $"venv {QuoteArg(VenvDir)}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _workDir,
            };
            var p = Process.Start(psi);
            if (p is not null) await p.WaitForExitAsync(ct);
        }
        catch { /* uv unavailable - fall back to system python (no isolated venv) */ }
        _venvReady = true;
    }

    // ===== shell jobs =====

    public async Task<string> StartShellJobAsync(string command, CancellationToken ct)
    {
        if (SandboxGuard() is { } guard) return guard;
        try
        {
            if (OciSandboxed) _oci!.EnsureStarted();           // command runs inside the container
            else if (!Sandboxed) await EnsureVenvAsync(ct);    // host: activate the session venv
        }
        catch (SandboxException ex) { return $"[SANDBOX ERROR] {ex.Message}"; }
        return StartShellJob(command);
    }

    private string StartShellJob(string command)
    {
        string id = "job_" + Interlocked.Increment(ref _shellSeq);
        var job = new ShellJob(id, command);
        _shellJobs[id] = job;
        // Host path passes the venv dir to activate it; sandbox paths route the command through the
        // backend (container exec / wrapper / custom) instead - see ShellJob.Start.
        string? venv = (!Sandboxed && Directory.Exists(VenvDir)) ? VenvDir : null;
        try { job.Start(_workDir, venv, _spec, _oci); }
        catch (Exception ex) { job.MarkFailed(ex.Message); }
        return $"Job ID: {id}\nStatus: {job.Status}\nCommand: {command}\n\nUse check_job_status('{id}') to see the output.";
    }

    public async Task<string> InstallPackageAsync(string package, CancellationToken ct)
    {
        if (SandboxGuard() is { } guard) return guard;
        if (OciSandboxed)
        {
            // Inside the container, install with pip (the base image's python). No host venv.
            try { _oci!.EnsureStarted(); }
            catch (SandboxException ex) { return $"[SANDBOX ERROR] {ex.Message}"; }
            return StartShellJob($"python -m pip install {package}");
        }
        await EnsureVenvAsync(ct);
        string py = File.Exists(VenvPython) ? VenvPython : (OperatingSystem.IsWindows() ? "python" : "python3");
        string cmd = $"uv pip install --python {QuoteArg(py)} {package}";
        return StartShellJob(cmd);
    }

    public string CheckJobStatus(string jobId)
    {
        if (!_shellJobs.TryGetValue(jobId, out var job)) return $"No such job: {jobId}";
        return job.Render();
    }

    /// <summary>Event-driven progress wait for a background shell job: blocks up to waitSeconds but returns
    /// early on new output / terminal status, emitting only the delta since the caller's cursors. The legacy
    /// CheckJobStatus full-snapshot path is untouched.</summary>
    public async Task<string> WaitJobProgressAsync(
        string jobId, int waitSeconds, int stdoutCursor, int stderrCursor, int maxChars, CancellationToken ct)
    {
        if (!_shellJobs.TryGetValue(jobId, out var job)) return $"No such job: {jobId}";
        // Pass negatives through as the auto-resume sentinel; ShellJob resolves them to the last
        // delivered cursor. Only a >=0 value is a real explicit position.
        var p = await job.WaitProgressAsync(
            stdoutCursor < 0 ? -1 : stdoutCursor, stderrCursor < 0 ? -1 : stderrCursor,
            Clamp(maxChars, 512, 60000), Clamp(waitSeconds, 1, 120), ct);
        return RenderShellProgress(jobId, p);
    }

    private static string RenderShellProgress(string jobId, ShellJob.ShellProgress p)
    {
        bool terminal = p.Status is "completed" or "failed";
        bool changed = p.StdoutDelta.Length > 0 || p.StderrDelta.Length > 0 || terminal;
        var sb = new StringBuilder();
        sb.Append("Job ID: ").Append(jobId).Append('\n');
        sb.Append("Status: ").Append(p.Status);
        if (p.ExitCode is { } ec) sb.Append(" (exit ").Append(ec).Append(')');
        sb.Append('\n');
        sb.Append("Changed: ").Append(changed ? "true" : "false").Append('\n');
        sb.Append("Elapsed: ").Append(p.ElapsedSeconds).Append("s   Idle: ").Append(p.IdleSeconds).Append("s\n");
        sb.Append("ProcessExited: ").Append(p.ProcessExited ? "true" : "false")
          .Append("   OutputDrained: ").Append(p.OutputDrained ? "true" : "false").Append('\n');
        sb.Append("StdoutCursor: ").Append(p.NextStdoutCursor - p.StdoutDelta.Length).Append(" -> ").Append(p.NextStdoutCursor)
          .Append("   (total ").Append(p.TotalStdout).Append(")\n");
        sb.Append("StderrCursor: ").Append(p.NextStderrCursor - p.StderrDelta.Length).Append(" -> ").Append(p.NextStderrCursor)
          .Append("   (total ").Append(p.TotalStderr).Append(")\n");
        sb.Append("Truncated: ").Append(p.Truncated ? "true" : "false").Append("   Dropped: ").Append(p.Dropped).Append('\n');
        sb.Append("SuggestedPollSeconds: ").Append(SuggestedPoll(p.Status, changed, p.IdleSeconds));
        if (p.StdoutDelta.Length > 0) sb.Append("\n\n--- STDOUT (new) ---\n").Append(p.StdoutDelta);
        if (p.StderrDelta.Length > 0) sb.Append("\n\n--- STDERR (new) ---\n").Append(p.StderrDelta);
        return sb.ToString();
    }

    public string SendShellInput(string jobId, string text)
    {
        if (!_shellJobs.TryGetValue(jobId, out var job)) return $"No such job: {jobId}";
        return job.SendInput(text);
    }

    private static string QuoteArg(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock) KillWorker_NoLock();
        foreach (var j in _shellJobs.Values) j.Kill();
        _shellJobs.Clear();
        try { _oci?.Dispose(); } catch { /* best effort container teardown */ }
    }
}
