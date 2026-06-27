using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// Central registry that lets the native in-house tools (Filesystem + shell/REPL) be gated by the
/// SAME two mechanisms MCP servers use:
///   1. config.json mcpServers["Filesystem"|"Shell"].Enabled  -> global on/off (like any MCP).
///   2. swarm.json per-agent mcpServers / toolPatterns         -> which agents receive them.
///
/// To make (2) "function the same as" MCP filtering, each native tool is mapped to a VIRTUAL server
/// name. Filesystem tools already carry the <c>Filesystem_</c> prefix so the existing prefix rule in
/// the ToolFilter matches them with zero changes; the shell/REPL tools have unprefixed Claude-style
/// names (repl_shell_exec, execute_command_async, ...), so <see cref="VirtualServer"/> maps those to
/// the "Shell" virtual server and the ToolFilter consults it. No new config surface: the gate reuses
/// the existing mcpServers block, with the server entry marked native via a no-op command/arg
/// (<see cref="NativeMarker"/>) so it is shown to the user as native and never spawns a subprocess.
/// </summary>
public static class NativeToolRegistry
{
    public const string FilesystemServer = "Filesystem";
    public const string ShellServer = "Shell";

    /// <summary>No-op command/arg sentinel marking an mcpServers entry as a native, in-process toolset.</summary>
    public const string NativeMarker = "native-runtime-tools";

    // Stable lower-cased set of the shell/REPL tool names (the unprefixed Claude-style ones).
    private static readonly HashSet<string> _shellToolNames = new(StringComparer.OrdinalIgnoreCase);

    static NativeToolRegistry()
    {
        foreach (var t in ReplShellTools.Build())
            if (t is AIFunction fn) _shellToolNames.Add(fn.Name);
    }

    /// <summary>
    /// Map a tool name to its virtual server, or null if it is not a native tool. Filesystem tools
    /// resolve by their <c>Filesystem_</c> prefix; shell/REPL tools by the known-name set.
    /// </summary>
    public static string? VirtualServer(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return null;
        if (toolName.StartsWith(FilesystemServer + "_", StringComparison.OrdinalIgnoreCase))
            return FilesystemServer;
        if (_shellToolNames.Contains(toolName))
            return ShellServer;
        return null;
    }

    /// <summary>
    /// True when a tool belongs to the given server slot - by the MCP <c>{server}_</c> name prefix
    /// OR by the native virtual-server mapping. Used by the per-agent ToolFilter so native tools
    /// gate identically to MCP tools (listing "Filesystem"/"Shell" in an agent's mcpServers grants them).
    /// </summary>
    public static bool ServerMatches(string toolName, string server)
    {
        if (toolName.StartsWith(server + "_", StringComparison.OrdinalIgnoreCase)) return true;
        var v = VirtualServer(toolName);
        return v is not null && server.Equals(v, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when an mcpServers entry is a native marker (no-op command/arg), not a real server.</summary>
    public static bool IsNativeEntry(McpServerConfig? c)
    {
        if (c is null) return false;
        bool Has(string? s) => s is not null && s.Contains(NativeMarker, StringComparison.OrdinalIgnoreCase);
        if (Has(c.Command)) return true;
        if (c.Args is not null)
            foreach (var a in c.Args) if (Has(a)) return true;
        return false;
    }

    /// <summary>
    /// True when the legacy npx @modelcontextprotocol/server-filesystem entry is present - we now
    /// satisfy it natively, so existing configs upgrade transparently (skip spawn, bind native).
    /// </summary>
    public static bool IsLegacyFilesystemEntry(McpServerConfig? c)
    {
        if (c is null) return false;
        bool Has(string? s) => s is not null && s.Contains("server-filesystem", StringComparison.OrdinalIgnoreCase);
        if (Has(c.Command)) return true;
        if (c.Args is not null)
            foreach (var a in c.Args) if (Has(a)) return true;
        return false;
    }

    /// <summary>True when the named native server is globally enabled (absent entry -> enabled; native is default).</summary>
    public static bool ServerEnabled(AppConfig config, string serverName)
    {
        if (config.McpServers != null && config.McpServers.TryGetValue(serverName, out var cfg))
            return cfg.Enabled;
        return true; // not listed -> native default on; per-agent ToolFilter still gates delivery
    }

    /// <summary>
    /// The native tool pool to MERGE INTO the pre-filter allTools list (alongside MCP tools), so the
    /// existing per-agent ToolFilter selects them. Globally-disabled native servers contribute nothing.
    /// </summary>
    public static IList<AITool> BuildPool(AppConfig config)
    {
        var pool = new List<AITool>();
        if (ServerEnabled(config, FilesystemServer))
            pool.AddRange(FilesystemTools.Build());
        if (ServerEnabled(config, ShellServer))
            pool.AddRange(ReplShellTools.Build());
        return pool;
    }
}
