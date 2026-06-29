using System.Text;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

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
    /// the App.* statics; no side effects.
    /// </summary>
    public static string BuildSnapshot()
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
            bool isCliProxy = string.Equals(prov.ApiKeyEnvVar, MuxSwarm.Utils.Proxy.CliProxyManager.ClientKeyEnvVar, StringComparison.Ordinal);
            sb.AppendLine($"usesCliProxySidecar: {isCliProxy}");
        }
        sb.AppendLine();

        // MCP servers: configured vs actually connected
        sb.AppendLine("## MCP servers (configured vs connected)");
        var configured = App.Config?.McpServers ?? new();
        if (configured.Count == 0)
            sb.AppendLine("(no MCP servers configured)");
        foreach (var (name, cfg) in configured)
        {
            bool nativeMarker = string.Equals(cfg.Command, "native-runtime-tools", StringComparison.OrdinalIgnoreCase)
                || (cfg.Args?.Any(a => string.Equals(a, "native-runtime-tools", StringComparison.OrdinalIgnoreCase)) ?? false);
            bool connected = App.McpClients.ContainsKey(name);
            string state = nativeMarker ? "native (in-process)"
                : !cfg.Enabled ? "disabled"
                : connected ? "connected"
                : "NOT CONNECTED";
            sb.AppendLine($"- {name}: {state}{(nativeMarker ? "" : $" [{cfg.Type}]")}");
        }
        int toolCount = App.McpTools?.Count ?? 0;
        sb.AppendLine($"mcpConnectTimeoutSeconds: {App.Config?.McpConnectTimeoutSeconds ?? 90}");
        sb.AppendLine($"totalMcpTools: {toolCount}");
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
        sb.AppendLine($"resolvedActive: {MuxSwarm.Utils.NativeTools.SandboxRuntime.IsActive}");
        if (MuxSwarm.Utils.NativeTools.SandboxRuntime.Active is { } spec)
            sb.AppendLine($"resolvedBackend: {spec.Backend}");
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
        system.AppendLine("- If an MCP server shows NOT CONNECTED: likely a bad command/PATH, a missing env var, or a");
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
