using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using MuxSwarm.State;
using MuxSwarm.Utils;
using OpenAI;
using static MuxSwarm.Setup.Setup;

namespace MuxSwarm;

public class App
{
    public static readonly string Version = "0.12.3";
    /// <summary>Local debug/build tag shown next to the version on the splash. Empty string = release (no tag rendered). Bump per local test build.</summary>
    public static readonly string DebugTag = "";
    
    private static readonly string BaseDir = PlatformContext.BaseDirectory;
    public static readonly string ConfigPath = PlatformContext.ConfigPath;
    
    private static bool _showToolCallResults;
    public static IList<McpClientTool>? McpTools;
    private static string? _cliModelOverride;
    private static bool _watchDogEnabled;
    private static bool _verboseToggle;
    private static bool VerboseInit => _verboseToggle || Debugger.IsAttached || string.Equals(Environment.GetEnvironmentVariable("MUXSWARM_VERBOSE"), "1", StringComparison.OrdinalIgnoreCase);
    private static bool _mcpStrictMode = !string.Equals(Environment.GetEnvironmentVariable("MUXSWARM_MCP_STRICT"), "0", StringComparison.OrdinalIgnoreCase);
    private static CancellationTokenSource _cts = new();
    private static readonly Lock CtsLock = new();
    public static int ServePort;
    public static DaemonRunner? DaemonRunner;

    public static readonly Dictionary<string, McpClient> McpClients = new();
    public static AppConfig Config = new();
    public static SwarmConfig? SwarmConfig = new();
    public static ProviderConfig? ActiveProvider;
    public static int MaxDegreeParallelism = 4;
    
    protected static bool ContinuousExec;
    protected static int MinContDelay = 300;
    protected static bool ShouldPlan = false;
    // /ultra: composite deep-reasoning toggle (plan + max reasoning + ultra steering).
    // Public so orchestrators and /api/status can read it without threading a new param.
    public static bool UltraMode = false;
    private static bool _ultraPriorPlan = false;
    private static bool _ultraPriorParaSub = false;
    // v0.12.0 M6 Giga mode: a superset of /ultra that also grants dynamic orchestration tools
    // (spawn_team / run_team / write_workflow / run_workflow). Public so orchestrators + /api can read it.
    public static bool GigaMode = false;
    private static bool _gigaPriorPlan = false;
    private static bool _gigaPriorParaSub = false;
    private static bool _gigaPriorUltra = false;

    // Interactive render-mode preference from the CLI (--classic / --tui). Null = use
    // console.renderMode config (default "auto"). Never affects stdio/serve output.
    private static string? _cliRenderModeOverride = null;
    /// <summary>Read-only view of plan mode for the serve layer (/api/status).</summary>
    public static bool PlanMode => ShouldPlan;
    public static bool ParallelSubAgentsMode => AllowParallelSubAgents;
    public static bool SubAgentsMode => AllowSubagents;
    
    //Refers to single agent mode only for ephemeral sub-tasks, swarm and parallel swarm modes utilize multiple agents by default. 
    protected static bool AllowSubagents = false;
    protected static bool AllowParallelSubAgents = false;
    
    // Background MCP server initialization; awaited lazily before first tool use.
    protected static Task<bool>? McpInitTask;
    private static int _mcpReadyGate;

    private static CancellationTokenSource GetOrResetCts()
    {
        lock (CtsLock)
        {
            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
            return _cts;
        }
    }

    /// <summary>
    /// Drive a detachable interactive session until it either PARKS (the user typed /detach inside
    /// it) or FINISHES (quit/completed/error). The session's ChatAgentAsync runs as
    /// <c>handle.ChatTask</c>; we race it against the handle's detach signal so the menu reclaims
    /// the single console reader the instant the frame parks - the frame stays alive (its whole
    /// closure preserved) awaiting a later /attach. Returns true when the session is DONE (already
    /// removed from the registry), false when it merely parked and remains attachable. Single
    /// console reader at all times: this method only returns once the parked frame has stopped
    /// reading (it is blocked on the attach gate) or the task has completed.
    /// </summary>
    private static async Task<bool> PumpSessionAsync(MuxSwarm.Utils.InteractiveSession handle)
    {
        var winner = await Task.WhenAny(handle.ChatTask!, handle.WaitForDetachAsync());
        if (winner == handle.ChatTask)
        {
            try { await handle.ChatTask!; }
            catch (OperationCanceledException) { /* quit/cancel is a normal session end */ }
            MuxSwarm.Utils.InteractiveSessionRegistry.Remove(handle);
            return true;
        }
        // Parked: the frame is blocked on its attach gate; the menu owns the console again.
        return false;
    }

    // Awaits the background MCP init (idempotent). On total connection failure,
    // reports the error and exits — preserving the original strict/non-strict
    // semantics, just deferred from startup to first tool use.
    private static async Task EnsureMcpReadyAsync()
    {
        var task = McpInitTask;
        if (task == null)
            return;

        bool ok = await task;

        // Only evaluate the failure/exit path once.
        if (Interlocked.Exchange(ref _mcpReadyGate, 1) != 0)
            return;

        if (!ok)
        {
            MuxConsole.WriteError(_mcpStrictMode
                ? "Failed to connect to all enabled MCP servers (strict mode). Exiting."
                : "Failed to connect to any MCP servers. Exiting.");

            OtelLogger.Error(_mcpStrictMode
                ? "Failed to connect to all enabled MCP servers (strict mode). Exiting."
                : "Failed to connect to any MCP servers. Exiting.");

            Environment.Exit(1);
        }

        OtelLogger.Info(_mcpStrictMode
            ? "Established connection to all enabled MCP servers."
            : "Established connection to at least one MCP server.");
    }

