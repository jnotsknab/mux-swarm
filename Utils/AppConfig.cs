using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class AppConfig
{
    [JsonPropertyName("setupCompleted")]
    public bool SetupCompleted { get; set; } = false;

    [JsonPropertyName("isUsingDockerForExec")]
    public bool IsUsingDockerForExec { get; set; } = false;

    [JsonPropertyName("serveAddress")]
    public string ServeAddress { get; set; } = "0.0.0.0";

    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();

    [JsonPropertyName("llmProviders")]
    public List<ProviderConfig> LlmProviders { get; set; } = [];

    [JsonPropertyName("filesystem")]
    public FilesystemConfig Filesystem { get; set; } = new();

    [JsonPropertyName("userInfo")]
    public UserInfoConfig UserInfo { get; set; } = new();

    [JsonPropertyName("telemetry")]
    public TelemetryConfig Telemetry { get; set; } = new();

    [JsonPropertyName("daemon")]
    public DaemonConfig? Daemon { get; set; } = new();

    [JsonPropertyName("serve")]
    public ServeConfig Serve { get; set; } = new();

    [JsonPropertyName("ultra")]
    public UltraConfig Ultra { get; set; } = new();

    [JsonPropertyName("console")]
    public ConsoleConfig Console { get; set; } = new();


}

/// <summary>
/// Console render-layer settings. Additive; absent in older configs, in which case
/// defaults apply. Controls which interactive renderer MuxConsole uses. The stdio/serve
/// NDJSON path is unaffected by this block - it always short-circuits first.
/// </summary>
public class ConsoleConfig
{
    /// <summary>
    /// Interactive render mode preference: <c>"auto"</c> (capability-aware default -
    /// live TUI on a capable interactive terminal, classic line renderer otherwise),
    /// <c>"tui"</c> (force the full-screen live renderer), or <c>"classic"</c> (force the
    /// pre-v0.11.0 line-by-line renderer). A <c>--classic</c>/<c>--tui</c> CLI flag or the
    /// <c>/classic</c> toggle overrides this value at runtime. Ignored in stdio/serve mode.
    /// </summary>
    [JsonPropertyName("renderMode")]
    public string RenderMode { get; set; } = "auto";

    /// <summary>
    /// Tool-output verbosity in the TUI renderer. <c>"compact"</c> (default) collapses
    /// each tool result to a one-line summary (Claude-Code style); errors and diffs still
    /// expand. <c>"full"</c> renders the full bordered result panel. Toggled at runtime by
    /// <c>/verbose</c>. Ignored outside TUI render mode.
    /// </summary>
    [JsonPropertyName("toolOutput")]
    public string ToolOutput { get; set; } = "compact";

    /// <summary>
    /// When true (default), the TUI pins a docked status footer (context meter + mode
    /// badges) to the bottom of the terminal using an ANSI scroll region, the way Claude
    /// Code keeps a persistent bottom bar. Set false to fall back to an inline status line
    /// printed before each prompt. Ignored outside TUI render mode / on non-capable terminals.
    /// </summary>
    [JsonPropertyName("dockedFooter")]
    public bool DockedFooter { get; set; } = true;
}

/// <summary>
/// Settings for /ultra deep-reasoning mode. Additive; absent in older configs,
/// in which case defaults apply. Default-off behavior is owned by the App toggle,
/// not this block — these values only tune what /ultra does once enabled.
/// </summary>
public class UltraConfig
{
    /// <summary>
    /// Numeric provider-native thinking budget injected (via AdditionalParams) on
    /// providers that take one (e.g. Anthropic thinking.budget_tokens). Matches
    /// Claude Code's ultrathink ceiling. Sidesteps the effort-enum xhigh path.
    /// </summary>
    [JsonPropertyName("thinkingBudget")]
    public int ThinkingBudget { get; set; } = 31999;

    /// <summary>When true, /ultra raises reasoning for delegated sub-agents too, not just the lead.</summary>
    [JsonPropertyName("includeSubAgents")]
    public bool IncludeSubAgents { get; set; } = true;

    /// <summary>
    /// When true, /ultra auto-enables parallel sub-agent delegation for the session
    /// (single-agent loop) and the steering preamble encourages fanning parallelizable
    /// or investigative work out to sub-agents with isolated sessions. Prior toggle state
    /// is captured and restored when /ultra is turned off. Set false for deep single-agent.
    /// </summary>
    [JsonPropertyName("autoSubAgents")]
    public bool AutoSubAgents { get; set; } = true;
}

/// <summary>
/// Serve-layer (web UI / HTTP API) settings. Additive; absent in older configs,
/// in which case defaults apply (read-only, no auth).
/// </summary>
public class ServeConfig
{
    /// <summary>
    /// When true, the IDE write endpoints (/api/save, /api/fs) are enabled for
    /// the sandbox root. Default false keeps existing deployments read-only.
    /// </summary>
    [JsonPropertyName("editable")]
    public bool Editable { get; set; } = false;

    /// <summary>Optional app-level auth for the serve layer. Default disabled.</summary>
    [JsonPropertyName("auth")]
    public ServeAuthConfig Auth { get; set; } = new();
}

/// <summary>
/// Opt-in authentication for the serve layer. Disabled by default so the runtime
/// stays zero-auth-by-design (nginx perimeter). When enabled, HTTP /api/* and the
/// /ws upgrade require a bearer token. The token value may be a literal or an
/// env-var reference of the form <c>{MUX_SERVE_TOKEN}</c>, <c>${MUX_SERVE_TOKEN}</c>,
/// or <c>$MUX_SERVE_TOKEN</c>, resolved at startup.
/// </summary>
public class ServeAuthConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("scheme")]
    public string Scheme { get; set; } = "bearer";
}
