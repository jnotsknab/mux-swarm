using System.Text;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine.NativeTools;
using MuxSwarm.Engine.Proxy;

namespace MuxSwarm.Engine;

/// <summary>
/// Drives /fix: when something in Mux is misbehaving the user runs /fix [symptom], and this
/// collects a live system-state snapshot (config paths, configured-vs-connected MCP servers,
/// active provider + CLIProxy sidecar state, loaded skills, sandbox backend, allowed paths,
/// execution limits) and hands it to the ACTIVE session model alongside the symptom. The model
/// returns a concrete diagnosis + ordered, copy-pasteable repair steps (slash commands / config
/// edits / shell), favouring Mux's own remediation commands (/refresh, /reloadskills, /proxy
/// update, /setup, /sandbox, /provider, /login) over guesswork.
///
/// Read-only by design: /fix never mutates state itself - it diagnoses and PROPOSES. The user runs
/// the suggested commands. This keeps a "things are broken" entry point safe to invoke at any time.
/// </summary>
public static class SystemDiagnostics
{
    /// <summary>
    /// Build a compact, model-readable snapshot of current runtime state. Pure string assembly off
    /// the App.* statics; no side effects. When <paramref name="sandboxStatus"/> is null (e.g. /fix) the
    /// sandbox is reported as not probed; /doctor passes a <see cref="ProbeSandbox"/> result.
    /// </summary>
    public static string BuildSnapshot() => BuildSnapshot(null);

    /// <summary>Snapshot including a sandbox readiness result (see <see cref="ProbeSandbox"/>).</summary>
    internal static string BuildSnapshot(SandboxStatus? sandboxStatus)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Mux-Swarm runtime snapshot");
        sb.AppendLine($"version: {App.Version}{(string.IsNullOrEmpty(App.DebugTag) ? "" : " " + App.DebugTag)}");
        sb.AppendLine($"os: {(PlatformContext.IsWindows ? "windows" : PlatformContext.IsMac ? "macos" : "linux")}");
        sb.AppendLine($"configPath: {App.ConfigPath}");
        sb.AppendLine($"swarmPath: {PlatformContext.SwarmPath}");
        sb.AppendLine();

