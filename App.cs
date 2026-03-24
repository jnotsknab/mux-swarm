using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using MuxSwarm.State;
using MuxSwarm.Utils;
using static MuxSwarm.Setup.Setup;

namespace MuxSwarm;

public class App
{
    private static readonly string BaseDir = PlatformContext.BaseDirectory;
    private static readonly string ConfigPath = PlatformContext.ConfigPath;
    private static bool _showToolCallResults;
    private static IList<McpClientTool>? _mcpTools;
    private static string? _cliModelOverride;
    private static readonly Dictionary<string, McpClient> McpClients = new();
    private static bool _watchDogEnabled;
    private static readonly bool VerboseInit = Debugger.IsAttached || string.Equals(Environment.GetEnvironmentVariable("MUXSWARM_VERBOSE"), "1", StringComparison.OrdinalIgnoreCase);
    private static bool _mcpStrictMode = !string.Equals(Environment.GetEnvironmentVariable("MUXSWARM_MCP_STRICT"), "0", StringComparison.OrdinalIgnoreCase);
    private static CancellationTokenSource _cts = new();
    private static readonly Lock CtsLock = new();
    private static int servePort;

    public static AppConfig Config = new();
    public static SwarmConfig SwarmConfig = new();
    public static ProviderConfig? ActiveProvider;


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

        AppDomain.CurrentDomain.ProcessExit += (_, e) =>
        {
            HookWorker.Stop();
            ProcessCleanup.Instance.Shutdown();
        };

        var hbPath = Path.Combine(BaseDir, "watchdog.heartbeat");
        if (File.Exists(hbPath))
            File.WriteAllText(hbPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());

        Config = LoadConfig(ConfigPath);


        MuxConsole.WriteSplashScreen(version: "0.8.2");

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

        //Shouldnt be null
        SwarmConfig = LoadSwarm() ?? throw new InvalidOperationException();

        //Load and populate exec limits from swarm cfg
        FetchSetExecLimits();

        InitLlmProvider();
        SkillLoader.LoadSkills();

        bool servInitResult = InitMcpServersAsync(Config).GetAwaiter().GetResult();
        if (!servInitResult)
        {
            MuxConsole.WriteError(_mcpStrictMode
                ? "Failed to connect to all enabled MCP servers (strict mode). Exiting."
                : "Failed to connect to any MCP servers. Exiting.");

            Environment.Exit(1);
        }

        MuxConsole.WriteSuccess(_mcpStrictMode
            ? "Established connection to all enabled MCP servers."
            : "Established connection to at least one MCP server.");

