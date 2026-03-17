using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MuxSwarm.Utils;

namespace MuxSwarm.Setup;

public static class Setup
{
    private static AppConfig _appConfig = new();

    public static readonly JsonSerializerOptions CfgSerialOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static void EnsureConfigInitialized()
    {
        _appConfig ??= new AppConfig();

        _appConfig.McpServers ??= new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
        _appConfig.LlmProviders ??= [];
        _appConfig.Filesystem ??= new FilesystemConfig();

        _appConfig.Filesystem.AllowedPaths ??= new List<string>();
    }

    /// <summary>
    /// Loads the application configuration from the specified file path or creates a default configuration if the file does not exist.
    /// </summary>
    /// <param name="configPath">The file path to the JSON configuration file.</param>
    /// <param name="configObj">An optional pre-existing AppConfig object to use directly without loading from file.</param>
    /// <returns>The loaded or newly created AppConfig instance with defaults applied.</returns>
    public static AppConfig LoadConfig(string configPath, AppConfig? configObj = null)
    {
        if (configObj != null)
        {
            _appConfig = configObj;
            EnsureConfigInitialized();
            return _appConfig;
        }

        var cfgDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(cfgDir))
            Directory.CreateDirectory(cfgDir);

        if (!File.Exists(configPath))
        {
            _appConfig = new AppConfig();
            EnsureConfigInitialized();
            McpServerDefaults.EnsureDefaultsPresent(_appConfig);

            File.WriteAllText(configPath, JsonSerializer.Serialize(_appConfig, CfgSerialOpts));

            MuxConsole.WriteWarning($"No config found. Wrote default config to: {configPath}");

            return _appConfig;
        }

        var json = File.ReadAllText(configPath);
        _appConfig = JsonSerializer.Deserialize<AppConfig>(json, CfgSerialOpts) ?? new AppConfig();

