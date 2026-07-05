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

    /// <summary>
    /// Timeout (seconds) for establishing a connection to an MCP server (stdio spawn + handshake, or
    /// HTTP transport connect) and the initial ListTools call. A slow-starting server (npx cold start,
    /// a Python server building a venv, a remote HTTP MCP) that exceeds this is skipped with an error
    /// rather than blocking startup forever. Default 90. Applies to both stdio and HTTP MCP transports.
    /// </summary>
    [JsonPropertyName("mcpConnectTimeoutSeconds")]
    public int McpConnectTimeoutSeconds { get; set; } = 90;

    [JsonPropertyName("llmProviders")]
    public List<ProviderConfig> LlmProviders { get; set; } = [];

    [JsonPropertyName("filesystem")]
    public FilesystemConfig Filesystem { get; set; } = new();

    [JsonPropertyName("shell")]
    public ShellConfig Shell { get; set; } = new();

    [JsonPropertyName("sandbox")]
    public SandboxConfig Sandbox { get; set; } = new();

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

    [JsonPropertyName("contextLimits")]
    public ContextLimitsConfig ContextLimits { get; set; } = new();

    /// <summary>
    /// Client-side display gate for streamed reasoning text in interactive (TUI/console)
    /// agent and swarm loops: "full" / "summary" both SHOW reasoning (rendered grey + italic),
    /// "none" SUPPRESSES streaming reasoning text entirely (gates the stream). This is a
    /// presentation gate only — it does NOT change the native API reasoning level and does not
    /// affect stdio/NDJSON/WebSocket output (protocol unchanged). Adjusted with
    /// <c>/showreasoning &lt;full|summary|none&gt;</c> or <c>/set showReasoning &lt;...&gt;</c>.
    /// </summary>
    [JsonPropertyName("showReasoning")]
    public string ShowReasoning { get; set; } = "summary";

    /// <summary>
    /// Extra CLI arguments applied automatically at every startup, as if typed on the command
    /// line (prepended before the real argv, so real flags can still override). Lets the user
    /// boot straight into a mode/agent - e.g. "--agent CodeAgent --giga" or "--swarm --ultra".
    /// Set with <c>/startargs &lt;args&gt;</c> (or <c>/startargs clear</c>). Empty by default.
    /// </summary>
    [JsonPropertyName("startupArgs")]
    public string StartupArgs { get; set; } = "";

}

/// <summary>
/// Hard character limits for the shared context memory files (BRAIN.md and MEMORY.md).
/// Each file has an independent limit (0 = off/no cap) and a mode: "off" (ignore),
/// "warn" (print a console warning on startup and after any mutation that exceeds the
/// limit), or "force" (back up the file as <name>.bak then spawn a one-shot LLM rewrite
/// that intelligently condenses the content under the cap, preserving high-signal facts).
/// Additive and non-invasive: absent in older configs (all default to off). Adjusted live
/// with <c>/set brainMdCharLimit &lt;int&gt;</c>, <c>/set brainMdCapMode off|warn|force</c>,
/// and the memoryMd equivalents.
/// </summary>
/// <summary>
/// Security posture for the NATIVE shell/REPL tools (execute_command_async, repl_shell_exec, and
/// the install/input helpers). Mux owns these in-process, so it can gate command execution the way
/// Claude Code gates Bash. Default "off" preserves today's run-anything behavior. Modes:
///   "off" (default) - run anything, no prompts. Unchanged behavior.
///   "prompt"        - EVERY shell command + python exec elevates to the user (shows the exact
///                     command). Deny hard-blocks at the process level (the process never spawns).
///   "allowlist"     - commands whose first token matches an AllowedCommands prefix run freely;
///                     anything else elevates.
/// Elevation routes through the same confirm path as ask_user; in a non-interactive context
/// (stdio/serve, ACP, captured sub-agent, daemon) any non-off mode AUTO-DENIES with a clear message.
/// Additive: absent in older configs -> "off" (zero behavior change).
/// </summary>
public class ShellConfig
{
    [JsonPropertyName("securityMode")]
    public string SecurityMode { get; set; } = "off";

    /// <summary>
    /// First-token command prefixes permitted without a prompt under "allowlist" mode
    /// (e.g. "git", "ls", "cat", "python", "rg"). Matched case-insensitively against the leading
    /// token of the command. Ignored in "off" / "prompt" modes.
    /// </summary>
    [JsonPropertyName("allowedCommands")]
    public List<string> AllowedCommands { get; set; } = [];
}

