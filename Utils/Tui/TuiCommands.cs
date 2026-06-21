namespace MuxSwarm.Utils.Tui;

/// <summary>
/// The single canonical list of interactive slash commands, kept in sync with the two
/// real command handlers: the App.cs top-level menu switch (REPL-only commands) and the
/// SingleAgentOrchestrator in-session meta-loop (session-native commands). Both palette
/// scopes are filtered views of this list, so the as-you-type preview can never show a
/// command in a scope where it does not actually work. Each entry is tagged with where it
/// applies.
///
/// GROUND TRUTH (verified against the handlers, not guessed):
///   - In-session meta-loop (SingleAgentOrchestrator): /compact /wipe /tokens /context
///     /undo /retry /redo /effort, plus the quit aliases /qc /qm and the bare "/" palette.
///     These are the ONLY commands that act inside a live session.
///   - Everything else is handled by the App.cs top-level switch and is REPL-only: typing
///     it inside a session does NOT work today (it would be sent to the agent as text), so
///     the in-session palette must offer "slash-anywhere": intercept it and offer to end
///     the session first.
/// </summary>
internal static class TuiCommands
{
    /// <summary>Where a command is offered/handled.</summary>
    public enum Scope
    {
        /// <summary>Handled only by the App.cs top-level menu switch.</summary>
        ReplOnly,
        /// <summary>Handled only by the in-session meta-loop.</summary>
        SessionOnly,
    }

    public readonly record struct Entry(string Cmd, string Desc, Scope Scope);

    /// <summary>
    /// Canonical command catalog. REPL-only mirrors App.cs's switch (lines ~370-724);
    /// SessionOnly mirrors SingleAgentOrchestrator's meta-loop (lines ~1043-1091).
    /// </summary>
    public static readonly Entry[] All =
    {
        // --- in-session meta-loop (SingleAgentOrchestrator) ---
        new("/compact",      "Compact the live session context", Scope.SessionOnly),
        new("/wipe",         "Wipe session context, start fresh", Scope.SessionOnly),
        new("/tokens",       "Show context/token usage", Scope.SessionOnly),
        new("/context",      "Show context/token usage", Scope.SessionOnly),
        new("/undo",         "Undo the last exchange", Scope.SessionOnly),
        new("/retry",        "Retry the last message", Scope.SessionOnly),
        new("/redo",         "Retry the last message", Scope.SessionOnly),
        new("/effort",       "Cycle reasoning effort (low/med/high)", Scope.SessionOnly),
        new("/qc",           "Quit the session loop", Scope.SessionOnly),
        new("/qm",           "Quit the session loop", Scope.SessionOnly),

        // --- mode launch (App.cs top-level menu) ---
        new("/swarm",        "Launch interactive multi-agent swarm loop", Scope.ReplOnly),
        new("/pswarm",       "Parallel swarm - concurrent batch dispatch", Scope.ReplOnly),
        new("/agent",        "Launch interactive single-agent loop", Scope.ReplOnly),
        new("/stateless",    "Stateless single-agent loop (one-off tasks)", Scope.ReplOnly),
        new("/subagents",    "Enable sub-agent delegation (/sub)", Scope.ReplOnly),
        new("/parasubagents","Enable parallel sub-agent delegation (/psub)", Scope.ReplOnly),
        new("/workflow",     "Run a deterministic workflow file", Scope.ReplOnly),
        new("/onboard",      "Create/update operator profile (BRAIN + MEMORY)", Scope.ReplOnly),

        // --- session/mode toggles (App.cs menu - applied to the NEXT launched session) ---
        new("/plan",         "Toggle plan mode (approve before exec)", Scope.ReplOnly),
        new("/ultra",        "Toggle deep-reasoning mode (plan + max reasoning)", Scope.ReplOnly),
        new("/continuous",   "Toggle autonomous execution (/cont)", Scope.ReplOnly),
        new("/addcontext",   "Configure per-agent injected context", Scope.ReplOnly),
        new("/maxp",         "Max agents running in parallel (default 4)", Scope.ReplOnly),
        new("/setmodel",     "Change an agent/orchestrator model", Scope.ReplOnly),
        new("/set",          "Set a config value (e.g. /set collapse 10)", Scope.ReplOnly),
        new("/config",       "Show all current config settings", Scope.ReplOnly),
        new("/newagent",     "Scaffold a new swarm agent (/newagent <name> [desc])", Scope.ReplOnly),
        new("/swap",         "Swap the active single-agent model", Scope.ReplOnly),
        new("/verbose",      "Toggle compact/full tool output", Scope.ReplOnly),
        new("/subagentview", "Toggle collapsed/expanded sub-agent output (/sav)", Scope.ReplOnly),
        new("/dockerexec",   "Toggle Docker execution mode", Scope.ReplOnly),
        new("/delimiter",    "Toggle multi-line input delimiter", Scope.ReplOnly),

        // --- global utilities (App.cs menu) ---
        new("/classic",      "Switch to the classic line renderer", Scope.ReplOnly),
        new("/tui",          "Switch to the live TUI renderer", Scope.ReplOnly),
        new("/resume",       "Resume a previous single-agent session", Scope.ReplOnly),
        new("/model",        "View current swarm models", Scope.ReplOnly),
        new("/provider",     "View or switch the active LLM provider", Scope.ReplOnly),
        new("/limits",       "Display current execution limits", Scope.ReplOnly),
        new("/tools",        "List available MCP tools", Scope.ReplOnly),
        new("/skills",       "List available local skills", Scope.ReplOnly),
        new("/memory",       "View the knowledge graph", Scope.ReplOnly),
        new("/sessions",     "List all saved sessions", Scope.ReplOnly),
        new("/setup",        "Run initial setup / reconfigure", Scope.ReplOnly),
        new("/reloadskills", "Refresh the skills directory", Scope.ReplOnly),
        new("/refresh",      "Full refresh: config, MCP servers, skills", Scope.ReplOnly),
        new("/report",       "Generate a session audit report", Scope.ReplOnly),
        new("/clear",        "Clear the screen", Scope.ReplOnly),
        new("/status",       "View full system status", Scope.ReplOnly),
        new("/help",         "Full command reference", Scope.ReplOnly),
        new("/exit",         "Exit Mux-Swarm", Scope.ReplOnly),
    };