        EnsureConfigInitialized();
        SwarmDefaults.EnsurePresent(_appConfig);
        return _appConfig;
    }

    public static void FetchSetExecLimits()
    {
        try
        {
            if (File.Exists(PlatformContext.SwarmPath))
            {
                var swarmJson = File.ReadAllText(PlatformContext.SwarmPath);
                var swarm = JsonSerializer.Deserialize<SwarmConfig>(swarmJson);
                if (swarm?.ExecutionLimits != null)
                    ExecutionLimits.Current = swarm.ExecutionLimits;
            }
        }
        catch { /* defaults */ }
    }

    /// <summary>
    /// Executes the interactive setup process for the Mux-Swarm application.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the setup completed successfully and all configuration steps were performed;
    /// <c>false</c> if the setup was interrupted or failed at any step.
    /// </returns>
    public static bool RunSetup()
    {
        MuxConsole.WriteBanner("Mux-Swarm SETUP");

        _appConfig = new AppConfig();
        EnsureConfigInitialized();

        if (!StepCheckDependencies()) return false;

        MuxConsole.WriteRule();

        if (!StepCollectFilesystemPaths()) return false;

        MuxConsole.WriteRule();

        if (!StepCollectStorageDirs()) return false;

        MuxConsole.WriteRule();

        if (!StepCollectEndpointConfig()) return false;

        MuxConsole.WriteRule();

        if (!StepCollectUserInfo()) return false;

        McpServerDefaults.EnsureDefaultsPresent(_appConfig);

        MuxConsole.WriteRule();

        if (!StepCollectMcpSecrets()) return false;

        MuxConsole.WriteRule();

        if (!StepResolveMcpServerPaths()) return false;

        if (!SwarmDefaults.ForceWrite(_appConfig))
        {
            MuxConsole.WriteWarning("Setup completed, but swarm.json was not generated.");
            MuxConsole.WriteWarning("Please configure it manually before running agents.");
        }

        MuxConsole.WriteRule();

        StepPrintSummary();
        return true;
    }

    //STEPS
    private static bool StepCheckDependencies()
    {
        MuxConsole.WriteStep(1, "Dependency Check");

        var deps = new[]
        {
            new DepResolver.Dep("python", "Some skills and tooling rely on Python"),
            new DepResolver.Dep("node", "Required for npx-based MCP servers"),
            new DepResolver.Dep("npm", "Required for npx-based MCP servers"),
            new DepResolver.Dep("npx", "Required for MCP servers (memory/filesystem/shell)"),
            new DepResolver.Dep("uv",  "Required for uv/uvx-based MCP servers (fetch/chroma)"),
            new DepResolver.Dep("uvx", "Required for uv/uvx-based MCP servers (fetch/chroma)"),
        };

        var verbose = Debugger.IsAttached || Environment.GetEnvironmentVariable("MUXSWARM_VERBOSE") == "1";

        var results = DepResolver.EnsureDepsInteractive(
            deps,
            isBinaryAvailable: BinaryResolver.IsBinaryAvailable,
            findBinary: BinaryResolver.FindBinary,
            verbose: verbose
        );

        McpServerDefaults.EnsureDefaultsPresent(_appConfig);
        McpServerDefaults.PatchCommandsFromDepResults(_appConfig, results);

        MuxConsole.WriteLine();
        return true;
    }

    private static bool StepCollectFilesystemPaths()
    {
        MuxConsole.WriteStep(2, "File System Access");

        MuxConsole.WriteBody("Enter file paths MuxSwarm agents should have access to (comma separated).");

        if (PlatformContext.IsWindows)
            MuxConsole.WriteMuted(@"Example: C:\Users\john, D:\Projects, \\nas\Public");
        else
            MuxConsole.WriteMuted("Example: /home/john, /mnt/data, /opt/projects");

        MuxConsole.WriteLine();
        var input = MuxConsole.Prompt("Paths: ");

        var paths = ParsePaths(input);

        if (paths.Count == 0)
        {
            MuxConsole.WriteError("No valid paths provided. Setup failed.");
            return false;
        }

        MuxConsole.WriteLine();
        MuxConsole.WriteBody("Parsed paths:");
        for (int i = 0; i < paths.Count; i++)
            MuxConsole.WriteInfo($"[{i + 1}] {paths[i]}");

        MuxConsole.WriteLine();
        MuxConsole.WriteBody("Which path should be used as the agent output sandbox?");
        MuxConsole.WriteMuted("This is where agents will write files and artifacts.");

        var sandboxInput = MuxConsole.Prompt("Enter number or full path: ");
        string sandboxPath;

        if (int.TryParse(sandboxInput, out int idx) && idx >= 1 && idx <= paths.Count)
        {
            sandboxPath = paths[idx - 1];
        }
        else if (Directory.Exists(sandboxInput))
        {
            sandboxPath = sandboxInput;
            if (!paths.Contains(sandboxPath))
                paths.Add(sandboxPath);
        }
        else
        {
            MuxConsole.WriteWarning($"Invalid selection. Defaulting to first path: {paths[0]}");
            sandboxPath = paths[0];
        }

        MuxConsole.WriteSuccess($"Sandbox set to: {sandboxPath}");

        _appConfig.Filesystem ??= new FilesystemConfig();
        _appConfig.Filesystem.AllowedPaths = paths;
        _appConfig.Filesystem.SandboxPath = sandboxPath;
        _appConfig.Filesystem.SkillsPath = PlatformContext.SkillsDirectory;
        _appConfig.Filesystem.SessionsPath = PlatformContext.SessionsDirectory;
        _appConfig.Filesystem.PromptsPath = PlatformContext.PromptsDirectory;
        _appConfig.Filesystem.ConfigDir = PlatformContext.ConfigDirectory;

        if (!_appConfig.Filesystem.AllowedPaths.Contains(sandboxPath))
            _appConfig.Filesystem.AllowedPaths.Add(sandboxPath);

        _appConfig.Filesystem.AllowedPaths.Add(_appConfig.Filesystem.SkillsPath);
        _appConfig.Filesystem.AllowedPaths.Add(_appConfig.Filesystem.SessionsPath);
        _appConfig.Filesystem.AllowedPaths.Add(_appConfig.Filesystem.PromptsPath);
        _appConfig.Filesystem.AllowedPaths.Add(_appConfig.Filesystem.ConfigDir);

        return true;
    }

    private static bool StepCollectStorageDirs()
    {
        MuxConsole.WriteStep(3, "Storage Configuration");

        MuxConsole.WriteBody("ChromaDB stores agent search indexes and vector embeddings.");
        MuxConsole.WriteBody("Enter a directory to store ChromaDB persistent data (SQLite, indexes, etc).");
        MuxConsole.WriteMuted("Press Enter to use the default inside your sandbox.");

        var sandbox = _appConfig.Filesystem?.SandboxPath;
        if (string.IsNullOrWhiteSpace(sandbox))
            throw new InvalidOperationException("Sandbox must be set before configuring storage.");

        var defaultDir = Path.Combine(sandbox, "chroma-db");

        MuxConsole.WriteLine();
        var input = MuxConsole.Prompt("Chroma data dir", defaultDir);

        var chromaDir = string.IsNullOrWhiteSpace(input) ? defaultDir : input;

        if (!PlatformContext.IsWindows && chromaDir.StartsWith("~/"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            chromaDir = Path.Combine(home, chromaDir[2..]);
        }

        chromaDir = Path.GetFullPath(chromaDir);

        try
        {
            Directory.CreateDirectory(chromaDir);
        }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"Failed to create/use directory: {chromaDir}");
            MuxConsole.WriteError(ex.Message);
            return false;
        }

        _appConfig.Filesystem ??= new FilesystemConfig();
        _appConfig.Filesystem.ChromaDbPath = chromaDir;

        if (_appConfig.McpServers.TryGetValue("ChromaDB", out var chromaServer) && chromaServer.Enabled)
        {
            chromaServer.Args = new[]
            {
                "chroma-mcp",
                "--client-type", "persistent",
                "--data-dir", chromaDir
            };
        }

        MuxConsole.WriteSuccess($"ChromaDB data dir set to: {chromaDir}");

        MuxConsole.WriteLine();

        var memoryFile = Path.Combine(sandbox, "memory.jsonl");
        _appConfig.Filesystem.KnowledgeGraphPath = memoryFile;

        if (_appConfig.McpServers.TryGetValue("Memory", out var memoryServer) && memoryServer.Enabled)
        {
            memoryServer.Env ??= new Dictionary<string, string?>();
            memoryServer.Env["MEMORY_FILE_PATH"] = memoryFile;
        }

        MuxConsole.WriteSuccess($"Knowledge graph set to: {memoryFile}");

        return true;
    }

    private static bool StepCollectEndpointConfig()
    {
        MuxConsole.WriteStep(4, "Model Endpoint Configuration");

        MuxConsole.WriteBody("Enter your OpenAI-compatible API endpoint.");
        MuxConsole.WriteMuted("Example: https://openrouter.ai/api/v1");

        MuxConsole.WriteLine();
        var endpoint = MuxConsole.Prompt("Endpoint: ");

        if (string.IsNullOrEmpty(endpoint))
        {
            MuxConsole.WriteError("No endpoint provided. Setup failed.");
            return false;
        }

        MuxConsole.WriteLine();
        MuxConsole.WriteBody("Enter the ENVIRONMENT VARIABLE NAME that holds your API key.");
        MuxConsole.WriteMuted("Example: OPENROUTER_API_KEY, OPENAI_API_KEY");
        MuxConsole.WriteLine();
        MuxConsole.WriteMuted("If you prefer (not recommended), you may paste your RAW API key instead.");
        MuxConsole.WriteMuted("Raw keys will only be stored in-memory for this session.");
        MuxConsole.WriteMuted("Leave blank for local endpoints (e.g. Ollama).");

        MuxConsole.WriteLine();
        var input = MuxConsole.Prompt("Env var name or raw key: ");

        string? apiKeyEnvVar = null;

        if (string.IsNullOrEmpty(input))
        {
            MuxConsole.WriteMuted("No API key configured — suitable for local endpoints.");
        }
        else if (Common.LooksLikeEnvVarName(input))
        {
            apiKeyEnvVar = input;

            var existing = Environment.GetEnvironmentVariable(apiKeyEnvVar);
            if (string.IsNullOrEmpty(existing))
            {
                MuxConsole.WriteWarning($"Env var '{apiKeyEnvVar}' is not currently set.");
                MuxConsole.WriteWarning("Make sure to set it before running agents.");
            }
        }
        else
        {
            MuxConsole.WriteWarning("Raw API key detected. It will NOT be saved to config.");

            apiKeyEnvVar = "MUXSWARM_SESSION_API_KEY";
            Environment.SetEnvironmentVariable(apiKeyEnvVar, input, EnvironmentVariableTarget.Process);

            MuxConsole.WriteSuccess("API key stored securely for this session only.");
        }

        var provider = new ProviderConfig
        {
            Name = "default",
            Endpoint = endpoint,
            ApiKeyEnvVar = apiKeyEnvVar,
            Enabled = true
        };

        _appConfig.LlmProviders ??= [];
        _appConfig.LlmProviders.Clear();
        _appConfig.LlmProviders.Add(provider);

        return true;
    }

    private static bool StepCollectUserInfo()
    {
        MuxConsole.WriteStep(5, "User Profile (Optional)");

        MuxConsole.WriteBody("You can optionally tell your agents who you are.");
        MuxConsole.WriteMuted("This helps agents personalize responses and address you by name.");
        MuxConsole.WriteMuted("Press Enter to skip any field, or type 'skip' to skip this step entirely.");
        MuxConsole.WriteLine();

        var nameInput = MuxConsole.Prompt("Your name");

        if (string.IsNullOrWhiteSpace(nameInput) || nameInput.Trim().Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            MuxConsole.WriteMuted("Skipped user profile.");
            return true;
        }

        var userInfo = new UserInfoConfig { Name = nameInput.Trim() };

        var role = MuxConsole.Prompt("Role (e.g. admin, developer, analyst)");
        if (!string.IsNullOrWhiteSpace(role)) userInfo.Role = role.Trim();

        var tz = MuxConsole.Prompt("Timezone (e.g. America/New_York)");
        if (!string.IsNullOrWhiteSpace(tz)) userInfo.Timezone = tz.Trim();

        var locale = MuxConsole.Prompt("Locale (e.g. en-US)");
        if (!string.IsNullOrWhiteSpace(locale)) userInfo.Locale = locale.Trim();

        MuxConsole.WriteLine();
        MuxConsole.WriteMuted("Anything else your agents should know about you?");
        MuxConsole.WriteMuted("e.g. preferred language, tech stack, communication style");
        var info = MuxConsole.Prompt("Info");
        if (!string.IsNullOrWhiteSpace(info)) userInfo.Info = info.Trim();

        _appConfig.UserInfo = userInfo;

        MuxConsole.WriteSuccess($"Profile set for {userInfo.Name}.");
        return true;
    }

    private static bool StepCollectMcpSecrets()
    {
        MuxConsole.WriteStep(6, "MCP API Keys");

        MuxConsole.WriteBody("Some MCP servers require API keys.");
        MuxConsole.WriteBody("By default, MuxSwarm stores ONLY the env-var names in config (no secrets).");
        MuxConsole.WriteLine();
        MuxConsole.WriteMuted("Press ENTER to keep the default env-var name");
        MuxConsole.WriteMuted("Type a different env-var name you want MuxSwarm to use");
        MuxConsole.WriteMuted("(Not recommended) Paste a raw API key — not written to config,");
        MuxConsole.WriteMuted("but set into the current process for this run.");
        MuxConsole.WriteLine();

        bool anyPrompted = false;

        foreach (var (serverName, server) in _appConfig.McpServers)
        {
            if (!server.Enabled) continue;
            if (server.Env == null || server.Env.Count == 0) continue;

            foreach (var key in server.Env.Keys.ToList())
            {
                var raw = server.Env[key] ?? string.Empty;
                var currentMapping = raw.Trim();

                if (string.IsNullOrEmpty(currentMapping)) continue;
                if (!Common.LooksLikeEnvVarName(currentMapping)) continue;

                var existingValue = Environment.GetEnvironmentVariable(currentMapping);
                if (!string.IsNullOrEmpty(existingValue)) continue;

                if (anyPrompted) MuxConsole.WriteLine();

                MuxConsole.WriteWarning($"[{serverName}] Tool requires env var for '{key}'.");
                MuxConsole.WriteMuted($"Default env var name: {currentMapping}");
                MuxConsole.WriteMuted("Press ENTER to keep it, or type a new env var name / raw key.");

                MuxConsole.WriteLine();
                var input = MuxConsole.Prompt($"{serverName}.{key}");

                if (string.IsNullOrEmpty(input))
                {
                    MuxConsole.WriteWarning($"'{currentMapping}' is not set. {serverName} may fail until you set it.");
                    MuxConsole.WriteMuted($"  Example (PowerShell):  $env:{currentMapping} = \"...\"");
                    anyPrompted = true;
                    continue;
                }

                if (Common.LooksLikeEnvVarName(input))
                {
                    server.Env[key] = input;

                    var now = Environment.GetEnvironmentVariable(input);
                    if (string.IsNullOrEmpty(now))
                    {
                        MuxConsole.WriteWarning($"Env var '{input}' is not set yet. {serverName} may fail until you set it.");
                        MuxConsole.WriteMuted($"  Example (PowerShell):  $env:{input} = \"...\"");
                    }
                    else
                    {
                        MuxConsole.WriteSuccess($"Using env var '{input}' (currently set).");
                    }

                    anyPrompted = true;
                    continue;
                }

                Environment.SetEnvironmentVariable(currentMapping, input);
                MuxConsole.WriteSuccess($"Set '{currentMapping}' for this run (not written to config).");
                anyPrompted = true;
            }
        }

        if (!anyPrompted)
            MuxConsole.WriteSuccess("All MCP API keys are already configured.");

        return true;
    }

    private static bool StepResolveMcpServerPaths()
    {
        MuxConsole.WriteStep(7, "MCP Server Validation");

        foreach (var (name, server) in _appConfig.McpServers)
        {
            if (!server.Enabled) continue;

            if (!server.Type.Equals("http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(server.Command))
            {
                var binary = Path.GetFileName(server.Command);
                var found = BinaryResolver.IsBinaryAvailable(binary);

                if (found)
                    MuxConsole.WriteSuccess($"{name} — {binary}");
                else
                    MuxConsole.WriteWarning($"{name} — '{binary}' not found on PATH");
            }

            if (server.Env != null && server.Env.Count > 0)
            {
                foreach (var (key, rawValue) in server.Env)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    var v = (rawValue ?? "").Trim();
                    if (v.Length == 0)
                    {
                        MuxConsole.WriteWarning($"{name} — env '{key}' is empty (will pass empty string).");
                        continue;
                    }

                    if (Common.LooksLikeEnvVarName(v))
                    {
                        var value = Environment.GetEnvironmentVariable(v);
                        var exists = !string.IsNullOrEmpty(value);

                        if (exists)
                        {
                            MuxConsole.WriteSuccess($"{name} — {key} sourced from env var '{v}'");
                        }
                        else
                        {
                            MuxConsole.WriteInfo($"{name} — expects env var '{v}' for '{key}', but it is not currently set.");
                            MuxConsole.WriteMuted($"  The MCP server can start, but related tool calls may fail.");

                            if (PlatformContext.IsWindows)
                            {
                                MuxConsole.WriteMuted($"  Temporary:  $env:{v} = \"your_api_key_here\"");
                                MuxConsole.WriteMuted($"  Permanent:  setx {v} \"your_api_key_here\"");
                            }
                            else
                            {
                                MuxConsole.WriteMuted($"  Temporary:  export {v}=\"your_api_key_here\"");
                                MuxConsole.WriteMuted($"  Permanent:  echo 'export {v}=\"...\"' >> ~/.bashrc");
                            }
                        }
                    }
                    else
                    {
                        MuxConsole.WriteSuccess($"{name} — {key} uses literal value");
                    }
                }
            }
        }

        MuxConsole.WriteLine();
        return true;
    }

    private static void StepPrintSummary()
    {
        _appConfig.SetupCompleted = true;
        Directory.CreateDirectory(PlatformContext.ConfigDirectory);

        var json = JsonSerializer.Serialize(_appConfig, CfgSerialOpts);
        File.WriteAllText(PlatformContext.ConfigPath, json);

        MuxConsole.WriteSummaryTable("Setup Complete", new[]
        {
            ("Config",        PlatformContext.ConfigPath),
            ("Swarm",         PlatformContext.SwarmPath),
            ("OS",            Common.GetOsFriendlyName()),
            ("Shell",         PlatformContext.Shell),
            ("Sandbox",       _appConfig.Filesystem?.SandboxPath ?? "—"),
            ("Allowed paths", string.Join(", ", _appConfig.Filesystem?.AllowedPaths ?? [])),
            ("ChromaDB path", _appConfig.Filesystem?.ChromaDbPath ?? "—"),
            ("Knowledge graph", _appConfig.Filesystem?.KnowledgeGraphPath ?? "—"),
        });

        /*
        MuxConsole.WriteSuccess("Setup complete. Run 'mux-swarm' to start.");
        */
        MuxConsole.WriteLine();
    }

    // ─────────────────────────────────────────────────────────────
    // PUBLIC DELEGATES (kept for backward compat)
    // ─────────────────────────────────────────────────────────────

    public static bool IsBinaryAvailable(string binary) => BinaryResolver.IsBinaryAvailable(binary);

    public static bool TryFindBinaryPath(string binary, out string? fullPath) =>
        BinaryResolver.TryFindBinaryPath(binary, out fullPath);

    // ─────────────────────────────────────────────────────────────
    // UTILITIES
    // ─────────────────────────────────────────────────────────────

    private static List<string> ParsePaths(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                var path = current.ToString().Trim();
                if (!string.IsNullOrEmpty(path))
                {
                    if (PlatformContext.IsWindows && !path.StartsWith("\\\\"))
                        path = path.Replace("\\\\", "\\");
                    result.Add(path);
                }
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        var last = current.ToString().Trim();
        if (!string.IsNullOrEmpty(last))
        {
            if (PlatformContext.IsWindows && !last.StartsWith("\\\\"))
                last = last.Replace("\\\\", "\\");
            result.Add(last);
        }

        return result;
    }
}