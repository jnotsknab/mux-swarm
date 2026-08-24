using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class McpServerConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "stdio"; // "stdio" or "http"

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string?>? Env { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Optional MCP protocol version pin (e.g. "2024-11-05"). When set, the client opens with the
    /// legacy initialize handshake instead of the 2026-07-28 discovery-first probe. Used for
    /// 1.x-era servers whose SDKs crash on the unknown server/discover method (e.g. chroma-mcp
    /// pinned to mcp 1.6.0). Null/omitted = full 2.0 negotiation (discovery-first + fallback).
    /// </summary>
    [JsonPropertyName("protocolVersion")]
    public string? ProtocolVersion { get; set; }
}