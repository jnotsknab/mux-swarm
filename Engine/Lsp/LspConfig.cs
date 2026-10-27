using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuxSwarm.Engine.Lsp;

/// <summary>
/// The <c>lsp</c> block in config.json: language servers Mux can talk to. Off by default. Mux has no
/// built-in server list; you point it at any server that speaks the Language Server Protocol over
/// stdio, keyed by a name of your choice.
/// </summary>
public sealed class LspConfig
{
    /// <summary>Master switch. False (default) = no tool, no servers started.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Append a short "LSP: N errors" block to the result of any tool call that changed a file a
    /// configured server covers (file tools, shell, REPL). Only applies while <see cref="Enabled"/>.
    /// </summary>
    [JsonPropertyName("diagnosticsOnWrite")]
    public bool DiagnosticsOnWrite { get; set; } = true;

    /// <summary>How long a tool result waits for fresh diagnostics, in milliseconds (default 1500, max 30000).</summary>
    [JsonPropertyName("diagnosticsWaitMs")]
    public int DiagnosticsWaitMs { get; set; } = 1500;

    /// <summary>Stop a server after this many idle minutes (default 10; 0 = keep it until exit).</summary>
    [JsonPropertyName("idleMinutes")]
    public int IdleMinutes { get; set; } = 10;

    /// <summary>Servers by name, e.g. <c>"python": { "command": "basedpyright-langserver", "args": ["--stdio"], "extensions": [".py"] }</c>.</summary>
    [JsonPropertyName("servers")]
    public Dictionary<string, LspServerConfig> Servers { get; set; } = [];
}

/// <summary>One language server. Never started through a shell.</summary>
public sealed class LspServerConfig
{
    /// <summary>Executable: a name looked up on PATH (Windows also tries .exe/.cmd/.bat), or a full path.</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    /// <summary>Arguments, passed one-for-one.</summary>
    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];

    /// <summary>Extra environment variables for the server process.</summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>File extensions this server handles, with the dot (<c>.py</c>). Required.</summary>
    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = [];

    /// <summary>
    /// LSP language id per extension when it is not the extension without its dot, e.g.
    /// <c>{ ".cs": "csharp", ".ts": "typescript" }</c>.
    /// </summary>
    [JsonPropertyName("languageIds")]
    public Dictionary<string, string> LanguageIds { get; set; } = [];

    /// <summary>
    /// Files or folders marking a project root, searched upward from the file (e.g. <c>pyproject.toml</c>,
    /// <c>*.sln</c>). First match wins; none = the workspace root.
    /// </summary>
    [JsonPropertyName("rootMarkers")]
    public List<string> RootMarkers { get; set; } = [];

    /// <summary>Sent as <c>initializationOptions</c>.</summary>
    [JsonPropertyName("initializationOptions")]
    public JsonElement? InitializationOptions { get; set; }

    /// <summary>Server settings: answers <c>workspace/configuration</c> and is sent with <c>workspace/didChangeConfiguration</c>.</summary>
    [JsonPropertyName("settings")]
    public JsonElement? Settings { get; set; }

    /// <summary>How long to wait for the server to initialize, in milliseconds (default 30000).</summary>
    [JsonPropertyName("startupTimeoutMs")]
    public int StartupTimeoutMs { get; set; } = 30000;

    /// <summary>Skip this server without deleting it.</summary>
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }
}
