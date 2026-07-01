using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// Native, in-house REPL + shell execution tools (a session-scoped C# port of the user-owned
/// <c>mcp-async-repl</c> MCP server). The MCP server held a single PROCESS-GLOBAL Python worker +
/// shell-job table, so concurrent sub-agents sharing the one MCP connection corrupted each other's
/// REPL globals and job ids (the "sub-agent clashing bug"). Here every agent session owns its own
/// <see cref="ReplSession"/> (resolved from the same AsyncLocal capture scope that drives the
/// sub-agent activity panel), so parallel children are isolated BY CONSTRUCTION - no config, no
/// prompt cooperation, no shared state.
///
/// The Python REPL is a faithful port: a dedicated Python subprocess running the same newline-
/// delimited JSON protocol (persistent <c>repl_globals</c>, <c>input()</c> bridging). Shell jobs
/// run as native <see cref="Process"/> instances with dedicated reader threads. Tool surface +
/// result framing mirror the MCP server so the model sees an identical contract, with cleaner
/// stdout/stderr exposure for the user and downstream systems.
/// </summary>
public static class ReplShellTools
{
    // The embedded Python worker. Stored with whatever EOL the source file uses; ALWAYS normalized
    // to '\n' before being written to the worker temp file (Python is LF-native and the per-line
    // JSON protocol must not carry CR). Mirrors mcp-async-repl's WORKER_CODE exactly.
    private const string WorkerCode = @"import sys, json, traceback, builtins, queue, threading

original_stdout = sys.stdout
original_stderr = sys.stderr

def send_msg(msg):
    original_stdout.write(json.dumps(msg) + ""\n"")
    original_stdout.flush()

class StreamSender:
    def __init__(self, name):
        self.name = name
    def write(self, s):
        if s:
            send_msg({""type"": ""stream"", ""stream"": self.name, ""data"": s})
    def flush(self):
        pass

sys.stdout = StreamSender(""stdout"")
sys.stderr = StreamSender(""stderr"")

input_queue = queue.Queue()

def patched_input(prompt=""""):
    send_msg({""type"": ""input_request"", ""prompt"": str(prompt) if prompt else """"})
    return input_queue.get()

builtins.input = patched_input

repl_globals = {}

def run_code(code):
    try:
        exec(code, repl_globals)
        send_msg({""type"": ""done"", ""status"": ""ok""})
    except Exception:
        send_msg({""type"": ""done"", ""status"": ""error"", ""error"": traceback.format_exc()})

for line in sys.stdin:
    try:
        req = json.loads(line)
    except Exception:
        continue
    cmd = req.get(""cmd"")
    if cmd == ""execute"":
        t = threading.Thread(target=run_code, args=(req.get(""code"", """"),), daemon=True)
        t.start()
    elif cmd == ""input"":
        input_queue.put(req.get(""text"", """"))
    elif cmd == ""list_vars"":
        send_msg({""type"": ""vars"", ""vars"": [k for k in repl_globals.keys() if not k.startswith(""__"")]})
";

    /// <summary>Always-LF worker source for the temp file / worker stdin protocol.</summary>
    internal static string WorkerCodeLf => WorkerCode.Replace("\r\n", "\n").Replace("\r", "\n");

    // ----- session registry --------------------------------------------------------------

    // The AsyncLocal "current session key": each agent run (single agent OR a delegate_parallel
    // child) flows under its own key, set by BeginScope at the same place sub-agent capture begins.
    // Null key = the primary/default session.
    private static readonly AsyncLocal<string?> _scope = new();
    private static readonly ConcurrentDictionary<string, ReplSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// DISPLAY-ONLY read of the full source of the most recent code submitted to the CURRENT session
    /// scope. The TUI tool-result card shows this to the USER above the output (every python REPL tool
    /// - repl_shell_exec, check_python_status, send_python_input - reports the SAME running/last job, so
    /// all three cards can echo the code). Sourced from the session (which already holds _currentCode),
    /// so no AsyncLocal is needed (those flow DOWN the call tree and would not reach the renderer). It
    /// is NEVER folded into the string returned to the model - the model generated the code, so echoing
    /// it back is pure dead-weight tokens. Returns null/empty when nothing has run in this scope.
    /// </summary>
    public static string? CurrentReplCode()
        => _sessions.TryGetValue(CurrentKey, out var s) && s.CurrentCode is { Length: > 0 } c ? c : null;

    private static string CurrentKey => _scope.Value ?? "__primary__";

    /// <summary>
    /// Bind a fresh session scope for the calling async flow (mirrors MuxConsole.BeginSubAgentCapture's
    /// AsyncLocal model). Disposing the returned scope tears the session's worker + shell jobs down.
    /// Returns a no-op scope when native tools are disabled.
    /// </summary>
    public static IDisposable BeginScope(string sessionKey)
    {
        var prev = _scope.Value;
        _scope.Value = sessionKey;
        return new ScopeHandle(sessionKey, prev);
    }

    private static ReplSession Session() => _sessions.GetOrAdd(CurrentKey, k => new ReplSession(k));

    private sealed class ScopeHandle : IDisposable
    {
        private readonly string _key;
        private readonly string? _prev;
        private bool _done;
        public ScopeHandle(string key, string? prev) { _key = key; _prev = prev; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _scope.Value = _prev;
            if (_sessions.TryRemove(_key, out var s)) s.Dispose();
        }
    }

    // ----- tool construction -------------------------------------------------------------

    /// <summary>
    /// Build the native REPL/shell AIFunctions. Each tool resolves the CURRENT session at call time
    /// (so a delegate_parallel child transparently uses its own worker). Names + descriptions match
    /// the mcp-async-repl surface so the model contract is unchanged.
    /// </summary>
    public static IReadOnlyList<AITool> Build()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                method: async (
                    [Description("Python code to run in the persistent REPL. Variables persist between calls.")] string code,
                    CancellationToken cancellationToken = default) =>
                    NativeShellSecurity.Gate(code, "Run python") is { } _deny
                        ? _deny
                        : await Session().ExecutePythonAsync(code, cancellationToken),
                name: "repl_shell_exec",
                description: "Execute Python code in a persistent background worker. Variables persist between executions. " +
                             "Safe from hanging the server. If the code takes longer than 2 seconds, it returns a running status. " +
                             "If the code calls input(), the status becomes 'waiting_input' - use send_python_input to respond. " +
                             "stdout/stderr are returned under clear --- STDOUT --- / --- STDERR --- sections."),

            AIFunctionFactory.Create(
                method: async (
                    [Description("Text to deliver to the worker's pending input() call.")] string text,
                    CancellationToken cancellationToken = default) =>
                    await Session().SendPythonInputAsync(text, cancellationToken),
                name: "send_python_input",
                description: "Send text to the Python worker when it is waiting for input(). Use when repl_shell_exec or " +
                             "check_python_status reports 'waiting_input'."),

            AIFunctionFactory.Create(
                method: () => Session().CheckPythonStatus(),
                name: "check_python_status",
                description: "Check the status and accumulated output of the running or last Python job. Reports running / " +
                             "waiting_input / completed / error / dead, with the stdout/stderr captured so far."),

            AIFunctionFactory.Create(
                method: async (CancellationToken cancellationToken = default) =>
                    await Session().ListVariablesAsync(cancellationToken),
                name: "list_variables",
                description: "List the variable names currently defined in the persistent Python session."),

            AIFunctionFactory.Create(
                method: () => Session().RestartPythonWorker(),
                name: "restart_python_worker",
                description: "Kill the current Python worker and start a fresh one. Use when a script hangs (e.g. an infinite " +
                             "loop). NOTE: this clears all variables in memory."),

            AIFunctionFactory.Create(
                method: async (
                    [Description("Shell command to run as a background job.")] string command,
                    CancellationToken cancellationToken = default) =>
                    NativeShellSecurity.Gate(command, "Run shell command") is { } _deny
                        ? _deny
                        : await Session().StartShellJobAsync(command, cancellationToken),
                name: "execute_command_async",
                description: "Execute a shell command asynchronously to prevent timeouts. Returns a Job ID immediately. " +
                             "Use this for long-running scripts, then poll with check_job_status."),

            AIFunctionFactory.Create(
                method: (
                    [Description("The Job ID returned by execute_command_async / install_package_async.")] string job_id) =>
                    Session().CheckJobStatus(job_id),
                name: "check_job_status",
                description: "Retrieve the current status and accumulated stdout/stderr of a background shell job."),

            AIFunctionFactory.Create(
                method: (
                    [Description("Job ID of the running async command.")] string job_id,
                    [Description("Text to send to the job's stdin (e.g. 'y\\n' or a password).")] string text) =>
                    Session().SendShellInput(job_id, text),
                name: "send_command_input",
                description: "Send text input (like 'y\\n' or passwords) to a running async shell command."),

            AIFunctionFactory.Create(
                method: async (
                    [Description("Package to install into the session venv (uv pip install).")] string package,
                    CancellationToken cancellationToken = default) =>
                    NativeShellSecurity.Gate("uv pip install " + package, "Install package") is { } _deny
                        ? _deny
                        : await Session().InstallPackageAsync(package, cancellationToken),
                name: "install_package_async",
                description: "Install a Python package asynchronously using uv into the session's virtual environment. " +
                             "Returns a Job ID to poll with check_job_status."),
        };
        return tools;
    }
}
