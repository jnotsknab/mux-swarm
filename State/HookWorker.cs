using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using MuxSwarm.Engine;

namespace MuxSwarm.State;

public static class HookWorker
{
    private static readonly Channel<HookEvent> Channel =
        System.Threading.Channels.Channel.CreateUnbounded<HookEvent>(new UnboundedChannelOptions { SingleReader = true });

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static CancellationTokenSource? _cts;
    private static Task? _loopTask;
    private static List<HookConfig> _hooks = [];

    private static bool _enabled = true;

    /// <summary>Session enable gate (/hooks on|off). When false, events are still enqueued but the
    /// process loop drops them without running any command. Default true so configured hooks run.
    /// Flipping to true after the worker has started will lazily start any persistent hook processes
    /// that were skipped because the session booted disabled.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            bool wasOff = !_enabled;
            _enabled = value;
            // Re-enabling after a disabled boot: start persistent processes that never came up.
            if (value && wasOff && _cts is not null && _persistentProcesses.IsEmpty)
            {
                try { StartPersistentHooks(); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>The count of currently-loaded hook configs (for /hooks status).</summary>
    public static int Count => _hooks.Count;

    /// <summary>Snapshot of the loaded hooks for status display.</summary>
    public static IReadOnlyList<HookConfig> Loaded => _hooks;

    /// <summary>
    /// Long-lived processes for persistent hooks. Keyed by hook ID.
    /// These receive events as NDJSON lines on stdin for the entire session.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Process> _persistentProcesses = new();

    /// <summary>
    /// Enqueue a hook event for evaluation. Non-blocking, never throws.
    /// Safe to call from MuxConsole before Start().
    /// </summary>
    public static void Enqueue(HookEvent e)
    {
        e.GoalId ??= _goalId.Value;
        Channel.Writer.TryWrite(e);
    }

    private static readonly AsyncLocal<string?> _goalId = new();

    /// <summary>Ambient goal id (<c>--goal-id</c> or a continuous run's generated id) stamped onto every
    /// event enqueued from this async flow. Set at orchestrator entry; flows into child tasks. A null id
    /// is ignored so a nested session without one keeps the parent's goal.</summary>
    public static void SetGoalId(string? goalId)
    {
        if (!string.IsNullOrWhiteSpace(goalId)) _goalId.Value = goalId;
    }

    // callId -> tool name, so tool_result events can carry the tool that produced them.
    private static readonly ConcurrentDictionary<string, string> _callTools = new();

    /// <summary>Builds the tool_call hook event, including the call arguments as JSON.</summary>
    internal static HookEvent ToolCallEvent(string? agent, Microsoft.Extensions.AI.FunctionCallContent call)
    {
        if (!string.IsNullOrEmpty(call.CallId))
        {
            if (_callTools.Count > 1000) _callTools.Clear();   // calls whose result never arrived
            _callTools[call.CallId] = call.Name;
        }
        string? args = null;
        try { args = call.Arguments is null ? null : JsonSerializer.Serialize(call.Arguments); } catch { /* unserializable arg */ }
        return new HookEvent { Event = "tool_call", Agent = agent, Tool = call.Name, Args = args, Timestamp = DateTimeOffset.UtcNow };
    }

    /// <summary>Builds the tool_result hook event: the tool name (from the matching call) and the result text.</summary>
    internal static HookEvent ToolResultEvent(string? agent, Microsoft.Extensions.AI.FunctionResultContent result)
    {
        string? tool = null;
        if (!string.IsNullOrEmpty(result.CallId)) _callTools.TryRemove(result.CallId, out tool);
        return new HookEvent { Event = "tool_result", Agent = agent, Tool = tool, Summary = ResultText(result.Result), Timestamp = DateTimeOffset.UtcNow };
    }

    /// <summary>Plain text of a tool result: strings as-is, MCP content lists as their joined text parts
    /// (ToString() on those yields a type name), other JSON as raw JSON.</summary>
    internal static string? ResultText(object? result) => result switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } j => j.GetString(),
        JsonElement j => j.GetRawText(),
        IEnumerable<Microsoft.Extensions.AI.AIContent> parts =>
            string.Join("\n", parts.OfType<Microsoft.Extensions.AI.TextContent>().Select(p => p.Text)),
        _ => result.ToString()
    };

