using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class FilesystemConfig
{
    [JsonPropertyName("allowedPaths")]
    public List<string> AllowedPaths { get; set; } = [];

    [JsonPropertyName("sandboxPath")]
    public string? SandboxPath { get; set; }

    [JsonPropertyName("skillsPath")]
    public string? SkillsPath { get; set; }

    [JsonPropertyName("sessionsPath")]
    public string? SessionsPath { get; set; }

    [JsonPropertyName("chromaDbPath")]
    public string? ChromaDbPath { get; set; }

    [JsonPropertyName("knowledgeGraphPath")]
    public string? KnowledgeGraphPath { get; set; }

    [JsonPropertyName("promptsPath")]
    public string? PromptsPath { get; set; }

    [JsonPropertyName("configDir")]
    public string? ConfigDir { get; set; }

    /// <summary>
    /// Security posture for the NATIVE Filesystem tools (Mux now owns these in-process instead
    /// of shelling out to @modelcontextprotocol/server-filesystem, so AllowedPaths is enforced
    /// here directly). Modes:
    ///   "standard" (default) - read+write within AllowedPaths. Identical to prior behavior.
    ///   "secure"   - read freely within AllowedPaths; any write/edit/move/create ELEVATES to
    ///                the user (confirm). Deny hard-blocks at the process level (never hits disk).
    ///   "lax"/"yolo" - read+write anywhere EXCEPT the cross-platform sensitive/system blocklist;
    ///                AllowedPaths always permitted.
    ///   "none"     - unrestricted, no path checks.
    /// Additive: absent in older configs -> "standard" (zero behavior change).
    /// </summary>
    [JsonPropertyName("securityMode")]
    public string SecurityMode { get; set; } = "standard";
}