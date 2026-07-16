using System.Text;
using System.Text.Json;
using MuxSwarm.State;
using Spectre.Console;

namespace MuxSwarm.Utils;

/// <summary>
/// Centralized console output for the entire MUX-SWARM application.
/// Interactive mode: Spectre.Console rich rendering (panels, rules, tables, colors).
/// Stdio mode:       NDJSON (newline-delimited JSON) for machine parsing and pipeline integration.
///
/// Every file in the project should use MuxConsole instead of Console.Write/WriteLine.
/// </summary>
public static partial class MuxConsole
{
    /// <summary>
    /// When true, all output is NDJSON (one JSON object per line) suitable for piping / parsing.
    /// Set this from App.ParseArgs() when --stdio is present.
    /// </summary>
    public static bool StdioMode { get; set; } = false;

    /// <summary>
    /// True while an ACP (--acp) adapter session owns stdout. ACP and stdio are mutually
    /// exclusive transports: when AcpActive is set, structured events are routed to
    /// <see cref="AcpSink"/> instead of being written as NDJSON to stdout (which is reserved
    /// for ACP JSON-RPC). Off the --acp path this is always false and every emit path is
    /// byte-identical to before.
    /// </summary>
    public static bool AcpActive { get; set; } = false;

    /// <summary>
    /// Optional sink that receives structured events (type + fields) INSTEAD of the NDJSON
    /// stdout writer while an ACP session is active. The ACP adapter installs this to
    /// translate Mux events into ACP session/update notifications. Null off --acp.
    /// </summary>
    public static Action<string, IReadOnlyDictionary<string, object?>>? AcpSink { get; set; }

    /// <summary>
    /// Interactive render layer (v0.11.0). This is orthogonal to <see cref="StdioMode"/>:
    /// stdio/serve always emits NDJSON and short-circuits BEFORE any render-mode branch,
    /// so the machine/web contract is byte-identical regardless of this value.
    /// <list type="bullet">
    /// <item><see cref="MuxSwarm.Utils.RenderMode.Stdio"/> - reported when <see cref="StdioMode"/> is on.</item>
    /// <item><see cref="MuxSwarm.Utils.RenderMode.Tui"/> - full-screen live renderer (interactive default on a capable TTY).</item>
    /// <item><see cref="MuxSwarm.Utils.RenderMode.Classic"/> - pre-v0.11.0 line-by-line renderer (opt-out / non-capable fallback).</item>
    /// </list>
    /// </summary>
    public static RenderMode RenderMode
    {
        get
        {
            // stdio/serve is sacrosanct and takes absolute precedence.
            if (StdioMode) return RenderMode.Stdio;
            return _interactiveRenderMode;
        }
    }

    private static RenderMode _interactiveRenderMode = RenderMode.Classic;

    /// <summary>
    /// True when the active interactive renderer is the live full-screen TUI.
    /// Always false in stdio/serve mode. Render helpers use this to decide whether to
    /// route through the live layout; until the TUI layout lands it renders identically
    /// to classic, so flipping the default is behaviorally safe.
    /// </summary>
    public static bool IsTui => !StdioMode && _interactiveRenderMode == RenderMode.Tui;

    /// <summary>
    /// Resolve the interactive render mode from an explicit preference plus terminal
    /// capability. Call once at startup (after args/config are parsed) on the interactive
    /// path. No effect on the stdio/serve contract — <see cref="RenderMode"/> still reports
    /// <see cref="MuxSwarm.Utils.RenderMode.Stdio"/> whenever <see cref="StdioMode"/> is set.
    /// </summary>
    /// <param name="preference">"auto" (capability-aware), "tui" (force), or "classic" (force). Null/empty = "auto".</param>
    public static void ResolveRenderMode(string? preference)
    {
        var pref = (preference ?? "auto").Trim().ToLowerInvariant();
        _interactiveRenderMode = pref switch
        {
            "classic" => RenderMode.Classic,
            "tui"     => RenderMode.Tui,
            // "auto" and anything unrecognized: capability-aware default.
            _ => IsTuiCapableTerminal() ? RenderMode.Tui : RenderMode.Classic
        };
    }

    /// <summary>
    /// Force the classic line renderer (e.g. the <c>/classic</c> toggle or <c>--classic</c> flag).
    /// </summary>
    public static void SetClassicRenderMode() => _interactiveRenderMode = RenderMode.Classic;

    /// <summary>
    /// Force the live TUI renderer (e.g. a <c>/tui</c> toggle or <c>--tui</c> flag). Falls back to
    /// classic automatically on a non-capable terminal so a broken TUI is never shown.
    /// </summary>
    public static void SetTuiRenderMode()
        => _interactiveRenderMode = IsTuiCapableTerminal() ? RenderMode.Tui : RenderMode.Classic;

    /// <summary>
    /// Heuristic terminal-capability probe driving the capability-aware default. A terminal
    /// is considered TUI-capable when stdout is an interactive console (not redirected/piped),
    /// not a dumb terminal, and not a known non-interactive CI environment.
    /// </summary>
    public static bool IsTuiCapableTerminal()
    {
        try
        {
            // Redirected/piped stdout or stdin => no live full-screen layout.
            if (Console.IsOutputRedirected || Console.IsInputRedirected) return false;

            // Dumb terminals can't render ANSI/alt-screen.
            var term = Environment.GetEnvironmentVariable("TERM");
            if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase)) return false;

            // Common CI signal: default to the safe line renderer in automation.
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                return false;

            // Spectre's own capability check (ANSI support, interactivity).
            if (!AnsiConsole.Profile.Capabilities.Ansi) return false;

