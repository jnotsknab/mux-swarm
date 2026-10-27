using System.Text.Json;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Requests an extension process sends to Mux (JSON-RPC requests with an <c>id</c>, answered with
/// <c>result</c> or <c>error</c>). Only trusted processes run, so only they can call these. Text an
/// extension submits goes through the normal input path, so it runs when the session is idle and is
/// dispatched exactly as if the user typed it.
/// </summary>
internal static class ExtensionHostApi
{
    /// <summary>Methods a process may call (documented under <c>### Extension host API</c> in DOCS.md).</summary>
    internal static readonly string[] Methods = ["session/info", "ui/toast", "ui/log", "ui/open", "ui/update", "ui/close", "prompt/submit", "command/run", "status/set"];

    /// <summary>Longest text accepted by <c>prompt/submit</c>.</summary>
    internal const int MaxPromptChars = 64_000;

    /// <summary>
    /// Built-in commands an extension may run with <c>command/run</c> (any extension command is also
    /// allowed). An allowlist, so new commands are refused until listed: nothing that changes trust,
    /// the sandbox, config, providers, sharing or the app lifecycle.
    /// </summary>
    internal static readonly HashSet<string> AllowedBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "/compact", "/prune", "/handoff", "/tokens", "/context", "/cost", "/diff", "/status", "/help",
        "/retry", "/undo", "/redo", "/effort", "/max", "/tag", "/theme", "/verbose", "/subagentview",
        "/daemonview", "/plan", "/ultra", "/giga", "/tools", "/skills", "/sessions", "/model", "/limits",
        "/doctor", "/review", "/clear", "/shortcuts",
    };

    /// <summary>
    /// Where submitted lines go: (extension, line) -> null when queued, else why not. Defaults to
    /// <see cref="MuxConsole.TryInjectInput"/>; tests replace it.
    /// </summary>
    internal static Func<string, string, string?> Inject = MuxConsole.TryInjectInput;

    /// <summary>An answer to a request: a result object, or an error code + message.</summary>
    internal readonly record struct Reply(object? Result, int Code = 0, string? Error = null)
    {
        internal static Reply Fail(string message, int code = 1) => new(null, code, message);
    }

    /// <summary>Handle request <paramref name="method"/> from extension <paramref name="ext"/>. Never throws.</summary>
    internal static Reply Handle(string ext, string method, JsonElement p)
    {
        string? Str(string key) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        try
        {
            switch (method)
            {
                case "session/info":
                    return new(SessionInfo());
                case "ui/toast":
                    if (OneLine(Str("text"), 300) is not { Length: > 0 } toast) return Fail("text");
                    MuxConsole.ExtensionToast(ext, toast);
                    return new(new { });
                case "status/set":
                {
                    // Keyed status text in the footer (the request form of the footer/set notification, with an answer).
                    if (!ExtensionFooter.IsValidId(Str("key"))) return Fail("key");
                    bool changed = ExtensionFooter.Set(ext, Str("key")!, Str("text"));
                    if (changed) MuxConsole.TuiRepaintSoon();
                    return new(new { changed });
                }
                case "ui/log":
                    if (OneLine(Str("text"), 1000) is not { Length: > 0 } log) return Fail("text");
                    MuxConsole.WriteMuted($"\u25cf {ext}: {log}");
                    return new(new { });
                case "ui/open":
                case "ui/update":
                {
                    if (Str("id") is not { Length: > 0 } id) return Fail("id");
                    var view = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("view", out var v) ? v : default;
                    int? Int(string k) => p.TryGetProperty(k, out var n) && n.TryGetInt32(out var i) ? i : null;
                    var err = method == "ui/open"
                        ? ExtensionPanels.Open(ext, id, Str("title"), view, Str("placement"), Int("columns"), Int("rows"))
                        : ExtensionPanels.Update(ext, id, view);
                    return err is null ? new(new { }) : Reply.Fail(err, -32602);
                }
                case "ui/close":
                    if (Str("id") is not { Length: > 0 } closeId) return Fail("id");
                    return new(new { closed = ExtensionPanels.Close(ext, closeId, byUser: false) });
                case "prompt/submit":
                {
                    var text = Str("text")?.Trim();
                    if (string.IsNullOrEmpty(text)) return Fail("text");
                    if (CheckPrompt(text) is { } bad) return Reply.Fail(bad);
                    if (!InSession) return Reply.Fail("no session is running (prompts need /agent, /swarm or /pswarm)");
                    return Inject(ext, text!) is { } why ? Reply.Fail(why) : new(new { queued = true });
                }
                case "command/run":
                {
                    var line = Str("line")?.Trim();
                    if (string.IsNullOrEmpty(line)) return Fail("line");
                    if (CheckCommand(line) is { } bad) return Reply.Fail(bad);
                    return Inject(ext, line!) is { } why ? Reply.Fail(why) : new(new { queued = true });
                }
                default:
                    return Reply.Fail($"unknown method '{method}'", -32601);
            }
        }
        catch (Exception ex)
        {
            return Reply.Fail(ex.Message);
        }

        static Reply Fail(string field) => Reply.Fail($"params.{field} must be a non-empty string", -32602);
    }

    /// <summary>Why <c>prompt/submit</c> refuses <paramref name="text"/>, or null when it is fine.</summary>
    internal static string? CheckPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "params.text must be a non-empty string";
        if (text.Length > MaxPromptChars) return $"text is longer than {MaxPromptChars} characters";
        // Commands go through command/run (allowlisted); a shell escape would run outside the extension's sandbox.
        if (text[0] == '/') return "prompts cannot start with '/': use command/run";
        if (text[0] == '!') return "prompts cannot start with '!' (shell escape)";
        return null;
    }

    /// <summary>Why <c>command/run</c> refuses <paramref name="line"/>, or null when it is allowed.</summary>
    internal static string? CheckCommand(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '/') return "params.line must be a slash command, e.g. \"/compact\"";
        if (line.Contains('\n') || line.Contains('\r')) return "params.line must be one line";
        if (ExtensionCommands.Match(line) is not null) return null;
        var word = line.Split(' ', 2)[0];
        return AllowedBuiltIns.Contains(word) ? null : $"'{word}' cannot be run by an extension";
    }

    private static bool InSession => !string.Equals(ServeMode.ActiveMode, "interactive", StringComparison.OrdinalIgnoreCase);

    /// <summary>The lead agent of the running session, or null at the menu (no session).</summary>
    internal static string? SessionAgent() =>
        InSession ? SingleAgentOrchestrator.AgentDef?.Name ?? App.SwarmConfig?.SingleAgent?.Name : null;

    /// <summary>The <c>session/info</c> result.</summary>
    internal static object SessionInfo()
    {
        var cfg = App.SwarmConfig;
        string? agent = SingleAgentOrchestrator.AgentDef?.Name ?? cfg?.SingleAgent?.Name;
        string? model = agent is null ? null
            : cfg?.Agents?.FirstOrDefault(a => string.Equals(a.Name, agent, StringComparison.OrdinalIgnoreCase))?.Model ?? cfg?.SingleAgent?.Model;
        uint tokens = SingleAgentOrchestrator.SessionTokens;
        int window = SingleAgentOrchestrator.AutoCompactThreshold;
        return new
        {
            mode = ServeMode.ActiveMode,
            inSession = InSession,
            agent,
            model,
            sessionId = MuxConsole.CurrentSessionId,
            cwd = PlatformContext.WorkspaceRoot,
            surface = MuxConsole.Surface,
            muxVersion = App.Version,
            context = new { tokens, window, percent = window > 0 ? Math.Round(tokens * 100.0 / window, 1) : 0 },
        };
    }

    // One line of plain text: control characters become spaces, whitespace collapses, capped length.
    private static string? OneLine(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = string.Join(' ', new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max];
    }
}