/// <summary>
/// Pluggable execution-sandbox backend for the native shell + Python REPL tools. When the backend is
/// anything other than "host", a session's shell jobs AND its persistent Python worker run INSIDE a
/// sandbox (one per session) instead of directly on the host, so model-generated code cannot escape
/// to the host by choice - the tool wraps execution, the agent does not opt in. Default "host"
/// preserves today's behavior byte-for-byte. Mirrors the pluggable-runtime pattern used by OpenHands /
/// smolagents / hermes-agent (one backend interface, interchangeable implementations).
///
/// Backends:
///   "host"                       - no sandbox (default; current behavior).
///   "docker" / "podman" / "nerdctl" - OCI container per session (image mounted at the work dir).
///   "gvisor"                     - docker + the runsc runtime (userspace-kernel isolation).
///   "bwrap" / "firejail" / "sandbox-exec" - lightweight OS-native wrapper (Linux/macOS; no image).
///   "custom"                     - render Command as a template ({cmd},{workdir},{image}); drive any
///                                  containerization service we did not hardcode.
///
/// Network: AllowedDomains non-empty => enforced proxy allowlist (OCI backends only; the sandbox has
/// NO direct egress and reaches only listed domains via an injected filtering proxy). Empty +
/// Network=true => open. Empty + Network=false => fully air-gapped. An allowlist on a non-OCI backend
/// is a hard validation error (no silent false-confidence). Unknown backend / missing binary / custom
/// without a template => hard error surfaced to the user, never a silent host fallback.
/// Additive: absent in older configs => "host" (zero behavior change).
/// </summary>
public class SandboxConfig
{
    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "host";

    /// <summary>Container image for OCI backends. Ignored by wrapper/host backends.</summary>
    [JsonPropertyName("image")]
    public string Image { get; set; } = "python:3.12-slim";

    /// <summary>
    /// Network posture when AllowedDomains is empty: true = open egress, false = air-gapped.
    /// Ignored when AllowedDomains is non-empty (which forces the filtered-proxy allowlist).
    /// </summary>
    [JsonPropertyName("network")]
    public bool Network { get; set; } = false;

    /// <summary>
    /// Domain allowlist. Non-empty => the sandbox can reach ONLY these hosts, enforced by an injected
    /// CONNECT-filtering proxy on an internal (egress-less) network. OCI backends only. Deny-by-default.
    /// </summary>
    [JsonPropertyName("allowedDomains")]
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>
    /// Command template for the "custom" backend. Placeholders: {cmd} (the command to run), {workdir}
    /// (host work dir), {image}. Lets you point at any containerization/sandbox CLI. Required (non-empty)
    /// when Backend == "custom"; ignored otherwise.
    /// </summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    /// <summary>
    /// Explicit OCI runtime to pass as <c>--runtime=&lt;value&gt;</c> for OCI backends (docker/podman/
    /// nerdctl/gvisor/kata). Optional. When empty the backend's default runtime is used, except the
    /// microVM / sandboxed-kernel backends which imply their runtime (gvisor =&gt; runsc, kata =&gt;
    /// kata-runtime). Set this to layer a microVM runtime onto a base OCI backend (e.g. backend=podman
    /// runtime=kata-runtime, or backend=nerdctl runtime=io.containerd.kata.v2). Ignored by
    /// wrapper/custom/host backends. Additive: absent in older configs =&gt; backend default.
    /// </summary>
    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = "";
}

public class ContextLimitsConfig
{
    [JsonPropertyName("brainMdCharLimit")]
    public int BrainMdCharLimit { get; set; } = 0;

    [JsonPropertyName("brainMdCapMode")]
    public string BrainMdCapMode { get; set; } = "off";

    [JsonPropertyName("memoryMdCharLimit")]
    public int MemoryMdCharLimit { get; set; } = 0;

    [JsonPropertyName("memoryMdCapMode")]
    public string MemoryMdCapMode { get; set; } = "off";