        // Provider + proxy
        var prov = App.ActiveProvider;
        sb.AppendLine("## Provider");
        if (prov is null)
            sb.AppendLine("activeProvider: (none) -- no LLM provider is active; use /provider or /setup.");
        else
        {
            sb.AppendLine($"activeProvider: {prov.Name}");
            sb.AppendLine($"endpoint: {prov.Endpoint}");
            sb.AppendLine($"apiKeyEnvVar: {prov.ApiKeyEnvVar ?? "(none)"}");
            if (!string.IsNullOrWhiteSpace(prov.ApiKeyEnvVar))
            {
                bool keySet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(prov.ApiKeyEnvVar));
                sb.AppendLine($"apiKeyEnvSet: {keySet}");
            }
            bool isCliProxy = string.Equals(prov.ApiKeyEnvVar, CliProxyManager.ClientKeyEnvVar, StringComparison.Ordinal);
            sb.AppendLine($"usesCliProxySidecar: {isCliProxy}");
        }
        sb.AppendLine();

        // MCP servers: configured vs actually connected
        sb.AppendLine("## MCP servers (configured vs connected)");
        var configured = App.Config?.McpServers ?? new();
        if (configured.Count == 0)
            sb.AppendLine("(no MCP servers configured)");
        foreach (var server in ClassifyServers(configured, App.McpClients.Keys))
            sb.AppendLine($"- {server.Name}: {server.State}");
        int toolCount = App.McpTools?.Count ?? 0;
        sb.AppendLine($"mcpConnectTimeoutSeconds: {App.Config?.McpConnectTimeoutSeconds ?? 90}");
        sb.AppendLine($"totalMcpTools: {toolCount}");
        sb.AppendLine();

        // Native groups are available independently of the external-MCP connection dictionary.
        // Report global configuration only, not an execution probe or an agent's permission grant.
        sb.AppendLine("## Native tool groups (in-process; no MCP connection)");
        var nativeConfig = App.Config ?? new AppConfig();
        foreach (string name in new[] { NativeToolRegistry.FilesystemServer, NativeToolRegistry.ShellServer })
        {
            bool enabled = NativeToolRegistry.ServerEnabled(nativeConfig, name);
            sb.AppendLine($"- {name}: {(enabled ? "enabled" : "disabled")} (native, in-process)");
        }
        sb.AppendLine("Native group enablement is global; per-agent mcpServers/toolPatterns and filesystem/shell security gates still apply.");
        sb.AppendLine();

        // Skills
        sb.AppendLine("## Skills");
        var skills = SkillLoader.GetSkillMetadata();
        sb.AppendLine($"loadedSkills: {skills.Count}");
        sb.AppendLine($"skillsDir: {PlatformContext.SkillsDirectory}");
        sb.AppendLine();

        // Sandbox
        sb.AppendLine("## Execution sandbox");
        var sandbox = App.Config?.Sandbox;
        sb.AppendLine($"configuredBackend: {sandbox?.Backend ?? "host"}");
        sb.AppendLine($"resolvedActive: {SandboxRuntime.IsActive}");
        if (SandboxRuntime.Active is { } spec)
            sb.AppendLine($"resolvedBackend: {spec.Backend}");
        sb.AppendLine(sandboxStatus is null
            ? "status: not probed (run /doctor for a live check)"
            : $"status: {(sandboxStatus.Configured ? sandboxStatus.Usable ? "usable" : "NOT USABLE" : "none")} -- {sandboxStatus.Detail}");
        sb.AppendLine();

        // Filesystem + limits
        sb.AppendLine("## Filesystem & limits");
        var fs = App.Config?.Filesystem;
        sb.AppendLine($"sandboxPath: {fs?.SandboxPath ?? "(unset)"}");
        sb.AppendLine($"securityMode(fs): {fs?.SecurityMode ?? "(default)"}");
        sb.AppendLine($"allowedPaths: {(fs?.AllowedPaths?.Count ?? 0)} configured");
        var lim = ExecutionLimits.Current;
        sb.AppendLine($"activityTimeoutSeconds: {lim.ActivityTimeoutSeconds}");
        sb.AppendLine($"maxToolIterationsPerTurn: {lim.MaxToolIterationsPerTurn}");

        return sb.ToString();
    }

    /// <summary>Result of a live sandbox readiness check. <c>Configured</c> is false for host execution.</summary>
    internal sealed record SandboxStatus(bool Configured, bool Usable, string Detail);

    /// <summary>
    /// Live readiness check of the configured sandbox backend for /doctor. Reuses
    /// <see cref="SandboxBackend.Resolve"/> (the same read-only probes a session runs: binary
    /// <c>--version</c>, engine <c>info</c>, <c>sbx ls</c>, OS/KVM gates), plus a check that an explicit
    /// OCI runtime (e.g. runsc) is known to the engine. Never creates a sandbox, container or VM.
    /// </summary>
    internal static SandboxStatus ProbeSandbox(SandboxConfig? cfg)
    {
        string backend = SandboxBackend.Canonical(cfg?.Backend);
        if (cfg is null || backend is "host" or "" or "none")
            return new(false, true, "no sandbox configured; shell and Python run directly on the host");
        SandboxSpec? spec;
        try { spec = SandboxBackend.Resolve(cfg); }
        catch (SandboxException ex) { return new(true, false, ex.Message); }
        if (spec is null)
            return new(false, true, "no sandbox configured; shell and Python run directly on the host");
        if (spec.Kind == SandboxKind.Oci && spec.Runtime is { } rt)
        {
            string? info = Path.IsPathRooted(rt) ? null : OciSandbox.Run(spec.Binary, "info", allowFail: true, timeoutMs: 10_000).outp;
            if (RuntimeProblem(spec.Binary, rt, info) is { } problem) return new(true, false, problem);
        }
        string what = spec.Kind switch
        {
            SandboxKind.Custom => "template set (custom backends are not probed)",
            SandboxKind.Wrapper => $"'{spec.Binary}' found",
            _ => $"'{spec.Binary}' found and reachable{(spec.Runtime is { } r ? $", runtime '{r}'" : "")}",
        };
        return new(true, true, $"backend '{spec.Backend}' ready: {what}");
    }

    /// <summary>
    /// Reason an explicit OCI runtime is unusable, or null when it looks present (or cannot be checked).
    /// An absolute path must exist; a runtime name is checked against <c>docker info</c> output, which
    /// lists registered runtimes. Other engines do not list runtimes reliably, so they are not checked.
    /// </summary>
    internal static string? RuntimeProblem(string binary, string runtime, string? infoOutput)
    {
        if (Path.IsPathRooted(runtime))
            return File.Exists(runtime) ? null : $"OCI runtime '{runtime}' does not exist";
        if (binary == "docker" && infoOutput is { Length: > 0 }
            && !infoOutput.Contains(runtime, StringComparison.OrdinalIgnoreCase))
            return $"OCI runtime '{runtime}' is not registered with docker ('docker info' does not list it). " +
                   "Install it and register it in the docker daemon config, then restart docker.";
        return null;
    }

    /// <summary>One configured entry's effective transport status, not per-agent tool access.</summary>
    internal sealed record ServerStatus(string Name, string State, bool MissingConnection);

    /// <summary>Classify entries using the same native-replacement policy as MCP startup.
    /// Disabled entries never require a connection; only enabled external entries can be missing.</summary>
    internal static IReadOnlyList<ServerStatus> ClassifyServers(
        IReadOnlyDictionary<string, McpServerConfig> configured, IEnumerable<string> connectedNames)
    {
        // App.McpClients uses ordinal keys. Do not silently apply a different name policy here.
        var connected = new HashSet<string>(connectedNames, StringComparer.Ordinal);
        var states = new List<ServerStatus>(configured.Count);
        foreach (var (name, config) in configured)
        {
            bool native = NativeToolRegistry.ReplacesMcpEntry(config);
            bool missing = config.Enabled && !native && !connected.Contains(name);
            string state = !config.Enabled ? "disabled"
                : native ? "native replacement (no MCP connection; canonical groups below)"
                : missing ? "NOT CONNECTED" : "connected";
            if (!native) state += $" [{config.Type}]";
            states.Add(new(name, state, missing));
        }
        return states;
    }

    /// <summary>
    /// Run a diagnosis: snapshot + user symptom -> active model -> diagnosis + ordered repair steps.
    /// Returns the model's text (already streamed to the console by the caller is NOT done here; this
    /// returns the full text so the caller can render it). Read-only.
    /// </summary>
    public static async Task<string> DiagnoseAsync(
        string? symptom,
        IChatClient client,
        ChatOptions? chatOptions,
        CancellationToken ct)
    {
        var snapshot = BuildSnapshot();

        var system = new StringBuilder();
        system.AppendLine("You are the Mux-Swarm self-repair diagnostician. The user invoked /fix because something");
        system.AppendLine("in the runtime is misbehaving. Using ONLY the runtime snapshot and the user's symptom,");
        system.AppendLine("produce a SHORT, concrete diagnosis followed by an ordered, copy-pasteable repair plan.");
        system.AppendLine();
        system.AppendLine("Rules:");
        system.AppendLine("- Prefer Mux's own remediation commands over manual fixes: /refresh (reload config+MCP+skills),");
        system.AppendLine("  /reloadskills, /proxy status|update, /login <provider>, /ping, /provider, /setup, /sandbox,");
        system.AppendLine("  /setmodel, /set <key> <value>, /tools, /status.");
        system.AppendLine("- Native replacements intentionally have no MCP connection. Use the Native tool groups section for");
        system.AppendLine("  global enablement; per-agent filtering and security gates are separate from connectivity.");
        system.AppendLine("- If an external MCP server shows NOT CONNECTED: likely a bad command/PATH, a missing env var, or a");
        system.AppendLine("  connect timeout -- suggest checking the command, raising mcpConnectTimeoutSeconds, then /refresh.");
        system.AppendLine("- If the active provider uses the CLIProxy sidecar: suggest /proxy status and /ping; a missing");
        system.AppendLine("  apiKeyEnvSet=false usually means the bearer key wasn't exported -- /login or /proxy update.");
        system.AppendLine("- If no provider is active or the model id looks wrong: /provider, /setmodel, or /setup.");
        system.AppendLine("- If skills look wrong after edits: /reloadskills. If sandbox claims active but exec fails: /sandbox host.");
        system.AppendLine("- Be specific to the snapshot. Do NOT invent servers, providers, or paths not present. If the");
        system.AppendLine("  snapshot looks healthy and the symptom is vague, say so and ask one clarifying question.");
        system.AppendLine("- Keep it tight: diagnosis (1-3 lines) then a numbered fix list. No filler.");

        var user = new StringBuilder();
        user.AppendLine("## Symptom");
        user.AppendLine(string.IsNullOrWhiteSpace(symptom) ? "(none given -- do a general health check of the snapshot)" : symptom);
        user.AppendLine();
        user.AppendLine(snapshot);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system.ToString()),
            new(ChatRole.User, user.ToString())
        };

        var response = await client.GetResponseAsync(messages, chatOptions, ct);
        return response?.Text ?? "";
    }
}