    public static void Start(List<HookConfig> hooks)
    {
        if (_cts is not null) return;

        _hooks = hooks;

        if (hooks.Count > 0)
        {
            MuxConsole.WriteWarning($"[Hooks] {hooks.Count} hook(s) configured in swarm.json.");
            MuxConsole.WriteWarning("Hooks execute arbitrary external commands with your user permissions.");

            if (!MuxConsole.StdioMode)
            {
                if (!MuxConsole.Confirm("Proceed with hooks enabled?", defaultValue: false))
                {
                    // Mute hook EXECUTION for the session but KEEP the configs loaded, so /hooks shows
                    // the real count and /hooks on can re-enable them without a restart. (Previously
                    // this wiped _hooks, which made the count read 0 after a decline.)
                    Enabled = false;
                    MuxConsole.WriteInfo("Hooks disabled for this session - re-enable with /hooks on.");
                }
            }
        }

        _cts = new CancellationTokenSource();
        // Only spin up persistent hook processes when execution is enabled; otherwise they start
        // lazily when the user runs /hooks on (the process loop also gates on Enabled).
        if (Enabled) StartPersistentHooks();
        _loopTask = Task.Run(() => HookProcessLoopAsync(_cts.Token));
    }

    public static void Stop()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _cts = null;
        StopPersistentHooks();
    }

    private static void StartPersistentHooks()
    {
        foreach (var hook in _hooks.Where(h => h.Persistent))
        {
            try
            {
                var process = StartHookProcess(hook);
                if (process is null)
                {
                    MuxConsole.WriteWarning($"[Hook:{hook.Id}] Empty command, skipping persistent start.");
                    continue;
                }
                _persistentProcesses[hook.Id] = process;
                MuxConsole.WriteSuccess($"[Hook:{hook.Id}] Persistent process started (PID {process.Id}).");
            }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"[Hook:{hook.Id}] Failed to start persistent process: {ex.Message}");
            }
        }
    }

    /// <summary>Builds the start info for a hook. With <see cref="HookConfig.Args"/> the command is the
    /// executable and each arg is passed verbatim; without it the legacy split at the first space applies.
    /// stdin/stdout/stderr are always redirected: stdin carries the event JSON, and the output streams are
    /// drained by the worker instead of being inherited (under --stdio/--acp stdout is the protocol, and in
    /// the TUI inherited output draws over the frame). Returns null for an empty command.</summary>
    internal static ProcessStartInfo? BuildStartInfo(HookConfig hook)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (hook.Args is not null)
        {
            if (string.IsNullOrWhiteSpace(hook.Command)) return null;
            psi.FileName = hook.Command.Trim();
            foreach (var arg in hook.Args) psi.ArgumentList.Add(arg);
            return psi;
        }

        var parts = hook.Command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        psi.FileName = parts[0];
        psi.Arguments = parts.Length > 1 ? parts[1] : string.Empty;
        return psi;
    }

    /// <summary>Starts a hook process and drains its stdout/stderr asynchronously into muted log lines
    /// (a full pipe can never block the hook). Returns null for an empty command.</summary>
    private static Process? StartHookProcess(HookConfig hook)
    {
        var psi = BuildStartInfo(hook);
        if (psi is null) return null;

        var process = new Process { StartInfo = psi };
        _startedUtc[hook.Id] = DateTime.UtcNow;
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) MuxConsole.WriteMuted($"[Hook:{hook.Id}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) MuxConsole.WriteMuted($"[Hook:{hook.Id}:stderr] {e.Data}"); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        ProcessCleanup.Instance.Track(process);
        return process;
    }

    private static void StopPersistentHooks()
    {
        foreach (var (id, process) in _persistentProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill();
                        MuxConsole.WriteMuted($"[Hook:{id}] Persistent process killed (didn't exit in 3s).");
                    }
                }
                ProcessCleanup.Instance.Untrack(process.Id);
            }
            catch { /* already exited */ }
        }
        _persistentProcesses.Clear();
        _restarts.Clear();
    }

    private static async Task HookProcessLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var hookEvent in Channel.Reader.ReadAllAsync(ct))
            {
                // /hooks off: drain the channel but run nothing (session-level mute).
                if (!Enabled) continue;
                var matched = _hooks.Where(h => Matches(h, hookEvent)).ToList();
                if (matched.Count == 0) continue;

                // Surface each matched hook on the JSON stream (serve/ACP) + outbound webhook sinks.
                // No-op in interactive TUI with no sinks configured.
                foreach (var h in matched)
                    MuxConsole.EmitHookFired(h.Id, hookEvent.Event, hookEvent.Agent, hookEvent.Tool);

                var payload = JsonSerializer.Serialize(hookEvent, JsonOpts);

                // Persistent hooks: write to running process stdin
                foreach (var hook in matched.Where(h => h.Persistent))
                {
                    if (!_persistentProcesses.TryGetValue(hook.Id, out var proc))
                    {
                        // Not running: only a crashed hook whose backoff has elapsed is restarted.
                        if (_restarts.TryGetValue(hook.Id, out var waiting) && DateTime.UtcNow >= waiting.NextAttemptUtc)
                            RestartPersistentHook(hook, payload);
                        continue;
                    }

                    try
                    {
                        if (!proc.HasExited)
                        {
                            await proc.StandardInput.WriteLineAsync(payload);
                            await proc.StandardInput.FlushAsync(ct);
                        }
                        else
                        {
                            _persistentProcesses.TryRemove(hook.Id, out _);
                            ProcessCleanup.Instance.Untrack(proc.Id);
                            var delay = RecordCrash(hook.Id, _startedUtc.GetValueOrDefault(hook.Id, DateTime.UtcNow));
                            if (delay == TimeSpan.Zero)
                            {
                                MuxConsole.WriteWarning($"[Hook:{hook.Id}] Persistent process exited (code {proc.ExitCode}). Restarting...");
                                RestartPersistentHook(hook, payload);
                            }
                            else
                            {
                                MuxConsole.WriteWarning($"[Hook:{hook.Id}] Persistent process exited again (code {proc.ExitCode}). " +
                                                        $"Restarting in {delay.TotalSeconds:0}s; its events are skipped until then.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MuxConsole.WriteWarning($"[Hook:{hook.Id}] Write to persistent process failed: {ex.Message}");
                    }
                }

                // Non-persistent hooks: existing per-event dispatch
                var nonPersistent = matched.Where(h => !h.Persistent).ToList();

                var blocking = nonPersistent.Where(h => h.Mode == HookMode.Blocking).ToList();
                var async = nonPersistent.Where(h => h.Mode == HookMode.Async).ToList();

                foreach (var hook in async)
                    _ = Task.Run(() => DispatchHook(hook, payload, ct), ct);

                if (blocking.Count > 0)
                    await Task.WhenAll(blocking.Select(h => DispatchHook(h, payload, ct)));
            }
        }
        catch (OperationCanceledException) { /* normal shutdown path */ }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[HookWorker] Unexpected loop error: {ex.Message}");
        }
    }

    /// <summary>Consecutive-crash state of a persistent hook (process loop is the single writer).</summary>
    private sealed class RestartState
    {
        public int Failures;
        public DateTime NextAttemptUtc;
    }

    private static readonly ConcurrentDictionary<string, RestartState> _restarts = new();
    private static readonly ConcurrentDictionary<string, DateTime> _startedUtc = new();

    /// <summary>A persistent hook that stayed up at least this long before crashing resets its backoff.</summary>
    internal static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);

    /// <summary>Delay before restarting a persistent hook after its Nth consecutive crash: the first
    /// restart is immediate (as before), then 1s, 2s, 4s ... capped at 60s.</summary>
    internal static TimeSpan RestartDelay(int failures)
    {
        if (failures <= 1) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures - 2, 6))));
    }

    /// <summary>Records a crash and returns how long to wait before the next restart attempt.</summary>
    private static TimeSpan RecordCrash(string hookId, DateTime startedUtc)
    {
        var state = _restarts.GetOrAdd(hookId, _ => new RestartState());
        if (DateTime.UtcNow - startedUtc >= StableUptime) state.Failures = 0;
        state.Failures++;
        var delay = RestartDelay(state.Failures);
        state.NextAttemptUtc = DateTime.UtcNow + delay;
        return delay;
    }

    /// <summary>
    /// Restart a persistent hook that died, and deliver the event that triggered the restart.
    /// A failed start counts as another crash, so the backoff keeps growing.
    /// </summary>
    private static void RestartPersistentHook(HookConfig hook, string payload)
    {
        try
        {
            var process = StartHookProcess(hook);
            if (process is null) return;
            _persistentProcesses[hook.Id] = process;

            process.StandardInput.WriteLine(payload);
            process.StandardInput.Flush();

            MuxConsole.WriteInfo($"[Hook:{hook.Id}] Persistent process restarted (PID {process.Id}).");
        }
        catch (Exception ex)
        {
            var delay = RecordCrash(hook.Id, DateTime.UtcNow);
            MuxConsole.WriteWarning($"[Hook:{hook.Id}] Restart failed: {ex.Message}. Next attempt in {Math.Max(1, delay.TotalSeconds):0}s.");
            if (delay == TimeSpan.Zero) _restarts[hook.Id].NextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        }
    }

    private static async Task DispatchHook(HookConfig hook, string payload, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            process = StartHookProcess(hook);
            if (process is null)
            {
                MuxConsole.WriteWarning($"[Hook:{hook.Id}] Empty command, skipping.");
                return;
            }

            await process.StandardInput.WriteLineAsync(payload);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();

            if (hook.Mode == HookMode.Blocking)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(hook.TimeoutSeconds));

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                    ProcessCleanup.Instance.Untrack(process.Id);

                    if (process.ExitCode != 0)
                        MuxConsole.WriteWarning($"[Hook:{hook.Id}] Exited with code {process.ExitCode}.");
                }
                catch (OperationCanceledException)
                {
                    MuxConsole.WriteWarning($"[Hook:{hook.Id}] Timed out after {hook.TimeoutSeconds}s, killing process.");
                    try { process.Kill(); } catch { /* already exited */ }
                }
            }
            else
            {
                using var exitCheckCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                exitCheckCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await process.WaitForExitAsync(exitCheckCts.Token);
                    ProcessCleanup.Instance.Untrack(process.Id);

                    if (process.ExitCode != 0)
                        MuxConsole.WriteWarning($"[Hook:{hook.Id}] Exited with code {process.ExitCode}.");
                }
                catch (OperationCanceledException)
                {

                }
            }
        }
        catch (OperationCanceledException) { /* shutdown during dispatch */ }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[Hook:{hook.Id}] Dispatch failed: {ex.Message}");
        }
        finally
        {
            // An async hook may outlive the 5s exit check: disposing now would close its output pipes
            // under it, so release the handle when it exits instead.
            if (process is not null)
            {
                try
                {
                    if (process.HasExited) process.Dispose();
                    else { process.EnableRaisingEvents = true; process.Exited += (_, _) => process.Dispose(); }
                }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>True when the event name is one of the hook's events and the agent/tool filters pass.
    /// A hook matches an event occurrence at most once, however many events it lists.</summary>
    internal static bool Matches(HookConfig hook, HookEvent e)
    {
        if (!hook.When.Events.Any(ev => string.Equals(ev, e.Event, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (hook.When.Agent is not null &&
            !string.Equals(hook.When.Agent, e.Agent, StringComparison.OrdinalIgnoreCase))
            return false;

        if (hook.When.Tool is not null &&
            !string.Equals(hook.When.Tool, e.Tool, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}