    /// <summary>
    /// Optional background prune pulse. When &gt; 0 AND at least one context file is in "force"
    /// mode, a background timer re-checks the capped files every N seconds (first tick +30s after
    /// start) and force-rewrites any that are over their limit. A status line is surfaced ONLY
    /// when a rewrite actually fires; idle ticks are silent. 0 (default) disables the pulse, so
    /// existing installs are unaffected. Interactive sessions only (never stdio/serve/acp).
    /// </summary>
    [JsonPropertyName("prunePulseSeconds")]
    public int PrunePulseSeconds { get; set; } = 0;
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
    /// Named TUI color theme (see <see cref="Theme"/>): one of default | dark | light | mono |
    /// solarized | dracula | gruvbox. Selected via <c>/theme &lt;name&gt;</c> or the <c>/setup</c>
    /// theme step. <c>"default"</c> (or any unknown value) yields the original hardcoded palette,
    /// so an absent/legacy config is byte-identical to prior behaviour. Ignored in stdio/serve mode.
    /// </summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "default";

    /// <summary>
    /// Tool-output verbosity in the TUI renderer. <c>"compact"</c> (default) collapses
    /// each tool result to a one-line summary (Claude-Code style); errors and diffs still
    /// expand. <c>"full"</c> renders the full bordered result panel. Toggled at runtime by
    /// <c>/verbose</c>. Ignored outside TUI render mode.
    /// </summary>
    [JsonPropertyName("toolOutput")]
    public string ToolOutput { get; set; } = "compact";

    /// <summary>
    /// When true (default), the TUI runs the frame-owned live-region driver: a pinned
    /// status footer (context meter + mode badges) and input box stay anchored at the
    /// bottom while the transcript flows into the terminal's native scrollback above them,
    /// the way Claude Code keeps a persistent bottom bar. This uses a log-update repaint
    /// (no DECSTBM scroll region, no alternate screen), so scrollback is preserved and the
    /// footer is never stranded. Set false to fall back to inline chrome with a status line
    /// printed before each prompt. Ignored outside TUI render mode / on non-capable terminals.
    /// </summary>
    [JsonPropertyName("dockedFooter")]
    public bool DockedFooter { get; set; } = true;

    /// <summary>
    /// Line threshold above which a collapsed (compact-mode) tool result advertises a
    /// "(ctrl+e expand)" affordance and can be re-expanded in place with Ctrl+E. A result
    /// whose informative-line count exceeds this value is considered "large". Additive and
    /// non-invasive: absent in older configs (defaults to 6); has no effect in /verbose
    /// (full) output mode where results already render as full panels. Ignored outside TUI.
    /// </summary>
    [JsonPropertyName("collapseToolLines")]
    public int CollapseToolLines { get; set; } = 6;

    /// <summary>
    /// Number of blank lines emitted BELOW a tool-call / sub-agent-delegation block before the
    /// following agent output in the live TUI. Tool/dot groups stay docked directly under the
    /// output text that introduced them; this controls only the separator gap beneath the group,
    /// which keeps dense parallel-fanout delegation readable. 0 = tight (no gap), 1 = one blank
    /// line (default). Additive and non-invasive: absent in older configs (defaults to 1). Ignored
    /// outside TUI. Adjusted live with <c>/set delegationSpacing &lt;n&gt;</c>.
    /// </summary>
    [JsonPropertyName("delegationSpacing")]
    public int DelegationSpacing { get; set; } = 1;

    /// <summary>
    /// When true (default), a delegated sub-agent's live output (streamed text, reasoning,
    /// and tool results) is collapsed into a single expandable transcript line instead of
    /// flowing inline, the way Claude Code collapses a launched Task. The sub-agent's thinking
    /// spinner still animates while it works (live progress), and the full transcript is kept
    /// expandable in place (Ctrl+E). Keeps dense single-agent and parallel-swarm delegation
    /// from cluttering the terminal. Toggled at runtime by <c>/subagentview</c> (alias
    /// <c>/sav</c>). Has no effect in stdio/serve mode (the web app demultiplexes sub-agent
    /// streams) and is ignored outside TUI render mode.
    /// </summary>
    [JsonPropertyName("collapseSubAgents")]
    public bool CollapseSubAgents { get; set; } = true;

    /// <summary>
    /// When true (default), goals fired by the in-house daemon (cron/watch triggers, agent mode)
    /// collapse their full agent run into one expandable Agent-View line instead of streaming the
    /// whole reasoning + tool transcript verbosely into the main viewport - the same density the
    /// sub-agent / background view uses. Toggled at runtime by <c>/daemonview</c> (alias
    /// <c>/dv</c>). Additive and non-invasive: absent in older configs (defaults to true). Has no
    /// effect in stdio/serve mode and is ignored outside TUI render mode.
    /// </summary>
    [JsonPropertyName("collapseDaemon")]
    public bool CollapseDaemon { get; set; } = true;