    /// <summary>
    /// Commands that expect an inline argument after the command word, so Tab-completion leaves
    /// a trailing space ready for it. Everything else completes bare (no trailing space) because
    /// the dispatcher exact-matches the command token and "/agent " would not be recognized.
    /// </summary>
    private static readonly HashSet<string> ArgTaking = new(StringComparer.OrdinalIgnoreCase)
    {
        "/skill", "/skills", "/resume", "/setmodel", "/swap", "/provider", "/maxp",
        "/workflow", "/report", "/addcontext", "/set", "/newagent",
    };

    /// <summary>True when <paramref name="cmd"/> expects an inline argument (Tab keeps a space).</summary>
    public static bool TakesArgument(string cmd)
        => ArgTaking.Contains((cmd ?? "").Trim());

    /// <summary>True when <paramref name="cmd"/> is a session-native meta command.</summary>
    public static bool IsSessionNative(string cmd)
    {
        var c = (cmd ?? "").Trim().ToLowerInvariant();
        foreach (var e in All)
            if (e.Scope == Scope.SessionOnly && e.Cmd == c) return true;
        return false;
    }

    /// <summary>True when <paramref name="cmd"/> is a known REPL-only command.</summary>
    public static bool IsReplOnly(string cmd)
    {
        var c = (cmd ?? "").Trim().ToLowerInvariant();
        foreach (var e in All)
            if (e.Scope == Scope.ReplOnly && e.Cmd == c) return true;
        return false;
    }

    private static (string Cmd, string Desc)[] ForScope(Scope scope)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>();
        foreach (var e in All)
            if (e.Scope == scope && seen.Add(e.Cmd))
                list.Add((e.Cmd, e.Desc));
        return list.ToArray();
    }

    /// <summary>Commands offered at the top-level mode-select menu (REPL-only set).</summary>
    public static readonly (string Cmd, string Desc)[] Repl = ForScope(Scope.ReplOnly);

    /// <summary>Commands offered inside an agent/swarm session (session-native set).</summary>
    public static readonly (string Cmd, string Desc)[] Session = ForScope(Scope.SessionOnly);
}
