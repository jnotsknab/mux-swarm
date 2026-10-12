using System.Text.Json.Serialization;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// The <c>extension.json</c> manifest at the root of an extension folder. Everything an
/// extension contributes is declared here, so Mux can list and validate it without running code.
/// </summary>
internal sealed class ExtensionManifest
{
    /// <summary>
    /// Unique id: lowercase letters, digits and hyphens, 1-32 chars, starting with a letter or
    /// digit. Defaults to the folder name when omitted.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Free-form version string shown by <c>/extensions</c>.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>One-line description shown by <c>/extensions</c>.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// TUI color themes. Each entry needs a <c>name</c>, may name a built-in <c>base</c> preset
    /// (default <c>default</c>), and overrides any theme color role by its camelCase key
    /// (<c>accent</c>, <c>success</c>, <c>cardBg</c>, ...). Values are Spectre color tokens:
    /// <c>#RRGGBB</c>, a named color, or <c>default</c>.
    /// </summary>
    [JsonPropertyName("themes")]
    public List<Dictionary<string, string>> Themes { get; set; } = [];

    /// <summary>
    /// Optional long-running process Mux talks to with JSON-RPC 2.0 over stdin/stdout. Started
    /// only when the extension is listed in <c>extensions.trusted</c>.
    /// </summary>
    [JsonPropertyName("process")]
    public ExtensionProcessSpec? Process { get; set; }

    /// <summary>
    /// Lifecycle events the process receives as <c>event</c> notifications (the same names and
    /// payload as hooks: <c>tool_call</c>, <c>turn_end</c>, ...; <c>*</c> = all).
    /// </summary>
    [JsonPropertyName("events")]
    public List<string> Events { get; set; } = [];

    /// <summary>
    /// Tool-call interceptors served by the process (<c>tool/before</c> / <c>tool/after</c>
    /// requests). Each entry: <c>tool</c> (name or prefix ending in <c>*</c>), <c>on</c>
    /// (<c>before</c>, <c>after</c> or <c>both</c>, default <c>before</c>), <c>timeoutMs</c>
    /// (default 2000, max 30000), and <c>onTimeout</c> (<c>allow</c> default, or <c>deny</c>).
    /// </summary>
    [JsonPropertyName("intercepts")]
    public List<InterceptSpec> Intercepts { get; set; } = [];

    /// <summary>
    /// Tools the process serves. Each is exposed to the model as <c>{extension}_{name}</c> and is
    /// only available while the (trusted) process runs.
    /// </summary>
    [JsonPropertyName("tools")]
    public List<ExtensionToolSpec> Tools { get; set; } = [];

    /// <summary>Thinking-indicator spinner frames, replacing the built-in ones. 1-32 frames, each one terminal cell wide.</summary>
    [JsonPropertyName("spinner")]
    public List<string>? Spinner { get; set; }

    /// <summary>Words cycled by the thinking indicator while the agent reasons ("Thinking", "Pondering", ...). Replaces the built-in list.</summary>
    [JsonPropertyName("quips")]
    public List<string>? Quips { get; set; }

    /// <summary>
    /// Tool-call action labels keyed by verb stem (the first word of a tool name after any server
    /// prefix, e.g. <c>read</c>, <c>search</c>). Merged over the built-ins; the extension wins per key.
    /// </summary>
    [JsonPropertyName("toolLabels")]
    public Dictionary<string, string>? ToolLabels { get; set; }

    /// <summary>Startup splash lines. Replaces the whole built-in pool (quotes, facts, tips, taglines).</summary>
    [JsonPropertyName("splash")]
    public List<string>? Splash { get; set; }
}

/// <summary>One raw <c>intercepts</c> entry; validated into an <see cref="InterceptRule"/>.</summary>
internal sealed class InterceptSpec
{
    /// <summary>Tool name, or a prefix ending in <c>*</c> (<c>*</c> alone = every tool).</summary>
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    /// <summary><c>before</c> (default), <c>after</c> or <c>both</c>.</summary>
    [JsonPropertyName("on")]
    public string? On { get; set; }

    /// <summary>How long to wait for the extension's answer, in milliseconds (default 2000).</summary>
    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; set; }

    /// <summary><c>allow</c> (default) runs the tool when the extension does not answer; <c>deny</c> blocks it.</summary>
    [JsonPropertyName("onTimeout")]
    public string? OnTimeout { get; set; }
}

/// <summary>One tool an extension process serves via <c>tool/call</c> requests.</summary>
internal sealed class ExtensionToolSpec
{
    /// <summary>Tool name: lowercase letters, digits and '_', starting with a letter (max 48).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>What the tool does, shown to the model (required, max 1024 chars).</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>JSON Schema object for the arguments. Defaults to an object with no properties.</summary>
    [JsonPropertyName("parameters")]
    public System.Text.Json.JsonElement Parameters { get; set; }

    /// <summary>Seconds to wait for the reply (default 60, max 600).</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Agents that get this tool without listing the extension in Swarm.json: <c>["*"]</c> = every
    /// agent (including the orchestrator), or agent names (case-insensitive). Omitted = only agents
    /// whose <c>mcpServers</c>/<c>toolPatterns</c> select it, like an MCP server.
    /// </summary>
    [JsonPropertyName("agents")]
    public List<string>? Agents { get; set; }
}

/// <summary>How to start an extension process. Never run through a shell.</summary>
internal sealed class ExtensionProcessSpec
{
    /// <summary>Executable: a bare name looked up on PATH, or a path relative to the extension folder.</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    /// <summary>Arguments, passed one-for-one (no quoting or splitting).</summary>
    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];
}