            return true;
        }
        catch
        {
            // Any probe failure => safest choice is the classic line renderer.
            return false;
        }
    }

    /// <summary>
    /// Shared lock to prevent the thinking indicator's \r-based rendering
    /// from interleaving with streamed text output, which causes garbled lines.
    /// </summary>
    internal static readonly object ConsoleLock = new();


    /// <summary>
    /// Gets or sets the text reader used for console input.
    /// Defaults to <see cref="Console.In"/>. Set this property to redirect input from a different source
    /// such as a <see cref="StringReader"/> for testing or automated scenarios.
    /// </summary>
    public static TextReader InputOverride { get; set; } = Console.In;

    private static ThinkingIndicator? _activeIndicator;
    public static string? MultiLineDelimiter { get; set; }
    public static bool UsingDelimiter { get; set; }
    private static bool _isStreaming;
    public static string? MascotPath { get; set; }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Serve/stdio origin tag for the NDJSON stream. Independent of the TUI-bound SubAgentCapture
    // (which no-ops under StdioMode), this AsyncLocal flows into orchestrator child tasks so every
    // frame a daemon-fired goal emits -- stream, tool_call, success/error -- carries an origin+lane
    // tag. The web app routes origin=="daemon" frames to a dedicated Node-Graph lane instead of the
    // main viewport. Additive: absent tag => byte-identical legacy frames, unknown-field-ignored by
    // older clients.
    private readonly struct ServeOrigin
    {
        public readonly string Origin;
        public readonly string Lane;
        public ServeOrigin(string origin, string lane) { Origin = origin; Lane = lane; }
    }
    private static readonly AsyncLocal<ServeOrigin?> _serveOrigin = new();

    /// <summary>Tag every EmitJson frame on the calling async flow (and its child tasks) with an
    /// origin + lane for the serve/stdio NDJSON stream. Returns a scope that clears the tag on
    /// dispose. Used by the daemon runner so background goal output can be routed out of the main
    /// viewport client-side.</summary>
    public static IDisposable BeginServeOrigin(string origin, string lane)
    {
        var prev = _serveOrigin.Value;
        _serveOrigin.Value = new ServeOrigin(origin, lane);
        return new ServeOriginScope(prev);
    }
    private sealed class ServeOriginScope : IDisposable
    {
        private readonly ServeOrigin? _prev;
        private bool _done;
        public ServeOriginScope(ServeOrigin? prev) { _prev = prev; }
        public void Dispose() { if (_done) return; _done = true; _serveOrigin.Value = _prev; }
    }

    private static void EmitJson(string type, object? data = null)
    {
        // Outbound webhook fan-out taps the emit chokepoint so every structured event can reach an
        // external sink independent of the console transport (serve/stdio/acp/tui). No-op + zero
        // allocation when no outbound sinks are configured (WebhookSink.IsActive == false).
        if (WebhookSink.IsActive)
        {
            IReadOnlyDictionary<string, object?>? whFields =
                data as Dictionary<string, object?>
                ?? (data is not null ? new Dictionary<string, object?> { ["message"] = data } : null);
            WebhookSink.Notify(type, whFields);
        }

        // ACP transport owns stdout (pure JSON-RPC), so structured events are diverted to the
        // ACP sink instead of being written as an NDJSON line. The 'type' field is implicit in
        // the (type, fields) pair handed to the sink; only the extra fields are forwarded.
        if (AcpSink is { } sink)
        {
            var fields = new Dictionary<string, object?>();
            if (data is Dictionary<string, object?> sdict)
            {
                foreach (var kvp in sdict)
                    fields[kvp.Key] = kvp.Value;
            }
            else if (data is not null)
            {
                fields["message"] = data;
            }
            if (_serveOrigin.Value is { } so0)
            {
                fields["origin"] = so0.Origin;
                fields["lane"] = so0.Lane;
            }
            sink(type, fields);
            return;
        }

        // ACP transport is active but its sink is not yet installed (startup events before
        // AcpServer.RunAsync wires it up). stdout is reserved for ACP JSON-RPC, so these
        // pre-session NDJSON frames are dropped rather than written.
        if (AcpActive)
            return;

        var payload = new Dictionary<string, object?> { ["type"] = type };
        if (data is Dictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
                payload[kvp.Key] = kvp.Value;
        }
        else if (data is not null)
        {
            payload["message"] = data;
        }

        if (_serveOrigin.Value is { } so1)
        {
            payload["origin"] = so1.Origin;
            payload["lane"] = so1.Lane;
        }
        Console.WriteLine(JsonSerializer.Serialize(payload, _jsonOpts));
    }

    private static Dictionary<string, object?> D(params (string Key, object? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
            dict[key] = value;
        return dict;
    }

    public static string PromptColor => Theme.Active.Prompt;

    // Color roles now resolve from the ACTIVE theme (see Theme.cs) rather than hardcoded consts, so
    // /theme + the setup theme step recolor all chrome live. The default theme reproduces the exact
    // pre-theme palette, so unset/default is byte-identical to prior behaviour.
    private static class C
    {
        public static string Step => Theme.Active.Step;
        public static string Success => Theme.Active.Success;
        public static string Warning => Theme.Active.Warning;
        public static string Error => Theme.Active.Error;
        public static string Info => Theme.Active.Info;
        public static string Muted => Theme.Active.Muted;
        public static string Accent => Theme.Active.Accent;
        public static string Prompt => Theme.Active.Prompt;
        public static string Banner => Theme.Active.Banner;
        public static string Agent => Theme.Active.Agent;
    }

    private static void WithConsole(Action write, bool clearIndicator = true)
    {
        lock (ConsoleLock)
        {
            if (clearIndicator && !_isStreaming && _activeIndicator is not null)
                StopActiveIndicator_NoLock();

            write();
        }
    }

    private static void StopActiveIndicator_NoLock()
    {
        if (_activeIndicator is null) return;

        var ind = _activeIndicator;
        _activeIndicator = null;

        try
        {
            if (ind.HasRendered)
                ind.ClearNow_NoLock();
        }
        catch { /* ignore */ }

        ind.CancelNoWait();
    }

    public static string? ReadInput(CancellationToken ct = default)
    {
        // Live-region TUI owns the input box (pinned at the bottom). Only for real keyboard
        // input without a multi-line delimiter; stdio/serve and workflow input are unaffected.
        if (TuiActive && InputOverride == Console.In && !(UsingDelimiter && MultiLineDelimiter is not null))
        {
            return TuiReadLine();
        }

        if (InputOverride != Console.In)
        {
            var line = InputOverride.ReadLine();
            if (line == null)
            {
                //exhausted workflow return to keyboard input
                InputOverride = Console.In;
                return Console.ReadLine();
            }
            return line;
        }

        if (UsingDelimiter && MultiLineDelimiter is not null)
        {
            var sb = new StringBuilder();
            while (true)
            {
                var line = StdinCancelMonitor.Instance?.ReadLine(ct) ?? Console.ReadLine();
                if (line is null || line.Trim() == MultiLineDelimiter) break;
                sb.AppendLine(line);
            }
            return sb.ToString().Trim();
        }

        return StdinCancelMonitor.Instance?.ReadLine(ct) ?? Console.ReadLine();
    }

    public static string AskText(string question, string? defaultValue)
    {
        string response = Prompt(question, defaultValue);
        return string.IsNullOrWhiteSpace(response)
            ? "User provided no input."
            : $"User response: {response}";
    }

    public static string AskSelect(string question, string? options)
    {
        var choices = ParseOptions(options);
        if (choices.Count == 0)
            return "Error: 'select' type requires at least one option in the options parameter.";

        var c = MuxConsole.SelectChoice(question, choices);
        if (c.Cancelled) return "User cancelled the prompt without choosing (no option selected).";
        if (c.Custom) return $"User entered a custom response (outside the offered options): {c.Value}";
        return $"User selected: {c.Value}";
    }

    public static string AskMultiSelect(string question, string? options)
    {
        var choices = ParseOptions(options);
        if (choices.Count == 0)
            return "Error: 'multi_select' type requires at least one option in the options parameter.";

        var c = MuxConsole.MultiSelectChoice(question, choices);
        if (c.Cancelled) return "User cancelled the prompt without choosing (no option selected).";
        if (c.Custom) return $"User entered a custom response (outside the offered options): {c.Value}";
        return $"User selected: {c.Value}";
    }

    // ── M-F: bailout-aware interactive prompts ───────────────────────────────────
    // The base Confirm/Select/MultiSelect lock the user into the offered set. These *Choice
    // variants add two affordances so the user is never trapped: "enter custom" (type a free-text
    // value outside the option set) and "cancel" (back out entirely, returning a Cancelled result
    // the caller handles gracefully — no wasted extra model turn). The base methods are unchanged,
    // so every existing caller keeps its current behaviour; only callers that opt into the *Choice
    // variants (and the ask_user tool) gain the bailout. NOTE: Spectre's blocking selection prompt
    // does not expose a raw Esc key hook, so cancellation is delivered via an explicit "cancel"
    // affordance (labelled with Esc for discoverability) rather than a literal key intercept.
    public const string CustomAffordanceLabel = "\u270e enter custom\u2026";
    public const string CancelAffordanceLabel = "\u2717 cancel (Esc)";

    /// <summary>Outcome of a bailout-aware prompt. Exactly one of Cancelled / Custom is true, or
    /// both false for a normal in-set selection. <see cref="Value"/> holds the chosen option, the
    /// typed free text (Custom), or empty (Cancelled).</summary>
    public readonly record struct PromptChoice(bool Cancelled, bool Custom, string Value)
    {
        public static PromptChoice Cancel() => new(true, false, string.Empty);
        public static PromptChoice CustomText(string v) => new(false, true, v);
        public static PromptChoice Picked(string v) => new(false, false, v);
    }

    // Shared resolver for the scripted (StdioMode / InputOverride) path: a leading '=' means a
    // typed custom value (rest of line); "cancel"/"esc"/empty means cancel; a 1-based index picks
    // a choice; an out-of-range index equal to count+1 maps to the custom affordance (then cancel
    // if no follow-up text is available).
    private static PromptChoice ResolveScriptedBailout(string? raw, List<string> list)
    {
        var input = (raw ?? string.Empty).Trim();
        if (input.Length == 0) return PromptChoice.Cancel();
        if (input.StartsWith('=')) return PromptChoice.CustomText(input[1..].Trim());
        if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase)
            || input.Equals("esc", StringComparison.OrdinalIgnoreCase))
            return PromptChoice.Cancel();
        if (int.TryParse(input, out int idx) && idx >= 1 && idx <= list.Count)
            return PromptChoice.Picked(list[idx - 1]);
        // Anything else is treated as a literal typed custom value.
        return PromptChoice.CustomText(input);
    }

    /// <summary>Single-select with custom + cancel bailout. See <see cref="PromptChoice"/>.</summary>
    public static PromptChoice SelectChoice(string title, IEnumerable<string> choices)
    {
        var list = choices.ToList();
        lock (ConsoleLock) { StopActiveIndicator_NoLock(); }
        TuiSuspend();
        int _resTop = SafeCursorTop();

        if (StdioMode)
        {
            var emit = new List<string>(list) { CustomAffordanceLabel, CancelAffordanceLabel };
            EmitJson("select_request", D(("prompt", title), ("choices", emit), ("bailout", true)));
            return ResolveScriptedBailout(InputOverride.ReadLine(), list);
        }
        if (InputOverride != Console.In)
            return ResolveScriptedBailout(InputOverride.ReadLine(), list);

        var withBail = new List<string>(list) { CustomAffordanceLabel, CancelAffordanceLabel };
        var sel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"  [{C.Prompt}]{Esc(title)}[/]")
                .HighlightStyle(new Style(Color.White, decoration: Decoration.Bold))
                .UseConverter(Esc)
                .AddChoices(withBail));
        ErasePromptResidue(_resTop);

        if (sel == CancelAffordanceLabel) return PromptChoice.Cancel();
        if (sel == CustomAffordanceLabel)
        {
            var typed = Prompt($"{title} (custom)", null);
            return string.IsNullOrWhiteSpace(typed) ? PromptChoice.Cancel() : PromptChoice.CustomText(typed);
        }
        return PromptChoice.Picked(sel);
    }

    /// <summary>Confirm with cancel bailout: returns yes/no, or Cancelled when the user backs out.</summary>
    public static PromptChoice ConfirmChoice(string message, bool defaultValue = true)
    {
        lock (ConsoleLock) { StopActiveIndicator_NoLock(); }
        TuiSuspend();
        int _resTop = SafeCursorTop();

        var opts = new List<string> { "Yes", "No" };
        if (StdioMode)
        {
            EmitJson("confirm_request", D(("prompt", message), ("default", defaultValue), ("bailout", true)));
            var input = (InputOverride.ReadLine() ?? string.Empty).Trim();
            if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase)
                || input.Equals("esc", StringComparison.OrdinalIgnoreCase))
                return PromptChoice.Cancel();
            if (input.Length == 0) return PromptChoice.Picked(defaultValue ? "yes" : "no");
            return PromptChoice.Picked(input.StartsWith("y", StringComparison.OrdinalIgnoreCase) ? "yes" : "no");
        }
        if (InputOverride != Console.In)
        {
            var input = (InputOverride.ReadLine() ?? string.Empty).Trim();
            if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase)
                || input.Equals("esc", StringComparison.OrdinalIgnoreCase))
                return PromptChoice.Cancel();
            if (input.Length == 0) return PromptChoice.Picked(defaultValue ? "yes" : "no");
            return PromptChoice.Picked(input.StartsWith("y", StringComparison.OrdinalIgnoreCase) ? "yes" : "no");
        }

        var sel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"  [{C.Prompt}]{Esc(message)}[/]")
                .HighlightStyle(new Style(Color.White, decoration: Decoration.Bold))
                .UseConverter(Esc)
                .AddChoices(new List<string>(opts) { CancelAffordanceLabel }));
        ErasePromptResidue(_resTop);
        if (sel == CancelAffordanceLabel) return PromptChoice.Cancel();
        return PromptChoice.Picked(sel.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? "yes" : "no");
    }

    /// <summary>Multi-select with custom + cancel bailout. On a normal pick <see cref="PromptChoice.Value"/>
    /// is the selected options joined by ", "; Custom carries typed free text; Cancelled when backed out.</summary>
    public static PromptChoice MultiSelectChoice(string title, IEnumerable<string> choices)
    {
        var list = choices.ToList();
        lock (ConsoleLock) { StopActiveIndicator_NoLock(); }
        TuiSuspend();
        int _resTop = SafeCursorTop();

        if (StdioMode)
        {
            var emit = new List<string>(list) { CustomAffordanceLabel, CancelAffordanceLabel };
            EmitJson("multiselect_request", D(("prompt", title), ("choices", emit), ("bailout", true)));
            return ResolveScriptedMulti(InputOverride.ReadLine(), list);
        }
        if (InputOverride != Console.In)
            return ResolveScriptedMulti(InputOverride.ReadLine(), list);

        // Spectre multi-select: add the custom/cancel affordances as selectable rows. If the user
        // selects cancel, treat the whole prompt as cancelled; if they select custom, follow up with
        // a free-text line.
        var withBail = new List<string>(list) { CustomAffordanceLabel, CancelAffordanceLabel };
        var picked = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title($"  [{C.Prompt}]{Esc(title)}[/]")
                .HighlightStyle(new Style(Color.White, decoration: Decoration.Bold))
                .UseConverter(Esc)
                .NotRequired()
                .AddChoices(withBail))
            .ToList();
        ErasePromptResidue(_resTop);

        if (picked.Contains(CancelAffordanceLabel) || picked.Count == 0)
            return PromptChoice.Cancel();
        if (picked.Contains(CustomAffordanceLabel))
        {
            var typed = Prompt($"{title} (custom)", null);
            return string.IsNullOrWhiteSpace(typed) ? PromptChoice.Cancel() : PromptChoice.CustomText(typed);
        }
        return PromptChoice.Picked(string.Join(", ", picked));
    }

    private static PromptChoice ResolveScriptedMulti(string? raw, List<string> list)
    {
        var input = (raw ?? string.Empty).Trim();
        if (input.Length == 0) return PromptChoice.Cancel();
        if (input.StartsWith('=')) return PromptChoice.CustomText(input[1..].Trim());
        if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase)
            || input.Equals("esc", StringComparison.OrdinalIgnoreCase))
            return PromptChoice.Cancel();
        var selected = new List<string>();
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= list.Count)
                selected.Add(list[idx - 1]);
        return selected.Count > 0 ? PromptChoice.Picked(string.Join(", ", selected)) : PromptChoice.Cancel();
    }

    public static List<string> ParseOptions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Renders a splash screen: block-art title with ASCII mascot, version, and repo link.
    /// No external dependencies or image files required.
    /// </summary>
    public static void WriteSplashScreen(string version, string debugTag = "")
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("splash", string.IsNullOrEmpty(debugTag) ? D(("version", version)) : D(("version", version), ("debug", debugTag)));
                return;
            }

            AnsiConsole.WriteLine();

            // One random curated line per launch (quote / fact / tip / nudge / tagline). Both the
            // narrow-panel and wide two-column layouts render the SAME pick, computed once here.
            var (splashLabel, splashText) = SplashMessages.Pick();
            string splashLine = string.IsNullOrEmpty(splashLabel)
                ? $"[{C.Prompt}]{Esc(splashText)}[/]"
                : $"[{C.Prompt}]{Esc(splashText)}[/]  [{C.Muted}]{Esc(splashLabel)}[/]";

            if (AnsiConsole.Profile.Width < 56)
            {
                AnsiConsole.Write(new Rule($"[bold {C.Banner}]MUX-SWARM[/]  [{C.Muted}]v{Esc(version)}[/]{(string.IsNullOrEmpty(debugTag) ? "" : $"  [{C.Warning}]{Esc("[debug: " + debugTag + "]")}[/]")}")
                    .RuleStyle(new Style(Color.Grey35))
                    .LeftJustified());
            }
            else if (AnsiConsole.Profile.Width < 180)
            {
                string[] mux =
                {
                    "███╗   ███╗██╗   ██╗██╗  ██╗",
                    "████╗ ████║██║   ██║╚██╗██╔╝",
                    "██╔████╔██║██║   ██║ ╚███╔╝ ",
                    "██║╚██╔╝██║██║   ██║ ██╔██╗ ",
                    "██║ ╚═╝ ██║╚██████╔╝██╔╝ ██╗",
                    "╚═╝     ╚═╝ ╚═════╝ ╚═╝  ╚═╝",
                };

                string[] bot =
                {
                    @"   █     █ ",
                    @" ▄█████████▄ ",
                    @" █   ◠ ◠   █ ",
                    @" █    ◡    █ ",
                    @" █▄▄▄▄▄▄▄▄▄█ ",
                    @"  ▀▀▀▀▀▀▀▀▀  ",
                };

                int muxW = mux.Max(l => l.Length);
                var sb = new StringBuilder();

                for (int i = 0; i < mux.Length; i++)
                {
                    var muxLine = mux[i].PadRight(muxW + 4);
                    var botLine = i < bot.Length ? bot[i] : "";
                    sb.AppendLine($"[{C.Banner}]{Esc(muxLine)}[/][{C.Success}]{Esc(botLine)}[/]");
                }

                const string swarmArt =
                    "███████╗██╗    ██╗ █████╗ ██████╗ ███╗   ███╗\n" +
                    "██╔════╝██║    ██║██╔══██╗██╔══██╗████╗ ████║\n" +
                    "███████╗██║ █╗ ██║███████║██████╔╝██╔████╔██║\n" +
                    "╚════██║██║███╗██║██╔══██║██╔══██╗██║╚██╔╝██║\n" +
                    "███████║╚███╔███╔╝██║  ██║██║  ██║██║ ╚═╝ ██║\n" +
                    "╚══════╝ ╚══╝╚══╝ ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝     ╚═╝";

                sb.AppendLine($"[{C.Banner}]{Esc(swarmArt)}[/]");
                sb.AppendLine();
                sb.AppendLine($"[{C.Step}]v{Esc(version)}[/]{(string.IsNullOrEmpty(debugTag) ? "" : $"  [{C.Warning}]{Esc("[debug: " + debugTag + "]")}[/]")}  [{C.Muted}]·[/]  {splashLine}");
                sb.Append($"[{C.Muted}][link=https://github.com/jnotsknab/mux-swarm]Check Out The Repo Here![/][/]  [{C.Muted}]·[/]  [{C.Muted}]Type /help for commands[/]");

                var panel = new Panel(sb.ToString())
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(new Style(Color.Grey35))
                    .Padding(1, 1)
                    .Expand();

                AnsiConsole.Write(panel);
            }
            else
            {
                string[] mux =
                {
                    "███╗   ███╗██╗   ██╗██╗  ██╗",
                    "████╗ ████║██║   ██║╚██╗██╔╝",
                    "██╔████╔██║██║   ██║ ╚███╔╝ ",
                    "██║╚██╔╝██║██║   ██║ ██╔██╗ ",
                    "██║ ╚═╝ ██║╚██████╔╝██╔╝ ██╗",
                    "╚═╝     ╚═╝ ╚═════╝ ╚═╝  ╚═╝",
                };

                // swap in whatever mascot you landed on
                string[] bot =
                {
                    @"   █     █ ",
                    @" ▄█████████▄ ",
                    @" █   ◠ ◠   █ ",
                    @" █    ◡    █ ",
                    @" █▄▄▄▄▄▄▄▄▄█ ",
                    @"  ▀▀▀▀▀▀▀▀▀  ",
                };

                int muxW = mux.Max(l => l.Length);
                var left = new StringBuilder();

                for (int i = 0; i < mux.Length; i++)
                {
                    var muxLine = mux[i].PadRight(muxW + 4);
                    var botLine = i < bot.Length ? bot[i] : "";
                    left.AppendLine($"[{C.Banner}]{Esc(muxLine)}[/][{C.Success}]{Esc(botLine)}[/]");
                }

                const string swarmArt =
                    "███████╗██╗    ██╗ █████╗ ██████╗ ███╗   ███╗\n" +
                    "██╔════╝██║    ██║██╔══██╗██╔══██╗████╗ ████║\n" +
                    "███████╗██║ █╗ ██║███████║██████╔╝██╔████╔██║\n" +
                    "╚════██║██║███╗██║██╔══██║██╔══██╗██║╚██╔╝██║\n" +
                    "███████║╚███╔███╔╝██║  ██║██║  ██║██║ ╚═╝ ██║\n" +
                    "╚══════╝ ╚══╝╚══╝ ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝     ╚═╝";

                left.AppendLine($"[{C.Banner}]{Esc(swarmArt)}[/]");
                left.AppendLine();
                left.AppendLine($"[{C.Step}]v{Esc(version)}[/]{(string.IsNullOrEmpty(debugTag) ? "" : $"  [{C.Warning}]{Esc("[debug: " + debugTag + "]")}[/]")}  [{C.Muted}]·[/]  {splashLine}");
                left.Append($"[{C.Muted}][link=https://github.com/jnotsknab/mux-swarm]Check Out The Repo Here![/][/]");

                var right = new StringBuilder();
                right.AppendLine($"[{C.Step}]Getting Started[/]");
                right.AppendLine($"[{C.Muted}]Pick a mode to begin, then enter your task[/]");
                right.AppendLine($"[{C.Muted}]────────────────────────────────────────────[/]");
                right.AppendLine();
                right.AppendLine($"  [{C.Prompt}]/swarm[/]       [{C.Muted}]Multi-agent orchestrated loop[/]");
                right.AppendLine($"  [{C.Prompt}]/pswarm[/]      [{C.Muted}]Parallel concurrent dispatch[/]");
                right.AppendLine($"  [{C.Prompt}]/agent[/]       [{C.Muted}]Single-agent conversation[/]");
                right.AppendLine($"  [{C.Prompt}]/stateless[/]   [{C.Muted}]One-off stateless task[/]");
                right.AppendLine($"  [{C.Prompt}]/onboard[/]     [{C.Muted}]Set up your operator profile[/]");
                right.AppendLine($"  [{C.Prompt}]/workflow[/]    [{C.Muted}]Run a workflow file[/]");
                right.AppendLine($"  [{C.Prompt}]/help[/]        [{C.Muted}]Full command reference[/]");
                right.AppendLine();
                right.AppendLine($"[{C.Muted}]────────────────────────────────────────────[/]");
                right.AppendLine($"[{C.Step}]Quick Tips[/]");
                right.AppendLine($"  [{C.Muted}]/qc or /qm to exit an active session[/]");
                right.Append($"  [{C.Muted}]/status to view current config[/]");

                var rightFar = new StringBuilder();
                rightFar.AppendLine($"[{C.Step}]Recent Sessions[/]");
                rightFar.AppendLine($"[{C.Muted}]────────────────────────────────[/]");

                var sessionsPath = PlatformContext.SessionsDirectory;
                if (Directory.Exists(sessionsPath))
                {
                    var recentDirs = Directory.GetDirectories(sessionsPath)
                        .OrderByDescending(d => d)
                        .Take(5)
                        .ToList();

                    if (recentDirs.Count > 0)
                    {
                        foreach (var dir in recentDirs)
                        {
                            string ts = Path.GetFileName(dir);
                            var files = Directory.GetFiles(dir, "*_session.json", SearchOption.AllDirectories);
                            string type = files.Length > 1 ? "swarm" : "agent";

                            string preview = "No preview";
                            if (files.Length > 0)
                            {
                                try
                                {
                                    var raw = Common.GetFirstUserMessage(files[0]);
                                    if (!string.IsNullOrWhiteSpace(raw)
                                        && !raw.StartsWith("[SYSTEM", StringComparison.OrdinalIgnoreCase)
                                        && !raw.StartsWith("[CONTEXT SUMMARY", StringComparison.OrdinalIgnoreCase)
                                        && !raw.StartsWith("## User Context", StringComparison.OrdinalIgnoreCase)
                                        && !raw.Contains("Context restoration", StringComparison.OrdinalIgnoreCase)
                                        && !raw.Contains("Filesystem Write Rules", StringComparison.OrdinalIgnoreCase))
                                    {
                                        preview = raw.Length > 40 ? raw[..40] + "..." : raw;
                                    }
                                }
                                catch { /* ignore */ }
                            }

                            rightFar.AppendLine($"  [{C.Prompt}]{Esc(ts)}[/]");
                            rightFar.AppendLine($"    [{C.Muted}]{type} · {Esc(preview)}[/]");
                        }
                    }
                    else
                    {
                        rightFar.AppendLine($"  [{C.Muted}]No sessions yet[/]");
                    }
                }
                else
                {
                    rightFar.AppendLine($"  [{C.Muted}]No sessions yet[/]");
                }

                rightFar.AppendLine();
                rightFar.AppendLine($"[{C.Muted}]────────────────────────────────[/]");
                rightFar.AppendLine($"  [{C.Muted}]/resume to continue a session[/]");
                rightFar.Append($"  [{C.Muted}]/sessions to view session info[/]");

                var grid = new Grid();
                grid.AddColumn(new GridColumn().NoWrap().PadRight(4));
                grid.AddColumn(new GridColumn().Width(1).NoWrap());
                grid.AddColumn(new GridColumn().PadLeft(3).PadRight(3));
                grid.AddColumn(new GridColumn().Width(1).NoWrap());
                grid.AddColumn(new GridColumn().PadLeft(3));
                grid.AddRow(
                    new Markup(left.ToString()),
                    new Markup($"[{C.Muted}]│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│[/]"),
                    new Markup(right.ToString()),
                    new Markup($"[{C.Muted}]│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│[/]"),
                    new Markup(rightFar.ToString())
                );

                var panel = new Panel(grid)
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(new Style(Color.Grey35))
                    .Padding(1, 1)
                    .Expand();

                AnsiConsole.Write(panel);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule().RuleStyle(new Style(Color.Grey23)));
            AnsiConsole.WriteLine();
        });
    }


    public static void WriteBanner(string title = "MUX-SWARM SETUP")
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("banner", D(("title", title)));
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold {C.Banner}]{Esc(title)}[/]")
                .RuleStyle(new Style(Color.Grey))
                .LeftJustified());
            AnsiConsole.WriteLine();
        });
    }

    /// <summary>
    /// Creates and starts a thinking indicator for the given agent.
    /// In StdioMode, emits thinking_start/thinking_update/thinking_end NDJSON events
    /// so integrations can render loading/spinner UI.
    /// </summary>
    public static ThinkingIndicator BeginThinking(string agentName)
    {
        // Captured (collapsed) sub-agent: do NOT start a competing per-agent spinner on the
        // shared live line - with parallel delegation, N of them flicker against each other.
        // Return an inert indicator whose status updates feed the consolidated activity panel.
        if (Capturing)
            return new ThinkingIndicator(
                renderRaw: _ => { }, clearLine: _ => { }, consoleLock: ConsoleLock,
                onStatusUpdate: status => SetCapturedLiveStatus(status));

        if (StdioMode)
        {
            EmitJson("thinking_start", D(("agent", agentName)));
            return new ThinkingIndicator(
                renderRaw: _ => { },
                clearLine: _ => { },
                consoleLock: ConsoleLock,
                onStatusUpdate: status => EmitJson("thinking_update", D(("agent", agentName), ("status", status))),
                onDispose: () => EmitJson("thinking_end", D(("agent", agentName)))
            );
        }

        lock (ConsoleLock)
        {
            if (_isStreaming)
                return new ThinkingIndicator(_ => { }, _ => { }, ConsoleLock);

            // The live-region driver pins its own footer/input; route the spinner into the
            // driver's live "thinking" line (animated) instead of the inline \r renderer,
            // which would fight the region. The indicator's status updates flow through the
            // onStatusUpdate callback below.
            if (ViaDriver)
            {
                var tuiInd = new ThinkingIndicator(
                    // The indicator's animation loop composes a spinner+status line every
                    // ~80ms and hands it to renderRaw; forward it to the driver's live
                    // thinking line (stripping the leading CR the inline renderer used).
                    renderRaw: raw => TuiSetThinking(raw.Replace("\r", "").TrimEnd()),
                    clearLine: _ => TuiSetThinking(null),
                    consoleLock: ConsoleLock,
                    onDispose: () => TuiSetThinking(null));
                _activeIndicator = tuiInd;
                tuiInd.Start(agentName);
                return tuiInd;
            }

            StopActiveIndicator_NoLock();
        }

        var indicator = new ThinkingIndicator(
            renderRaw: raw => Console.Write(raw),
            clearLine: maxLen => Console.Write("\r" + new string(' ', maxLen) + "\r"),
            consoleLock: ConsoleLock
        );

        lock (ConsoleLock)
        {
            _activeIndicator = indicator;
        }

        indicator.Start(agentName);
        return indicator;
    }

    public static void EndThinking(ThinkingIndicator indicator)
    {
        lock (ConsoleLock)
        {
            try
            {
                if (indicator.HasRendered)
                    indicator.ClearNow_NoLock();
            }
            catch { /* ignore */ }

            indicator.CancelNoWait();

            if (ReferenceEquals(_activeIndicator, indicator))
                _activeIndicator = null;
        }
    }

    public static void BeginStreaming(string? agentName = null)
    {
        if (Capturing) return;   // captured sub-agent: no live stream region (output is buffered)
        lock (ConsoleLock)
        {
            _isStreaming = true;
            StopActiveIndicator_NoLock();
            TuiBeginStream();
        }
    }

    public static void EndStreaming(string? agentName = null)
    {
        if (Capturing) return;   // captured sub-agent: nothing was streamed to end
        lock (ConsoleLock)
        {
            if (!_isStreaming)
                return;

            _isStreaming = false;

            if (StdioMode)
            {
                // agent key added only when non-null so single-agent stream_end frames
                // stay byte-identical (dictionary entries are not covered by WhenWritingNull).
                if (agentName is null)
                    EmitJson("stream_end");
                else
                    EmitJson("stream_end", D(("agent", agentName)));
                return;
            }

            if (ViaDriver) { TuiEndStream(); return; }

            Console.WriteLine();
            try { Console.Out.Flush(); } catch { /* ignore */ }
        }
    }

    public static ThinkingIndicator ResumeThinking(string agentName)
    {
        if (Capturing) return BeginThinking(agentName);
        lock (ConsoleLock)
        {
            if (_isStreaming)
            {
                _isStreaming = false;

                if (StdioMode)
                    EmitJson("stream_end");
                else if (ViaDriver)
                    TuiEndStream();
                else
                {
                    Console.WriteLine();
                    try { Console.Out.Flush(); } catch { /* ignore */ }
                }
            }
        }

        return BeginThinking(agentName);
    }

    /// <summary>
    /// Client-side gate for streamed reasoning text, seeded from config.json <c>showReasoning</c>.
    /// When "none", muted (reasoning) chunks are dropped in interactive renderers so reasoning
    /// never reaches the screen or the collapsed sub-agent transcript. stdio/NDJSON/WS output is
    /// never gated (protocol parity). Set at startup and by <c>/showreasoning</c>.
    /// </summary>
    public static string ShowReasoning { get; set; } = "summary";

    public static void WriteStream(string text, bool muted = false, string? agentName = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Collapsed sub-agent: buffer the chunk for the expandable transcript instead of
        // committing it inline. The thinking spinner keeps animating (live progress).
        if (Capturing) { CaptureAppend(text); return; }

        // Client-side reasoning gate: when showReasoning == "none", drop muted (reasoning)
        // chunks for interactive renderers so streaming reasoning text never reaches the TUI,
        // the classic console, or the captured sub-agent transcript. stdio is never gated so the
        // NDJSON/WebSocket protocol is unchanged. (The Capturing buffer above already returned
        // before this, so a suppressed reasoning chunk is also kept out of the transcript.)
        if (muted && !StdioMode && string.Equals(ShowReasoning, "none", StringComparison.OrdinalIgnoreCase))
            return;

        WithConsole(() =>
        {
            if (StdioMode)
            {
                // agent key is added only when non-null so single-agent stream frames are
                // byte-identical; parallel callers pass the specialist name so the web app
                // can demultiplex concurrent sub-agent streams. (Dictionary entries are not
                // covered by WhenWritingNull, so the key must be omitted explicitly.)
                // The reasoning flag is attached ONLY under the ACP adapter so it can split
                // agent_message_chunk vs agent_thought_chunk; off --acp the NDJSON contract is
                // unchanged (AcpActive is always false there).
                var streamFields = agentName is null
                    ? D(("text", text))
                    : D(("text", text), ("agent", agentName));
                // Mark reasoning (muted) chunks on the NDJSON/WS stream frame so the web app
                // can keep tool-call groups intact across think->call->think bursts (only
                // answer prose breaks a group). ACP already relied on this to split
                // message vs thought chunks; broadening to all stdio is additive
                // (absent => legacy; unknown-field-ignored by older web clients).
                if (muted) streamFields["reasoning"] = true;
                EmitJson("stream", streamFields);
            }
            else if (ViaDriver)
                // Live-region TUI: feed the chunk to the driver, which commits complete
                // lines into scrollback and shows the partial tail live above the footer. The
                // muted flag marks reasoning content so the driver renders it grey+italic,
                // distinct from the final answer (parity with the classic renderer below).
                TuiStreamChunk(text, reasoning: muted);
            else if (muted)
                AnsiConsole.Markup(muted
                    ? $"[grey italic]{Esc(text)}[/]"
                    : Esc(text));
            else
                AnsiConsole.Markup(Esc(text));
        }, clearIndicator: false);
    }

    public static void WriteStep(int number, string title)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("step", D(("number", number), ("title", title)));
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[{C.Step}]Step {number}[/]  [{C.Prompt}]{Esc(title)}[/]")
                .RuleStyle(new Style(Color.Grey35))
                .LeftJustified());
            AnsiConsole.WriteLine();
        });
    }

    public static void WriteRule(string? label = null)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("rule", label != null ? D(("label", label)) : null);
                return;
            }
            if (Capturing) return;   // collapsed sub-agent: no inline rules

            // Under the driver, a raw Spectre rule paints below the footer and desyncs the
            // live region. Commit a markup rule line through the driver instead.
            if (ViaDriver)
            {
                string ruleLine = label != null
                    ? $"  [{C.Muted}]── {Esc(label)} ──[/]"
                    : $"[{C.Muted}]{new string('\u2500', Math.Max(8, Console.WindowWidth))}[/]";
                if (TuiCommit(ruleLine)) return;
            }

            var rule = label != null
                ? new Rule($"[{C.Muted}]{Esc(label)}[/]").RuleStyle(new Style(Color.Grey23))
                : new Rule().RuleStyle(new Style(Color.Grey23));

            AnsiConsole.Write(rule.LeftJustified());
        });
    }

    /// <summary>
    /// When the live-region driver is active, commit a single markup line into native
    /// scrollback (above the pinned footer) and return true; otherwise return false so the
    /// caller falls through to its normal AnsiConsole rendering. Keeps simple line writers
    /// from painting underneath the live region and corrupting it.
    /// </summary>
    private static bool TuiCommit(string markupLine)
    {
        if (!ViaDriver) return false;
        CommitToDriver(markupLine);
        return true;
    }

    /// <summary>
    /// Commit a titled block (header + indented detail lines) into scrollback via the live
    /// region instead of drawing a bordered Spectre panel/table. Borderless panels never get
    /// clipped at the footer and keep the cursor where it belongs. Returns false when the
    /// driver is not active so the caller can fall back to its Spectre rendering. Each detail
    /// line is already Spectre markup (caller escapes content). Claude-Code /context feel:
    ///   header
    ///     ⎿ line
    ///     ⎿ line
    /// </summary>
    private static bool TuiCommitBlock(string headerMarkup, IEnumerable<string> detailMarkupLines)
    {
        if (!ViaDriver) return false;
        // A single continuous accent rail runs down the whole block - the rail IS the grouping,
        // so no per-row bullet is needed. Header sits on the rail; a rail-only spacer separates it
        // from the body; each detail row is the rail + one uniform space + the (already-trimmed)
        // content. Callers must NOT pre-indent or bracket their rows; the rail provides structure.
        string rail = $"  [{C.Step}]\u2502[/]";
        var lines = new List<string> { "", $"{rail} {headerMarkup}", rail };
        foreach (var d in detailMarkupLines)
            lines.Add($"{rail} {d}");
        lines.Add("");
        CommitLinesToDriver(lines);
        return true;
    }

    /// <summary>
    /// When true (default) the periodic "[AGENT SESSION] Saved to ..." confirmation is
    /// suppressed under the live TUI - it is noisy and the docked footer already shows the
    /// active session id. Off the TUI it still prints (muted). Set false to restore it.
    /// </summary>
    public static bool QuietSessionSaves { get; set; } = true;

    /// <summary>Session-save confirmation: muted, and suppressed entirely under the TUI when
    /// <see cref="QuietSessionSaves"/> is set (the default).</summary>
    public static void WriteSessionSaved(string message)
    {
        if (QuietSessionSaves && ViaDriver) return;   // footer carries the session id instead
        WriteMuted(message);
    }

    public static void WriteSuccess(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("success", message); return; }
            if (TuiCommit($"  [{C.Success}]✓[/] [{C.Prompt}]{Esc(message)}[/]")) return;
            AnsiConsole.MarkupLine($"  [{C.Success}]✓[/] [{C.Prompt}]{Esc(message)}[/]");
        });
    }

    public static void WriteWarning(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("warning", message); return; }
            if (TuiCommit($"  [{C.Warning}]![/] [{C.Warning}]{Esc(message)}[/]")) return;
            AnsiConsole.MarkupLine($"  [{C.Warning}]![/] [{C.Warning}]{Esc(message)}[/]");
        });
    }

    public static void WriteError(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("error", message); return; }
            if (TuiCommit($"  [{C.Error}]x[/] [{C.Error}]{Esc(message)}[/]")) return;
            AnsiConsole.MarkupLine($"  [{C.Error}]x[/] [{C.Error}]{Esc(message)}[/]");
        });
    }

    public static void WriteInfo(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("info", message); return; }
            if (TuiCommit($"  [{C.Info}]{Esc(message)}[/]")) return;
            AnsiConsole.MarkupLine($"  [{C.Info}]{Esc(message)}[/]");
        });
    }

    /// <summary>
    /// Structured signal for the size-tiered delegation engine: which posture a sub-agent result
    /// took (inline | summary | pointer | pointer-stub | summary-fallback). Surfaced to the web app
    /// / stdio via the JSON event stream; no-op visual side effect (the muted text line is written
    /// separately by the caller).
    /// </summary>
    public static void EmitDelegationCompacted(string agent, int rawLen, string posture, string? handle)
    {
        // Structured event for the web app / stdio JSON stream ONLY. EmitJson writes a raw NDJSON
        // line to stdout (or the ACP sink); in interactive TUI/console mode that line would leak
        // into the rendered transcript (the "{\"type\":\"delegation_compacted\",...}" artifact).
        // Gate it like every other EmitJson caller: emit in stdio/serve or ACP transport, never in
        // the interactive console. The muted human-readable line is written separately by the caller.
        if (!StdioMode && AcpSink is null && !AcpActive) return;
        EmitJson("delegation_compacted", D(("agent", agent), ("rawLen", rawLen), ("posture", posture), ("handle", handle)));
    }

    /// <summary>
    /// Surface a fired lifecycle hook as a structured <c>hook_fired</c> event on the JSON stream
    /// (serve NDJSON / ACP) and to outbound webhook sinks. Prior to this, HookWorker emitted nothing
    /// structured - hook dispatch was invisible to /ws listeners and outbound sinks. Additive.
    /// Emitted whenever a transport or an outbound sink can consume it; otherwise a no-op (no console
    /// artifact in interactive TUI mode).
    /// </summary>
    public static void EmitHookFired(string hookId, string eventType, string? agent, string? tool)
    {
        if (!StdioMode && AcpSink is null && !AcpActive && !WebhookSink.IsActive) return;
        EmitJson("hook_fired", D(("hookId", hookId), ("event", eventType), ("agent", agent), ("tool", tool)));
    }

    public static void WriteMuted(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("debug", message); return; }
            if (TuiCommit($"  [{C.Muted}]{Esc(message)}[/]")) return;
            AnsiConsole.MarkupLine($"  [{C.Muted}]{Esc(message)}[/]");
        });
    }

    public static void WriteBody(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("body", message); return; }
            if (TuiCommit($"  [{C.Prompt}]{Esc(message)}[/]")) return;
            AnsiConsole.MarkupLine($"  [{C.Prompt}]{Esc(message)}[/]");
        });
    }

    public static void WriteMarkup(string spectreMarkup, string? stdioFallback = null)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("markup", stdioFallback ?? StripMarkup(spectreMarkup)); return; }
            if (TuiCommit(spectreMarkup)) return;
            AnsiConsole.MarkupLine(spectreMarkup);
        });
    }

    public static void WriteInline(string spectreMarkup, string? stdioFallback = null)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("prompt", stdioFallback ?? StripMarkup(spectreMarkup)); return; }
            AnsiConsole.Markup(spectreMarkup);
        });
    }

    public static void WriteLine()
    {
        WithConsole(() =>
        {
            if (StdioMode) return;
            if (Capturing) return;   // collapsed sub-agent: no inline blank lines
            // Under the live-region driver, raw AnsiConsole writes land BELOW the pinned
            // footer and desync the painted-row count, stranding frozen copies of the
            // footer/chip in scrollback. Commit a blank line through the driver instead.
            if (TuiCommit("")) return;
            AnsiConsole.WriteLine();
        });
    }

    /// <summary>Safe cursor-row read (returns -1 when the console has no usable position).</summary>
    private static int SafeCursorTop()
    {
        try { return Console.CursorTop; } catch { return -1; }
    }

    /// <summary>
    /// Erase the residue a blocking Spectre prompt leaves in scrollback - the answered
    /// "question (current) answer" line - so only the caller's own confirmation (e.g. the
    /// "\u2713 ... saved" line) remains. Live TUI only: in the classic renderer the prompt
    /// history is intentionally kept. Best-effort; all cursor ops are guarded. <paramref name="fromTop"/>
    /// is the row captured right after the footer was suspended (i.e. the row the prompt starts on).
    /// </summary>
    private static void ErasePromptResidue(int fromTop)
    {
        if (StdioMode || !ViaDriver || fromTop < 0) return;
        try
        {
            if (Console.CursorTop < fromTop) return;
            Console.SetCursorPosition(0, fromTop);
            Console.Write("\x1b[J");   // erase from cursor to end of screen
        }
        catch { /* non-positionable console: leave residue rather than risk corruption */ }
    }

    public static string Prompt(string message, string? defaultValue = null, bool secret = false)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        // Clear the docked TUI footer band before a blocking interactive prompt; otherwise the
        // pinned footer is left painted and Spectre's prompt scrolls it up into scrollback,
        // leaving a stranded/duplicate badge row. The next status update repaints it cleanly.
        TuiSuspend();
        int _resTop = SafeCursorTop();

        if (StdioMode)
        {
            EmitJson("input_request", D(("prompt", message), ("default", defaultValue)));
            var input = InputOverride.ReadLine()?.Trim() ?? string.Empty;
            return string.IsNullOrEmpty(input) && defaultValue != null ? defaultValue : input;
        }

        if (InputOverride != Console.In)
        {
            string input = InputOverride.ReadLine()?.Trim() ?? string.Empty;
            return string.IsNullOrEmpty(input) && defaultValue != null ? defaultValue : input;
        }

        if (UsingDelimiter && MultiLineDelimiter is not null)
        {
            if (StdioMode)
                EmitJson("input_request", D(("prompt", message), ("default", defaultValue), ("delimiter", MultiLineDelimiter)));

            var sb = new StringBuilder();
            var reader = StdioMode ? InputOverride : Console.In;
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim() == MultiLineDelimiter) break;
                sb.AppendLine(line);
            }
            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) && defaultValue != null ? defaultValue : result;
        }

        var prompt = new TextPrompt<string>($"  [{C.Prompt}]{Esc(message)}[/]")
            .PromptStyle(new Style(Color.White));

        if (secret) prompt.Secret();

        if (defaultValue != null)
            prompt.DefaultValue(defaultValue).DefaultValueStyle(new Style(Color.Grey));
        else
            prompt.AllowEmpty();

        var _ans = AnsiConsole.Prompt(prompt).Trim();
        ErasePromptResidue(_resTop);
        return _ans;
    }

    public static string PromptSecret(string message)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        // Clear the docked TUI footer band before a blocking interactive prompt; otherwise the
        // pinned footer is left painted and Spectre's prompt scrolls it up into scrollback,
        // leaving a stranded/duplicate badge row. The next status update repaints it cleanly.
        TuiSuspend();

        if (StdioMode)
        {
            EmitJson("input_request", D(("prompt", message), ("secret", true)));
            return InputOverride.ReadLine()?.Trim() ?? string.Empty;
        }

        return AnsiConsole.Prompt(
            new TextPrompt<string>($"  [{C.Prompt}]{Esc(message)}[/]")
                .PromptStyle(new Style(Color.White))
                .Secret());
    }

    public static bool Confirm(string message, bool defaultValue = true)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        // Clear the docked TUI footer band before a blocking interactive prompt; otherwise the
        // pinned footer is left painted and Spectre's prompt scrolls it up into scrollback,
        // leaving a stranded/duplicate badge row. The next status update repaints it cleanly.
        TuiSuspend();
        int _resTop = SafeCursorTop();

        if (StdioMode)
        {
            EmitJson("confirm_request", D(("prompt", message), ("default", defaultValue)));
            var input = InputOverride.ReadLine()?.Trim().ToLowerInvariant() ?? string.Empty;
            return string.IsNullOrEmpty(input) ? defaultValue : input.StartsWith('y');
        }

        var _c = AnsiConsole.Confirm($"  [{C.Prompt}]{Esc(message)}[/]", defaultValue);
        ErasePromptResidue(_resTop);
        return _c;
    }

    public static string Select(string title, IEnumerable<string> choices)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        // Clear the docked TUI footer band before a blocking interactive prompt; otherwise the
        // pinned footer is left painted and Spectre's prompt scrolls it up into scrollback,
        // leaving a stranded/duplicate badge row. The next status update repaints it cleanly.
        TuiSuspend();
        int _resTop = SafeCursorTop();

        if (StdioMode)
        {
            var list = choices.ToList();
            EmitJson("select_request", D(("prompt", title), ("choices", list)));

            var input = InputOverride.ReadLine()?.Trim() ?? "1";
            if (int.TryParse(input, out int idx) && idx >= 1 && idx <= list.Count)
                return list[idx - 1];
            return list[0];
        }

        var _sel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"  [{C.Prompt}]{Esc(title)}[/]")
                .HighlightStyle(new Style(Color.White, decoration: Decoration.Bold))
                .UseConverter(Esc)
                .AddChoices(choices));
        ErasePromptResidue(_resTop);
        return _sel;
    }

    public static List<string> MultiSelect(string title, IEnumerable<string> choices)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        // Clear the docked TUI footer band before a blocking interactive prompt; otherwise the
        // pinned footer is left painted and Spectre's prompt scrolls it up into scrollback,
        // leaving a stranded/duplicate badge row. The next status update repaints it cleanly.
        TuiSuspend();
        int _resTop = SafeCursorTop();

        if (StdioMode)
        {
            var list = choices.ToList();
            EmitJson("multiselect_request", D(("prompt", title), ("choices", list)));

            var input = InputOverride.ReadLine()?.Trim() ?? "";
            var selected = new List<string>();
            foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= list.Count)
                    selected.Add(list[idx - 1]);
            }
            return selected.Count > 0 ? selected : new List<string> { list[0] };
        }

        var _ms = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title($"  [{C.Prompt}]{Esc(title)}[/]")
                .HighlightStyle(new Style(Color.White, decoration: Decoration.Bold))
                .UseConverter(Esc)
                .AddChoices(choices))
            .ToList();
        ErasePromptResidue(_resTop);
        return _ms;
    }

    public static void WriteSummaryTable(string title, IEnumerable<(string Key, string Value)> rows)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                var rowList = rows.Select(r => new Dictionary<string, object?> { ["key"] = r.Key, ["value"] = r.Value }).ToList();
                EmitJson("table", D(("title", title), ("rows", rowList)));
                return;
            }

            // TUI: borderless key/value block through the live region (no clipping, cursor safe).
            if (ViaDriver)
            {
                var rowList = rows.ToList();
                int keyW = rowList.Count == 0 ? 0 : rowList.Max(r => (r.Key ?? "").Length);
                var detail = rowList.Select(r =>
                    $"[{C.Info}]{Esc((r.Key ?? "").PadRight(keyW))}[/]  [{C.Prompt}]{Esc(r.Value ?? "")}[/]");
                if (TuiCommitBlock($"[{C.Step}]{Esc(title)}[/]", detail)) return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(Color.Grey35))
                .Title($"[{C.Step}]{Esc(title)}[/]")
                .AddColumn(new TableColumn($"[{C.Muted}]Setting[/]").PadRight(2))
                .AddColumn(new TableColumn($"[{C.Muted}]Value[/]"));

            foreach (var (key, value) in rows)
                table.AddRow($"[{C.Info}]{Esc(key)}[/]", $"[{C.Prompt}]{Esc(value)}[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        });
    }

    public static void WritePanel(string title, string content)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("panel", D(("title", title), ("content", content)));
                return;
            }

            // TUI: commit a borderless titled block through the live region so it can never be
            // clipped at the docked footer and the cursor stays put. Each content line becomes
            // an indented detail row (Claude-Code feel). Falls back to a Spectre panel in classic.
            if (ViaDriver)
            {
                var detail = (content ?? "").Replace("\r\n", "\n").Split('\n')
                    .Select(l => $"[{C.Prompt}]{Esc(l.TrimEnd())}[/]");
                if (TuiCommitBlock($"[{C.Step}]{Esc(title)}[/]", detail)) return;
            }

            AnsiConsole.Write(new Panel($"[{C.Prompt}]{Esc(content)}[/]")
                .Header($"[{C.Step}]{Esc(title)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Grey35))
                .Padding(1, 0));
        });
    }

    public static void WithSpinner(string message, Action work)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            EmitJson("task_start", message);
            work();
            EmitJson("task_done", message);
            return;
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey))
            .Start($"[{C.Info}]{Esc(message)}[/]", _ => work());
    }

    public static async Task WithSpinnerAsync(string message, Func<Task> work)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            EmitJson("task_start", message);
            await work();
            EmitJson("task_done", message);
            return;
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey))
            .StartAsync($"[{C.Info}]{Esc(message)}[/]", async _ => await work());
    }

    public static void WriteAgentTurnHeader(string agentName)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("agent_turn_start", D(("agent", agentName))); }
            else if (Capturing) { /* collapsed sub-agent: header folded into the summary line */ }
            else if (IsTui) { RenderTuiTurnHeader(agentName); }
            else
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule($"[{C.Agent}]{Esc(agentName)}[/]")
                    .RuleStyle(new Style(Color.Grey23))
                    .LeftJustified());
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "agent_turn_start",
            Agent = agentName,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public static void WriteAgentTurnFooter()
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("agent_turn_end"); return; }
            if (Capturing) return;   // collapsed sub-agent: no inline turn spacer
            if (TuiCommit("")) return;
            AnsiConsole.WriteLine();
        }, clearIndicator: false);
    }

    public static void WriteDelegation(string fromAgent, string toAgent, string task, int truncLength = 800)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("delegation", D(("from", fromAgent), ("to", toAgent), ("task", task)));
            }
            else if (IsTui) { RenderTuiDelegation(fromAgent, toAgent, task, truncLength); }
            else
            {
                string truncTask = task.Length > truncLength ? task[..truncLength] + "..." : task;
                AnsiConsole.MarkupLine($"  [{C.Accent}]>>[/] [{C.Info}]{Esc(fromAgent)}[/] [{C.Muted}]->[/] [{C.Step}]{Esc(toAgent)}[/]");
                AnsiConsole.MarkupLine($"     [{C.Muted}]{Esc(truncTask)}[/]");
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "delegation",
            Agent = fromAgent,
            Summary = task.Length > truncLength ? task[..truncLength] + "..." : task,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public static void WriteToolCall(string agent, string tool, string? args = null)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("tool_call", D(("agent", agent), ("tool", tool), ("args", args)));
            }
            else if (IsTui) { RenderTuiToolCall(agent, tool, args); }
            else
            {
                AnsiConsole.MarkupLine($"  [{C.Muted}]--[/] [{C.Info}]{Esc(agent)}[/] [{C.Muted}]->[/] [{C.Accent}]{Esc(tool)}[/]");
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "tool_call",
            Agent = agent,
            Tool = tool,
            Args = args,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public static void WriteToolResult(string agent, string summary)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("tool_result", D(("agent", agent), ("summary", summary)));
            }
            else if (Capturing) { CaptureToolResult(summary); }
            else if (IsTui) { RenderTuiToolResultSummary(agent, summary); }
            else
            {
                string clean = CollapseWhitespace(summary);
                if (clean.Length > 120) clean = clean[..120] + "...";
                AnsiConsole.MarkupLine($"     [{C.Muted}]{Esc(clean)}[/]");
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "tool_result",
            Agent = agent,
            Summary = summary,
            Timestamp = DateTimeOffset.UtcNow
        });

        // Deep-memory mid-turn activity: a streamed tool result is real progress, so mark the
        // gatherer dirty. Its timer-gated loop (reflectionAgent.pollIntervalSeconds) then reflects
        // on the next tick instead of waiting for the next USER turn - so a long autonomous turn
        // (many tool calls) is captured as it happens. No-op in standard mode; never fires when idle.
        MuxSwarm.Utils.Memory.ReflectionGatherer.Touch();
    }

    public static void WriteToolResult(string agent, string tool, string fullResult, bool swarm = false)
    {
        //no need to display this unnecessary
        //TODO: We have to filter out .AIContent[] as there is currently no check for if the returned function content is a flat obj or not, update logic in agent orchestrators to check for dict etc
        if (fullResult.Length <= 0 || fullResult.Trim().Equals("Task marked as complete.", StringComparison.OrdinalIgnoreCase)
            || fullResult.Trim().Equals("Microsoft.Extensions.AI.AIContent[]", StringComparison.OrdinalIgnoreCase)) return;

        // Collapsed sub-agent: fold the tool result into the buffered transcript (the hook
        // below still fires) instead of rendering a panel/merged line inline.
        if (Capturing) { CaptureToolResult($"{tool}: {Common.ExtractMcpText(fullResult)}"); return; }

        if (!StdioMode)
        {
            if (IsTui) { RenderTuiToolResultPanel(agent, tool, fullResult, swarm); return; }

            fullResult = Common.ExtractMcpText(fullResult);

            var display = fullResult.Length > 2000
                ? Esc(fullResult[..2000]) + "\n[dim]... truncated[/]"
                : Esc(fullResult);

            if (swarm)
            {
                display = fullResult.Length > 500
                    ? Esc(fullResult[..500]) + "\n[dim]... truncated[/]"
                    : Esc(fullResult);
            }

            var panel = new Panel($"[grey]{display}[/]")
            {
                Header = new PanelHeader($"[{C.Accent}] {Esc(tool)} [/]", Justify.Left),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Grey37),
                Padding = new Padding(2, 1),
                Expand = false
            };

            WriteRule();
            AnsiConsole.Write(panel);
            WriteRule();
            return;
        }
        EmitJson("tool_result", D(("agent", agent), ("tool", tool), ("result", fullResult)));
    }

    public static void WriteTaskComplete(string agent, string summary)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("task_complete", D(("agent", agent), ("summary", summary)));
            }
            else if (Capturing) { /* collapsed sub-agent: completion folded into the summary line */ }
            else if (IsTui) { RenderTuiTaskComplete(agent, summary); }
            else
            {
                AnsiConsole.MarkupLine($"  [{C.Success}]✓[/] [{C.Step}]{Esc(agent)}[/] [{C.Muted}]completed[/]  [{C.Info}]{Esc(summary)}[/]");
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "task_complete",
            Agent = agent,
            Summary = summary,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    private static string Esc(string text) => Markup.Escape(text);

    private static string StripMarkup(string markup)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(markup, @"\[/?[^\]]*\]", "");
        return result.Trim();
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
    }

    public static void FlushAndNewline()
    {
        WithConsole(() =>
        {
            if (StdioMode) return;
            Console.WriteLine();
            try { Console.Out.Flush(); } catch { /* ignore */ }
        });
    }

    public static void PrintHelp(string helpText)
    {
        if (StdioMode)
        {
            WriteBody(helpText);
            return;
        }

        // Under the live-region driver, the multi-column Spectre grid paints below the
        // footer and gets clipped/desynced. Commit the plain help text through the driver
        // as a borderless block (Claude-Code feel) so it scrolls into native history.
        if (ViaDriver)
        {
            var lines = new List<string> { "", $"  [{C.Step}]\u2503[/] [{C.Step}]Mux-Swarm \u2014 Command Reference[/]", "" };
            foreach (var raw in (helpText ?? "").Replace("\r\n", "\n").Split('\n'))
                lines.Add($"  [{C.Prompt}]{Esc(raw.TrimEnd())}[/]");
            lines.Add("");
            CommitLinesToDriver(lines);
            return;
        }

        // Single source of truth: render Help.HelpText (the same catalog used by stdio and the
        // live driver) in a rounded panel. The old hand-maintained multi-column grid was a
        // duplicate that silently drifted from the real command set; it has been removed.
        WritePanel("Mux-Swarm \u2014 Command Reference", helpText);
    }

    /// <summary>
    /// Render the keyboard-shortcut reference. Sourced entirely from the canonical
    /// <see cref="Tui.TuiCommands.Keys"/> catalog so it can never drift from the live key
    /// handlers or the /api/commands web endpoint. Grouped by context (prompt / turn / view).
    /// Honors stdio (plain text), the live driver (borderless block), and classic (panel).
    /// </summary>
    public static void PrintShortcuts()
    {
        string ContextTitle(string ctx) => ctx switch
        {
            "prompt" => "At the prompt (input line)",
            "turn"   => "During an agent turn",
            "view"   => "Transcript / expand view",
            _         => ctx,
        };
        var order = new[] { "prompt", "turn", "view" };

        // Plain text for stdio.
        if (StdioMode)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Keyboard Shortcuts");
            foreach (var ctx in order)
            {
                sb.AppendLine();
                sb.AppendLine(ContextTitle(ctx));
                foreach (var k in Tui.TuiCommands.Keys)
                    if (k.Context == ctx)
                        sb.AppendLine($"  {k.Keys,-14}{k.Desc}");
            }
            WriteBody(sb.ToString().TrimEnd());
            return;
        }

        // Live driver: borderless block committed into native scrollback.
        if (ViaDriver)
        {
            var lines = new List<string> { "", $"  [{C.Step}]\u2503[/] [{C.Step}]Keyboard Shortcuts[/]" };
            foreach (var ctx in order)
            {
                lines.Add("");
                lines.Add($"  [{C.Step}]{Esc(ContextTitle(ctx))}[/]");
                foreach (var k in Tui.TuiCommands.Keys)
                    if (k.Context == ctx)
                        lines.Add($"    [{C.Prompt}]{Esc(k.Keys.PadRight(14))}[/][{C.Muted}]{Esc(k.Desc)}[/]");
            }
            lines.Add("");
            CommitLinesToDriver(lines);
            return;
        }

        // Classic: a rounded panel.
        var body = new StringBuilder();
        bool firstGroup = true;
        foreach (var ctx in order)
        {
            if (!firstGroup) body.AppendLine();
            firstGroup = false;
            body.AppendLine($"[{C.Step}]{Esc(ContextTitle(ctx))}[/]");
            body.AppendLine($"[{C.Muted}]────────────────────────────────────[/]");
            foreach (var k in Tui.TuiCommands.Keys)
                if (k.Context == ctx)
                    body.AppendLine($"  [{C.Prompt}]{Esc(k.Keys.PadRight(14))}[/][{C.Muted}]{Esc(k.Desc)}[/]");
        }

        var shortcutsPanel = new Panel(new Markup(body.ToString().TrimEnd()))
            .Header($"[{C.Step}]Mux-Swarm — Keyboard Shortcuts[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Grey35))
            .Padding(1, 1)
            .Expand();
        AnsiConsole.Write(shortcutsPanel);
    }
}


/// <summary>
/// Interactive render mode for <see cref="MuxConsole"/> (v0.11.0). Distinct from the
/// stdio/serve NDJSON path, which is selected by <see cref="MuxConsole.StdioMode"/> and
/// always takes precedence.
/// </summary>
public enum RenderMode
{
    /// <summary>NDJSON line protocol for --stdio / --serve. Byte-identical, machine-parseable.</summary>
    Stdio,

    /// <summary>Full-screen live renderer (interactive default on a capable terminal).</summary>
    Tui,

    /// <summary>Pre-v0.11.0 line-by-line renderer (opt-out and non-capable-terminal fallback).</summary>
    Classic
}
