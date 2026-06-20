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
