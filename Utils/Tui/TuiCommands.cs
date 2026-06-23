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
        new("/tag",          "Tag this session for easy resume/search (/tag <text>)", Scope.SessionOnly),
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
        new("/showreasoning","Show/hide streamed reasoning (full|summary|none)", Scope.ReplOnly),
        new("/config",       "Show all current config settings", Scope.ReplOnly),
        new("/newagent",     "Scaffold a new swarm agent (/newagent <name> [desc])", Scope.ReplOnly),
        new("/editagent",    "Edit a swarm agent (model/desc/MCP/delegate)", Scope.ReplOnly),
        new("/delagent",     "Remove a swarm agent (/delagent [name])", Scope.ReplOnly),
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
        new("/workspace",    "Show or set the @-file workspace root (/workspace <path>)", Scope.ReplOnly),
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
        new("/shortcuts",    "Show keyboard shortcuts (/keys)", Scope.ReplOnly),
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
        "/workflow", "/report", "/addcontext", "/set", "/newagent", "/editagent", "/delagent",
        "/tag", "/showreasoning", "/workspace",
    };

    /// <summary>True when <paramref name="cmd"/> expects an inline argument (Tab keeps a space).</summary>
    public static bool TakesArgument(string cmd)
        => ArgTaking.Contains((cmd ?? "").Trim());

    /// <summary>
    /// Commands whose handler opens a BLOCKING interactive picker/prompt (Spectre TextPrompt /
    /// SelectionPrompt). For these the submitted command line is NOT echoed into scrollback: the
    /// picker draws its own UI and the handler prints a "&#x2713; ... saved" confirmation, so an
    /// extra echo of the bare "/set" / "/swap" line is just residue. Bare invocation only - a
    /// command WITH an inline argument (e.g. "/set key value") runs non-interactively and is echoed
    /// normally so the turn stays delimited.
    /// </summary>
    private static readonly HashSet<string> InteractivePicker = new(StringComparer.OrdinalIgnoreCase)
    {
        "/set", "/swap", "/setmodel", "/provider", "/resume", "/editagent", "/delagent", "/addcontext",
    };

    /// <summary>True when <paramref name="line"/> is a bare command that opens an interactive picker
    /// (no inline argument), so its echo should be suppressed.</summary>
    public static bool OpensInteractivePrompt(string line)
    {
        var t = (line ?? "").Trim();
        return InteractivePicker.Contains(t);   // bare token only (no space/arg)
    }

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

    /// <summary>A user-facing keyboard shortcut: the key chord, what it does, and where it
    /// applies (prompt = the input line; turn = while an agent turn is streaming; view = inside
    /// the transcript/expand overlay).</summary>
    public readonly record struct KeyBind(string Keys, string Desc, string Context);

    /// <summary>
    /// Canonical keyboard-shortcut catalog. Single source of truth for the /shortcuts command,
    /// the Help.cs reference block, and the /api/commands web endpoint, so the three never drift.
    /// Verified against the live handlers: LineEditor.Feed (prompt editing + vim), TuiDriver.ReadLine
    /// (prompt-level Ctrl+E / Ctrl+G), EscapeKeyListener (mid-turn Esc / Ctrl+E / Ctrl+G), and the
    /// EnterNavMode overlay loop (view navigation).
    /// </summary>
    public static readonly KeyBind[] Keys =
    {
        // --- prompt (input line) ---
        new("Enter",       "Submit the current message", "prompt"),
        new("Alt+Enter",   "Insert a newline (multi-line compose) without submitting", "prompt"),
        new("Ctrl+J",      "Insert a newline (alias for Alt+Enter)", "prompt"),
        new("Tab",         "Accept the top autocomplete (/command, @file, /skill, /resume)", "prompt"),
        new("Shift+Tab",   "Cycle reasoning effort (low/med/high)", "prompt"),
        new("Up/Down",     "Browse command history (or move the autocomplete selection)", "prompt"),
        new("Ctrl+A",      "Move cursor to start of line", "prompt"),
        new("Ctrl+E",      "Move cursor to end of line (or expand latest tool result if collapsible)", "prompt"),
        new("Ctrl+U",      "Delete to start of line", "prompt"),
        new("Ctrl+K",      "Delete to end of line", "prompt"),
        new("Ctrl+W",      "Delete the previous word", "prompt"),
        new("Ctrl+C",      "Cancel the current input line", "prompt"),
        new("Esc",         "Empty prompt: open the transcript view; with text: enter vim Normal mode", "prompt"),
        new("Ctrl+G",      "Open the transcript/expand view (does not cancel)", "prompt"),
        new("Ctrl+L",      "Clear resize/redraw artifacts and repaint", "prompt"),

        // --- during an agent turn (mid-stream) ---
        new("Esc",         "Cancel the current turn", "turn"),
        new("Ctrl+E",      "Expand the latest large tool result inline without cancelling", "turn"),
        new("Ctrl+G",      "Expand the latest large tool result inline (alias for Ctrl+E)", "turn"),
        new("Ctrl+L",      "Clear resize/redraw artifacts and repaint (does not cancel)", "turn"),

        // --- transcript / expand view (NAV overlay) ---
        new("j / k",       "Move cursor down / up one line", "view"),
        new("Ctrl+D/U",    "Scroll half a page down / up", "view"),
        new("Ctrl+F/B",    "Scroll a full page down / up (also PgDn/PgUp)", "view"),
        new("g / G",       "Jump to top / bottom (also Home/End)", "view"),
        new("Ctrl+E/Enter","Toggle the focused tool result open / closed", "view"),
        new("q / Esc / i", "Exit the view and return to the prompt", "view"),
    };
}