    /// <summary>
    /// When true (default), the user input/compose field is drawn on a subtle shaded band
    /// (a step off the terminal background) so the prompt reads as a contained input region
    /// rather than blending into the transcript, the way Claude Code shades its composer.
    /// Additive and non-invasive: absent in older configs (defaults to true). Toggled live
    /// with <c>/set inputHighlight false</c>. Ignored outside TUI render mode.
    /// </summary>
    [JsonPropertyName("inputHighlight")]
    public bool InputHighlight { get; set; } = true;

    /// <summary>
    /// When true (default), the BODY of expanded tool-result / batch-summary cards renders
    /// as muted markdown (headings, bold, inline code styled but subordinate) instead of raw
    /// markdown source (literal <c>###</c>, <c>**</c>, backticks). Tool names and status stay
    /// literal. Matches the sub-agent panel treatment for uniform formatting across the TUI.
    /// Additive and non-invasive: absent in older configs (defaults to true). Toggled live
    /// with <c>/set cardMarkdown false</c>. Ignored outside TUI render mode.
    /// </summary>
    [JsonPropertyName("cardMarkdown")]
    public bool CardMarkdown { get; set; } = true;

    /// <summary>
    /// When true (default), a <c>delegates -&gt; X</c> dispatch line auto-collapses to a
    /// single expandable summary row (the full task prompt is retained behind Ctrl+E), the
    /// same way sub-agent results already collapse, so dense parallel fanout stays scannable
    /// instead of printing every full prompt inline. Additive and non-invasive: absent in
    /// older configs (defaults to true). Toggled live with <c>/set collapseDelegations false</c>.
    /// Ignored outside TUI render mode.
    /// </summary>
    [JsonPropertyName("collapseDelegations")]
    public bool CollapseDelegations { get; set; } = true;

    /// <summary>
    /// When true (default), bracketed-paste mode (DECSET 2004) is enabled while the live
    /// TUI owns the terminal, so a multi-line clipboard paste is captured as one literal
    /// block (embedded newlines kept as soft line breaks in the compose buffer) and submitted
    /// only when the user presses Enter - instead of the first pasted newline submitting a
    /// truncated first line. Additive and non-invasive: absent in older configs (defaults to
    /// true). Set false to revert to line-at-a-time paste. Ignored outside TUI render mode.
    /// </summary>
    [JsonPropertyName("bracketedPaste")]
    public bool BracketedPaste { get; set; } = true;
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

    /// <summary>
    /// When true, the native config editor endpoints (GET/PUT /api/config-files/{config|swarm})
    /// are enabled, letting the web app read and write Config.json / Swarm.json. Default false
    /// keeps config files unreachable over the serve layer. Sibling of <see cref="Editable"/>
    /// (which gates sandbox file writes); kept separate so config editing can be granted or
    /// withheld independently of IDE file writes.
    /// </summary>
    [JsonPropertyName("configExposed")]
    public bool ConfigExposed { get; set; } = false;

    /// <summary>Optional app-level auth for the serve layer. Default disabled.</summary>
    [JsonPropertyName("auth")]
    public ServeAuthConfig Auth { get; set; } = new();

    /// <summary>Code editor (Monaco) asset provisioning for the serve layer.</summary>
    [JsonPropertyName("editor")]
    public ServeEditorConfig Editor { get; set; } = new();
}

/// <summary>
/// Controls how the Monaco editor assets backing the in-browser IDE pane are
/// provisioned. The ~13 MB minified asset tree is no longer vendored in the
/// repo; instead it is fetched once at first startup and cached on disk under
/// <c>Runtime/mux-web-app/monaco</c>. Additive; absent in older configs, in
/// which case defaults apply (auto-fetch on).
/// </summary>
public class ServeEditorConfig
{
    /// <summary>
    /// When true (default), the runtime downloads the Monaco asset bundle in the
    /// background on first startup if it is not already present on disk. Set to
    /// false to skip the fetch entirely -- for offline/air-gapped deployments
    /// that vendor the assets manually, or where the editor pane is unused.
    /// The fetch is a no-op when the assets already exist, so this is safe to
    /// leave on. Never blocks startup.
    /// </summary>
    [JsonPropertyName("autoFetch")]
    public bool AutoFetch { get; set; } = true;

    /// <summary>
    /// Pinned Monaco editor version to fetch from the npm registry. Must match a
    /// published <c>monaco-editor</c> release. Changing this re-fetches into a
    /// version-stamped cache on next startup.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.52.2";
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