        HookWorker.Start(SwarmConfig.Hooks ?? []);
    }

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
        var parsed = ParseArgs(args);
        
        if (_watchDogEnabled)
            Common.StartExternalWatchdog(args: args, baseDir: BaseDir, cts: new CancellationTokenSource());
        
        if (parsed.ServePort > 0)
            await ServeMode.StartAsync((int)parsed.ServePort);

        if (MuxConsole.StdioMode)
            StdinCancelMonitor.Start();

        if (parsed.DockerExecOverride.HasValue)
        {
            Config.IsUsingDockerForExec = parsed.DockerExecOverride.Value;
            MuxConsole.WriteInfo($"Docker Exec set to: {Config.IsUsingDockerForExec}");
        }

        if (parsed.McpStrictOverride.HasValue)
        {
            _mcpStrictMode = parsed.McpStrictOverride.Value;
            MuxConsole.WriteInfo($"MCP Strict Mode set to: {_mcpStrictMode}");
        }

        if (!string.IsNullOrWhiteSpace(parsed.Goal))
            return await HandleParsedRun(parsed);

        if (parsed.ReportAll || parsed.ReportSessionId != null)
        {
            CliCmdUtils.GenerateSessionReports(parsed.ReportSessionId);
            return Environment.ExitCode;
        }
        
        DaemonRunner? daemon = null;
        if (parsed.DaemonMode && Config.Daemon is { Enabled: true })
        {
            daemon = new DaemonRunner(Config.Daemon);

            if (servePort > 0)
            {
                foreach (var trigger in Config.Daemon.Triggers
                             .Where(t => t.Type == "status" && t.Restart &&
                                         t.Check != null && t.Check.Contains($":{servePort}")))
                {
                    daemon.RegisterRestart(trigger.Check!,
                        () => ServeMode.StartAsync(servePort));
                }
            }

            daemon.Start(
                chatClientFactory: modelId => CreateChatClient(modelId),
                mcpTools: _mcpTools!.Cast<AITool>().ToList(),
                agentModels: LoadAgentModels());

            MuxConsole.WriteInfo("[Daemon] Running in background.");
        }
        
        // Interactive loop
        while (!Environment.HasShutdownStarted)
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
                        }
                    }
                    continue;
                }
            }

            string? userInput = MuxConsole.ReadInput();

            if (string.IsNullOrEmpty(userInput))
                continue;

            if (userInput.Trim() == "__CANCEL__")
            {
                lock (CtsLock)
                {
                    if (!_cts.IsCancellationRequested)
                    {
                        _cts.Cancel();
                        MuxConsole.WriteInfo("Cancelled by client.");
                    }
                }
                continue;
            }

            switch (userInput)
            {
                case "/help":
                    MuxConsole.PrintHelp(Help.HelpText);
                    break;

                case "/exit":
                    MuxConsole.WriteInfo("Shutting down gracefully...");
                    _cts.Cancel();
                    ProcessCleanup.Instance.Dispose();
                    Environment.Exit(0);
                    break;

                case "/setup":
                    bool setupSuccess = RunSetup();
                    if (setupSuccess) MuxConsole.WriteSuccess("Setup complete!");
                    break;

                case "/swarm":
                    Config = LoadConfig(ConfigPath);
                    var maModels = LoadAgentModels();
                    var maCts = GetOrResetCts();

                    await MultiAgentOrchestrator.RunAsync(
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        mcpTools: (_mcpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
                        agentModels: maModels,
                        cancellationToken: maCts.Token
                    );
                    break;

                case "/pswarm":
                    Config = LoadConfig(ConfigPath);
                    var pModels = LoadAgentModels();
                    var pCts = GetOrResetCts();

                    await ParallelSwarmOrchestrator.RunAsync(
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        mcpTools: (_mcpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
                        agentModels: pModels,
                        cancellationToken: pCts.Token
                    );
                    break;

                case "/agent":
                    Config = LoadConfig(ConfigPath);
                    var singleAgentModel = LoadSingleAgentModel();
                    var agentCts = GetOrResetCts();

                    await SingleAgentOrchestrator.ChatAgentAsync(
                        client: CreateChatClient(singleAgentModel),
                        agentCts.Token,
                        maxIterations: 3,
                        mcpTools: _mcpTools,
                        showToolResultCalls: _showToolCallResults,
                        chatClientFactory: modelId => CreateChatClient(modelId)
                    );
                    break;

                case "/stateless":
                    Config = LoadConfig(ConfigPath);
                    var statelessAgent = LoadSingleAgentModel();
                    var statelessAgentCts = GetOrResetCts();

                    await SingleAgentOrchestrator.ChatAgentAsync(
                        client: CreateChatClient(statelessAgent),
                        statelessAgentCts.Token,
                        maxIterations: 3,
                        mcpTools: _mcpTools,
                        showToolResultCalls: _showToolCallResults,
                        chatClientFactory: modelId => CreateChatClient(modelId),
                        persistSession: false
                    );
                    break;

                case "/workflow":
                    CliCmdUtils.HandleInteractiveWorkflow();
                    break;
                case "/resume":
                    var resumeData = CliCmdUtils.HandleSessionResume();
                    if (resumeData.HasValue)
                    {
                        Config = LoadConfig(ConfigPath);
                        var resumeModel = LoadSingleAgentModel();
                        var resumeCts = GetOrResetCts();

                        await SingleAgentOrchestrator.ChatAgentAsync(
                            client: CreateChatClient(resumeModel),
                            resumeCts.Token,
                            maxIterations: 3,
                            mcpTools: _mcpTools,
                            showToolResultCalls: _showToolCallResults,
                            chatClientFactory: modelId => CreateChatClient(modelId),
                            resumedSession: resumeData.Value.data,
                            resumedSessionDir: resumeData.Value.sessionDir
                        );
                    }
                    break;

                case "/setmodel":
                    CliCmdUtils.HandleModelSwap();
                    break;

                case "/tools":
                    if (_mcpTools != null) Common.LogAvailableTools(_mcpTools);
                    break;

                case "/model":
                    var currentModel = LoadSingleAgentModel();
                    MuxConsole.WriteInfo($"Single agent model: {currentModel}");
                    var models = LoadAgentModels();
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
                    Console.Clear();
                    break;

                case "/status":
                    CliCmdUtils.HandleStatus(_mcpTools, LoadAgentModels());
                    break;

                case "/disabletools":
                    DisableTools();
                    break;

                case "/dockerexec":
                    CliCmdUtils.HandleDockerExec(ConfigPath);
                    break;
                case "/delimiter":
                    CliCmdUtils.HandleMultiDelimiterToggle();
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

                case "/memory":
                    CliCmdUtils.ShowKnowledgeGraph(McpClients, _mcpTools);
                    break;

                case "/skills":
                    CliCmdUtils.ShowLoadedSkills();
                    break;

                case "/reloadskills":
                    CliCmdUtils.ReloadSkills();
                    break;

                case "/refresh":
                    await CliCmdUtils.ReloadMcpServersAsync(InitMcpServersAsync, ConfigPath);
                    CliCmdUtils.ReloadSkills();
                    break;

                case var cmd when cmd.StartsWith("/report"):
                    var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    CliCmdUtils.GenerateSessionReports(parts.Length > 1 ? parts[1] : null);
                    break;

                case "/sessions":
                    CliCmdUtils.ListSessions();
                    break;

                default:
                    if (userInput.StartsWith("/"))
                        MuxConsole.WriteWarning("Unknown command. Type /help.");
                    else
                        MuxConsole.WriteMuted("Type /help for commands.");
                    break;
            }
        }
        
        if (daemon != null)
            await daemon.DisposeAsync();
        
        return Environment.ExitCode;
    }

    private Dictionary<string, string> LoadAgentModels()
    {
        var agentModels = new Dictionary<string, string>();

        if (File.Exists(MultiAgentOrchestrator.SwarmConfPath))
        {
            var json = File.ReadAllText(MultiAgentOrchestrator.SwarmConfPath);
            var swarm = JsonSerializer.Deserialize<SwarmConfig>(json);

            if (swarm?.Orchestrator != null && !string.IsNullOrEmpty(swarm.Orchestrator.Model))
                agentModels["Orchestrator"] = swarm.Orchestrator.Model;

            if (swarm?.CompactionAgent != null && !string.IsNullOrEmpty(swarm.CompactionAgent.Model))
                agentModels["Compaction"] = swarm.CompactionAgent.Model;

            if (swarm?.Agents != null)
            {
                foreach (var agent in swarm.Agents)
                {
                    if (!string.IsNullOrEmpty(agent.Name) && !string.IsNullOrEmpty(agent.Model))
                        agentModels[agent.Name] = agent.Model;
                }
            }
        }

        if (!agentModels.ContainsKey("Orchestrator"))
            agentModels["Orchestrator"] = "x-ai/grok-4.1-fast";

        return agentModels;
    }

    private string LoadSingleAgentModel()
    {
        if (!string.IsNullOrEmpty(_cliModelOverride))
            return _cliModelOverride;

        if (SingleAgentOrchestrator.AgentDef != null)
        {
            var agentModels = LoadAgentModels();
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
        bool DaemonMode
    );

    private static string? NextValue(string[] args, ref int i)
        => (i + 1 < args.Length) ? args[++i] : null;

    private static bool? NextBool(string[] args, ref int i)
    {
        var v = (i + 1 < args.Length) ? args[i + 1] : null;
        if (v != null && bool.TryParse(v, out var b)) { i++; return b; }
        return null;
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
                case "--agent":
                    var an = NextValue(args, ref i);
                    if (!string.IsNullOrWhiteSpace(an))
                        agentName = an;
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
                    if (Common.TryNextUInt(args, ref i, out var sp)) servePort = (int)sp;
                    else servePort = 6723;
                    break;
                case "--daemon":
                    daemonMode = true;
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
            servePort,
            daemonMode
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

    private static async Task<bool> InitMcpServersAsync(AppConfig config)
    {
        _mcpTools = new List<McpClientTool>();

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

        int enabledCount = config.McpServers.Count(kvp => kvp.Value.Enabled);
        int successCount = 0;

        foreach (var (name, serverConfig) in config.McpServers)
        {
            if (!serverConfig.Enabled)
            {
                if (VerboseInit) MuxConsole.WriteMuted($"Skipping {name} (disabled)");
                continue;
            }

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

                    var options = new HttpClientTransportOptions
                    {
                        Name = name,
                        Endpoint = endpoint,
                        AdditionalHeaders = serverConfig.Headers
                    };

                    var transport = new HttpClientTransport(options);
                    var client = await McpClient.CreateAsync(transport);
                    McpClients[name] = client;

                    var tools = await client.ListToolsAsync();
                    foreach (var tool in tools)
                        _mcpTools?.Add(tool.WithName($"{name}_{tool.Name}"));

                    successCount++;
                    MuxConsole.WriteSuccess($"Loaded {tools.Count} tools from {name} (HTTP)");
                }
                else
                {
                    var command = ResolveConfigValue(serverConfig.Command ?? "", baseDir);
                    var args = serverConfig.Args?.Select(a => ResolveConfigValue(a, baseDir)).ToArray() ?? Array.Empty<string>();

                    if (name == "Filesystem" && config.Filesystem.AllowedPaths?.Count > 0)
                    {
                        args = args
                            .Concat(config.Filesystem.AllowedPaths)
                            .ToArray();
                    }

                    var env = ResolveEnvVariables(serverConfig.Env, baseDir, name, verbose: VerboseInit);

                    var options = new StdioClientTransportOptions
                    {
                        Name = name,
                        Command = command,
                        Arguments = args,
                        EnvironmentVariables = env!
                    };

                    var transport = new StdioClientTransport(options);

                    if (VerboseInit)
                    {
                        MuxConsole.WriteMuted($"Starting {name}: {command} {string.Join(" ", args)}");
                        foreach (var (k, v) in env)
                            MuxConsole.WriteMuted($"  ENV: '{k}' = '{MaskSecret(v)}'");
                    }

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);

                    McpClients[name] = client;

                    var tools = await client.ListToolsAsync();
                    foreach (var tool in tools)
                        _mcpTools?.Add(tool.WithName($"{name}_{tool.Name}"));

                    successCount++;
                    MuxConsole.WriteSuccess($"Loaded {tools.Count} tools from {name}");
                }
            }
            catch (Exception ex)
            {
                MuxConsole.WriteError($"Failed to connect to {name}: {ex.Message}");
            }
        }

        if (_mcpStrictMode)
            return enabledCount > 0 && successCount == enabledCount;

        return successCount > 0;
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

                var input = MuxConsole.PromptSecret("Paste API key (hidden)");
                if (!string.IsNullOrEmpty(input))
                {
                    Environment.SetEnvironmentVariable(provider.ApiKeyEnvVar, input, EnvironmentVariableTarget.Process);
                    apiKeyValue = input;
                    MuxConsole.WriteSuccess("API key set for this session.");
                }
                else
                {
                    MuxConsole.WriteError("No API key provided. Agent commands will fail until a key is set.");
                    return;
                }
            }

            // MuxConsole.WriteMuted($"API key: {MaskSecret(apiKeyValue)}");
        }

        var rawEndpoint = provider.Endpoint ?? "";
        var normalizedEndpoint = NormalizeOpenAiEndpoint(rawEndpoint);

        MuxConsole.WriteInfo($"Provider: {provider.Name}");
        MuxConsole.WriteInfo($"Endpoint: {normalizedEndpoint}");
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

    private static OpenAI.OpenAIClient CreateOpenAiClient()
    {
        if (ActiveProvider == null)
            InitLlmProvider();

        var provider = ActiveProvider ?? LoadConfig(configPath: ConfigPath).LlmProviders.FirstOrDefault(p => p.Enabled);

        var apiKey = !string.IsNullOrWhiteSpace(provider?.ApiKeyEnvVar)
            ? Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar) ?? "no-key"
            : "no-key";

        var normalized = NormalizeOpenAiEndpoint(provider?.Endpoint ?? "https://openrouter.ai/api/v1");

        return new OpenAI.OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(normalized) }
        );
    }

    private static IChatClient CreateChatClient(string modelId)
    {
        return CreateOpenAiClient()
            .GetChatClient(modelId)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }



    private async Task<int> HandleParsedRun(ParsedArgs? parsed)
    {
        var cliCts = GetOrResetCts();

        var agentModels = LoadAgentModels();
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
                mcpTools: _mcpTools,
                chatClientFactory: modelId => CreateChatClient(modelId),
                incomingGoal: parsed.Goal,
                continuous: parsed.Continuous,
                goalId: parsed.GoalId,
                minDelaySeconds: parsed.MinDelay,
                persistIntervalSeconds: parsed.PersistInterval,
                sessionRetention: parsed.SessionRetention,
                prodMode: parsed.ProdMode);
            return Environment.ExitCode;
        }

        if (parsed.Parallel)
        {
            await ParallelSwarmOrchestrator.RunAsync(
                chatClientFactory: modelId => CreateChatClient(modelId),
                mcpTools: (_mcpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
                agentModels: agentModels,
                maxDegreeOfParallelism: parsed.MaxParallelism,
                prodMode: parsed.ProdMode,
                incomingGoal: parsed.Goal,
                continuous: parsed.Continuous,
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
            mcpTools: (_mcpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
            agentModels: agentModels,
            prodMode: parsed.ProdMode,
            incomingGoal: parsed.Goal,
            continuous: parsed.Continuous,
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

            if (_mcpTools != null && start >= 0 && end < _mcpTools.Count)
            {
                for (int i = 0; i < count; i++)
                    _mcpTools.RemoveAt(start);

                MuxConsole.WriteSuccess($"Disabled tools {start} through {end}.");
                return;
            }
        }

        MuxConsole.WriteWarning("Failed to disable tools — invalid format or range.");
    }
}