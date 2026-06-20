namespace MuxSwarm.Utils.Tui;

/// <summary>
/// The single canonical list of interactive slash commands, kept in sync with the App.cs
/// command switch and Help.cs. Both palette scopes (top-level repl menu vs in-session) are
/// filtered views of this list, so the as-you-type preview can never be missing a command
/// that actually works. Each entry is tagged with where it applies.
/// </summary>
internal static class TuiCommands
{
    /// <summary>Where a command is offered in the as-you-type palette.</summary>
    public enum Scope
    {
        /// <summary>Mode-launch commands - only meaningful at the top-level menu.</summary>
        ReplOnly,
        /// <summary>Session controls - only meaningful inside an agent/swarm session.</summary>
        SessionOnly,
        /// <summary>Global commands available in both scopes.</summary>
        Both
    }

    public readonly record struct Entry(string Cmd, string Desc, Scope Scope);

    /// <summary>Canonical command catalog (mirror of App.cs switch + Help.cs).</summary>
    public static readonly Entry[] All =
    {
        // --- mode launch (top-level menu) ---
        new("/swarm",        "Launch interactive multi-agent swarm loop", Scope.ReplOnly),
        new("/pswarm",       "Parallel swarm - concurrent batch dispatch", Scope.ReplOnly),
        new("/agent",        "Launch interactive single-agent loop", Scope.ReplOnly),
        new("/stateless",    "Stateless single-agent loop (one-off tasks)", Scope.ReplOnly),
        new("/subagents",    "Enable sub-agent delegation (/sub)", Scope.ReplOnly),
        new("/parasubagents","Enable parallel sub-agent delegation (/psub)", Scope.ReplOnly),
        new("/workflow",     "Run a deterministic workflow file", Scope.ReplOnly),
        new("/onboard",      "Create/update operator profile (BRAIN + MEMORY)", Scope.ReplOnly),

        // --- session controls (in a session) ---
        new("/plan",         "Toggle plan mode (approve before exec)", Scope.SessionOnly),
        new("/ultra",        "Deep-reasoning mode (plan + max reasoning)", Scope.SessionOnly),
        new("/continuous",   "Toggle autonomous execution (/cont)", Scope.SessionOnly),
        new("/compact",      "Compact current session context", Scope.SessionOnly),
        new("/tokens",       "Show context/token usage", Scope.SessionOnly),
        new("/undo",         "Undo the last exchange", Scope.SessionOnly),
        new("/retry",        "Retry the last turn", Scope.SessionOnly),
        new("/swap",         "Swap the active single-agent model", Scope.SessionOnly),
        new("/qc",           "Exit the current session loop", Scope.SessionOnly),

        // --- global (both scopes) ---
        new("/classic",      "Switch to the classic line renderer", Scope.Both),
        new("/tui",          "Switch to the live TUI renderer", Scope.Both),
        new("/verbose",      "Toggle compact/full tool output", Scope.Both),
        new("/resume",       "Resume a previous single-agent session", Scope.Both),
        new("/addcontext",   "Configure per-agent injected context", Scope.Both),
        new("/maxp",         "Max agents running in parallel (default 4)", Scope.Both),
        new("/model",        "View current swarm models", Scope.Both),
        new("/setmodel",     "Change an agent/orchestrator model", Scope.Both),
        new("/provider",     "View or switch the active LLM provider", Scope.Both),
        new("/limits",       "Display current execution limits", Scope.Both),
        new("/tools",        "List available MCP tools", Scope.Both),
        new("/skills",       "List available local skills", Scope.Both),
        new("/memory",       "View the knowledge graph", Scope.Both),
        new("/sessions",     "List all saved sessions", Scope.Both),
        new("/dockerexec",   "Toggle Docker execution mode", Scope.Both),
        new("/delimiter",    "Toggle multi-line input delimiter", Scope.Both),
        new("/setup",        "Run initial setup / reconfigure", Scope.Both),
        new("/reloadskills", "Refresh the skills directory", Scope.Both),
        new("/refresh",      "Full refresh: config, MCP servers, skills", Scope.Both),
        new("/report",       "Generate a session audit report", Scope.Both),
        new("/clear",        "Clear the screen", Scope.Both),
        new("/status",       "View full system status", Scope.Both),
        new("/help",         "Full command reference", Scope.Both),
        new("/exit",         "Exit Mux-Swarm", Scope.Both),
    };

    private static (string Cmd, string Desc)[] ForScope(bool sessionScope)
    {
        var wanted = sessionScope ? Scope.SessionOnly : Scope.ReplOnly;
        var list = new List<(string, string)>();
        foreach (var e in All)
            if (e.Scope == wanted || e.Scope == Scope.Both)
                list.Add((e.Cmd, e.Desc));
        return list.ToArray();
    }

    /// <summary>Commands offered at the top-level mode-select menu.</summary>
    public static readonly (string Cmd, string Desc)[] Repl = ForScope(sessionScope: false);

    /// <summary>Commands offered inside an agent/swarm session.</summary>
    public static readonly (string Cmd, string Desc)[] Session = ForScope(sessionScope: true);
}
