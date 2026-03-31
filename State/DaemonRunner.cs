using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using MuxSwarm.Utils;

namespace MuxSwarm.State;

/// <summary>
/// Manages daemon triggers: file watchers, cron schedules, and status checks.
/// Each trigger runs as an independent background task. Watch and cron triggers
/// fire goals into the existing orchestrators. Status triggers monitor resources
/// and optionally restart them on failure.
///
/// Start via --daemon CLI flag. Watchdog wraps the process externally.
/// </summary>
public sealed class DaemonRunner : IAsyncDisposable
{
    private readonly DaemonConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = [];
    private readonly ConcurrentDictionary<string, DateTime> _lastFired = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
    private ConcurrentDictionary<string, Process> _bridgeProcesses = new();
    private Func<string, IChatClient>? _chatClientFactory;
    private IList<AITool>? _mcpTools;
    private Dictionary<string, string>? _agentModels;

    private readonly Dictionary<string, Func<Task>> _restartHandlers = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _killed;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public DaemonRunner(DaemonConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Register a restart handler for a resource identifier.
    /// Called when a status check with restart=true fails.
    /// Example: RegisterRestart("http://localhost:6723", () => ServeMode.StartAsync(6723))
    /// </summary>
    public void RegisterRestart(string checkPattern, Func<Task> handler)
    {
        _restartHandlers[checkPattern] = handler;
    }

    public void ForceKill()
    {
        _killed = true;
        _cts.Cancel();
    
        foreach (var (_, proc) in _bridgeProcesses)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }
    }
    
