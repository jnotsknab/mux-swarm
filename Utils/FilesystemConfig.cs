using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

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
}