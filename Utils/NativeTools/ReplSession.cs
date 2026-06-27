using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _err = new();
    private string _inputPrompt = "";
    private TaskCompletionSource<bool>? _doneTcs;
    private TaskCompletionSource<List<string>>? _varsTcs;
    private bool _venvReady;

    // ---- shell jobs ----
    private readonly ConcurrentDictionary<string, ShellJob> _shellJobs = new(StringComparer.Ordinal);
    private int _shellSeq;

    public ReplSession(string key)
    {
        _key = key;
        string sub = "repl_" + Sanitize(key);
        _workDir = Path.Combine(LocalDataRoot(), "repl", sub);
        Directory.CreateDirectory(_workDir);
    }

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
        await EnsureWorkerAsync(ct);
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
            _jobStatus = "running";
            _out.Clear();
            _err.Clear();
            _inputPrompt = "";
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
        if (_out.Length > 0) sb.Append("\n--- STDOUT ---\n").Append(_out);
        if (_err.Length > 0) sb.Append("\n--- STDERR ---\n").Append(_err);
        return sb.ToString().TrimEnd();
    }

    private async Task EnsureWorkerAsync(CancellationToken ct)
    {
        bool needStart;
        lock (_lock) needStart = _worker is null || _worker.HasExited;
        if (!needStart) return;

        await EnsureVenvAsync(ct);

        lock (_lock)
        {
            if (_worker is not null && !_worker.HasExited) return;
            _workerFile = Path.Combine(_workDir, "worker.py");
            // Write the worker as LF (Python) bytes; never let the host EOL leak in.
            File.WriteAllText(_workerFile, ReplShellTools.WorkerCodeLf, new UTF8Encoding(false));

            var psi = new ProcessStartInfo
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
                            break;
                        case "done":
                            bool ok = msg.TryGetProperty("status", out var stEl) && stEl.GetString() == "ok";
                            _jobStatus = ok ? "completed" : "error";
                            if (!ok && msg.TryGetProperty("error", out var eEl) && eEl.ValueKind == JsonValueKind.String) _err.Append(eEl.GetString());
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
                            break;
                    }
                }
            }
        }
        catch { /* worker exited */ }
        lock (_lock)
        {
            if (_jobStatus is "running" or "waiting_input") _jobStatus = "dead";
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
        // Ensure the session venv exists before launching so the shell job is activated into the
        // SAME interpreter the REPL worker / install_package_async use, regardless of call order.
        await EnsureVenvAsync(ct);
        return StartShellJob(command);
    }

    private string StartShellJob(string command)
    {
        string id = "job_" + Interlocked.Increment(ref _shellSeq);
        var job = new ShellJob(id, command);
        _shellJobs[id] = job;
        // Pass the venv dir only when it actually materialized (uv present); otherwise leave the
        // job on the system PATH, matching the worker's own system-python fallback.
        string? venv = Directory.Exists(VenvDir) ? VenvDir : null;
        try { job.Start(_workDir, venv); }
        catch (Exception ex) { job.MarkFailed(ex.Message); }
        return $"Job ID: {id}\nStatus: {job.Status}\nCommand: {command}\n\nUse check_job_status('{id}') to see the output.";
    }

    public async Task<string> InstallPackageAsync(string package, CancellationToken ct)
    {
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
    }
}
