using System;
using System.Collections.Generic;
using System.Linq;
using MuxSwarm.Utils;

namespace MuxSwarm.Setup;

/// <summary>
/// Provides default MCP server definitions and command-patching logic.
/// </summary>
public static class McpServerDefaults
{
    /// <summary>
    /// Adds any missing default MCP servers to the given dictionary.
    /// Only fills gaps — never overwrites user-customized entries.
    /// </summary>
    public static void EnsureDefaultsPresent(AppConfig config)
    {
        config.McpServers ??= new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        void AddIfMissing(string name, McpServerConfig cfg)
        {
            if (!config.McpServers.ContainsKey(name))
                config.McpServers[name] = cfg;
        }

        // Core MCPs (cross-platform)
        AddIfMissing("Memory", new McpServerConfig
        {
            Type = "stdio",
            Command = "npx",
            Args = new[] { "-y", "@modelcontextprotocol/server-memory" },
            Env = new Dictionary<string, string?>(),
            Enabled = true
        });

        AddIfMissing("Filesystem", new McpServerConfig
        {
            Type = "stdio",
            Command = "npx",
            Args = new[] { "-y", "@modelcontextprotocol/server-filesystem" },
            Env = new Dictionary<string, string?>(),
            Enabled = true
        });

        AddIfMissing("Fetch", new McpServerConfig
        {
            Type = "stdio",
            Command = "uvx",
            Args = new[] { "mcp-server-fetch" },
            Env = new Dictionary<string, string?>(),
            Enabled = true
        });

        AddIfMissing("ChromaDB", new McpServerConfig
        {
            Type = "stdio",
            Command = "uvx",
            Args = new[]
            {
                "chroma-mcp",
                "--client-type", "persistent",
                "--data-dir", "chroma-db"
            },
            Env = new Dictionary<string, string?>(),
            Enabled = true
        });

        AddIfMissing("BraveSearchMCP", new McpServerConfig
        {
            Type = "stdio",
            Command = "npx",
            Args = new[] { "-y", "@brave/brave-search-mcp-server" },
            Env = new Dictionary<string, string?> { ["BRAVE_API_KEY"] = "BRAVE_API_KEY" },
            Enabled = true
        });
        
        var replPath = Path.Combine(PlatformContext.BaseDirectory, "Runtime", "mcps", "py_async_repl_mcp.py");

        AddIfMissing("PythonReplMCP", new McpServerConfig
        {
            Type = "stdio",
            Command = "uv",
            Args = new[] { "run", "--with", "mcp", "python", replPath },
            Env = new Dictionary<string, string?>(),
            Enabled = true
        });

        // OS-specific
        if (PlatformContext.IsWindows)
        {
            AddIfMissing("Windows", new McpServerConfig
            {
                Type = "stdio",
                Command = "uvx",
                Args = new[] { "windows-mcp" },
                Env = new Dictionary<string, string?>
                {
                    ["ANONYMIZED_TELEMETRY"] = "true"
                },
                Enabled = true
            });
        }
    }

    /// <summary>
    /// After dependency resolution, patches MCP server commands from bare names (e.g. "npx")
    /// to absolute paths if a resolved path was found.
    /// </summary>
    public static void PatchCommandsFromDepResults(AppConfig config, List<DepResolver.DepResult> results)
    {
        config.McpServers ??= new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        string? npx = results.FirstOrDefault(r => r.Dep.Name == "npx")?.FoundPath;
        string? uvx = results.FirstOrDefault(r => r.Dep.Name == "uvx")?.FoundPath;

        if (!string.IsNullOrEmpty(npx))
        {
            PatchIfMatches(config, "Memory", "npx", npx);
            PatchIfMatches(config, "Filesystem", "npx", npx);
            if (!PlatformContext.IsWindows) PatchIfMatches(config, "Shell", "npx", npx);
            PatchIfMatches(config, "BraveSearchMCP", "npx", npx);
        }

        if (!string.IsNullOrEmpty(uvx))
        {
            PatchIfMatches(config, "Fetch", "uvx", uvx);
            PatchIfMatches(config, "ChromaDB", "uvx", uvx);

            if (PlatformContext.IsWindows)
                PatchIfMatches(config, "Windows", "uvx", uvx);
        }
    }

    private static void PatchIfMatches(AppConfig config, string serverName, string expectedCmd, string absoluteCmd)
    {
        if (!config.McpServers.TryGetValue(serverName, out var cfg)) return;
        if (string.IsNullOrWhiteSpace(cfg.Command)) return;

        // only patch defaults
        if (!cfg.Command.Equals(expectedCmd, StringComparison.OrdinalIgnoreCase))
            return;

        cfg.Command = absoluteCmd;
    }
}
