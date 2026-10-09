using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MuxSwarm.Engine.NativeTools;

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
    private ISessionSandbox? _oci;
    private bool Sandboxed => _spec is not null;
    private bool OciSandboxed => _spec is { IsSession: true };

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
        if (OciSandboxed)
            _oci = _spec!.Kind == SandboxKind.Sbx ? new SbxSandbox(_spec, _workDir, _key) : new OciSandbox(_spec, _workDir, _key);
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
                if (terminal || behind) return RenderPyProgressAndRemember(so, se, maxChars);
                changed = _pyChangeTcs.Task; // capture UNDER lock before await
            }
            var timeout = Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
            var done = await Task.WhenAny(changed, timeout).ConfigureAwait(false);
            if (done == timeout)
            {
                ct.ThrowIfCancellationRequested();
                lock (_lock)
                {
                    int so = autoOut ? _pyLastDeliveredOut : stdoutCursor;
                    int se = autoErr ? _pyLastDeliveredErr : stderrCursor;
                    return RenderPyProgressAndRemember(so, se, maxChars); // Changed=false
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
        // Deltas: cursors index the RAW buffers (unchanged); only the delivered text sheds
        // end-of-line padding. An unterminated tail segment is never trimmed (mid-line split).
        if (outDelta.Length > 0) sb.Append("\n\n--- STDOUT (new) ---\n").Append(OutputWhitespace.Apply(outDelta));
        if (errDelta.Length > 0) sb.Append("\n\n--- STDERR (new) ---\n").Append(OutputWhitespace.Apply(errDelta));
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


    public async Task<string> ListVariablesAsync(CancellationToken ct)
    {
        if (SandboxGuard() is { } guard) return guard;
        try { await EnsureWorkerAsync(ct); }
        catch (SandboxException ex) { return $"[SANDBOX ERROR] {ex.Message}"; }
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
        if (_out.Length > 0) sb.Append("\n--- STDOUT ---\n").Append(OutputWhitespace.Apply(_out.ToString()));
        if (_err.Length > 0) sb.Append("\n--- STDERR ---\n").Append(OutputWhitespace.Apply(_err.ToString()));
        return sb.ToString().TrimEnd();
    }

    private async Task EnsureWorkerAsync(CancellationToken ct)
    {
        bool needStart;
        lock (_lock) needStart = _worker is null || _worker.HasExited;
        if (!needStart) return;

        // A custom template is opaque: only run the long-lived stdio worker through it when the user
        // declared it can host one. Otherwise refuse - never a silent host fallback.
        if (_spec is { Kind: SandboxKind.Custom, CustomReplStdio: false })
            throw new SandboxException("the Python REPL cannot run under sandbox.backend 'custom' unless the template " +
                "passes stdin/stdout through to {cmd}. If it does, set sandbox.replStdio true; otherwise use the shell " +
                "tools (they run through the template) or another backend.");

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
                string py = File.Exists(VenvPython) ? VenvPython : (OperatingSystem.IsWindows() ? "python" : "python3");
                var (file, args) = WorkerCommand(_spec, py, _workerFile, _workDir);
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

    private string StartShellJob(string command, IReadOnlyList<string>? readOnlyPaths = null)
    {
        string id = "job_" + Interlocked.Increment(ref _shellSeq);
        var job = new ShellJob(id, command, readOnlyPaths);
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
        // Wrapper backends run this as a wrapped shell job (network limits apply). The user's uv cache
        // (~/.cache/uv) is read-only or hidden inside the wrapper, so keep the cache in the session work dir.
        if (_spec is { Kind: SandboxKind.Wrapper })
        {
            cmd = $"UV_CACHE_DIR={QuoteArg(Path.Combine(_workDir, ".uv-cache"))} " + cmd;
            // firejail hides ~ and Seatbelt denies by default: expose the host uv binary + base interpreter, read-only.
            return StartShellJob(cmd, InstallReadPaths(py));
        }
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
            maxChars, waitSeconds, ct);
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
        if (p.StdoutDelta.Length > 0) sb.Append("\n\n--- STDOUT (new) ---\n").Append(OutputWhitespace.Apply(p.StdoutDelta));
        if (p.StderrDelta.Length > 0) sb.Append("\n\n--- STDERR (new) ---\n").Append(OutputWhitespace.Apply(p.StderrDelta));
        return sb.ToString();
    }

    public string SendShellInput(string jobId, string text)
    {
        if (!_shellJobs.TryGetValue(jobId, out var job)) return $"No such job: {jobId}";
        return job.SendInput(text);
    }

    private static string QuoteArg(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>
    /// Host paths a wrapped venv interpreter must read: the base Python install the venv points at
    /// (<c>home</c> in pyvenv.cfg, e.g. a uv-managed CPython under ~/.local/share/uv). Wrappers that hide
    /// $HOME (firejail) or deny by default (sandbox-exec) would otherwise break the interpreter.
    /// pyvenv.cfg lives in the sandbox's WRITABLE work dir, so its value is untrusted: it is only used when it
    /// names an existing, normalized directory under a host-derived Python root (<see cref="TrustedPythonRoots"/>).
    /// Anything else exposes nothing extra (the interpreter may then fail to start; it never widens the sandbox).
    /// </summary>
    internal static IReadOnlyList<string> PythonReadPaths(string python, IReadOnlyList<string>? trustedRoots = null)
    {
        string? venv = Path.GetDirectoryName(Path.GetDirectoryName(python));
        string cfg = venv is null ? "" : Path.Combine(venv, "pyvenv.cfg");
        if (!File.Exists(cfg)) return Array.Empty<string>();
        string? home = null;
        try
        {
            foreach (var line in File.ReadLines(cfg))
            {
                int eq = line.IndexOf('=');
                if (eq >= 0 && line[..eq].Trim() == "home") { home = line[(eq + 1)..].Trim(); break; }
            }
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        string? prefix = home is null ? null : Path.GetDirectoryName(home.TrimEnd('/'));   // <prefix>/bin -> <prefix>
        string? trusted = prefix is null ? null : TrustedPythonPrefix(prefix, trustedRoots ?? TrustedPythonRoots());
        return trusted is not null ? new[] { trusted } : Array.Empty<string>();
    }

    /// <summary>
    /// Where a venv's base interpreter may legitimately live, derived from the HOST (never from sandbox-writable
    /// files): uv's managed-Python dir (<c>UV_PYTHON_INSTALL_DIR</c>, else <c>$XDG_DATA_HOME/uv/python</c>, else
    /// <c>~/.local/share/uv/python</c>) and, on macOS, Homebrew (<c>/opt/homebrew</c>, <c>/usr/local</c>).
    /// System locations (/usr, /System) are already readable inside every wrapper, so they are not listed.
    /// </summary>
    internal static IReadOnlyList<string> TrustedPythonRoots()
    {
        var roots = new List<string>();
        string? uvDir = Environment.GetEnvironmentVariable("UV_PYTHON_INSTALL_DIR");
        if (string.IsNullOrWhiteSpace(uvDir))
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            uvDir = !string.IsNullOrWhiteSpace(xdg) ? Path.Combine(xdg, "uv", "python")
                : home.Length > 0 ? Path.Combine(home, ".local", "share", "uv", "python") : null;
        }
        if (!string.IsNullOrWhiteSpace(uvDir)) roots.Add(uvDir);
        if (OperatingSystem.IsMacOS()) { roots.Add("/opt/homebrew"); roots.Add("/usr/local"); }
        return roots;
    }

    /// <summary>True when <paramref name="prefix"/> is an absolute, already-normalized, existing Python install prefix
    /// (it holds <c>bin/python3</c> or <c>bin/python</c>) with no quote/backslash/control characters, strictly inside
    /// one of <paramref name="roots"/>. Use <see cref="TrustedPythonPrefix"/> to get the symlink-resolved path.</summary>
    internal static bool IsTrustedPythonPrefix(string prefix, IReadOnlyList<string> roots) =>
        TrustedPythonPrefix(prefix, roots) is not null;

    /// <summary>The symlink-resolved form of <paramref name="prefix"/> when it is a trusted Python install prefix (see
    /// <see cref="IsTrustedPythonPrefix"/>), else null. Resolved BEFORE the root check (sandbox-exec matches real paths,
    /// and Homebrew prefixes are symlinks into Cellar); roots are resolved the same way.</summary>
    internal static string? TrustedPythonPrefix(string prefix, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathRooted(prefix) || !SandboxBackend.IsSafeSandboxPath(prefix)) return null;
        string full = Path.GetFullPath(prefix);
        if (!string.Equals(full.TrimEnd('/'), prefix.TrimEnd('/'), StringComparison.Ordinal)) return null;   // no ., .., //
        if (!Directory.Exists(full)) return null;
        string real = RealPath(full);
        if (!SandboxBackend.IsSafeSandboxPath(real)) return null;
        // Only a real interpreter install: a tampered `home` must not name an arbitrary directory under a root
        // (e.g. /opt/homebrew/var holds service data).
        if (!File.Exists(Path.Combine(real, "bin", "python3")) && !File.Exists(Path.Combine(real, "bin", "python"))) return null;
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r) || !Path.IsPathRooted(r)) continue;
            string root = RealPath(Path.GetFullPath(r)).TrimEnd('/');
            if (real.StartsWith(root + "/", StringComparison.Ordinal)) return real;
        }
        return null;
    }

    /// <summary>Fully resolves symlinks in <paramref name="path"/> component by component (like realpath); a
    /// component that does not exist is kept as written.</summary>
    internal static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return full;
        string current = root;
        foreach (var part in full[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            try
            {
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    next = RealPath(target.FullName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Host paths an install job (<c>uv pip install</c>) needs readable inside a wrapper: the directory of the host
    /// <c>uv</c> binary and the venv's validated base interpreter. Resolved on the host, never from the work dir.
    /// </summary>
    internal static IReadOnlyList<string> InstallReadPaths(string python, string? uvPath = null)
    {
        var paths = new List<string>(PythonReadPaths(python));
        string? uv = uvPath ?? FindOnPath("uv");
        // The real binary's directory: sandbox-exec matches resolved paths, and /opt/homebrew/bin/uv is a symlink.
        string? uvDir = uv is null ? null : Path.GetDirectoryName(RealPath(uv));
        if (uvDir is not null && Path.IsPathRooted(uvDir) && SandboxBackend.IsSafeSandboxPath(uvDir) && !paths.Contains(uvDir))
            paths.Add(uvDir);
        return paths;
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>
    /// The (file, args) that start the host-side Python worker. Host: the interpreter directly. Wrapper and
    /// opted-in custom backends: the worker process itself runs inside the sandbox, exactly like shell jobs,
    /// and its stdin/stdout JSON line protocol passes straight through the wrapper. Container/microVM
    /// backends never reach here (they exec into their session instance).
    /// </summary>
    internal static (string File, string Args) WorkerCommand(SandboxSpec? spec, string python, string workerFile, string workDir)
    {
        if (spec is { Kind: SandboxKind.Wrapper })
            return SandboxBackend.WrapProcess(spec, new[] { python, workerFile }, workDir, PythonReadPaths(python));
        if (spec is { Kind: SandboxKind.Custom, CustomReplStdio: true })
            return SandboxBackend.WrapShellCommand(spec, ShellWord(python) + " " + ShellWord(workerFile), workDir);
        return (python, QuoteArg(workerFile));
    }

    // One word for a custom template's {cmd}, which runs via sh (Unix) or cmd.exe (Windows).
    private static string ShellWord(string s) => OperatingSystem.IsWindows()
        ? QuoteArg(s)
        : "'" + s.Replace("'", "'\\''") + "'";

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