    public App()
    {
        _ = ProcessCleanup.Instance;

        //Ensure processes mux spawns are killed
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = false;
            MuxConsole.WriteInfo("Exiting...");
            HookWorker.Stop();
            ProcessCleanup.Instance.Shutdown();
            Environment.Exit(130);
        };
        
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            HookWorker.Stop();
            DaemonRunner?.DisposeAsync();
            OtelTracer.Shutdown();
            OtelMetrics.Shutdown();
        };
        
        System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, _ =>
            {
                Process.GetCurrentProcess().Kill();
            });
        
        var hbPath = Path.Combine(BaseDir, "watchdog.heartbeat");
        if (File.Exists(hbPath))
            File.WriteAllText(hbPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());

        Config = LoadConfig(ConfigPath);


        MuxConsole.WriteSplashScreen(version: Version, debugTag: DebugTag);

        if (!Config.SetupCompleted)
        {
            MuxConsole.WriteWarning("Setup has not been completed yet. Let's get you configured...");

            if (!RunSetup())
            {
                MuxConsole.WriteError("Setup failed or was aborted. Exiting.");
                Environment.Exit(1);
            }

            Config = LoadConfig(ConfigPath);
        }
        
        Activity? startupSpan = null;
        
        if (OtelTracer.TryInit())
        {
            OtelMetrics.TryInit();
            startupSpan = OtelTracer.GetSource().StartActivity("runtime_startup");
            MuxConsole.WriteSuccess($"Telemetry is enabled, endpoint: {Config.Telemetry.Endpoint}");
        }
        
        //Shouldnt be null
        SwarmConfig = LoadSwarm();

        //Load and populate exec limits from swarm cfg
        FetchSetExecLimits();
        var limits = ExecutionLimits.Current;
        
        OtelLogger.Info($"Agent Configuration Limits - Activity Timeout Seconds: {limits.ActivityTimeoutSeconds}, Compaction Char Budget: {limits.CompactionCharBudget}, " +
                        $"Max Chars Per Compacted Msg: {limits.CompactionMaxMessageChars}, Cross Agent Context Budget: {limits.CrossAgentContextBudget}, " +
                        $"Max Orchestrator Iterations: {limits.MaxOrchestratorIterations}, Max Stuck Count: {limits.MaxStuckCount}, " +
                        $"Max Sub-Agent Iterations: {limits.MaxSubAgentIterations}, Max Sub-Task Retries{limits.MaxSubTaskRetries}, " +
                        $"Progress Entry Budget: {limits.ProgressEntryBudget}, Progress Log Total Budget: {limits.ProgressLogTotalBudget}");

        // Startup prune of spilled sub-agent delegation raw older than the retention window
        // (size-tiered context passing). Best-effort; never blocks startup.
        try { MuxSwarm.Utils.DelegationStore.PruneOldRetention(limits.DelegationRetentionDays); } catch { }

        InitLlmProvider();
        SkillLoader.LoadSkills();
        
        // Hooks confirm BEFORE the MCP kickoff: HookWorker.Start shows a blocking
        // Confirm prompt when hooks are configured (interactive path), and putting it
        // after the MCP fan-out landed the prompt in the middle of the subprocess spawn
        // storm - key handling turned sluggish and startup felt slow. On a quiet machine
        // the prompt is instant; MCP init still overlaps everything after it and is
        // awaited lazily via EnsureMcpReadyAsync() before the first tool use.
        HookWorker.Start(SwarmConfig?.Hooks ?? []);
        OtelLogger.Info("Hook Worker Started");

        // Outbound webhook sinks (Mux -> external). Inert unless swarm.json defines webhooks[].
        WebhookSink.Start(SwarmConfig?.Webhooks);

        // Kick off MCP server connections in the background so the user is
        // dropped into the interactive prompt immediately. Connection results
        // are awaited lazily via EnsureMcpReadyAsync() before the first tool use.
        //
        // Task.Run so the SYNCHRONOUS prelude of InitMcpServersAsync (config patching +
        // setting up ~14 subprocess connections before the first real await yields) runs on a
        // background thread too. Calling it directly stalled startup ~700ms before the kickoff
        // returned; this makes time-to-prompt effectively instant.
        McpInitTask = Task.Run(() => InitMcpServersAsync(Config));
        startupSpan?.Dispose();
    }

    public static IList<McpClientTool>? GetMcpTools() => McpTools;
    
    public async Task<int> Run(string[] args)
    {
        try
        {
            return await AppLoop(args);
        }
        catch (Exception ex)
        {
            MuxConsole.WriteError(ex.Message);
            return await Task.FromResult(ex.HResult);
        }
    }

    private async Task<int> AppLoop(string[] args)
    {
        // Prepend persisted startup args (config.startupArgs) before the real argv so the
        // user can boot straight into a mode/agent every run (e.g. "--agent CodeAgent --giga").
        // Real CLI flags come AFTER and therefore win on any single-valued override. Set via
        // /startargs. Machine transports (--stdio/--serve/--acp) ignore startup mode entry.
        // Capture argv for a possible in-process restart (POST /api/restart, post-update relaunch),
        // then apply any staged update binary + honor a predecessor-wait handshake from a relaunch.
        MuxSwarm.State.Relauncher.OriginalArgs = args;
        MuxSwarm.State.SelfUpdater.ApplyStagedBinaryIfPresent();
        args = MuxSwarm.State.Relauncher.WaitForPredecessorAndStrip(args);
        MuxSwarm.State.Relauncher.OriginalArgs = args;

        args = MergeStartupArgs(Config.StartupArgs, args);
        var parsed = ParseArgs(args);

        // --update : do-and-exit self-update from the latest GitHub release.
        if (parsed.UpdateMode)
        {
            var (staged, msg) = await MuxSwarm.State.SelfUpdater.RunAsync(line => MuxConsole.WriteInfo(line));
            MuxConsole.WriteInfo(msg);
            if (staged)
            {
                MuxConsole.WriteWarning("Mux-Swarm must restart to finish applying the update. Restarting...");
                MuxSwarm.State.Relauncher.RestartNow(() => MuxConsole.DisableDockedFooter());
            }
            return 0;
        }

        // Resolve the interactive render mode (G1/G10). CLI flag (--classic/--tui) wins over
        // console.renderMode config; default "auto" is capability-aware. No effect on the
        // stdio/serve path — MuxConsole.RenderMode reports Stdio whenever StdioMode is set.
        MuxConsole.ResolveRenderMode(_cliRenderModeOverride ?? Config.Console.RenderMode);
        Theme.Set(Theme.Find(Config.Console.Theme) ?? Theme.Default);
        MuxConsole.ToolOutputCompact = !string.Equals(Config.Console.ToolOutput, "full", StringComparison.OrdinalIgnoreCase);
        MuxConsole.DockedFooterEnabled = Config.Console.DockedFooter;
        MuxConsole.FrameEngineEnabled = string.Equals(Config.Console.RenderEngine, "frame", StringComparison.OrdinalIgnoreCase);
        MuxConsole.CollapseToolLines = Config.Console.CollapseToolLines;
        MuxConsole.DelegationSpacing = Config.Console.DelegationSpacing;
        MuxConsole.ScrollSpeedRows = Config.Console.ScrollSpeedRows;
        MuxConsole.CollapseSubAgents = Config.Console.CollapseSubAgents;
        MuxConsole.CollapseDaemonOutput = Config.Console.CollapseDaemon;
        MuxConsole.InputHighlight = Config.Console.InputHighlight;
        MuxConsole.ContentBackgrounds = Config.Console.ContentBackgrounds;
        MuxConsole.CardMarkdown = Config.Console.CardMarkdown;
        MuxConsole.CollapseDelegations = Config.Console.CollapseDelegations;
        MuxConsole.BracketedPaste = Config.Console.BracketedPaste;
        MuxConsole.ShowReasoning = Config.ShowReasoning;

        // Item 5: startup char-cap check for BRAIN.md / MEMORY.md (interactive only). Startup is
        // warn-only by design - we never silently rewrite context files on boot. A configured
        // "force" mode still surfaces as a warning here; the actual force-rewrite happens on the
        // next mutation (e.g. a /tag MEMORY stub). Stdio/serve skip this entirely.
        if (!MuxConsole.StdioMode)
        {
            try
            {
                await ContextCap.CheckFileAsync(ContextCap.BrainFile);
                await ContextCap.CheckFileAsync(ContextCap.MemoryFile);
            }
            catch { /* cap check is best-effort */ }

            // Item 5: optional background prune pulse. Opt-in (contextLimits.prunePulseSeconds > 0
            // AND a file in "force" mode); first tick +30s, then every N seconds. Surfaces a status
            // line ONLY when a rewrite actually fires. Interactive only - never stdio/serve/acp.
            if (!parsed.AcpMode && parsed.ServePort <= 0 && ContextCap.ShouldPulse())
            {
                try
                {
                    var swarm = App.SwarmConfig;
                    string? pulseModel = swarm?.CompactionAgent?.Model;
                    if (string.IsNullOrWhiteSpace(pulseModel))
                    {
                        var models = Common.LoadAgentModels();
                        pulseModel = models.Values.FirstOrDefault();
                    }
                    if (!string.IsNullOrWhiteSpace(pulseModel))
                        ContextCap.StartPulse(modelId => CreateChatClient(modelId), pulseModel);
                }
                catch { /* pulse is best-effort */ }
            }
        }

        
        if (_watchDogEnabled)
            Common.StartExternalWatchdog(args: args, baseDir: BaseDir, cts: new CancellationTokenSource());
        
        if (parsed.ServePort > 0)
            await ServeMode.StartAsync((int)parsed.ServePort);

        if (parsed.AcpMode)
        {
            // ACP owns stdin (JSON-RPC line transport) and drives the single-agent REPL
            // headlessly. Start the cancel monitor PAUSED so session/cancel can abort a turn
            // without the monitor's background reader also consuming ACP's stdin.
            //
            // MCP init is NOT awaited here: it runs in the background (McpInitTask) and is
            // awaited lazily inside the session loop right before the first turn needs tools.
            // This lets the ACP handshake (initialize / session/new) respond in milliseconds
            // instead of blocking ~15s on MCP subprocess spawns.
            StdinCancelMonitor.Start(startPaused: true);
            return await RunAcpAsync();
        }

        if (MuxConsole.StdioMode)
            StdinCancelMonitor.Start();

        if (parsed.DockerExecOverride.HasValue)
        {
            Config.IsUsingDockerForExec = parsed.DockerExecOverride.Value;
            MuxConsole.WriteInfo($"Docker Exec set to: {Config.IsUsingDockerForExec}");
            OtelLogger.Info($"Docker Exec set to: {Config.IsUsingDockerForExec}");
            // Skills were loaded in the ctor with the pre-flag (default) value, so reload now that
            // --dockerexec has flipped it - otherwise the bundled-docker skill set (incl. the
            // docker-sandbox directive skill) is not active for this run.
            SkillLoader.LoadSkills();
        }

        if (_startupSandboxBackend is { } sbxBackend)
        {
            // --sandbox <backend> applied at startup: validate, set the config backend, sync the
            // docker-exec flag + reload skills so the directive set matches. Invalid backend warns
            // and leaves host execution intact (no silent half-apply).
            var candidate = new SandboxConfig
            {
                Backend = sbxBackend, Image = Config.Sandbox.Image, Network = Config.Sandbox.Network,
                AllowedDomains = Config.Sandbox.AllowedDomains, Command = Config.Sandbox.Command,
            };
            var sbxErr = MuxSwarm.Utils.NativeTools.SandboxBackend.Validate(candidate);
            if (sbxErr is null)
            {
                Config.Sandbox = candidate;
                Config.IsUsingDockerForExec = sbxBackend.Trim().ToLowerInvariant() is not ("host" or "none" or "");
                SkillLoader.LoadSkills();
                MuxConsole.WriteInfo($"Sandbox backend set to: {sbxBackend}");
            }
            else
            {
                MuxConsole.WriteWarning($"--sandbox {sbxBackend} ignored: {sbxErr}");
            }
        }

        // Resolve the authoritative sandbox state ONCE after all startup overrides (--sandbox,
        // --dockerexec, config) have settled, so the preamble's ACTIVE block tracks the real backend.
        MuxSwarm.Utils.NativeTools.SandboxRuntime.Refresh();

        if (parsed.McpStrictOverride.HasValue)
        {
            _mcpStrictMode = parsed.McpStrictOverride.Value;
            MuxConsole.WriteInfo($"MCP Strict Mode set to: {_mcpStrictMode}");
            OtelLogger.Info($"MCP Strict Mode set to: {_mcpStrictMode}");
        }
        
        HookWorker.Enqueue(new HookEvent
        {
            Event = "runtime_ready",
            Text = "Mux Swarm Runtime Startup Has Completed",
            Timestamp = DateTimeOffset.UtcNow
        });
        
        OtelLogger.Info("Runtime Ready Event Fired, Startup Completed Successfully");
        
        if (!string.IsNullOrWhiteSpace(parsed.Goal))
        {
            await EnsureMcpReadyAsync();
            return await HandleParsedRun(parsed);
        }

        if (parsed.ReportAll || parsed.ReportSessionId != null)
        {
            CliCmdUtils.GenerateSessionReports(parsed.ReportSessionId);
            OtelLogger.Info("Generated Session Reports From parsed --report arg");
            return Environment.ExitCode;
        }
        
        if (parsed.DaemonMode && Config.Daemon is { Enabled: true })
        {
            DaemonRunner = new DaemonRunner(Config.Daemon);
            
            if (ServePort > 0)
            {
                foreach (var trigger in Config.Daemon.Triggers
                             .Where(t => t.Type == "status" && t.Restart &&
                                         t.Check != null && t.Check.Contains($":{ServePort}")))
                {
                    DaemonRunner.RegisterRestart(trigger.Check!,
                        () => ServeMode.StartAsync(ServePort));
                }
            }

            await EnsureMcpReadyAsync();
            DaemonRunner.Start(
                chatClientFactory: modelId => CreateChatClient(modelId),
                mcpTools: McpTools!.Cast<AITool>().ToList(),
                agentModels: Common.LoadAgentModels());

            MuxConsole.WriteInfo("[Daemon] Running in background.");
            OtelLogger.Info("Daemon enabled and initialized successfully in background");
        }
        
        if (OnboardRequested)
        {
            OnboardRequested = false;
            await EnsureMcpReadyAsync();
            var onboardModel = LoadSingleAgentModel();
            var onboardCts = GetOrResetCts();
            await CliCmdUtils.HandleOnboard(
                chatClientFactory: modelId => CreateChatClient(modelId),
                singleAgentModel: onboardModel,
                mcpTools: McpTools,
                ct: onboardCts.Token
            );
        }
        
        OtelLogger.Info("Entered Main Interactive Loop");
        // The live-region TUI driver (pinned footer + as-you-type slash palette) is active
        // across the WHOLE interactive REPL - both this top-level mode-select menu and inside
        // agent/swarm sessions. At the menu it runs in "top-level" palette scope (mode-select
        // commands); sessions switch it to the in-session command set. No-op outside TUI.
        MuxConsole.EnableDockedFooter(topLevel: true);
        // Interactive loop
        while (!Environment.HasShutdownStarted)
        {
            // Re-assert top-level scope each iteration: a returning session left it in the
            // in-session scope, and the meter should read "ready" at the menu. Idempotent.
            MuxConsole.EnableDockedFooter(topLevel: true);

            // Slash-anywhere hand-off: a session the user just exited may have queued a
            // REPL-only command (they typed it in-session and confirmed ending the session to
            // run it). Dispatch it now as if typed at the menu, then clear it.
            string? pendingFromSession = SingleAgentOrchestrator.PendingReplCommand;
            if (pendingFromSession is not null)
                SingleAgentOrchestrator.PendingReplCommand = null;

            // Startup mode entry: a startup flag (--swarm/--pswarm/--stateless/--teams/
            // --agent-mode/--agent <name>) requested booting straight into a mode. Run it once
            // as the first input, exactly as if the user typed it at the menu. Consumed here so
            // it only fires on the very first loop iteration.
            if (_startupCommand is not null && pendingFromSession is null)
            {
                pendingFromSession = _startupCommand;
                _startupCommand = null;
                if (_startupAgentName is not null)
                {
                    var defs = Common.GetAgentDefinitions(PlatformContext.SwarmPath);
                    var matched = defs.FirstOrDefault(d => d.Name.Equals(_startupAgentName, StringComparison.OrdinalIgnoreCase));
                    if (matched is not null) SingleAgentOrchestrator.AgentDef = matched;
                    else MuxConsole.WriteWarning($"Startup agent '{_startupAgentName}' not found; using default.");
                    _startupAgentName = null;
                }
            }

            // When the live-region driver is active it owns the input box (pinned at the
            // bottom) and its own key loop (Esc -> cancel), so skip the inline "> " prompt and
            // the non-blocking Esc pre-check, which would otherwise steal a keystroke.
            if (!MuxConsole.TuiActive)
            {
                MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");

                if (!MuxConsole.StdioMode && !Console.IsInputRedirected && Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        lock (CtsLock)
                        {
                            if (!_cts.IsCancellationRequested)
                            {
                                _cts.Cancel();
                                MuxConsole.WriteInfo("Interrupted.");
                                OtelLogger.Warn("User Deployed Cancel Signal Received, Mux Swarm Interrupted");
                            }
                        }
                        continue;
                    }
                }
            }

            // Back at the top-level menu (no agentic loop active): stop the loop clock. It is
            // (re)started on the next /agent | /stateless | /swarm | /pswarm via the session header.
            if (pendingFromSession is null) MuxConsole.StopTuiLoopClock();
            string? userInput = pendingFromSession ?? MuxConsole.ReadInput();

            if (string.IsNullOrEmpty(userInput))
                continue;

            // Normalize a bare slash command with only trailing whitespace (e.g. "/agent " from
            // Tab-completion) to its exact token, so the exact-match command switch recognizes
            // it. A command WITH an argument (non-space content after the space) is left intact.
            if (userInput.Length > 0 && userInput[0] == '/' && userInput.TrimEnd() != userInput
                && !userInput.Trim().Contains(' '))
                userInput = userInput.Trim();

            if (userInput.Trim() == "__CANCEL__")
            {
                lock (CtsLock)
                {
                    if (!_cts.IsCancellationRequested)
                    {
                        _cts.Cancel();
                        MuxConsole.WriteInfo("Cancelled by client");
                        OtelLogger.Info("Piped Cancel Signal Received, Mux Swarm Interrupted");
                    }
                }
                continue;
            }

            // // Block only here (first real submission) on MCP readiness, so the
            // // prompt itself appears instantly while servers connect in the
            // // background. Idempotent after the first call.
            // await EnsureMcpReadyAsync();

            switch (userInput)
            {
                case "/help":
                    MuxConsole.PrintHelp(Help.HelpText);
                    break;

                case "/shortcuts":
                case "/keys":
                    MuxConsole.PrintShortcuts();
                    break;

                case "/":
                case "/?":
                    // G6: slash-command palette / preview. At the top-level menu the relevant
                    // commands are the mode-select (repl) set, not the in-session set, so show
                    // the repl-scoped palette. Falls back to full help in classic mode.
                    if (MuxConsole.IsTui) MuxConsole.RenderReplSlashPalette();
                    else MuxConsole.PrintHelp(Help.HelpText);
                    break;

                case "/exit":
                    MuxConsole.DisableDockedFooter();
                    MuxConsole.WriteInfo("Shutting down gracefully...");
                    _cts.Cancel();
                    ProcessCleanup.Instance.Dispose();
                    Environment.Exit(0);
                    break;

                case "/setup":
                    bool setupSuccess = RunSetup();
                    if (setupSuccess) MuxConsole.WriteSuccess("Setup complete!");
                    break;

                case var tc when tc == "/teams" || tc.StartsWith("/teams ", StringComparison.Ordinal):
                {
                    await EnsureMcpReadyAsync();
                    Config = LoadConfig(ConfigPath);
                    var teamsArg = tc.Length > "/teams".Length ? tc.Substring("/teams".Length).Trim() : "";
                    var teamsModels = Common.LoadAgentModels();

                    if (teamsArg.Length == 0)
                    {
                        CliCmdUtils.HandleListTeams(SwarmConfig);
                        break;
                    }

                    var teamCfg = MuxSwarm.Utils.Teams.TeamController.Find(SwarmConfig, teamsArg);
                    if (teamCfg is null)
                    {
                        MuxConsole.WriteWarning($"No team named '{teamsArg}' in swarm.json. Run /teams to list configured teams.");
                        break;
                    }

                    var teamsCts = GetOrResetCts();
                    var teamScope = MuxSwarm.Utils.Teams.TeamController.Build(
                        teamCfg, SwarmConfig ?? new SwarmConfig(),
                        modelId => CreateChatClient(modelId), teamsModels, teamsCts.Token);
                    if (teamScope is null) break;

                    var teamLeadModel = teamsModels.GetValueOrDefault(
                        teamScope.LeadDef.Name, LoadSingleAgentModel());

                    ServeMode.ActiveMode = "teams";
                    MuxConsole.StartTuiLoopClock();   // begin the loop clock for this agentic interface
                    try {
                    await SingleAgentOrchestrator.ChatAgentAsync(
                        client: CreateChatClient(teamLeadModel),
                        teamsCts.Token,
                        maxIterations: 3,
                        mcpTools: McpTools,
                        continuous: ContinuousExec,
                        autoCompactTokenThreshold: SwarmConfig?.CompactionAgent?.AutoCompactTokenThreshold,
                        minDelaySeconds: (uint)MinContDelay!,
                        showToolResultCalls: _showToolCallResults,
                        shouldPlan: ShouldPlan,
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        teamScope: teamScope
                    );
                    } finally { ServeMode.ActiveMode = "interactive"; MuxSwarm.Utils.Teams.TeamController.Clear(); }
                    break;
                }

                case "/swarm":
                    await EnsureMcpReadyAsync();
                    Config = LoadConfig(ConfigPath);
                    var maModels = Common.LoadAgentModels();
                    var maCts = GetOrResetCts();

                    ServeMode.ActiveMode = "swarm";
                    MuxConsole.StartTuiLoopClock();   // begin the loop clock for this agentic interface
                    try {
                    await MultiAgentOrchestrator.RunAsync(
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        mcpTools: (McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
                        continuous: ContinuousExec,
                        shouldPlan: ShouldPlan, 
                        minDelaySeconds: (uint)MinContDelay!,
                        agentModels: maModels,
                        cancellationToken: maCts.Token
                    );
                    } finally { ServeMode.ActiveMode = "interactive"; }
                    break;

                case "/pswarm":
                    await EnsureMcpReadyAsync();
                    Config = LoadConfig(ConfigPath);
                    var pModels = Common.LoadAgentModels();
                    var pCts = GetOrResetCts();

                    ServeMode.ActiveMode = "pswarm";
                    MuxConsole.StartTuiLoopClock();   // begin the loop clock for this agentic interface
                    try {
                    await ParallelSwarmOrchestrator.RunAsync(
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        mcpTools: (McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
                        continuous: ContinuousExec,
                        shouldPlan: ShouldPlan, 
                        minDelaySeconds: (uint)MinContDelay!,
                        agentModels: pModels,
                        cancellationToken: pCts.Token
                    );
                    } finally { ServeMode.ActiveMode = "interactive"; }
                    break;

                case "/agent":
                    await EnsureMcpReadyAsync();
                    Config = LoadConfig(ConfigPath);
                    var singleAgentModel = LoadSingleAgentModel();
                    var agentCts = GetOrResetCts();

                    ServeMode.ActiveMode = "agent";
                    MuxConsole.StartTuiLoopClock();   // begin the loop clock for this agentic interface
                    try {
                    var agentHandle = MuxSwarm.Utils.InteractiveSessionRegistry.Create(
                        "agent", SingleAgentOrchestrator.AgentDef?.Name ?? "agent");
                    agentHandle.ChatTask = SingleAgentOrchestrator.ChatAgentAsync(
                        client: CreateChatClient(singleAgentModel, null,
                            wrapMidTurnReflection: true,
                            reflectionAgentName: SingleAgentOrchestrator.AgentDef?.Name ?? "Agent"),
                        agentCts.Token,
                        maxIterations: 3,
                        mcpTools: McpTools,
                        continuous: ContinuousExec,
                        autoCompactTokenThreshold: SwarmConfig?.CompactionAgent?.AutoCompactTokenThreshold,
                        minDelaySeconds: (uint)MinContDelay!,
                        showToolResultCalls: _showToolCallResults,
                        shouldPlan: ShouldPlan, 
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        allowSubAgents: AllowSubagents,
                        allowParallelSubAgents: AllowParallelSubAgents,
                        interactiveHandle: agentHandle
                    );
                    await PumpSessionAsync(agentHandle);   // returns when the session parks or finishes
                    } finally { ServeMode.ActiveMode = "interactive"; }
                    break;
                case "/sub":
                case "/subagents":
                    AllowSubagents = CliCmdUtils.HandleToggleSingleModeSubAgents(AllowSubagents);
                    // Reflect the sub badge in the docked footer immediately.
                    MuxConsole.RefreshDockedFooterModes(ShouldPlan, UltraMode, AllowParallelSubAgents, AllowSubagents, GigaMode);
                    break;
                case "/psub":
                case "/parasubagents":
                    AllowParallelSubAgents = CliCmdUtils.HandleToggleSingleModeSubAgents(AllowParallelSubAgents, parallel: true);
                    // Reflect the psub badge in the docked footer immediately.
                    MuxConsole.RefreshDockedFooterModes(ShouldPlan, UltraMode, AllowParallelSubAgents, AllowSubagents, GigaMode);
                    break;
                case "/onboard":
                    Config = LoadConfig(ConfigPath);
                    var onboardModel = LoadSingleAgentModel();
                    var onboardCts = GetOrResetCts();
                    await CliCmdUtils.HandleOnboard(
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        singleAgentModel: onboardModel,
                        mcpTools: McpTools,
                        ct: onboardCts.Token
                    );
                    break;
                case "/stateless":
                    await EnsureMcpReadyAsync();
                    Config = LoadConfig(ConfigPath);
                    var statelessAgent = LoadSingleAgentModel();
                    var statelessAgentCts = GetOrResetCts();

                    ServeMode.ActiveMode = "stateless";
                    MuxConsole.StartTuiLoopClock();   // begin the loop clock for this agentic interface
                    try {
                    var statelessHandle = MuxSwarm.Utils.InteractiveSessionRegistry.Create(
                        "stateless", "stateless");
                    statelessHandle.ChatTask = SingleAgentOrchestrator.ChatAgentAsync(
                        client: CreateChatClient(statelessAgent),
                        statelessAgentCts.Token,
                        maxIterations: 3,
                        mcpTools: McpTools,
                        continuous: ContinuousExec,
                        minDelaySeconds: (uint)MinContDelay!,
                        autoCompactTokenThreshold: SwarmConfig?.CompactionAgent?.AutoCompactTokenThreshold,
                        showToolResultCalls: _showToolCallResults,
                        shouldPlan: ShouldPlan, 
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        persistSession: false,
                        allowSubAgents: AllowSubagents,
                        allowParallelSubAgents: AllowParallelSubAgents,
                        interactiveHandle: statelessHandle
                    );
                    await PumpSessionAsync(statelessHandle);
                    } finally { ServeMode.ActiveMode = "interactive"; }
                    break;

                case var atc when atc == "/attach" || atc.StartsWith("/attach ", StringComparison.Ordinal):
                {
                    // Re-enter a session parked via /detach. "/attach" lists parked sessions; "/attach
                    // <id>" releases that frame's attach gate (handing the console back to it) and
                    // pumps it again until it re-parks or finishes. Single console reader throughout:
                    // the menu stops reading the moment it releases the gate.
                    var parked = MuxSwarm.Utils.InteractiveSessionRegistry.ListParked();
                    var attachArg = atc.Length > "/attach".Length ? atc.Substring("/attach".Length).Trim() : "";
                    if (parked.Count == 0)
                    {
                        MuxConsole.WriteMuted("No detached sessions. Launch /agent or /stateless, then /detach.");
                        break;
                    }
                    if (attachArg.Length == 0)
                    {
                        MuxConsole.WriteInfo("Detached sessions:");
                        foreach (var (pid, plabel, pmode, ptok) in parked)
                            MuxConsole.WriteMuted($"  {pid}  {plabel} ({pmode}) ~{ptok:N0} tok  - /attach {pid}");
                        break;
                    }
                    var handle = MuxSwarm.Utils.InteractiveSessionRegistry.Find(attachArg);
                    if (handle is null || handle.Status != "parked")
                    {
                        MuxConsole.WriteWarning($"No detached session '{attachArg}'. Type /attach to list them.");
                        break;
                    }
                    MuxConsole.StartTuiLoopClock();
                    ServeMode.ActiveMode = handle.Mode;
                    try {
                        handle.ReleaseAttach();             // resume the parked frame (it takes the console)
                        await PumpSessionAsync(handle);     // returns when it re-parks or finishes
                    } finally { ServeMode.ActiveMode = "interactive"; }
                    break;
                }
                
                case "/addcontext":
                    CliCmdUtils.HandleContextInject();
                    break;
                case "/cont":
                case "/continuous":
                    ContinuousExec = !ContinuousExec;
                    MinContDelay = CliCmdUtils.HandleContToggle(ContinuousExec);
                    break;
                
                case "/workflow":
                    CliCmdUtils.HandleInteractiveWorkflow();
                    break;
                case var rc when rc == "/resume" || rc.StartsWith("/resume ", StringComparison.Ordinal):
                    // Bare "/resume" -> interactive picker. "/resume <id>" -> resume that
                    // session directly (used by the web app's Resume button over the WS).
                    var resumeArg = rc.Length > "/resume".Length
                        ? rc.Substring("/resume".Length).Trim()
                        : null;
                    var resumeData = CliCmdUtils.HandleSessionResume(
                        string.IsNullOrWhiteSpace(resumeArg) ? null : resumeArg);
                    if (resumeData.HasValue)
                    {
                        Config = LoadConfig(ConfigPath);
                        var resumeModel = LoadSingleAgentModel();
                        var resumeCts = GetOrResetCts();

                        ServeMode.ActiveMode = "agent";
                        try {
                        await SingleAgentOrchestrator.ChatAgentAsync(
                            client: CreateChatClient(resumeModel),
                            resumeCts.Token,
                            maxIterations: 3,
                            mcpTools: McpTools,
                            showToolResultCalls: _showToolCallResults,
                            shouldPlan: ShouldPlan, 
                            continuous: ContinuousExec,
                            autoCompactTokenThreshold: SwarmConfig?.CompactionAgent?.AutoCompactTokenThreshold,
                            minDelaySeconds: (uint)MinContDelay!,
                            chatClientFactory: modelId => CreateChatClient(modelId),
                            resumedSession: resumeData.Value.data,
                            resumedSessionDir: resumeData.Value.sessionDir,
                            allowSubAgents: AllowSubagents,
                            allowParallelSubAgents: AllowParallelSubAgents
                        );
                        } finally { ServeMode.ActiveMode = "interactive"; }
                    }
                    break;
                
                case "/maxp":
                    CliCmdUtils.HandleMaxP();
                    break;
                case "/setmodel":
                    CliCmdUtils.HandleModelSwap();
                    break;
                case "/plan":
                    ShouldPlan = !ShouldPlan;
                    if (ShouldPlan)
                    {
                        MuxConsole.WriteSuccess("Plan Mode enabled");
                        MuxConsole.WriteMuted("Agents will present a plan and ask for approval before executing.");
                        MuxConsole.WriteMuted("Applies to orchestrators and single agent mode only.");
                    }
                    else
                    {
                        MuxConsole.WriteSuccess("Plan Mode disabled");
                        MuxConsole.WriteMuted("Agents will execute immediately without plan confirmation.");
                    }
                    // Reflect the plan badge in the docked footer immediately (not after stream).
                    MuxConsole.RefreshDockedFooterModes(ShouldPlan, UltraMode, AllowParallelSubAgents, AllowSubagents, GigaMode);
                    break;
                case "/classic":
                case "/tui":
                    if (MuxConsole.StdioMode)
                    {
                        MuxConsole.WriteMuted("Render mode is fixed to stdio/serve in this session; ignoring.");
                        break;
                    }
                    if (userInput == "/classic")
                    {
                        MuxConsole.DisableDockedFooter();
                        MuxConsole.SetClassicRenderMode();
                        MuxConsole.WriteSuccess("Classic render mode enabled");
                        MuxConsole.WriteMuted("Line-by-line renderer (pre-v0.11.0 interactive experience).");
                    }
                    else
                    {
                        MuxConsole.SetTuiRenderMode();
                        if (MuxConsole.IsTui)
                        {
                            MuxConsole.EnableDockedFooter();
                            MuxConsole.WriteSuccess("Live TUI render mode enabled");
                            MuxConsole.WriteMuted("Live-region renderer with pinned footer + input box (takes effect in-session).");
                        }
                        else
                        {
                            MuxConsole.WriteWarning("Terminal is not TUI-capable; staying on classic renderer.");
                        }
                    }
                    break;
                case "/verbose":
                    MuxConsole.ToolOutputCompact = !MuxConsole.ToolOutputCompact;
                    MuxConsole.WriteSuccess(MuxConsole.ToolOutputCompact
                        ? "Tool output: compact (collapsed one-line results)"
                        : "Tool output: full (bordered result panels)");
                    break;
                case "/subagentview":
                case "/sav":
                    MuxConsole.CollapseSubAgents = !MuxConsole.CollapseSubAgents;
                    Config.Console.CollapseSubAgents = MuxConsole.CollapseSubAgents;
                    MuxConsole.WriteSuccess(MuxConsole.CollapseSubAgents
                        ? "Sub-agent view: collapsed (delegated agents fold into one expandable line)"
                        : "Sub-agent view: expanded (delegated agents stream inline)");
                    break;
                case "/daemonview":
                case "/dv":
                    MuxConsole.CollapseDaemonOutput = !MuxConsole.CollapseDaemonOutput;
                    Config.Console.CollapseDaemon = MuxConsole.CollapseDaemonOutput;
                    MuxConsole.WriteSuccess(MuxConsole.CollapseDaemonOutput
                        ? "Daemon view: collapsed (daemon-fired goals fold into one expandable line)"
                        : "Daemon view: expanded (daemon-fired goals stream inline)");
                    break;
                case "/ultra":
                case "/ultraplan":
                    UltraMode = !UltraMode;
                    if (UltraMode)
                    {
                        // Compose plan discipline + max reasoning + heavy parallel delegation.
                        // Capture prior flags so toggling /ultra off restores them exactly.
                        _ultraPriorPlan = ShouldPlan;
                        _ultraPriorParaSub = AllowParallelSubAgents;
                        ShouldPlan = true;
                        if (App.Config.Ultra.AutoSubAgents)
                            AllowParallelSubAgents = true;
                        MuxConsole.WriteSuccess("Ultra Mode enabled");
                        MuxConsole.WriteMuted($"Plan Mode forced on + maximum reasoning (thinking budget {App.Config.Ultra.ThinkingBudget}).");
                        if (App.Config.Ultra.AutoSubAgents)
                            MuxConsole.WriteMuted("Parallel sub-agents enabled — agents fan parallelizable work out to isolated sub-agent sessions.");
                        MuxConsole.WriteMuted("Agents decompose deeply, list assumptions, weigh alternatives, and self-review before finalizing.");
                    }
                    else
                    {
                        ShouldPlan = _ultraPriorPlan;
                        AllowParallelSubAgents = _ultraPriorParaSub;
                        MuxConsole.WriteSuccess("Ultra Mode disabled");
                        MuxConsole.WriteMuted($"Reasoning, plan, and delegation flags restored (Plan Mode: {(ShouldPlan ? "on" : "off")}, Parallel sub-agents: {(AllowParallelSubAgents ? "on" : "off")}).");
                    }
                    // Reflect the new mode badges in the docked footer immediately (not after stream).
                    MuxConsole.RefreshDockedFooterModes(ShouldPlan, UltraMode, AllowParallelSubAgents, AllowSubagents, GigaMode);
                    break;
                case "/giga":
                    GigaMode = !GigaMode;
                    if (GigaMode)
                    {
                        // Giga is a superset of ultra: force plan + max reasoning (via UltraMode) AND
                        // grant dynamic-orchestration tools. Capture prior flags to restore on toggle-off.
                        _gigaPriorPlan = ShouldPlan;
                        _gigaPriorParaSub = AllowParallelSubAgents;
                        _gigaPriorUltra = UltraMode;
                        UltraMode = true;
                        ShouldPlan = true;
                        if (App.Config.Ultra.AutoSubAgents)
                            AllowParallelSubAgents = true;
                        MuxConsole.WriteSuccess("Giga Mode enabled");
                        MuxConsole.WriteMuted("Dynamic orchestration unlocked: the agent can spawn_team, run_team, and write/run workflows on its own.");
                        MuxConsole.WriteMuted($"Maximum reasoning + plan discipline on (thinking budget {App.Config.Ultra.ThinkingBudget}). Giga teams are tagged 'giga:'.");
                    }
                    else
                    {
                        ShouldPlan = _gigaPriorPlan;
                        AllowParallelSubAgents = _gigaPriorParaSub;
                        UltraMode = _gigaPriorUltra;
                        MuxSwarm.Utils.Teams.GigaMode.Reset();
                        MuxConsole.WriteSuccess("Giga Mode disabled");
                        MuxConsole.WriteMuted($"Orchestration tools removed; reasoning/plan flags restored (Ultra: {(UltraMode ? "on" : "off")}, Plan: {(ShouldPlan ? "on" : "off")}).");
                    }
                    MuxConsole.RefreshDockedFooterModes(ShouldPlan, UltraMode, AllowParallelSubAgents, AllowSubagents, GigaMode);
                    break;
                case "/tools":
                    if (McpTools != null) Common.LogAvailableTools(McpTools);
                    break;

                case "/model":
                    var currentModel = LoadSingleAgentModel();
                    MuxConsole.WriteInfo($"Single agent model: {currentModel}");
                    var models = Common.LoadAgentModels();
                    foreach (var kvp in models)
                        MuxConsole.WriteInfo($"  {kvp.Key} -> {kvp.Value}");
                    break;

                case "/dbg":
                    MuxConsole.WriteSuccess("Debug enabled — showing tool call results (applies to stdio mode only).");
                    _showToolCallResults = true;
                    break;

                case "/nodbg":
                    MuxConsole.WriteInfo("Debug disabled — hiding tool call results (applies to stdio mode only).");
                    _showToolCallResults = false;
                    break;

                case "/clear":
                    // In TUI mode a raw Console.Clear() wipes the terminal out from under the
                    // live region, desyncing its frame model and stranding ghost rows. Route
                    // through the driver's managed clear+repaint (the Ctrl+L path); fall back to
                    // a raw clear only in classic/stdio where there is no live region.
                    if (MuxConsole.IsTui) MuxConsole.TuiForceRedraw();
                    else Console.Clear();
                    break;

                case "/status":
                    CliCmdUtils.HandleStatus(McpTools, Common.LoadAgentModels());
                    break;

                case "/disabletools":
                    DisableTools();
                    break;

                case "/dockerexec":
                    CliCmdUtils.HandleDockerExec(ConfigPath);
                    break;
                case var sbx when sbx == "/sandbox" || sbx.StartsWith("/sandbox "):
                    CliCmdUtils.HandleSandbox(userInput, ConfigPath);
                    break;
                case var lg when lg == "/login" || lg.StartsWith("/login "):
                    await CliCmdUtils.HandleLoginAsync(userInput, ConfigPath);
                    break;
                case var pg when pg == "/ping" || pg.StartsWith("/ping "):
                    await CliCmdUtils.HandlePingAsync(userInput);
                    break;
                case var px when px == "/proxy" || px.StartsWith("/proxy "):
                    await CliCmdUtils.HandleProxyAsync(userInput);
                    break;
                case "/delimiter":
                    CliCmdUtils.HandleMultiDelimiterToggle();
                    break;
                case var vc when vc == "/voice" || vc.StartsWith("/voice "):
                    CliCmdUtils.HandleVoice(vc);
                    break;
                case "/swap":
                    CliCmdUtils.HandleAgentSwap();
                    break;
                case "/provider":
                    CliCmdUtils.HandleProviderSwap();
                    break;
                case "/limits":
                    CliCmdUtils.ShowExecutionLimits();
                    break;
                case "/qc":
                case "/qm":
                    lock (CtsLock)
                    {
                        if (!_cts.IsCancellationRequested)
                        {
                            _cts.Cancel();
                            MuxConsole.WriteInfo("Stopping current session...");
                        }
                        else
                        {
                            MuxConsole.WriteMuted("No active session to stop.");
                        }
                    }
                    break;

                case var mem when mem == "/memory" || mem.StartsWith("/memory ")
                                  || mem == "/deep" || mem.StartsWith("/deep "):
                    CliCmdUtils.HandleMemory(userInput, McpClients, McpTools);
                    break;

                case var tg when tg == "/taskgraph" || tg.StartsWith("/taskgraph "):
                    {
                        var tgArg = userInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        var msg = MuxSwarm.Utils.Teams.TeamController.ToggleDecompose(
                            tgArg.Length > 1 ? tgArg[1] : "status");
                        MuxConsole.WriteInfo(msg);
                    }
                    break;

                case "/skills":
                    CliCmdUtils.ShowLoadedSkills();
                    break;

                case var th when th == "/theme" || th.StartsWith("/theme "):
                    CliCmdUtils.HandleTheme(userInput);
                    break;

                case "/reloadskills":
                    CliCmdUtils.ReloadSkills();
                    break;

                case var iskl when iskl == "/installskill" || iskl.StartsWith("/installskill "):
                    await CliCmdUtils.HandleInstallSkillAsync(userInput);
                    break;

                case "/refresh":
                    Config = LoadConfig(ConfigPath);
                    SwarmConfig = LoadSwarm();
                    await CliCmdUtils.HandleFullReload(InitMcpServersAsync, ConfigPath);
                    break;

                case var cmd when cmd.StartsWith("/report"):
                    var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    CliCmdUtils.GenerateSessionReports(parts.Length > 1 ? parts[1] : null);
                    break;

                case "/sessions":
                    CliCmdUtils.ListSessions();
                    break;

                case "/config":
                case var wsCmd when wsCmd == "/workspace" || wsCmd.StartsWith("/workspace "):
                {
                    // Runtime mirror of the --workspace CLI arg: with no path, report the current
                    // @-file workspace root; with a path, repoint it and re-index the live "@" file
                    // picker so the new root takes effect immediately (no restart).
                    var wparts = userInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (wparts.Length < 2)
                    {
                        var marker = PlatformContext.WorkspaceIsInstallDir ? "  (mux install dir - pass a path to repoint)" : "";
                        MuxConsole.WriteInfo($"workspace = {PlatformContext.WorkspaceRoot}{marker}");
                        break;
                    }
                    try
                    {
                        PlatformContext.WorkspaceRoot = wparts[1].Trim().Trim('"');
                        MuxConsole.SetTuiFilesCatalog(CliCmdUtils.GetWorkspaceFiles());
                        MuxConsole.WriteSuccess($"workspace = {PlatformContext.WorkspaceRoot} (@-file index refreshed).");
                    }
                    catch (Exception ex)
                    {
                        MuxConsole.WriteWarning($"Invalid --workspace path: {ex.Message}");
                    }
                    break;
                }

                case var cfgCmd when cfgCmd.StartsWith("/config "):
                {
                    var res = MuxSwarm.Utils.Tui.TuiConfigCommands.Handle(userInput);
                    if (res.Ok) MuxConsole.WriteInfo(res.Message);
                    else MuxConsole.WriteWarning(res.Message);
                    break;
                }

                case var setCmd when setCmd == "/set" || setCmd.StartsWith("/set "):
                {
                    var res = MuxSwarm.Utils.Tui.TuiConfigCommands.NeedsInteractive(userInput)
                        ? MuxSwarm.Utils.Tui.TuiConfigCommands.RunInteractive(userInput)
                        : MuxSwarm.Utils.Tui.TuiConfigCommands.Handle(userInput);
                    if (res.Ok) { MuxConsole.WriteSuccess(res.Message); Config = LoadConfig(ConfigPath); }
                    else MuxConsole.WriteWarning(res.Message);
                    break;
                }

                case var rsn when rsn == "/showreasoning" || rsn.StartsWith("/showreasoning "):
                {
                    // Top-level control of the client-side reasoning display gate. With no arg, show
                    // the current value; otherwise route through the showReasoning /set key so
                    // validation + persistence stay in one place. "none" suppresses streamed reasoning
                    // text in interactive renderers; "full"/"summary" both show it (grey + italic).
                    // Applies immediately (MuxConsole.ShowReasoning is re-seeded from the reloaded config).
                    var rparts = userInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (rparts.Length < 2)
                    {
                        MuxConsole.WriteInfo($"showReasoning = {Config.ShowReasoning}  (set with /showreasoning <full|summary|none>).");
                        break;
                    }
                    var res = MuxSwarm.Utils.Tui.TuiConfigCommands.Handle($"/set showReasoning {rparts[1].Trim()}");
                    if (res.Ok) { MuxConsole.WriteSuccess(res.Message); Config = LoadConfig(ConfigPath); MuxConsole.ShowReasoning = Config.ShowReasoning; }
                    else MuxConsole.WriteWarning(res.Message);
                    break;
                }

                case var saCmd when saCmd == "/startargs" || saCmd.StartsWith("/startargs "):
                {
                    // Persist CLI args applied automatically at every startup (config.startupArgs).
                    // No arg -> show current value. "clear" -> empty it. Otherwise set verbatim.
                    // Takes effect on the NEXT launch (args are merged before ParseArgs at boot).
                    var saParts = userInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (saParts.Length < 2)
                    {
                        var cur = string.IsNullOrWhiteSpace(Config.StartupArgs) ? "(none)" : Config.StartupArgs;
                        MuxConsole.WriteInfo($"startupArgs = {cur}");
                        MuxConsole.WriteMuted("Set with /startargs <args>  (e.g. /startargs --agent CodeAgent --giga). Clear with /startargs clear. Applies next launch.");
                        break;
                    }
                    var saVal = saParts[1].Trim();
                    Config.StartupArgs = saVal.Equals("clear", StringComparison.OrdinalIgnoreCase) ? "" : saVal;
                    Common.SaveConfig(Config);
                    Config = LoadConfig(ConfigPath);
                    MuxConsole.WriteSuccess(string.IsNullOrEmpty(Config.StartupArgs)
                        ? "Cleared startupArgs."
                        : $"startupArgs set to: {Config.StartupArgs}  (applies next launch).");
                    break;
                }

                case var naCmd when naCmd == "/newagent" || naCmd.StartsWith("/newagent "):
                {
                    // The wizard may offer to spawn a helper agent (like /onboard) to author the
                    // new agent's prompt; wire that callback so the user can opt in.
                    await EnsureMcpReadyAsync();
                    var helperModel = LoadSingleAgentModel();
                    void SpawnPromptHelper(string agentName, string desc)
                    {
                        var promptDir = MuxSwarm.Utils.PlatformContext.PromptsDirectory;
                        var promptAbs = System.IO.Path.Combine(promptDir, $"{agentName}.md");
                        var task = $"Help me write a high-quality system prompt for a new Mux-Swarm agent named '{agentName}'. " +
                                   $"Its purpose: {desc}. Ask me a few focused questions, then write the finished prompt to the file at {promptAbs} " +
                                   "(overwrite the starter template). Keep it concise and operational.";
                        MuxConsole.InputOverride = new MuxSwarm.Utils.FallbackReader(task, MuxConsole.InputOverride);
                        try
                        {
                            SingleAgentOrchestrator.ChatAgentAsync(
                                client: CreateChatClient(helperModel),
                                GetOrResetCts().Token,
                                maxIterations: 4,
                                mcpTools: McpTools,
                                continuous: false).GetAwaiter().GetResult();
                        }
                        finally { MuxConsole.InputOverride = System.Console.In; }
                    }

                    var res = MuxSwarm.Utils.Tui.TuiConfigCommands.RunInteractive(userInput, SpawnPromptHelper);
                    if (res.Ok)
                    {
                        MuxConsole.WriteSuccess(res.Message);
                        SwarmConfig = LoadSwarm();
                    }
                    else MuxConsole.WriteWarning(res.Message);
                    break;
                }

                case var hkCmd when hkCmd == "/createhook" || hkCmd.StartsWith("/createhook "):
                {
                    await EnsureMcpReadyAsync();
                    var helperModel = LoadSingleAgentModel();
                    // Helper that drafts a hook SCRIPT to the given file path (vs /newagent's prompt file).
                    void SpawnHookScriptHelper(string scriptPath, string purpose)
                    {
                        var task = BuildHookHelperBrief(scriptPath, purpose);
                        MuxConsole.InputOverride = new MuxSwarm.Utils.FallbackReader(task, MuxConsole.InputOverride);
                        try
                        {
                            SingleAgentOrchestrator.ChatAgentAsync(
                                client: CreateChatClient(helperModel),
                                GetOrResetCts().Token,
                                maxIterations: 4,
                                mcpTools: McpTools,
                                continuous: false).GetAwaiter().GetResult();
                        }
                        finally { MuxConsole.InputOverride = System.Console.In; }
                    }

                    var res = MuxSwarm.Utils.Tui.TuiConfigCommands.RunInteractive(userInput, SpawnHookScriptHelper);
                    if (res.Ok) { MuxConsole.WriteSuccess(res.Message); SwarmConfig = LoadSwarm(); }
                    else MuxConsole.WriteWarning(res.Message);
                    break;
                }

                case var hksCmd when hksCmd == "/hooks" || hksCmd.StartsWith("/hooks "):
                {
                    var hp = userInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var sub = hp.Length > 1 ? hp[1].Trim().ToLowerInvariant() : "";
                    switch (sub)
                    {
                        case "on":
                            MuxSwarm.State.HookWorker.Enabled = true;
                            MuxConsole.WriteSuccess($"Hooks ENABLED ({MuxSwarm.State.HookWorker.Count} configured).");
                            break;
                        case "off":
                            MuxSwarm.State.HookWorker.Enabled = false;
                            MuxConsole.WriteSuccess("Hooks DISABLED for this session (events are dropped).");
                            break;
                        case "create":
                        case "new":
                        {
                            await EnsureMcpReadyAsync();
                            var helperModel2 = LoadSingleAgentModel();
                            void SpawnHookScriptHelper2(string scriptPath, string purpose)
                            {
                                var task = BuildHookHelperBrief(scriptPath, purpose);
                                MuxConsole.InputOverride = new MuxSwarm.Utils.FallbackReader(task, MuxConsole.InputOverride);
                                try
                                {
                                    SingleAgentOrchestrator.ChatAgentAsync(
                                        client: CreateChatClient(helperModel2), GetOrResetCts().Token,
                                        maxIterations: 4, mcpTools: McpTools, continuous: false).GetAwaiter().GetResult();
                                }
                                finally { MuxConsole.InputOverride = System.Console.In; }
                            }
                            var cres = MuxSwarm.Utils.Tui.TuiConfigCommands.RunCreateHookWizard(
                                new[] { "/createhook" }, SpawnHookScriptHelper2);
                            if (cres.Ok) { MuxConsole.WriteSuccess(cres.Message); SwarmConfig = LoadSwarm(); }
                            else MuxConsole.WriteWarning(cres.Message);
                            break;
                        }
                        default:
                        {
                            var status = MuxSwarm.State.HookWorker.Enabled ? "ON" : "OFF";
                            var sb = new System.Text.StringBuilder();
                            sb.AppendLine($"Hooks: {status}  ({MuxSwarm.State.HookWorker.Count} configured)");
                            foreach (var h in MuxSwarm.State.HookWorker.Loaded)
                                sb.AppendLine($"  - {h.Id}: on {h.When.Event}" +
                                    (h.When.Agent is not null ? $" [agent={h.When.Agent}]" : "") +
                                    (h.When.Tool is not null ? $" [tool={h.When.Tool}]" : "") +
                                    $"  ({h.Mode})");
                            sb.Append("Usage: /hooks on|off|create");
                            MuxConsole.WriteInfo(sb.ToString().TrimEnd());
                            break;
                        }
                    }
                    break;
                }

                case var ctCmd when ctCmd == "/createteam" || ctCmd.StartsWith("/createteam "):
                {
                    var res = MuxSwarm.Utils.Tui.TuiConfigCommands.RunInteractive(userInput);
                    if (res.Ok)
                    {
                        MuxConsole.WriteSuccess(res.Message);
                        SwarmConfig = LoadSwarm();
                    }
                    else MuxConsole.WriteWarning(res.Message);
                    break;
                }

                case var eaCmd when eaCmd == "/editagent" || eaCmd.StartsWith("/editagent ")
                                 || eaCmd == "/delagent" || eaCmd.StartsWith("/delagent ")
                                 || eaCmd == "/removeagent" || eaCmd.StartsWith("/removeagent "):
                {
                    var res = MuxSwarm.Utils.Tui.TuiConfigCommands.RunInteractive(userInput);
                    if (res.Ok)
                    {
                        MuxConsole.WriteSuccess(res.Message);
                        SwarmConfig = LoadSwarm();
                    }
                    else MuxConsole.WriteWarning(res.Message);
                    break;
                }

                case "/update":
                {
                    // Session-agnostic process-level self-update (Scope.Both): runs identically at the
                    // menu and in-session. Downloads the latest release, verifies its published SHA256,
                    // replaces changed shipped files (user configs/sessions/memory preserved), and if the
                    // binary itself changed, stages it and relaunches to finish the swap.
                    var (staged, msg) = await MuxSwarm.State.SelfUpdater.RunAsync(line => MuxConsole.WriteInfo(line));
                    MuxConsole.WriteInfo(msg);
                    if (staged)
                    {
                        MuxConsole.WriteWarning("Mux-Swarm must restart to finish applying the update. Restarting...");
                        MuxSwarm.State.Relauncher.RestartNow(() => MuxConsole.DisableDockedFooter());
                    }
                    break;
                }

                case var dmnCmd when dmnCmd == "/daemon" || dmnCmd.StartsWith("/daemon ")
                                  || dmnCmd == "/da" || dmnCmd.StartsWith("/da "):
                {
                    // /daemon is session-AGNOSTIC: it controls process-level background triggers
                    // (DaemonRunner) that do not depend on any live session, so it runs at the menu
                    // exactly as it does in-session (via MetaCommandDispatch). EnsureRunner lazily
                    // starts the daemon and needs MCP tools; the background MCP init may not have
                    // landed yet at the menu, so make sure it's ready first (idempotent + cheap).
                    await EnsureMcpReadyAsync();
                    MuxSwarm.State.DaemonCommand.Run(userInput);
                    break;
                }

                default:
                    if (userInput.StartsWith("/"))
                    {
                        // Slash-anywhere symmetry: a SESSION-native command typed at the menu has
                        // no live session to act on. Warn that it needs an active session instead
                        // of the generic "unknown command", so the user knows to launch one first.
                        var menuCmd = userInput.Split(' ', 2)[0];
                        if (MuxSwarm.Utils.Tui.TuiCommands.IsSessionNative(menuCmd))
                            MuxConsole.WriteWarning($"'{menuCmd}' only runs inside an active session. Launch one first (e.g. /agent, /swarm, /teams).");
                        else
                            MuxConsole.WriteWarning("Unknown command. Type /help.");
                    }
                    else
                        MuxConsole.WriteMuted("Type /help for commands.");
                    break;
            }
        }
        
        if (DaemonRunner != null)
            await DaemonRunner.DisposeAsync();
        
        return Environment.ExitCode;
    }
    
    /// <summary>
    /// Build the grounding brief handed to the helper agent that authors a hook script. Gives the
    /// agent the full hook contract - how hooks run, the exact event payload schema, the canonical
    /// event list, the stdin/exit-code/timeout semantics, and safety rules - so it is not flying
    /// blind. Used by /createhook and /hooks create.
    /// </summary>
    private static string BuildHookHelperBrief(string scriptPath, string purpose)
    {
        var ext = System.IO.Path.GetExtension(scriptPath).ToLowerInvariant();
        var lang = ext switch
        {
            ".ps1" => "PowerShell",
            ".sh" => "bash",
            ".py" => "Python",
            ".js" => "Node.js",
            _ => "a shell script"
        };
        return
$@"You are writing a Mux-Swarm EVENT HOOK script. Write the finished, runnable {lang} script to this
EXACT path and nothing else there: {scriptPath}

PURPOSE (what the user wants this hook to do):
{purpose}

HOW MUX-SWARM HOOKS WORK (authoritative - do not invent behavior):
- A hook is an external command Mux-Swarm runs when a lifecycle EVENT fires. It is configured in
  swarm.json under hooks[] (the /createhook wizard writes that entry; you only write the SCRIPT).
- DELIVERY: every time the event fires, Mux writes ONE line of JSON to your script's STDIN (newline-
  delimited). Read exactly one line from stdin and JSON-parse it. For a 'persistent' hook the process
  stays alive and receives a STREAM of such lines (loop over stdin); for async/blocking it is invoked
  once per event with a single line then stdin closes.
- The command is split on the FIRST space into executable + args, so your script must be invoked as a
  single program (the wizard wraps it, e.g. 'bash <path>' or 'powershell -File <path>').
- OUTPUT: stdout/stderr are NOT captured or shown - do your own logging/IO (write a file, hit a
  webhook, send a notification). Exit code 0 = success. A 'blocking' hook is awaited up to its
  timeoutSeconds (default 30) and then killed; 'async' fire-and-forget; do not block forever unless
  the hook is configured persistent.

EVENT PAYLOAD (camelCase JSON fields; some are null depending on the event):
  event      string  - the event name (see list below)
  agent      string? - agent involved (e.g. the agent that called a tool)
  tool       string? - tool name (on tool_call / tool_result)
  text       string? - streamed text/reasoning chunk (on text_chunk / thinking_chunk)
  args       string? - tool-call arguments
  summary    string? - short summary (turn_end, delegation, task_complete, daemon_*)
  goalId     string? - active goal/session id when present
  timestamp  string  - ISO-8601 UTC time the event fired

CANONICAL EVENTS you can trigger on (use the one that matches the purpose):
  user_input, agent_turn_start, tool_call, tool_result, text_chunk, thinking_chunk,
  turn_end, delegation, task_complete, session_start, session_end, runtime_ready,
  daemon_start, daemon_stop, daemon_trigger, daemon_status, daemon_bridge

SAFETY: the hook runs with the user's permissions. Keep it minimal, validate the parsed JSON, never
hardcode secrets (read them from environment variables), and fail safe (a missing field must not crash).

Ask the user at most one or two focused clarifying questions ONLY if the purpose is ambiguous, then
write the complete script to {scriptPath} (overwrite the seed). Confirm the path when done.";
    }

    private string LoadSingleAgentModel()
    {
        if (!string.IsNullOrEmpty(_cliModelOverride))
            return _cliModelOverride;

        if (SingleAgentOrchestrator.AgentDef != null)
        {
            var agentModels = Common.LoadAgentModels();
            if (agentModels.TryGetValue(SingleAgentOrchestrator.AgentDef.Name, out var model))
                return model;
        }

        if (File.Exists(MultiAgentOrchestrator.SwarmConfPath))
        {
            var json = File.ReadAllText(MultiAgentOrchestrator.SwarmConfPath);
            var swarm = JsonSerializer.Deserialize<SwarmConfig>(json);

            if (swarm?.SingleAgent != null && !string.IsNullOrEmpty(swarm.SingleAgent.Model))
                return swarm.SingleAgent.Model;
        }

        MuxConsole.WriteWarning("No model resolved for single agent. Check swarm.json configuration.");
        return string.Empty;
    }

    /// <summary>
    /// Run the ACP (Zed Agent Client Protocol) adapter. Each ACP session maps to one
    /// interactive single-agent loop, fed by an <see cref="MuxSwarm.Utils.Acp.AcpInputReader"/>
    /// (installed as InputOverride) and observed via MuxConsole.AcpSink. Blocks until the
    /// client closes stdin.
    /// </summary>
    private async Task<int> RunAcpAsync()
    {
        var server = new MuxSwarm.Utils.Acp.AcpServer(
            version: Version,
            // Model selector for ACP clients' /models: current = the resolved single-agent
            // model, available = the distinct model ids configured across swarm.json. Setting
            // it applies a CLI-style model override for the next session/turn.
            modelsProvider: () =>
            {
                string current = LoadSingleAgentModel();
                var available = Common.LoadAgentModels().Values
                    .Where(m => !string.IsNullOrEmpty(m))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                return (current, (IReadOnlyList<string>)available);
            },
            setModel: m => { if (!string.IsNullOrWhiteSpace(m)) _cliModelOverride = m; },
            runSession: async (reader, resume) =>
            {
                // Tools are needed once the model actually runs a turn; await MCP readiness
                // here (not at transport start) so the ACP handshake stays instant.
                await EnsureMcpReadyAsync();
                var model = LoadSingleAgentModel();
                var acpCts = GetOrResetCts();
                ServeMode.ActiveMode = "agent";
                var handle = MuxSwarm.Utils.InteractiveSessionRegistry.Create(
                    "agent", SingleAgentOrchestrator.AgentDef?.Name ?? "agent");
                try
                {
                    handle.ChatTask = SingleAgentOrchestrator.ChatAgentAsync(
                        client: CreateChatClient(model),
                        acpCts.Token,
                        maxIterations: 3,
                        mcpTools: McpTools,
                        continuous: ContinuousExec,
                        autoCompactTokenThreshold: SwarmConfig?.CompactionAgent?.AutoCompactTokenThreshold,
                        minDelaySeconds: (uint)MinContDelay!,
                        showToolResultCalls: _showToolCallResults,
                        shouldPlan: ShouldPlan,
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        resumedSession: resume?.Data,
                        resumedSessionDir: resume?.Dir,
                        allowSubAgents: AllowSubagents,
                        allowParallelSubAgents: AllowParallelSubAgents,
                        interactiveHandle: handle);
                    await handle.ChatTask;
                }
                finally
                {
                    ServeMode.ActiveMode = "interactive";
                    MuxSwarm.Utils.InteractiveSessionRegistry.Remove(handle);
                }
            });
        await server.RunAsync();
        return 0;
    }

    private record ParsedArgs(
        string? Goal,
        bool Continuous,
        bool Parallel,
        int MaxParallelism,
        string? GoalId,
        uint MinDelay,
        uint PersistInterval,
        uint SessionRetention,
        bool ProdMode,
        bool? McpStrictOverride,
        bool? DockerExecOverride,
        string? ReportSessionId,
        bool ReportAll,
        string AgentName,
        int? ServePort,
        bool DaemonMode,
        bool AcpMode,
        bool UpdateMode
    );

    private static string? NextValue(string[] args, ref int i)
        => (i + 1 < args.Length) ? args[++i] : null;

    private static bool? NextBool(string[] args, ref int i)
    {
        var v = (i + 1 < args.Length) ? args[i + 1] : null;
        if (v != null && bool.TryParse(v, out var b)) { i++; return b; }
        return null;
    }

    /// <summary>The mode-entry command (e.g. "/agent", "/swarm") to auto-run as the first
    /// interactive input, set by startup mode flags. Null = land at the menu as usual.</summary>
    private static string? _startupCommand;
    /// <summary>Sandbox backend from --sandbox, applied to Config after load. Null = leave config as-is.</summary>
    private static string? _startupSandboxBackend;
    /// <summary>Agent name from --agent, applied when booting into a startup /agent session.</summary>
    private static string? _startupAgentName;

    /// <summary>
    /// Tokenize <paramref name="startupArgs"/> (a single config string) and prepend the tokens
    /// before the real argv. Honors simple double-quoted groups so a quoted value with spaces
    /// survives. Returns the original argv unchanged when there are no startup args.
    /// </summary>
    internal static string[] MergeStartupArgs(string? startupArgs, string[] argv)
    {
        if (string.IsNullOrWhiteSpace(startupArgs)) return argv;
        var tokens = TokenizeArgString(startupArgs);
        if (tokens.Count == 0) return argv;
        var merged = new string[tokens.Count + argv.Length];
        tokens.CopyTo(merged, 0);
        argv.CopyTo(merged, tokens.Count);
        return merged;
    }

    /// <summary>Split a shell-like argument string into tokens, respecting double quotes.</summary>
    internal static List<string> TokenizeArgString(string s)
    {
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    private static ParsedArgs ParseArgs(string[] args)
    {
        string? goal = null;
        bool continuous = false;
        bool parallel = false;
        int maxParallelism = 4;
        string? goalId = null;
        uint minDelay = 300;
        uint persistInterval = 60;
        uint sessionRetention = 10;
        bool prodMode = false;
        bool? mcpStrictOverride = null;
        bool? dockerExecOverride = null;
        string? reportSessionId = null;
        bool reportAll = false;
        string? agentName = null;
        bool daemonMode = false;
        bool acpMode = false;
        bool updateMode = false;


        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];

            if (!a.StartsWith("-", StringComparison.Ordinal) && goal == null)
            {
                goal = Common.ReadGoalValue(a);
                continue;
            }

            switch (a.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                    MuxConsole.PrintHelp(Help.HelpText);
                    Environment.Exit(0);
                    break;

                case "--continuous":
                    continuous = true;
                    break;

                case "--parallel":
                    parallel = true;
                    break;

                case "--max-parallelism":
                    if (Common.TryNextUInt(args, ref i, out var mp)) maxParallelism = (int)mp;
                    break;

                case "--prod":
                    prodMode = true;
                    break;

                case "--stdio":
                    MuxConsole.StdioMode = true;
                    break;
                case "--acp":
                    // ACP (Zed Agent Client Protocol) adapter. Routes structured events to the
                    // ACP sink (JSON-RPC over stdio) instead of NDJSON; StdioMode keeps the
                    // orchestrators on their machine-output path while AcpActive diverts it.
                    acpMode = true;
                    MuxConsole.StdioMode = true;
                    MuxConsole.AcpActive = true;
                    break;
                case "--delimiter":
                    var delim = NextValue(args, ref i);
                    CliCmdUtils.HandleSetMultiLineDelimiter(delim);
                    break;
                case "--goal":
                    {
                        var v = NextValue(args, ref i);
                        if (!string.IsNullOrWhiteSpace(v))
                            goal = Common.ReadGoalValue(v);
                        break;
                    }

                case "--goal-id":
                    goalId = NextValue(args, ref i);
                    break;

                case "--model":
                    {
                        var modelOverride = NextValue(args, ref i);
                        if (!string.IsNullOrWhiteSpace(modelOverride))
                            _cliModelOverride = modelOverride;
                        break;
                    }

                case "--min-delay":
                    if (Common.TryNextUInt(args, ref i, out var md)) minDelay = md;
                    break;

                case "--persist-interval":
                    if (Common.TryNextUInt(args, ref i, out var pi)) persistInterval = pi;
                    break;

                case "--session-retention":
                    if (Common.TryNextUInt(args, ref i, out var sr)) sessionRetention = sr;
                    break;

                case "--watchdog":
                    _watchDogEnabled = true;
                    break;

                case "--mcp-strict":
                    mcpStrictOverride = NextBool(args, ref i) ?? true;
                    break;

                case "--docker-exec":
                    dockerExecOverride = NextBool(args, ref i) ?? true;
                    break;
                case "--sandbox":
                    _startupSandboxBackend = (i + 1 < args.Length) ? args[++i] : "docker";
                    break;
                case "--agent":
                    var an = NextValue(args, ref i);
                    if (!string.IsNullOrWhiteSpace(an))
                    {
                        agentName = an;
                        // Interactive: pick this agent AND boot into a single-agent session
                        // (unless another startup mode was explicitly requested). For a
                        // goal-driven (--goal) or machine run, AgentName alone is used downstream.
                        _startupCommand ??= "/agent";
                        _startupAgentName = an;
                    }
                    break;
                
                case "--plan":
                    ShouldPlan = true;
                    break;
                case "--ultra":
                case "--ultraplan":
                    UltraMode = true;
                    ShouldPlan = true;
                    if (Config.Ultra.AutoSubAgents)
                        AllowParallelSubAgents = true;
                    break;
                case "--classic":
                    _cliRenderModeOverride = "classic";
                    break;
                case "--tui":
                    _cliRenderModeOverride = "tui";
                    break;
                case "--giga":
                    // Dynamic team/workflow orchestration (parity with /giga).
                    GigaMode = true;
                    if (Config.Ultra.AutoSubAgents)
                        AllowParallelSubAgents = true;
                    break;
                case "--sub":
                case "--subagents":
                    // Enable single-agent delegation to sub-agents (parity with /sub).
                    AllowSubagents = true;
                    break;
                case "--psub":
                case "--parasubagents":
                    // Enable parallel sub-agent delegation (parity with /psub).
                    AllowParallelSubAgents = true;
                    break;
                case "--verbose":
                    // Verbose MCP/init logging (parity with /verbose).
                    _verboseToggle = true;
                    break;
                case "--swarm":
                    _startupCommand = "/swarm";
                    break;
                case "--pswarm":
                    _startupCommand = "/pswarm";
                    break;
                case "--stateless":
                    _startupCommand = "/stateless";
                    break;
                case "--teams":
                    _startupCommand = "/teams";
                    break;
                case "--agent-mode":
                    // Boot straight into a single-agent session (parity with /agent). Combine
                    // with --agent <name> to also pick which agent drives it.
                    _startupCommand = "/agent";
                    break;
                case "--clear":
                    Console.Clear();
                    break;

                case "--report":
                    {
                        var v = NextValue(args, ref i);
                        // If next value looks like another flag or is missing, report all
                        if (v == null || v.StartsWith("-"))
                        {
                            if (v != null) i--; // put it back
                            reportAll = true;
                        }
                        else
                        {
                            reportSessionId = v;
                        }
                        break;
                    }
                case "--provider":
                    {
                        var v = NextValue(args, ref i);
                        if (v != null && !v.StartsWith("-"))
                        {
                            var config = LoadConfig(configPath: ConfigPath);
                            var match = config.LlmProviders.FirstOrDefault(p =>
                                p.Name.Equals(v, StringComparison.OrdinalIgnoreCase));

                            if (match != null)
                            {
                                ActiveProvider = match;
                                MuxConsole.WriteSuccess($"Provider set to: {match.Name}");
                            }
                            else
                            {
                                MuxConsole.WriteWarning($"No provider found matching: {v}");
                            }
                        }
                        else
                        {
                            if (v != null) i--;
                            MuxConsole.WriteWarning("--provider requires a name (e.g. --provider ollama)");
                        }
                        break;
                    }
                case "--cfg":
                case "--swarmcfg":
                    NextValue(args, ref i); //handled in program bootstrap
                    break;
                case "--workspace":
                case "--ws":
                    var wsPath = NextValue(args, ref i);
                    if (!string.IsNullOrWhiteSpace(wsPath))
                    {
                        try { PlatformContext.WorkspaceRoot = wsPath; }
                        catch { MuxConsole.WriteWarning($"Invalid --workspace path: {wsPath}"); }
                    }
                    break;
                case "--workflow":
                case "--wf":
                    var wfPath = NextValue(args, ref i);
                    if (!string.IsNullOrWhiteSpace(wfPath))
                    {
                        var wf = WorkflowHelper.Load(wfPath);
                        MuxConsole.WriteSuccess($"Loaded workflow: {wf.Name} ({wf.Steps.Count} steps)");
                        WorkflowHelper.RunWorkflow(wf);
                    }
                    break;
                case "--serve":
                    if (Common.TryNextUInt(args, ref i, out var sp)) ServePort = (int)sp;
                    else ServePort = 6723;
                    break;
                case "--daemon":
                    daemonMode = true;
                    break;
                case "--update":
                    updateMode = true;
                    break;
                case "--register":
                    ServiceRegistration.Register(args);
                    Environment.Exit(0);
                    break;
                case "--remove":
                    ServiceRegistration.Remove();
                    Environment.Exit(0);
                    break;
                default:
                    if (a.StartsWith("-", StringComparison.Ordinal))
                        MuxConsole.WriteWarning($"Unknown flag: {a}");
                    break;
            }
        }

        return new ParsedArgs(
            goal,
            continuous,
            parallel,
            maxParallelism,
            goalId,
            minDelay,
            persistInterval,
            sessionRetention,
            prodMode,
            mcpStrictOverride,
            dockerExecOverride,
            reportSessionId,
            reportAll,
            agentName,
            ServePort,
            daemonMode,
            acpMode,
            updateMode
        );
    }

    private static string ResolveConfigValue(string value, string baseDir)
    {
        if (string.IsNullOrEmpty(value)) return value;

        value = value.Replace("{BASE_DIR}", baseDir);

        var regex = new System.Text.RegularExpressions.Regex(@"\{([A-Z_][A-Z0-9_]*)\}");
        value = regex.Replace(value, m =>
        {
            var envName = m.Groups[1].Value;
            return Environment.GetEnvironmentVariable(envName) ?? m.Value;
        });

        return value;
    }

    private static Dictionary<string, string> ResolveEnvVariables(
        Dictionary<string, string?>? env,
        string baseDir,
        string serverName,
        bool verbose = true)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (env == null || env.Count == 0) return resolved;

        foreach (var (key, rawValue) in env)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;

            var value = ResolveConfigValue(rawValue ?? string.Empty, baseDir).Trim();

            if (value.Length == 0)
            {
                resolved[key] = "";
                if (verbose) MuxConsole.WriteWarning($"{serverName} env '{key}' is empty.");
                continue;
            }

            var envVarLookup = Environment.GetEnvironmentVariable(value);
            if (!string.IsNullOrEmpty(envVarLookup))
            {
                resolved[key] = envVarLookup;

                if (verbose)
                    MuxConsole.WriteMuted($"{serverName} '{key}' sourced from env var '{value}' ({MaskSecret(envVarLookup)}).");

                continue;
            }

            resolved[key] = value;

            if (verbose)
            {
                if (Common.LooksLikeEnvVarName(value))
                    MuxConsole.WriteWarning($"{serverName} env '{key}' looks like env-var name '{value}', but it is not set. Treating as literal.");

                if (LooksLikePath(value))
                    MuxConsole.WriteWarning($"{serverName} env '{key}' resolved to something that looks like a filesystem path. Check config.");

                MuxConsole.WriteMuted($"{serverName} '{key}' using literal value ({MaskSecret(value)}).");
            }
        }

        return resolved;
    }

    private static bool LooksLikePath(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;

        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z]:\\"))
            return true;

        if (s.StartsWith("/"))
            return true;

        if (s.StartsWith("\\\\"))
            return true;

        if (s.Contains(Path.PathSeparator) && (s.Contains("\\") || s.Contains("/")))
            return true;

        return false;
    }

    private static string MaskSecret(string s)
    {
        if (string.IsNullOrEmpty(s)) return "<empty>";
        if (s.Length <= 6) return "<redacted>";
        return $"{s[..2]}***{s[^2..]} (len={s.Length})";
    }

    public static async Task<bool> InitMcpServersAsync(AppConfig config)
    {
        McpTools = new List<McpClientTool>();

        var baseDir = PlatformContext.BaseDirectory;

        // Patch storage paths from config before server init
        if (!string.IsNullOrEmpty(config.Filesystem?.ChromaDbPath))
        {
            Directory.CreateDirectory(config.Filesystem.ChromaDbPath);

            if (config.McpServers.TryGetValue("ChromaDB", out var chromaCfg) && chromaCfg.Enabled)
            {
                chromaCfg.Args = new[]
                {
                    "chroma-mcp", "--client-type", "persistent",
                    "--data-dir", config.Filesystem.ChromaDbPath
                };
            }
        }

        if (!string.IsNullOrEmpty(config.Filesystem?.KnowledgeGraphPath))
        {
            var kgDir = Path.GetDirectoryName(config.Filesystem.KnowledgeGraphPath);
            if (!string.IsNullOrEmpty(kgDir))
                Directory.CreateDirectory(kgDir);

            if (config.McpServers.TryGetValue("Memory", out var memCfg) && memCfg.Enabled)
            {
                memCfg.Env ??= new Dictionary<string, string?>();
                memCfg.Env["MEMORY_FILE_PATH"] = config.Filesystem.KnowledgeGraphPath;
            }
        }

        // The native in-house REPL/shell tools (ReplShellTools) replace the mcp-async-repl server,
        // which used ONE shared worker/connection across all agents and clashed under parallel
        // sub-agents. Skip connecting any stdio server that launches it (by command or args) so
        // existing configs that still list it do not double-register the same tool names. This is
        // a runtime safety net; the bundled template no longer ships the entry.
        static bool IsNativeReplShellServer(McpServerConfig c)
        {
            bool Has(string? s) => s is not null && s.Contains("mcp-async-repl", StringComparison.OrdinalIgnoreCase);
            if (Has(c.Command)) return true;
            if (c.Args is not null)
                foreach (var a in c.Args) if (Has(a)) return true;
            return false;
        }

        // Native in-house toolsets (Filesystem + shell/REPL) are bound in-process via NativeToolRegistry,
        // NOT spawned as MCP subprocesses. Skip connecting any server that (a) carries the
        // native-runtime-tools marker, (b) is the legacy npx @modelcontextprotocol/server-filesystem
        // entry (now satisfied natively - existing configs upgrade transparently), or (c) launches the
        // old mcp-async-repl. This removes default subprocesses (faster startup) without losing surface.
        bool SkipBecauseNative(McpServerConfig c) =>
            IsNativeReplShellServer(c)
            || MuxSwarm.Utils.NativeTools.NativeToolRegistry.IsNativeEntry(c)
            || MuxSwarm.Utils.NativeTools.NativeToolRegistry.IsLegacyFilesystemEntry(c);

        var enabledServers = config.McpServers
            .Where(kvp => kvp.Value.Enabled && !SkipBecauseNative(kvp.Value))
            .ToList();
        int enabledCount = enabledServers.Count;

        // Connect to enabled servers concurrently but THROTTLED: an unbounded
        // Task.WhenAll over ~14 stdio servers spawns every subprocess at once
        // (uvx/npx/python package resolution + imports), and that spawn storm
        // saturates CPU/disk/thread pool right as the TUI paints and any startup
        // prompt reads keys - the whole app feels sluggish. A gate of 4 keeps the
        // pipeline full (spawns are mostly I/O-bound waits) while flattening the
        // instantaneous load spike. Shared collections are still populated
        // sequentially after the gather to avoid races on McpClients / McpTools.
        // Success is logged only (no console output); failures are reported to
        // the console inside ConnectMcpServerAsync.
        using var connectGate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(
            enabledServers.Select(async kvp =>
            {
                await connectGate.WaitAsync();
                try { return await ConnectMcpServerAsync(kvp.Key, kvp.Value, baseDir, config); }
                finally { connectGate.Release(); }
            }));

        int successCount = 0;
        foreach (var result in results)
        {
            if (result is null)
                continue;

            McpClients[result.Name] = result.Client;
            foreach (var tool in result.Tools)
                McpTools?.Add(tool);

            successCount++;
            OtelLogger.Info($"Loaded {result.Tools.Count} tools from {result.Name}{(result.IsHttp ? " (HTTP)" : "")}");
        }

        if (VerboseInit)
        {
            foreach (var kvp in config.McpServers)
                if (!kvp.Value.Enabled)
                    MuxConsole.WriteMuted($"Skipping {kvp.Key} (disabled)");
        }

        if (_mcpStrictMode)
            return enabledCount > 0 && successCount == enabledCount;

        return successCount > 0;
    }

    private sealed record McpInitResult(string Name, McpClient Client, IReadOnlyList<McpClientTool> Tools, bool IsHttp);

    // Resolve the MCP connect timeout from config with a 5s floor (a sub-5s value would make even a
    // healthy stdio spawn flaky). Non-positive / unset falls back to the AppConfig default (60s).
    private static int McpConnectTimeoutSeconds(AppConfig config)
    {
        int v = config?.McpConnectTimeoutSeconds ?? 90;
        return v <= 0 ? 90 : Math.Max(5, v);
    }

    private static async Task<McpInitResult?> ConnectMcpServerAsync(string name, McpServerConfig serverConfig, string baseDir, AppConfig config)
    {
        try
        {
            if (serverConfig.Type.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                var urlTemplate = serverConfig.Url ?? "";
                var resolvedUrl = ResolveConfigValue(urlTemplate, baseDir);

                var resolvedEnv = ResolveEnvVariables(serverConfig.Env, baseDir, name, verbose: VerboseInit);

                foreach (var (envKey, envValue) in resolvedEnv)
                {
                    if (string.IsNullOrEmpty(envValue)) continue;
                    resolvedUrl = resolvedUrl.Replace($"${{{envKey}}}", envValue);
                }

                if (resolvedUrl.Contains("${") && VerboseInit)
                    MuxConsole.WriteWarning($"{name} URL still contains unsubstituted tokens: {resolvedUrl}");

                var endpoint = new Uri(resolvedUrl);

                var httpOptions = new HttpClientTransportOptions
                {
                    Name = name,
                    Endpoint = endpoint,
                    AdditionalHeaders = serverConfig.Headers
                };

                var httpTransport = new HttpClientTransport(httpOptions);
                using var httpCts = new CancellationTokenSource(TimeSpan.FromSeconds(McpConnectTimeoutSeconds(config)));
                var httpClient = await McpClient.CreateAsync(httpTransport, cancellationToken: httpCts.Token);

                var httpTools = await httpClient.ListToolsAsync(cancellationToken: httpCts.Token);
                var namedHttpTools = httpTools.Select(t => t.WithName($"{name}_{t.Name}")).ToList();

                return new McpInitResult(name, httpClient, namedHttpTools, IsHttp: true);
            }
            else
            {
                var command = ResolveConfigValue(serverConfig.Command ?? "", baseDir);
                var args = serverConfig.Args?.Select(a => ResolveConfigValue(a, baseDir)).ToArray() ?? Array.Empty<string>();

                if (name == "Filesystem" && config.Filesystem?.AllowedPaths?.Count > 0)
                {
                    args = args
                        .Concat(config.Filesystem.AllowedPaths)
                        .ToArray();
                }

                var env = ResolveEnvVariables(serverConfig.Env, baseDir, name, verbose: VerboseInit);

                var stdioOptions = new StdioClientTransportOptions
                {
                    Name = name,
                    Command = command,
                    Arguments = args,
                    EnvironmentVariables = env!
                };

                var stdioTransport = new StdioClientTransport(stdioOptions);

                if (VerboseInit)
                {
                    MuxConsole.WriteMuted($"Starting {name}: {command} {string.Join(" ", args)}");
                    foreach (var (k, v) in env)
                        MuxConsole.WriteMuted($"  ENV: '{k}' = '{MaskSecret(v)}'");
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(McpConnectTimeoutSeconds(config)));
                var stdioClient = await McpClient.CreateAsync(stdioTransport, cancellationToken: cts.Token);

                var stdioTools = await stdioClient.ListToolsAsync(cancellationToken: cts.Token);
                var namedStdioTools = stdioTools.Select(t => t.WithName($"{name}_{t.Name}")).ToList();

                return new McpInitResult(name, stdioClient, namedStdioTools, IsHttp: false);
            }
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("EACCES", StringComparison.OrdinalIgnoreCase))
            {
                MuxConsole.WriteError($"Failed to connect to {name}: permission denied.");

                if (PlatformContext.IsWindows)
                {
                    MuxConsole.WriteMuted("  Try running your terminal as Administrator, or reinstall Node.js with the default settings.");
                }
                else if (PlatformContext.IsMac)
                {
                    MuxConsole.WriteMuted("  Try: sudo chown -R $(whoami) ~/.npm");
                    MuxConsole.WriteMuted("  Or reinstall Node via Homebrew: brew install node");
                }
                else
                {
                    MuxConsole.WriteMuted("  Try Running: sudo chown -R $(whoami) ~/.npm ~/.npm-global");
                    MuxConsole.WriteMuted("  Or configure a user-level prefix: npm config set prefix '~/.npm-global'");
                    MuxConsole.WriteMuted($"  Then add to your {(PlatformContext.IsLinux ? "~/.bashrc" : "~/.zshrc")}: export PATH=\"$HOME/.npm-global/bin:$PATH\"");
                }
            }
            else
            {
                MuxConsole.WriteError($"Failed to connect to {name}: {ex.Message}");
            }
            return null;
        }
    }

    private static void InitLlmProvider()
    {
        if (ActiveProvider != null)
            return;

        var config = LoadConfig(configPath: ConfigPath);

        var provider = config.LlmProviders.FirstOrDefault(p => p.Enabled);
        if (provider == null)
        {
            MuxConsole.WriteWarning("No LLM provider is enabled. Run /setup to configure.");
            return;
        }

        ActiveProvider = provider;

        // CLIProxyAPI sidecar provider: its bearer key lives in a persisted file and is exported to
        // the env var lazily at sidecar spawn. Export it from disk NOW (cheap, no spawn) so the
        // env-var check below passes - otherwise every launch after /login warns + prompts for a key
        // that is never user-supplied. The sidecar itself is still started lazily on first request.
        if (string.Equals(provider.ApiKeyEnvVar, MuxSwarm.Utils.Proxy.CliProxyManager.ClientKeyEnvVar, StringComparison.Ordinal))
            MuxSwarm.Utils.Proxy.CliProxyManager.ExportPersistedKey();

        if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnvVar))
        {
            var apiKeyValue = Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar);
            if (string.IsNullOrWhiteSpace(apiKeyValue))
            {
                MuxConsole.WriteWarning($"API key env var '{provider.ApiKeyEnvVar}' is not set.");

                MuxConsole.WriteBody("You can:");
                if (PlatformContext.IsWindows)
                {
                    MuxConsole.WriteMuted($"  1. Set permanently (PowerShell):  [Environment]::SetEnvironmentVariable(\"{provider.ApiKeyEnvVar}\", \"your-key\", \"User\")");
                    MuxConsole.WriteMuted($"     Or (cmd):  setx {provider.ApiKeyEnvVar} \"your-key\"");
                }
                else
                {
                    MuxConsole.WriteMuted($"  1. Set permanently:  echo 'export {provider.ApiKeyEnvVar}=\"your-key\"' >> ~/.{(PlatformContext.IsMac ? "zshrc" : "bashrc")}");
                }
                MuxConsole.WriteBody("  2. Set it for this session only:");
                var input = MuxConsole.Prompt("Paste API key (hidden) or press Enter if no key is necessary", secret: true);
                if (!string.IsNullOrEmpty(input))
                {
                    Environment.SetEnvironmentVariable(provider.ApiKeyEnvVar, input, EnvironmentVariableTarget.Process);
                    apiKeyValue = input;
                    MuxConsole.WriteSuccess("API key set for this session.");
                }
                else
                {
                    MuxConsole.WriteWarning("No API key provided. LLM Invocations may fail unless no key is needed.");
                }
            }

            // MuxConsole.WriteMuted($"API key: {MaskSecret(apiKeyValue)}");
        }

        var rawEndpoint = provider.Endpoint ?? "";
        var normalizedEndpoint = NormalizeOpenAiEndpoint(rawEndpoint);

        MuxConsole.WriteInfo($"Provider: {provider.Name}");
        MuxConsole.WriteInfo($"Endpoint: {normalizedEndpoint}");
        OtelLogger.Info($"Initialized LLM Provider: {provider.Name}, endpoint is {normalizedEndpoint}");
    }

    private static string NormalizeOpenAiEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint is empty.");

        var uri = new Uri(endpoint.Trim().TrimEnd('/'));

        var segs = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        static bool EndsWith(List<string> s, params string[] tail)
        {
            if (s.Count < tail.Length) return false;
            for (int i = 0; i < tail.Length; i++)
            {
                if (!s[s.Count - tail.Length + i].Equals(tail[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        if (EndsWith(segs, "chat", "completions"))
            segs.RemoveRange(segs.Count - 2, 2);
        else if (EndsWith(segs, "responses"))
            segs.RemoveAt(segs.Count - 1);
        else if (EndsWith(segs, "completions"))
            segs.RemoveAt(segs.Count - 1);

        var builder = new UriBuilder(uri)
        {
            Path = "/" + string.Join('/', segs),
            Query = "",
            Fragment = ""
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static OpenAIClient CreateOpenAiClient()
    {
        if (ActiveProvider == null)
            InitLlmProvider();

        var provider = ActiveProvider ?? LoadConfig(configPath: ConfigPath).LlmProviders.FirstOrDefault(p => p.Enabled);

        var apiKey = !string.IsNullOrWhiteSpace(provider?.ApiKeyEnvVar)
            ? Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar) ?? "no-key"
            : "no-key";

        var normalized = NormalizeOpenAiEndpoint(provider?.Endpoint ?? "https://openrouter.ai/api/v1");
        var opts = new OpenAIClientOptions
        {
            Endpoint = new Uri(normalized),
            NetworkTimeout = TimeSpan.FromSeconds(ExecutionLimits.Current.ActivityTimeoutSeconds)
        };

        if (provider?.Headers?.Count > 0)
        {
            var resolvedHeaders = new Dictionary<string, string>();
            foreach (var kvp in provider.Headers)
                resolvedHeaders[kvp.Key] = Common.ExpandEnvVars(kvp.Value);
            
            opts.AddPolicy(new CustomHeaderPolicy(resolvedHeaders), PipelinePosition.PerCall);
        }

        // CLIProxyAPI Claude-path fixes (internalized from the external cliproxy-filter shim): when the
        // active provider is the local cliproxy sidecar, strip sampling params the Claude/OAuth backend
        // rejects and fold system/developer messages into the first user turn (else the bridge drops them
        // and the agent loses all system context). Gated to Claude/Opus models inside the policy; other
        // models + other providers are untouched.
        if (string.Equals(provider?.ApiKeyEnvVar, MuxSwarm.Utils.Proxy.CliProxyManager.ClientKeyEnvVar, StringComparison.Ordinal))
            opts.AddPolicy(new CliProxyClaudePolicy(), PipelinePosition.PerCall);

        return new OpenAIClient(new ApiKeyCredential(apiKey), opts);
    }

    public static IChatClient CreateChatClient(string modelId, ChatOptions? chatOptions = null)
        => CreateChatClient(modelId, chatOptions, wrapMidTurnReflection: false, reflectionAgentName: null);

    /// <summary>
    /// Build a chat client. When <paramref name="wrapMidTurnReflection"/> is true the client is
    /// wrapped (INSIDE the function-invocation middleware) so freshly-gathered deep-memory deltas are
    /// injected on every model<->tool round-trip, not just at user-turn boundaries. Used for the LEAD
    /// single-agent client only; no-op pass-through in standard mode.
    /// </summary>
    public static IChatClient CreateChatClient(string modelId, ChatOptions? chatOptions,
        bool wrapMidTurnReflection, string? reflectionAgentName)
    {
        // Cap the function-invocation middleware's model->tool round-trips per turn. The default
        // (unbounded-ish SDK default) made long autonomous tool chains stop mid-task with no error
        // surfaced. ExecutionLimits.MaxToolIterationsPerTurn defaults high; <= 0 means unlimited.
        int toolIters = ExecutionLimits.Current.MaxToolIterationsPerTurn;

        // Local CLIProxyAPI sidecar: subscription providers (Claude/Codex/...) route through a managed
        // loopback proxy that is a plain OpenAI-compatible endpoint. When the active provider points at it
        // (apiKeyEnvVar == the manager's key var), ensure the detached sidecar is up (lazy, reused across
        // sessions) and its api key is exported before building the byte-identical OpenAI client below.
        if (ActiveProvider == null) InitLlmProvider();
        if (string.Equals(ActiveProvider?.ApiKeyEnvVar, MuxSwarm.Utils.Proxy.CliProxyManager.ClientKeyEnvVar, StringComparison.Ordinal))
        {
            try { MuxSwarm.Utils.Proxy.CliProxyManager.EnsureRunningAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { MuxConsole.WriteWarning($"CLIProxyAPI sidecar unavailable: {ex.Message}"); }
        }

        var client = CreateOpenAiClient()
            .GetChatClient(modelId)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(configure: c =>
                c.MaximumIterationsPerRequest = toolIters > 0 ? toolIters : int.MaxValue)
            // INSIDE function-invocation: if the endpoint rejects the top reasoning tier
            // (ExtraHigh -> wire "xhigh"), transparently retry that single call one tier lower
            // instead of failing the turn. No-op unless ExtraHigh was actually requested.
            .Use(inner => new MuxSwarm.Utils.ReasoningEffortFallbackClient(inner, modelId))
            .Build();

        // Lead-only: wrap so mid-turn (post-tool-result) reflection deltas reach the model on every
        // round-trip. The wrapper sits INSIDE the function-invocation loop (built above), so it is
        // invoked per tool round-trip. Inert in standard mode (BuildDelta returns empty).
        if (wrapMidTurnReflection)
            client = new MuxSwarm.Utils.Memory.MidTurnReflectionClient(client, reflectionAgentName ?? "Agent");

        return client;
    }
    
    private async Task<int> HandleParsedRun(ParsedArgs? parsed)
    {
        var cliCts = GetOrResetCts();

        var agentModels = Common.LoadAgentModels();
        if (parsed != null && !string.IsNullOrWhiteSpace(parsed.AgentName))
        {
            var agentDefs = Common.GetAgentDefinitions(PlatformContext.SwarmPath);
            var matched = agentDefs.FirstOrDefault(d => d.Name.Equals(parsed.AgentName, StringComparison.OrdinalIgnoreCase));

            if (matched == null)
            {
                var available = string.Join(", ", agentDefs.Select(d => d.Name).Distinct());
                MuxConsole.WriteError($"No agent found matching '{parsed.AgentName}'. Available: {available}");
                return 1;
            }

            SingleAgentOrchestrator.AgentDef = matched;
            var mId = agentModels.GetValueOrDefault(matched.Name, agentModels["Orchestrator"]);

            await SingleAgentOrchestrator.ChatAgentAsync(
                client: CreateChatClient(mId),
                cancellationToken: cliCts.Token,
                mcpTools: McpTools,
                chatClientFactory: modelId => CreateChatClient(modelId),
                incomingGoal: parsed.Goal,
                continuous: parsed.Continuous,
                shouldPlan: ShouldPlan, 
                goalId: parsed.GoalId,
                autoCompactTokenThreshold: SwarmConfig?.CompactionAgent?.AutoCompactTokenThreshold,
                minDelaySeconds: parsed.MinDelay,
                persistIntervalSeconds: parsed.PersistInterval,
                sessionRetention: parsed.SessionRetention,
                allowSubAgents: AllowSubagents,
                allowParallelSubAgents: AllowParallelSubAgents
                );
            return Environment.ExitCode;
        }

        if (parsed.Parallel)
        {
            await ParallelSwarmOrchestrator.RunAsync(
                chatClientFactory: modelId => CreateChatClient(modelId),
                mcpTools: (McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
                agentModels: agentModels,
                maxDegreeOfParallelism: parsed.MaxParallelism,
                prodMode: parsed.ProdMode,
                incomingGoal: parsed.Goal,
                continuous: parsed.Continuous,
                shouldPlan: ShouldPlan, 
                goalId: parsed.GoalId,
                minDelaySeconds: parsed.MinDelay,
                persistIntervalSeconds: parsed.PersistInterval,
                sessionRetention: parsed.SessionRetention,
                cancellationToken: cliCts.Token
            );
            return Environment.ExitCode;
        }

        await MultiAgentOrchestrator.RunAsync(
            chatClientFactory: modelId => CreateChatClient(modelId),
            mcpTools: (McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
            agentModels: agentModels,
            prodMode: parsed.ProdMode,
            incomingGoal: parsed.Goal,
            continuous: parsed.Continuous,
            shouldPlan: ShouldPlan, 
            goalId: parsed.GoalId,
            minDelaySeconds: parsed.MinDelay,
            persistIntervalSeconds: parsed.PersistInterval,
            sessionRetention: parsed.SessionRetention,
            cancellationToken: cliCts.Token
            );
        return Environment.ExitCode;
    }

    private void DisableTools()
    {
        MuxConsole.WriteBody("Enter the index or range of tools to disable (e.g. 1-10):");
        var input = MuxConsole.Prompt("Range");
        var parts = input.Split('-');

        if (parts.Length == 2 &&
            int.TryParse(parts[0], out int start) &&
            int.TryParse(parts[1], out int end))
        {
            var count = end - start + 1;

            if (McpTools != null && start >= 0 && end < McpTools.Count)
            {
                for (int i = 0; i < count; i++)
                    McpTools.RemoveAt(start);

                MuxConsole.WriteSuccess($"Disabled tools {start} through {end}.");
                return;
            }
        }

        MuxConsole.WriteWarning("Failed to disable tools — invalid format or range.");
    }
}