    /// <summary>
    /// Start all trigger loops. Non-blocking -- returns immediately.
    /// </summary>
    public void Start(
        Func<string, IChatClient> chatClientFactory,
        IList<AITool> mcpTools,
        Dictionary<string, string> agentModels)
    {
        _chatClientFactory = chatClientFactory;
        _mcpTools = mcpTools;
        _agentModels = agentModels;

        var ct = _cts.Token;

        MuxConsole.WriteInfo($"[Daemon] Starting {_config.Triggers.Count} trigger(s)...");

        HookWorker.Enqueue(new HookEvent
        {
            Event = "daemon_start",
            Summary = $"{_config.Triggers.Count} triggers",
            Timestamp = DateTimeOffset.UtcNow
        });

        foreach (var trigger in _config.Triggers)
        {
            var worker = trigger.Type.ToLowerInvariant() switch
            {
                "watch" => RunWatchLoop(trigger, ct),
                "cron" => RunCronLoop(trigger, ct),
                "status" => RunStatusLoop(trigger, ct),
                "bridge" => RunBridgeLoop(trigger, ct),
                _ => LogUnknownTrigger(trigger)
            };

            _workers.Add(worker);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        
        foreach (var (_, proc) in _bridgeProcesses)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }
        
        HookWorker.Enqueue(new HookEvent
        {
            Event = "daemon_stop",
            Timestamp = DateTimeOffset.UtcNow
        });

        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            foreach (var (_, proc) in _bridgeProcesses)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { }
            }
        }
        
        foreach (var (_, proc) in _bridgeProcesses)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }
        

        _cts.Dispose();
    }
    

    private async Task RunWatchLoop(DaemonTrigger trigger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(trigger.Path))
        {
            MuxConsole.WriteWarning($"[Daemon:{trigger.Id}] Watch trigger has no path configured.");
            return;
        }

        var directory = Path.GetDirectoryName(trigger.Path);
        var filter = Path.GetFileName(trigger.Path);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(filter))
        {
            MuxConsole.WriteWarning($"[Daemon:{trigger.Id}] Invalid watch path: {trigger.Path}");
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[Daemon:{trigger.Id}] Cannot create watch directory: {ex.Message}");
            return;
        }

        var pendingFiles = new ConcurrentQueue<string>();
        var fileSignal = new SemaphoreSlim(0);

        using var watcher = new FileSystemWatcher(directory, filter)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };

        void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            pendingFiles.Enqueue(e.FullPath);
            try { fileSignal.Release(); } catch { /* disposed */ }
        }

        watcher.Created += OnFileEvent;
        watcher.Changed += OnFileEvent;

        MuxConsole.WriteSuccess($"[Daemon:{trigger.Id}] Watching: {trigger.Path} (cooldown {trigger.EffectiveInterval}s)");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await fileSignal.WaitAsync(ct);

                var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (pendingFiles.TryDequeue(out var file))
                    batch.Add(file);

                foreach (var filePath in batch)
                {
                    var cooldownKey = $"{trigger.Id}:{filePath}";
                    if (_lastFired.TryGetValue(cooldownKey, out var lastFire)
                        && (DateTime.UtcNow - lastFire).TotalSeconds < trigger.EffectiveInterval)
                    {
                        continue;
                    }

                    _lastFired[cooldownKey] = DateTime.UtcNow;

                    await Task.Delay(500, ct);

                    var goal = SubstituteGoalTemplate(
                        trigger.Goal ?? "Process file: {file}",
                        new Dictionary<string, string>
                        {
                            ["{file}"] = filePath,
                            ["{filename}"] = Path.GetFileName(filePath),
                            ["{timestamp}"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            ["{id}"] = trigger.Id
                        });

                    MuxConsole.WriteInfo($"[Daemon:{trigger.Id}] File trigger: {Path.GetFileName(filePath)}");

                    HookWorker.Enqueue(new HookEvent
                    {
                        Event = "daemon_trigger",
                        Agent = trigger.Agent ?? "Daemon",
                        Summary = $"watch:{trigger.Id}",
                        Text = goal,
                        Timestamp = DateTimeOffset.UtcNow
                    });

                    await FireGoal(trigger, goal, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"[Daemon:{trigger.Id}] Watch loop error: {ex.Message}");
        }
    }

    private async Task RunCronLoop(DaemonTrigger trigger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(trigger.Schedule))
        {
            MuxConsole.WriteWarning($"[Daemon:{trigger.Id}] Cron trigger has no schedule.");
            return;
        }

        var cron = CronExpression.Parse(trigger.Schedule);
        if (cron is null)
        {
            MuxConsole.WriteWarning($"[Daemon:{trigger.Id}] Invalid cron expression: {trigger.Schedule}");
            return;
        }

        MuxConsole.WriteSuccess($"[Daemon:{trigger.Id}] Cron scheduled: {trigger.Schedule}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var next = cron.GetNextOccurrence(now);

                if (next is null)
                {
                    MuxConsole.WriteWarning($"[Daemon:{trigger.Id}] No future occurrence found. Stopping.");
                    break;
                }

                var delay = next.Value - now;
                MuxConsole.WriteMuted($"[Daemon:{trigger.Id}] Next fire: {next.Value:HH:mm:ss} ({delay.TotalMinutes:F1}m)");

                await Task.Delay(delay, ct);

                var goal = SubstituteGoalTemplate(
                    trigger.Goal ?? "Scheduled task",
                    new Dictionary<string, string>
                    {
                        ["{timestamp}"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["{id}"] = trigger.Id
                    });

                MuxConsole.WriteInfo($"[Daemon:{trigger.Id}] Cron fired: {trigger.Schedule}");

                HookWorker.Enqueue(new HookEvent
                {
                    Event = "daemon_trigger",
                    Agent = trigger.Agent ?? "Daemon",
                    Summary = $"cron:{trigger.Id}",
                    Text = goal,
                    Timestamp = DateTimeOffset.UtcNow
                });

                await FireGoal(trigger, goal, ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"[Daemon:{trigger.Id}] Cron loop error: {ex.Message}");
        }
    }
    

    private async Task RunStatusLoop(DaemonTrigger trigger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(trigger.Check))
        {
            MuxConsole.WriteWarning($"[Daemon:{trigger.Id}] Status trigger has no check configured.");
            return;
        }

        var interval = trigger.EffectiveInterval > 0 ? trigger.EffectiveInterval : 30;
        MuxConsole.WriteSuccess(
            $"[Daemon:{trigger.Id}] Status check: {trigger.Check} (every {interval}s, restart={trigger.Restart})");

        _consecutiveFailures[trigger.Id] = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);

                var (healthy, detail) = await RunHealthCheck(trigger.Check, ct);

                if (healthy)
                {
                    var prev = _consecutiveFailures.GetValueOrDefault(trigger.Id, 0);
                    if (prev > 0)
                    {
                        MuxConsole.WriteSuccess($"[Daemon:{trigger.Id}] Recovered after {prev} failure(s).");

                        HookWorker.Enqueue(new HookEvent
                        {
                            Event = "daemon_status",
                            Summary = $"recovered:{trigger.Id}",
                            Text = detail,
                            Timestamp = DateTimeOffset.UtcNow
                        });
                    }

                    _consecutiveFailures[trigger.Id] = 0;
                    continue;
                }

                // Failure path
                var failures = _consecutiveFailures.AddOrUpdate(trigger.Id, 1, (_, c) => c + 1);

                if (trigger.FailThreshold > 0 && failures < trigger.FailThreshold)
                {
                    MuxConsole.WriteMuted(
                        $"[Daemon:{trigger.Id}] Check failed ({failures}/{trigger.FailThreshold}): {detail}");
                    continue;
                }

                MuxConsole.WriteWarning(
                    $"[Daemon:{trigger.Id}] UNHEALTHY ({failures} consecutive): {detail}");

                HookWorker.Enqueue(new HookEvent
                {
                    Event = "daemon_status",
                    Summary = $"unhealthy:{trigger.Id}",
                    Text = detail,
                    Timestamp = DateTimeOffset.UtcNow
                });

                if (!trigger.Restart) continue;

                // Attempt restart
                MuxConsole.WriteInfo($"[Daemon:{trigger.Id}] Attempting restart...");

                bool restarted = false;
                if (_restartHandlers.TryGetValue(trigger.Check!, out var handler))
                {
                    try
                    {
                        await handler();
                        restarted = true;
                        MuxConsole.WriteSuccess($"[Daemon:{trigger.Id}] Restart successful.");
                    }
                    catch (Exception ex)
                    {
                        MuxConsole.WriteError($"[Daemon:{trigger.Id}] Restart failed: {ex.Message}");
                    }
                }
                else
                {
                    MuxConsole.WriteWarning(
                        $"[Daemon:{trigger.Id}] No restart handler registered for: {trigger.Check}");
                }

                HookWorker.Enqueue(new HookEvent
                {
                    Event = "daemon_status",
                    Summary = restarted ? $"restarted:{trigger.Id}" : $"restart_failed:{trigger.Id}",
                    Timestamp = DateTimeOffset.UtcNow
                });

                if (restarted)
                    _consecutiveFailures[trigger.Id] = 0;
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"[Daemon:{trigger.Id}] Status loop error: {ex.Message}");
        }
    }

    private async Task RunBridgeLoop(DaemonTrigger trigger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(trigger.Command))
        {
            MuxConsole.WriteWarning($"[Daemon:{trigger.Id}] Bridge has no command configured.");
            return;
        }

        var restartDelay = trigger.EffectiveInterval > 0 ? trigger.EffectiveInterval : 5;

        MuxConsole.WriteSuccess(
            $"[Daemon:{trigger.Id}] Bridge: {trigger.Command} (restart={trigger.Restart}, delay={restartDelay}s)");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Process? proc = null;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = trigger.Command,
                        Arguments = trigger.Args ?? "",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = PlatformContext.BaseDirectory
                    };
                    
                    //handle expected defaults  
                    if (!psi.EnvironmentVariables.ContainsKey("MUX_WS_URL"))
                        psi.EnvironmentVariables["MUX_WS_URL"] = $"ws://localhost:{App.ServePort}/ws";
                    
                    // Merge env vars from trigger
                    if (trigger.Env is { Count: > 0 })
                    {
                        foreach (var (k, v) in trigger.Env)
                            psi.EnvironmentVariables[k] = v;
                    }

                    proc = Process.Start(psi);
                    if (proc == null)
                    {
                        MuxConsole.WriteError($"[Daemon:{trigger.Id}] Failed to start bridge process.");
                        break;
                    }

                    _bridgeProcesses[trigger.Id] = proc;

                    MuxConsole.WriteSuccess($"[Daemon:{trigger.Id}] Bridge started (PID {proc.Id})");

                    HookWorker.Enqueue(new HookEvent
                    {
                        Event = "daemon_bridge",
                        Summary = $"started:{trigger.Id}",
                        Text = $"PID {proc.Id}",
                        Timestamp = DateTimeOffset.UtcNow
                    });

                    // Pipe stdout/stderr to log
                    _ = Task.Run(() => PipeLogs(trigger.Id, proc.StandardOutput, "OUT", ct), ct);
                    _ = Task.Run(() => PipeLogs(trigger.Id, proc.StandardError, "ERR", ct), ct);

                    // Wait for exit or cancellation
                    await proc.WaitForExitAsync(ct);

                    var exitCode = proc.ExitCode;
                    MuxConsole.WriteWarning(
                        $"[Daemon:{trigger.Id}] Bridge exited (code {exitCode})");

                    HookWorker.Enqueue(new HookEvent
                    {
                        Event = "daemon_bridge",
                        Summary = $"exited:{trigger.Id}",
                        Text = $"Exit code {exitCode}",
                        Timestamp = DateTimeOffset.UtcNow
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    MuxConsole.WriteError($"[Daemon:{trigger.Id}] Bridge error: {ex.Message}");
                }
                finally
                {
                    if (proc is { HasExited: false })
                    {
                        try { proc.Kill(entireProcessTree: true); }
                        catch { /* best effort */ }
                    }
                    _bridgeProcesses.TryRemove(trigger.Id, out _);
                }
                
                if (!trigger.Restart || ct.IsCancellationRequested || _killed)
                    break;
                
                MuxConsole.WriteInfo(
                    $"[Daemon:{trigger.Id}] Restarting bridge in {restartDelay}s...");
               
                await Task.Delay(TimeSpan.FromSeconds(restartDelay), ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"[Daemon:{trigger.Id}] Bridge loop fatal: {ex.Message}");
        }
    }

    private static async Task PipeLogs(string id, StreamReader reader, string prefix, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                MuxConsole.WriteMuted($"[Bridge:{id}:{prefix}] {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch { /* stream closed */ }
    }

    private static async Task<(bool Healthy, string Detail)> RunHealthCheck(
        string check, CancellationToken ct)
    {
        try
        {
            // HTTP/HTTPS -- HEAD request
            if (check.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                check.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, check);
                using var response = await HttpClient.SendAsync(request, ct);
                return response.IsSuccessStatusCode
                    ? (true, $"{(int)response.StatusCode} {response.ReasonPhrase}")
                    : (false, $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            // Process -- "process:ffplay"
            if (check.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
            {
                var processName = check["process:".Length..].Trim();
                var processes = Process.GetProcessesByName(processName);
                return processes.Length > 0
                    ? (true, $"{processes.Length} instance(s) running")
                    : (false, $"No '{processName}' process found");
            }

            // TCP -- "tcp:localhost:5432"
            if (check.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = check["tcp:".Length..].Split(':', 2);
                if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
                    return (false, $"Invalid tcp check format: {check}");

                using var tcpClient = new System.Net.Sockets.TcpClient();
                var connectTask = tcpClient.ConnectAsync(parts[0], port, ct).AsTask();
                var completed = await Task.WhenAny(connectTask, Task.Delay(5000, ct));

                return completed == connectTask && tcpClient.Connected
                    ? (true, $"TCP {parts[0]}:{port} open")
                    : (false, $"TCP {parts[0]}:{port} unreachable");
            }

            return (false, $"Unknown check type: {check}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
    
    private async Task FireGoal(DaemonTrigger trigger, string goal, CancellationToken ct)
    {
        if (_chatClientFactory is null || _mcpTools is null || _agentModels is null)
        {
            MuxConsole.WriteWarning(
                $"[Daemon:{trigger.Id}] Cannot fire goal -- dependencies not initialized.");
            return;
        }

        try
        {
            switch (trigger.Mode.ToLowerInvariant())
            {
                case "swarm":
                    await MultiAgentOrchestrator.RunAsync(
                        chatClientFactory: _chatClientFactory,
                        mcpTools: _mcpTools.Cast<AITool>().ToList(),
                        agentModels: _agentModels,
                        incomingGoal: goal,
                        cancellationToken: ct);
                    break;

                case "pswarm":
                    await ParallelSwarmOrchestrator.RunAsync(
                        chatClientFactory: _chatClientFactory,
                        mcpTools: _mcpTools.Cast<AITool>().ToList(),
                        agentModels: _agentModels,
                        incomingGoal: goal,
                        cancellationToken: ct);
                    break;

                case "agent":
                default:
                    if (!string.IsNullOrEmpty(trigger.Agent))
                    {
                        var agentDefs = Common.GetAgentDefinitions(PlatformContext.SwarmPath);
                        var matched = agentDefs.FirstOrDefault(d =>
                            d.Name.Equals(trigger.Agent, StringComparison.OrdinalIgnoreCase));
                        if (matched != null)
                            SingleAgentOrchestrator.AgentDef = matched;
                    }

                    var modelId = !string.IsNullOrEmpty(trigger.Agent)
                        ? _agentModels.GetValueOrDefault(trigger.Agent, _agentModels["Orchestrator"])
                        : _agentModels.GetValueOrDefault(
                            SingleAgentOrchestrator.AgentDef?.Name ?? "Orchestrator",
                            _agentModels["Orchestrator"]);

                    await SingleAgentOrchestrator.ChatAgentAsync(
                        client: _chatClientFactory(modelId),
                        cancellationToken: ct,
                        mcpTools: _mcpTools
                            .Cast<ModelContextProtocol.Client.McpClientTool>().ToList(),
                        chatClientFactory: _chatClientFactory,
                        incomingGoal: goal);
                    break;
            }

            MuxConsole.WriteSuccess($"[Daemon:{trigger.Id}] Goal completed.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"[Daemon:{trigger.Id}] Goal execution failed: {ex.Message}");
        }
    }
    
    private static string SubstituteGoalTemplate(
        string template, Dictionary<string, string> vars)
    {
        var result = template;
        foreach (var (key, value) in vars)
            result = result.Replace(key, value, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static Task LogUnknownTrigger(DaemonTrigger trigger)
    {
        MuxConsole.WriteWarning($"[Daemon:{trigger.Id}] Unknown trigger type: {trigger.Type}");
        return Task.CompletedTask;
    }
}