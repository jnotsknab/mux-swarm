using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using MuxSwarm.Utils;

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

    /// <summary>
    /// Long-lived processes for persistent hooks. Keyed by hook ID.
    /// These receive events as NDJSON lines on stdin for the entire session.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Process> _persistentProcesses = new();

    /// <summary>
    /// Enqueue a hook event for evaluation. Non-blocking, never throws.
    /// Safe to call from MuxConsole before Start().
    /// </summary>
    public static void Enqueue(HookEvent e) => Channel.Writer.TryWrite(e);

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
                    _hooks = [];
                    MuxConsole.WriteInfo("Hooks disabled for this session.");
                }
            }
        }

        _cts = new CancellationTokenSource();
        StartPersistentHooks();
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
                var parts = hook.Command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    MuxConsole.WriteWarning($"[Hook:{hook.Id}] Empty command, skipping persistent start.");
                    continue;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = parts[0],
                    Arguments = parts.Length > 1 ? parts[1] : string.Empty,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                var process = new Process { StartInfo = psi };
                process.Start();
                ProcessCleanup.Instance.Track(process);
                _persistentProcesses[hook.Id] = process;
                MuxConsole.WriteSuccess($"[Hook:{hook.Id}] Persistent process started (PID {process.Id}).");
            }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"[Hook:{hook.Id}] Failed to start persistent process: {ex.Message}");
            }
        }
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
    }

    private static async Task HookProcessLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var hookEvent in Channel.Reader.ReadAllAsync(ct))
            {
                var matched = _hooks.Where(h => Matches(h, hookEvent)).ToList();
                if (matched.Count == 0) continue;

                var payload = JsonSerializer.Serialize(hookEvent, JsonOpts);

                // Persistent hooks: write to running process stdin
                foreach (var hook in matched.Where(h => h.Persistent))
                {
                    if (_persistentProcesses.TryGetValue(hook.Id, out var proc))
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                await proc.StandardInput.WriteLineAsync(payload);
                                await proc.StandardInput.FlushAsync(ct);
                            }
                            else
                            {
                                MuxConsole.WriteWarning($"[Hook:{hook.Id}] Persistent process exited (code {proc.ExitCode}). Restarting...");
                                _persistentProcesses.TryRemove(hook.Id, out _);
                                ProcessCleanup.Instance.Untrack(proc.Id);
                                RestartPersistentHook(hook, payload);
                            }
                        }
                        catch (Exception ex)
                        {
                            MuxConsole.WriteWarning($"[Hook:{hook.Id}] Write to persistent process failed: {ex.Message}");
                        }
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

    /// <summary>
    /// Restart a persistent hook that died, and deliver the event that triggered the restart.
    /// </summary>
    private static void RestartPersistentHook(HookConfig hook, string payload)
    {
        try
        {
            var parts = hook.Command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var psi = new ProcessStartInfo
            {
                FileName = parts[0],
                Arguments = parts.Length > 1 ? parts[1] : string.Empty,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            process.Start();
            ProcessCleanup.Instance.Track(process);
            _persistentProcesses[hook.Id] = process;

            process.StandardInput.WriteLine(payload);
            process.StandardInput.Flush();

            MuxConsole.WriteInfo($"[Hook:{hook.Id}] Persistent process restarted (PID {process.Id}).");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[Hook:{hook.Id}] Restart failed: {ex.Message}");
        }
    }

    private static async Task DispatchHook(HookConfig hook, string payload, CancellationToken ct)
    {
        try
        {
            var parts = hook.Command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                MuxConsole.WriteWarning($"[Hook:{hook.Id}] Empty command, skipping.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = parts[0],
                Arguments = parts.Length > 1 ? parts[1] : string.Empty,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            ProcessCleanup.Instance.Track(process);

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
    }

    private static bool Matches(HookConfig hook, HookEvent e)
    {
        foreach (var clause in hook.When)
        {
            bool match = true;

            if (!string.Equals(clause.Event, e.Event, StringComparison.OrdinalIgnoreCase))
                match = false;

            if (match && clause.Agent is not null &&
                !string.Equals(clause.Agent, e.Agent, StringComparison.OrdinalIgnoreCase))
                match = false;

            if (match && clause.Tool is not null &&
                !string.Equals(clause.Tool, e.Tool, StringComparison.OrdinalIgnoreCase))
                match = false;

            if (match) return true;
        }

        return false;
    }
}