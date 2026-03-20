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
    /// Enqueue a hook event for evaluation. Non-blocking, never throws.
    /// Safe to call from MuxConsole before Start().
    /// </summary>
    public static void Enqueue(HookEvent e) => Channel.Writer.TryWrite(e);

    /// <summary>
    /// Swap the active hook list at runtime (called on /refresh).
    /// </summary>
    public static void UpdateHooks(List<HookConfig> hooks) => _hooks = hooks;

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
        _loopTask = Task.Run(() => HookProcessLoopAsync(_cts.Token));
    }

    public static void Stop()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _cts = null;
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

                var blocking = matched.Where(h => h.Mode == HookMode.Blocking).ToList();
                var async    = matched.Where(h => h.Mode == HookMode.Async).ToList();

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
                FileName               = parts[0],
                Arguments              = parts.Length > 1 ? parts[1] : string.Empty,
                UseShellExecute        = false,
                RedirectStandardInput  = true,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
                CreateNoWindow         = true
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
        if (!string.Equals(hook.When.Event, e.Event, StringComparison.OrdinalIgnoreCase))
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