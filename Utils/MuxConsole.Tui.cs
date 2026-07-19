using MuxSwarm.Utils.Tui;
using Spectre.Console;

namespace MuxSwarm.Utils;

/// <summary>
/// v0.11.0 Workstream G - the "tui" interactive render layer (Option B: a frame-owned
/// live-region renderer with the Claude-Code feel). When the TUI is active these helpers
/// drive a single <see cref="TuiDriver"/>: finished transcript content is committed into
/// the terminal's NATIVE scrollback while a pinned footer (context meter + mode badges)
/// and the input box stay anchored at the bottom - WITHOUT a DECSTBM scroll region or the
/// alternate screen buffer (both of which corrupted the earlier Model-A attempt). When the
/// driver is not active (docked footer disabled, or a non-capable terminal) these methods
/// fall back to inline Spectre chrome.
///
/// All of this is only reached when <see cref="MuxConsole.IsTui"/> is true - i.e. an
/// interactive capable TTY, never stdio/serve. Every caller branches on IsTui only AFTER
/// the StdioMode short-circuit, so the machine/web NDJSON contract is untouched.
/// </summary>
public static partial class MuxConsole
{
    private static class TC
    {
        public const string Accent  = "#64B4DC";
        public const string Agent   = "#8FB8D4";
        public const string Ok      = "#78C88C";
        public const string Warn    = "#D4A054";
        public const string Err     = "#D46C6C";
        public const string Muted   = "#787878";
        public const string Dim     = "#5A5A5A";
        public const string Text    = "#C8C8C8";
        public const string Plan    = "#B48EAD";
        public const string Ultra   = "#D08770";
        public const string DiffAdd = "#78C88C";
        public const string DiffDel = "#D46C6C";
        public const string Border  = "#3A3A3A";
    }

    /// <summary>
    /// Runtime tool-output verbosity. True (default) collapses tool results to a one-line
    /// summary with a "(+N lines)" hint, Claude-Code style; false renders the full panel.
    /// Toggled by /verbose. Errors and diffs always expand regardless.
    /// </summary>
    public static bool ToolOutputCompact { get; set; } = true;

    /// <summary>
    /// When true (default) the live TUI pins a docked footer + input box via the frame-owned
    /// <see cref="TuiDriver"/>. When false the TUI uses inline Spectre chrome with a status
    /// line printed before each prompt (no pinned footer). Set from console.dockedFooter.
    /// </summary>
    public static bool DockedFooterEnabled { get; set; } = true;

    /// <summary>
    /// v0.12.4 opt-in render backend. When true the live TUI uses the full-frame
    /// (alternate-screen) renderer instead of the inline native-scrollback live region. Set from
    /// console.renderEngine == "frame" at startup and passed to the driver on construction. Has no
    /// effect outside the live TUI / on non-capable terminals.
    /// </summary>
    public static bool FrameEngineEnabled { get; set; }

    /// <summary>Deferred full splash builder captured at startup for the frame engine. Layout runs
    /// at driver activation using the CURRENT terminal width, so a resize between the primary-buffer
    /// Spectre splash and alternate-screen takeover cannot leave stale panel geometry. Consumed once.</summary>
    internal static Func<int, List<string>>? FrameSplashFactory { get; set; }

    /// <summary>
    /// Informative-line threshold above which a collapsed tool result is Ctrl+E-expandable in
    /// the live TUI. Set from console.collapseToolLines; pushed to the driver on activation.
    /// 0 disables the expand affordance. Default 6.
    /// </summary>
    public static int CollapseToolLines { get; set; } = 6;

    /// <summary>Blank lines emitted BELOW a tool/delegation block before the next agent output
    /// in the live TUI (the docked-below separator). Tool groups stay docked directly under the
    /// output text above them; this controls only the gap beneath. 0 = tight. Default 1. Seeded
    /// from config at startup and pushed to the driver on activation + live via /set.</summary>
    public static int DelegationSpacing { get; set; } = 1;

    /// <summary>Rows scrolled per Ctrl+U / Ctrl+D step while paging the frame-mode viewport at the
    /// idle prompt (console.scrollSpeedRows). PgUp/PgDn and Ctrl+B/Ctrl+F always page a full
    /// viewport. Default 1. Seeded from config at startup and pushed to the driver on activation
    /// + live via /set. Ignored outside the frame render engine.</summary>
    public static int ScrollSpeedRows { get; set; } = 1;

    /// <summary>Mirror of console.mouseTracking (off|wheel|buttons) for the frame engine. Applied
    /// at TUI activation and live via /set mouseTracking or /mouse.</summary>
    public static string MouseTracking { get; set; } = "wheel";

    /// <summary>Shade the user input/compose field (console.inputHighlight). Pushed to the driver
    /// on activation + live via /set. Ignored outside the live TUI.</summary>
    public static bool InputHighlight { get; set; } = true;

    /// <summary>Mirror of console.contentBackgrounds: when false, tool/diff/code cards suppress their
    /// opaque themed background fill so the terminal's own background shows through. Seeded from config
    /// at startup and applied live via /set. Backed by <see cref="Tui.TuiComponents.ContentBackgrounds"/>
    /// (a static styling gate), so it takes effect for every builder path.</summary>
    public static bool ContentBackgrounds
    {
        get => Tui.TuiComponents.ContentBackgrounds;
        set => Tui.TuiComponents.ContentBackgrounds = value;
    }

    /// <summary>Render expanded tool-result card bodies as muted markdown (console.cardMarkdown).
    /// Pushed to the driver on activation + live via /set.</summary>
    public static bool CardMarkdown { get; set; } = true;

    /// <summary>Auto-collapse delegation dispatch lines to one expandable summary row
    /// (console.collapseDelegations). Read by RenderTuiDelegation; toggled live via /set.</summary>
    public static bool CollapseDelegations { get; set; } = true;

    /// <summary>Capture multi-line pastes as one block (console.bracketedPaste, DECSET 2004).
    /// Pushed to the driver on activation + live via /set.</summary>
    public static bool BracketedPaste { get; set; } = true;

    /// <summary>
    /// When true (default) a delegated sub-agent's live output is captured and collapsed into a
    /// single expandable transcript line instead of streaming inline (Claude-Code Task style).
    /// The sub-agent's thinking spinner still animates while it works. Set from
    /// console.collapseSubAgents; toggled at runtime by /subagentview (alias /sav). Only affects
    /// the live TUI - stdio/serve always streams (the web app demultiplexes sub-agent streams).
    /// </summary>
    public static bool CollapseSubAgents { get; set; } = true;

    /// <summary>
    /// When true (default), goals fired by the in-house daemon collapse their whole agent run into
    /// one expandable Agent-View line (reusing the sub-agent capture machinery), instead of
    /// streaming the full reasoning + tool transcript into the main viewport. Set from
    /// console.collapseDaemon; toggled at runtime by /daemonview (alias /dv). Independent of the
    /// /sav sub-agent toggle. Only affects the live TUI - stdio/serve always streams.
    /// </summary>
    public static bool CollapseDaemonOutput { get; set; } = true;

    // --- sub-agent output capture (collapse-by-default) ----------------------
    // While a delegated sub-agent runs, its streamed text / reasoning / tool-result summaries are
    // buffered here instead of being committed to the transcript; on completion one collapsed,
    // expandable line is emitted. AsyncLocal so each (possibly concurrent, parallel-swarm) sub-
    // agent flow has its own isolated capture and siblings never cross-contaminate.

    private sealed class SubAgentCapture
    {
        public required string Agent;
        // Stable, disambiguated lane name unique among ALIVE captures: "WebAgent", "WebAgent 2",
        // "WebAgent 3", ... Assigned once at registration so the Agent View can key selection +
        // body lookup per LANE (duplicate same-name delegations no longer collapse to one row).
        public string Lane = "";
        // /hide: when true the lane is removed from the docked activity strip + viewport but kept
        // in the backslash Agent View (tagged "hidden"), so the user can unhide it later.
        public volatile bool Hidden;
        public readonly System.Text.StringBuilder Buffer = new();
        // Rolling tail of streamed text (~240 chars) surfaced as a LIVE content preview in the panel.
        public readonly System.Text.StringBuilder Tail = new();
        public int ToolCalls;
        public string? Status;             // set from signal_task_complete (success/failure/partial)
        public volatile string LiveStatus = "working";  // concise live activity for the panel line
    }

    private static readonly AsyncLocal<SubAgentCapture?> _capture = new();

    // Registry of currently-running captured sub-agents, in start order. The consolidated live
    // activity panel renders one line per entry; a single shared ticker animates them all,
    // replacing the per-agent thinking spinners (which, run concurrently, fought over the one
    // shared live line and flickered heavily). Guarded by _captureGate.
    private static readonly object _captureGate = new();
    private static readonly List<SubAgentCapture> _activeCaptures = new();

    // Lane -> per-sub-agent CancellationTokenSource, so Esc can cancel a SINGLE foregrounded
    // sub-agent (the one expanded via the backslash Agent View) instead of the whole turn. The
    // orchestrator registers each child's own linked CTS under its capture lane for the lifetime
    // of the run; the cancel path looks the lane up from TuiDriver.ForegroundAgent. Guarded by
    // _captureGate. Cancelling one lane lets siblings continue (their batch token is unaffected).
    private static readonly Dictionary<string, CancellationTokenSource> _laneCts =
        new(StringComparer.Ordinal);
    private static System.Threading.Timer? _subAgentTimer;
    private static int _subAgentFrame;
    // Always-on resize poll: ticks ~100ms while the live-region driver is active and forces a
    // clean repaint when the terminal size changes (the only reliable cure for the buffer-reflow
    // artifacts a width resize leaves behind). Started on driver activation, disposed on teardown.
    private static System.Threading.Timer? _resizeTimer;

    /// <summary>True when the current async flow is a captured (collapsed) sub-agent.</summary>
    private static bool Capturing => _capture.Value is not null;

    /// <summary>Public read of whether the calling async flow is inside a sub-agent / daemon
    /// capture lane (no live interactive user). Used by native-tool security to auto-deny
    /// elevation prompts that would otherwise block on input that never comes.</summary>
    public static bool InSubAgentCapture => _capture.Value is not null;

    /// <summary>The active capture for this async flow, or null. Used by the gated sinks.</summary>
    private static SubAgentCapture? CurrentCapture => _capture.Value;

    /// <summary>
    /// Begin capturing the calling sub-agent's live output so it collapses to one expandable
    /// line. Returns a scope to dispose when the sub-agent finishes (commits the collapsed line),
    /// or null when capture does not apply (collapse disabled, stdio/serve, or non-TUI) - in
    /// which case the sub-agent streams inline as before. Safe to <c>using</c> the nullable.
    /// </summary>
    /// <summary>
    /// Begin capturing a daemon-fired goal's live output so the whole run collapses to one
    /// expandable Agent-View line (tagged with the trigger label), gated on the INDEPENDENT
    /// <see cref="CollapseDaemonOutput"/> flag rather than the /sav sub-agent toggle. Reuses the
    /// proven sub-agent capture plumbing. Returns null (stream inline as today) when collapse is
    /// off, in stdio/serve, or outside the TUI. Safe to <c>using</c> the nullable.
    /// </summary>
    public static IDisposable? BeginDaemonCapture(string label)
    {
        if (!CollapseDaemonOutput || StdioMode || !IsTui) return null;
        var cap = new SubAgentCapture { Agent = label };
        var prev = _capture.Value;
        _capture.Value = cap;
        lock (_captureGate)
        {
            cap.Lane = NextLane_NoGate(label);
            _activeCaptures.Add(cap);
            EnsureSubAgentTicker_NoGate();
        }
        PushSubAgentActivity();
        return new CaptureScope(cap, prev);
    }

    public static IDisposable? BeginSubAgentCapture(string agent)
    {
        if (!CollapseSubAgents || StdioMode || !IsTui) return null;
        var cap = new SubAgentCapture { Agent = agent };
        var prev = _capture.Value;       // restore on dispose so nested delegation is correct
        _capture.Value = cap;
        lock (_captureGate)
        {
            cap.Lane = NextLane_NoGate(agent);
            _activeCaptures.Add(cap);
            EnsureSubAgentTicker_NoGate();
        }
        PushSubAgentActivity();
        return new CaptureScope(cap, prev);
    }

    /// <summary>
    /// Register a sub-agent's own cancellation source under the CURRENT capture lane (so a scoped
    /// Esc on the foregrounded/expanded sub-agent cancels just that child) and return an
    /// IDisposable that deregisters it when the run ends. No-op (returns a null-object disposable)
    /// when not capturing - non-TUI / collapse disabled - in which case only the whole-turn cancel
    /// applies. Safe to <c>using</c>.
    /// </summary>
    public static IDisposable ScopedLaneCts(CancellationTokenSource cts)
    {
        var cap = _capture.Value;
        if (cap is null || string.IsNullOrEmpty(cap.Lane)) return _noopDisposable;
        string lane = cap.Lane;
        lock (_captureGate) { _laneCts[lane] = cts; }
        return new LaneCtsScope(lane);
    }

    private static readonly IDisposable _noopDisposable = new NoopDisposable();
    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    private sealed class LaneCtsScope : IDisposable
    {
        private readonly string _lane;
        private bool _done;
        public LaneCtsScope(string lane) { _lane = lane; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (_captureGate) { _laneCts.Remove(_lane); }
        }
    }

    /// <summary>
    /// Scoped cancellation entry point for Esc while sub-agents are live. If the user has
    /// FOREGROUNDED a specific sub-agent (Enter in the backslash Agent View -> sticky
    /// <c>TuiDriver.ForegroundAgent</c>) and that lane has a live CTS, cancel ONLY that child and
    /// return true (siblings + the lead turn keep running). Otherwise return false so the caller
    /// falls back to cancelling the whole turn. No sub-agents / none foregrounded -> false.
    /// </summary>
    public static bool TryCancelForegroundedSubAgent()
    {
        if (!ViaDriver) return false;
        string? lane;
        lock (ConsoleLock) { lane = _driver!.ForegroundAgent; }
        if (string.IsNullOrEmpty(lane)) return false;
        CancellationTokenSource? cts;
        lock (_captureGate) { _laneCts.TryGetValue(lane, out cts); }
        if (cts is null || cts.IsCancellationRequested) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        WriteInfo($"Cancelled sub-agent: {lane}");
        return true;
    }

    /// <summary>Record the sub-agent's completion status (from signal_task_complete) on the
    /// active capture, so the collapsed line can show success/failure. No-op when not capturing.</summary>
    public static void SetCapturedStatus(string? status)
    {
        if (_capture.Value is { } cap && !string.IsNullOrEmpty(status)) cap.Status = status;
    }

    /// <summary>Update the concise live-activity text for the active capture's panel line
    /// (e.g. "working", "calling: read_file"). No-op when not capturing.</summary>
    private static void SetCapturedLiveStatus(string status)
    {
        if (_capture.Value is { } cap && !string.IsNullOrEmpty(status))
        {
            cap.LiveStatus = status;
            PushSubAgentActivity();
        }
    }

    private sealed class CaptureScope : IDisposable
    {
        private readonly SubAgentCapture _cap;
        private readonly SubAgentCapture? _prev;
        private bool _done;
        public CaptureScope(SubAgentCapture cap, SubAgentCapture? prev) { _cap = cap; _prev = prev; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _capture.Value = _prev;
            lock (_captureGate)
            {
                _activeCaptures.Remove(_cap);
                if (_activeCaptures.Count == 0) StopSubAgentTicker_NoGate();
            }
            CommitCapturedSubAgent(_cap);   // commit the collapsed line for THIS agent
            PushSubAgentActivity();          // refresh the panel (now without this agent)
        }
    }

    // --- consolidated live activity panel (single ticker, no per-agent flicker) --------------

    /// <summary>Assign a unique disambiguated lane name for a new capture among the ALIVE lanes:
    /// the first "WebAgent" stays "WebAgent"; a concurrent second becomes "WebAgent 2", etc. Caller
    /// holds <see cref="_captureGate"/>.</summary>
    private static string NextLane_NoGate(string agent)
    {
        var baseName = string.IsNullOrWhiteSpace(agent) ? "agent" : agent.Trim();
        var taken = new HashSet<string>(_activeCaptures.Select(c => c.Lane), StringComparer.Ordinal);
        if (!taken.Contains(baseName)) return baseName;
        for (int n = 2; ; n++)
        {
            var cand = $"{baseName} {n}";
            if (!taken.Contains(cand)) return cand;
        }
    }

    /// <summary>/hide: hide a live sub-agent LANE from the docked strip + viewport (kept in the
    /// backslash Agent View, tagged "hidden"). Resolves <paramref name="lane"/> by exact lane name,
    /// else by base agent name (first match). Returns the resolved lane name, or null if not found.</summary>
    public static string? HideSubAgentLane(string lane)
    {
        string? resolved = null;
        lock (_captureGate)
        {
            var cap = _activeCaptures.FirstOrDefault(c => string.Equals(c.Lane, lane, StringComparison.OrdinalIgnoreCase))
                   ?? _activeCaptures.FirstOrDefault(c => string.Equals(c.Agent, lane, StringComparison.OrdinalIgnoreCase) && !c.Hidden);
            if (cap is not null) { cap.Hidden = true; resolved = cap.Lane; }
        }
        if (resolved is not null) PushSubAgentActivity();
        return resolved;
    }

    /// <summary>Unhide a previously /hide'd lane (exact lane name, else base agent name). Returns the
    /// resolved lane, or null if not found.</summary>
    public static string? UnhideSubAgentLane(string lane)
    {
        string? resolved = null;
        lock (_captureGate)
        {
            var cap = _activeCaptures.FirstOrDefault(c => string.Equals(c.Lane, lane, StringComparison.OrdinalIgnoreCase) && c.Hidden)
                   ?? _activeCaptures.FirstOrDefault(c => string.Equals(c.Agent, lane, StringComparison.OrdinalIgnoreCase) && c.Hidden);
            if (cap is not null) { cap.Hidden = false; resolved = cap.Lane; }
        }
        if (resolved is not null) PushSubAgentActivity();
        return resolved;
    }

    /// <summary>The live lane names (for /hide + /unhide autocomplete). Visible lanes first, then
    /// hidden ones suffixed with " (hidden)".</summary>
    public static IReadOnlyList<string> ActiveSubAgentLanes()
    {
        lock (_captureGate)
            return _activeCaptures.Select(c => c.Hidden ? $"{c.Lane} (hidden)" : c.Lane).ToList();
    }

    /// <summary>Lane names eligible for /hide (the currently-visible lanes).</summary>
    public static IReadOnlyList<string> VisibleSubAgentLanes()
    {
        lock (_captureGate)
            return _activeCaptures.Where(c => !c.Hidden).Select(c => c.Lane).ToList();
    }

    /// <summary>Lane names eligible for /unhide (the currently-hidden lanes).</summary>
    public static IReadOnlyList<string> HiddenSubAgentLanes()
    {
        lock (_captureGate)
            return _activeCaptures.Where(c => c.Hidden).Select(c => c.Lane).ToList();
    }

    /// <summary>Start the shared ~100ms ticker that animates the active-sub-agent panel. Caller
    /// holds <see cref="_captureGate"/>. Idempotent.</summary>
    private static void EnsureSubAgentTicker_NoGate()
    {
        _subAgentTimer ??= new System.Threading.Timer(
            _ => PushSubAgentActivity(advanceFrame: true), null, 0, 100);
    }

    /// <summary>Stop + dispose the shared ticker. Caller holds <see cref="_captureGate"/>.</summary>
    private static void StopSubAgentTicker_NoGate()
    {
        _subAgentTimer?.Dispose();
        _subAgentTimer = null;
    }

    /// <summary>Snapshot of ALL live captured sub-agents (any launcher: run_team, delegate_parallel
    /// blocking, swarm members, giga), each with lane, agent name, live activity, tool-call count,
    /// and a ~120-char tail preview. The subagent_status tool renders this so the lead can watch
    /// blocking delegations mid-batch, not just detached jobs. Ordered by start.</summary>
    internal static List<(string Lane, string Agent, string LiveStatus, int ToolCalls, string Tail)> GetLiveSubAgentDetails()
    {
        lock (_captureGate)
        {
            return _activeCaptures.Select(c =>
            {
                string tail = CollapseWhitespace(c.Tail.ToString()).Trim();
                if (tail.Length > 120) tail = "\u2026" + tail[^120..];
                return (c.Lane, c.Agent, c.LiveStatus, c.ToolCalls, tail);
            }).ToList();
        }
    }

    /// <summary>Live detail for a RUNNING captured sub-agent by agent name (exact or lane-prefix
    /// match, most-recent first): its lane label, one-line live activity, tool-call count, and a
    /// ~120-char tail preview of its streamed output. Returns null when no live capture matches.
    /// Used by the sub-agent status tools so the lead sees real progress instead of bare "running",
    /// without pulling the whole buffer into context.</summary>
    internal static (string Lane, string LiveStatus, int ToolCalls, string Tail)? GetLiveSubAgentDetail(string agent)
    {
        if (string.IsNullOrWhiteSpace(agent)) return null;
        lock (_captureGate)
        {
            var cap = _activeCaptures.FindLast(c =>
                string.Equals(c.Agent, agent, StringComparison.OrdinalIgnoreCase) ||
                c.Lane.StartsWith(agent, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Lane, agent, StringComparison.OrdinalIgnoreCase));
            if (cap is null) return null;
            string tail = CollapseWhitespace(cap.Tail.ToString()).Trim();
            if (tail.Length > 120) tail = "\u2026" + tail[^120..];
            return (cap.Lane, cap.LiveStatus, cap.ToolCalls, tail);
        }
    }

    /// <summary>
    /// Recompute the active-sub-agent snapshot from the registry and push it to the driver's
    /// live region (one line per running agent). Reading LiveStatus here keeps the panel fresh;
    /// the ticker calls this with advanceFrame=true to animate the shared spinner.
    /// </summary>
    private static void PushSubAgentActivity(bool advanceFrame = false)
    {
        if (!ViaDriver) return;
        List<(string Agent, string Status, string Tint)> items;
        lock (_captureGate)
        {
            if (advanceFrame) _subAgentFrame++;
            items = _activeCaptures
                .Where(c => !c.Hidden)
                .Select(c => (c.Lane, c.LiveStatus, TuiComponents.AgentTint(c.Agent)))
                .ToList();
        }
        lock (ConsoleLock) { _driver!.SetSubAgentActivity(items, _subAgentFrame); }
    }

    /// <summary>Append captured text/reasoning to the active sub-agent buffer, and surface a
    /// rolling tail of it as the live panel preview so the user watches the sub-agent work in
    /// real time (not just a spinner + tool name). Streamed text and "calling: X" tool status
    /// share the one LiveStatus line; whichever fired most recently wins, which reads naturally.</summary>
    private static void CaptureAppend(string text)
    {
        if (_capture.Value is not { } cap || string.IsNullOrEmpty(text)) return;
        cap.Buffer.Append(text);
        cap.Tail.Append(text);
        if (cap.Tail.Length > 240) cap.Tail.Remove(0, cap.Tail.Length - 240);
        string preview = CollapseWhitespace(cap.Tail.ToString()).Trim();
        if (preview.Length > 0) { cap.LiveStatus = preview; PushSubAgentActivity(); }
        // If THIS sub-agent is currently expanded (Ctrl+E), keep its bounded live panel growing in
        // place from the full buffer (not the rolling tail) - the panel itself bounds what shows.
        if (ViaDriver)
            lock (ConsoleLock) { _driver!.UpdateSubAgentExpandedBody(cap.Agent, cap.Buffer.ToString().Trim()); }
    }

    /// <summary>Record a captured tool marker as a clean one-line "\u00b7 &lt;Action&gt;" dot row
    /// (main-viewport style) + bump the tool counter, instead of a raw "[tool] &lt;dump&gt;" blob -
    /// so the expanded card reads as prose interleaved with tidy tool dots, not truncated JSON. The
    /// <paramref name="summary"/> is shaped "&lt;tool&gt;: &lt;result text&gt;"; the action label is
    /// derived from the tool id (Describe never returns a raw id), with a short trailing detail.
    /// Fallback: no parseable "tool:" prefix -&gt; dot + the trimmed text.</summary>
    private static void CaptureToolResult(string summary)
    {
        if (_capture.Value is not { } cap) return;
        cap.ToolCalls++;
        string raw = CollapseWhitespace(summary ?? "");
        if (raw.Length == 0) return;
        string toolId = raw, detail = "";
        int colon = raw.IndexOf(':');
        if (colon > 0) { toolId = raw[..colon].Trim(); detail = raw[(colon + 1)..].Trim(); }
        string action = ToolActionLabel.Describe(toolId);
        string detailShort = detail.Length > 80 ? detail[..80] + "\u2026" : detail;
        string row = detailShort.Length > 0 ? $"\u00b7 {action} \u2014 {detailShort}" : $"\u00b7 {action}";
        cap.Buffer.Append('\n').Append(row);
    }

    /// <summary>
    /// Emit the collapsed, expandable summary line for a finished sub-agent capture. The full
    /// buffered transcript is retained behind the line so Ctrl+E / NAV can expand it in place,
    /// reusing the same machinery as large tool results. No-op when the driver is inactive.
    /// </summary>
    private static void CommitCapturedSubAgent(SubAgentCapture cap)
    {
        string body = cap.Buffer.ToString().Trim();
        int lines = body.Length == 0 ? 0
            : body.Replace("\r\n", "\n").Split('\n').Count(l => l.Trim().Length > 0);
        string tint = TuiComponents.AgentTint(cap.Agent);
        string collapsed = TuiComponents.SubAgentCollapsed(cap.Agent, cap.Status, lines, cap.ToolCalls, tint);

        if (ViaDriver)
        {
            lock (ConsoleLock)
            {
                // If THIS sub-agent is the one currently expanded in the live region, keep its panel
                // open through completion instead of snapping it collapsed (the abrupt auto-collapse
                // bug): freeze it to the final transcript and let the user close it (Ctrl+E) or have
                // it fold away naturally when the input prompt returns. Otherwise drop any unrelated
                // in-region expansion so a stale one never lingers.
                // A finishing agent never collapses a panel - not its own (kept open through
                // completion) and crucially not a DIFFERENT agent's open panel (the old
                // ClearSubAgentExpanded() here closed whoever was expanded when ANY sibling
                // finished). Panels close only on the user's Ctrl+E or when the prompt returns.
                bool keepOpen = _driver!.IsSubAgentExpanded(cap.Agent);
                // Retain the full transcript expandable when there is anything to expand; otherwise
                // commit a bare collapsed line (an empty sub-agent turn).
                if (body.Length > 0)
                    _driver!.CommitCollapsed(collapsed, cap.Agent, body);
                else
                    _driver!.CommitLine(collapsed);
                // Re-anchor the still-open panel to the final body after the collapsed line commits.
                if (keepOpen) _driver!.UpdateSubAgentExpandedBody(cap.Agent, body);
            }
            return;
        }
        // Inline fallback (TUI without docked driver): just print the collapsed line.
        WithConsole(() => AnsiConsole.MarkupLine(collapsed));
    }

    // --- live-region driver --------------------------------------------------

    private static TuiDriver? _driver;
    private static bool _tuiActive;
    private static bool _teardownHooked;

    // The primary (foreground) agent name for the active interactive loop. Sub-agent /
    // swarm-specialist output (any agent != this) is guttered with a per-agent lane color so
    // a dense multi-agent transcript stays attributable. Set from the session header.
    private static string? _primaryAgent;

    // Cached footer state so any render path can repaint the footer with current values.
    private static uint _fTokens, _fThreshold, _fCached;
    private static bool _fPlan, _fUltra, _fPsub, _fSub, _fGiga;

    /// <summary>True when the frame-owned live-region driver is running.</summary>
    public static bool TuiActive => _tuiActive && _driver is not null;

    /// <summary>
    /// Activate the live-region TUI driver (pinned footer + input box). No-op outside TUI,
    /// on a non-capable terminal, or when the docked footer is disabled (then inline chrome
    /// is used). Idempotent. Installs guaranteed teardown on process exit / Ctrl-C so the
    /// terminal is never left in a dirty state.
    /// </summary>
    public static void EnableDockedFooter() => EnableDockedFooter(topLevel: false);

    /// <summary>
    /// Activate (or re-scope) the live-region driver. The driver persists for the whole
    /// interactive REPL so the pinned footer + as-you-type palette are available everywhere -
    /// at the top-level mode menu (<paramref name="topLevel"/> = true, repl command set) and
    /// inside an agent/swarm session (false, in-session command set). When entering a session
    /// the stale token cache is cleared so the footer never shows a prior session's counts.
    /// Idempotent: if already active it just switches palette scope / resets the meter.
    /// </summary>
    public static void EnableDockedFooter(bool topLevel)
    {
        if (!IsTui || !DockedFooterEnabled) return;
        lock (ConsoleLock)
        {
            try
            {
                if (_driver is null)
                {
                    _driver = new TuiDriver(frameEngine: FrameEngineEnabled);
                    if (FrameEngineEnabled)
                    {
                        // SINGLE INPUT PLANE: the pump becomes the only stdin reader for the frame
                        // engine (prompt loop, mid-turn listener, and overlays all consume its
                        // typed events). It reassembles SGR mouse reports + bracketed pastes
                        // upstream, so torn "[<64;…" fragments can never reach the editor.
                        Tui.ConsoleInputPump.Start(
                            mouseTracking: !string.Equals(MouseTracking, "off", StringComparison.OrdinalIgnoreCase),
                            bracketedPaste: BracketedPaste);
                    }
                    // Frame mode: seed the transcript with the retained splash so the first frame
                    // opens on the banner (the primary-buffer splash is hidden by the alt screen).
                    if (FrameEngineEnabled && FrameSplashFactory is { } splashFactory)
                    {
                        _driver.CommitStartup(splashFactory(_driver.Width + 1));
                        FrameSplashFactory = null;
                    }
                    _tuiActive = true;
                    // Idle-prompt backslash opens the Agent View dashboard (same entry as the
                    // mid-turn EscapeKeyListener path). Returns false when no agents are running,
                    // so backslash then inserts as a literal char.
                    _driver.AgentViewOpener = TuiEnterAgentView;
                    // Idle-prompt Ctrl+E targets the live sub-agent panel (toggle open/closed),
                    // mirroring the mid-turn EscapeKeyListener expand; TuiDriver.ReadLine falls back
                    // to the NAV overlay when no sub-agents are running.
                    _driver.OnSubAgentExpand = TuiExpandLatestInline;
                    // Backslash at the idle prompt with no live sub-agents offers the detached
                    // interactive sessions (v0.12.0 /detach). One parked session -> attach it
                    // directly; several -> route to the "/attach" list so the user picks an id.
                    _driver.AttachPicker = () =>
                    {
                        var parked = InteractiveSessionRegistry.ListParked();
                        if (parked.Count == 0) return null;
                        return parked.Count == 1 ? $"/attach {parked[0].Id}" : "/attach";
                    };
                    InstallTeardownHook_NoLock();
                    // Start the resize poll once, alongside the driver.
                    _resizeTimer ??= new System.Threading.Timer(_ => TuiPollResize(), null, 100, 100);
                }
                // Reset the meter when (re)entering any scope so menu shows "ready" and a new
                // session starts from zero rather than inheriting the prior session's tokens.
                _fTokens = 0; _fThreshold = 0;
                _driver.SetLaneTint(null);   // never inherit a prior session's sub-agent gutter
                _driver.SetPaletteScope(topLevel);
                // Seed the live "/skill" autocomplete with the loaded skill catalog.
                try { _driver.SetSkillsCatalog(SkillLoader.GetSkillMetadata().Select(sk => (sk.Name, sk.Description)).ToList()); }
                catch { /* skills optional */ }
                // Seed the live "/resume" autocomplete with resumable sessions.
                try { _driver.SetSessionsCatalog(CliCmdUtils.GetResumableSessions()); }
                catch { /* sessions optional */ }
                // Seed the live "@" file picker with a workspace file index (best-effort), and
                // flag when the workspace resolves to the mux install dir so the picker can hint
                // about --workspace.
                try
                {
                    _driver.SetFilesCatalog(CliCmdUtils.GetWorkspaceFiles());
                    _driver.SetFilesInstallDirHint(PlatformContext.WorkspaceIsInstallDir);
                }
                catch { /* files optional */ }
                _driver.SetCollapseThreshold(CollapseToolLines);
                _driver.SetBlockGap(DelegationSpacing);
                _driver.SetScrollSpeedRows(ScrollSpeedRows);
                _driver.SetInputHighlight(InputHighlight);
                _driver.SetCardMarkdown(CardMarkdown);
                _driver.SetBracketedPaste(BracketedPaste);
                _driver.SetMouseTrackingPreset(MouseTracking);   // frame engine only (gated internally)
                _driver.SetFooter(_fTokens, _fThreshold, _fPlan, _fUltra, _fPsub, _fSub, giga: _fGiga);
                // Mid-turn wheel hook: the pump reassembles SGR reports and delivers Wheel events
                // to the EscapeKeyListener, which forwards them here (scroll, never a cancel/leak).
                EscapeKeyListener.OnWheelScroll = rows => TuiWheelScroll(rows);
            }
            catch
            {
                _tuiActive = false;
                _driver = null;
                EscapeKeyListener.OnWheelScroll = null;
            }
        }
    }

    /// <summary>Switch the driver's palette scope without tearing it down (menu &lt;-&gt; session).</summary>
    public static void SetPaletteScope(bool topLevel)
    {
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetPaletteScope(topLevel); }
    }

    /// <summary>
    /// Populate the driver's skills catalog backing the live "/skill" autocomplete preview.
    /// Safe to call anytime; no-op when the driver is not active.
    /// </summary>
    public static void SetTuiSkillsCatalog(IReadOnlyList<(string Name, string Desc)> skills)
    {
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetSkillsCatalog(skills); }
    }

    /// <summary>
    /// Populate the driver's sessions catalog backing the live "/resume" autocomplete preview.
    /// Safe to call anytime; no-op when the driver is not active.
    /// </summary>
    public static void SetTuiSessionsCatalog(IReadOnlyList<(string Id, string Preview)> sessions)
    {
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetSessionsCatalog(sessions); }
    }

    /// <summary>
    /// Populate the driver's file catalog backing the live "@" fuzzy file picker. Safe to call
    /// anytime; no-op when the driver is not active.
    /// </summary>
    public static void SetTuiFilesCatalog(IReadOnlyList<string> files)
    {
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetFilesCatalog(files); }
    }

    /// <summary>
    /// Populate the driver's tools catalog backing the live "/tools" scrollable palette (the
    /// expandable view behind the session-header tool badge). Safe to call anytime; no-op when
    /// the driver is not active.
    /// </summary>
    public static void SetTuiToolsCatalog(IReadOnlyList<(string Name, string Desc)> tools)
    {
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetToolsCatalog(tools); }
    }

    /// <summary>
    /// Tear down the live-region driver and hand the terminal back cleanly (cursor shown,
    /// no residue). Safe to call unconditionally (exit, /classic, mode switch). Idempotent.
    /// </summary>
    public static void DisableDockedFooter()
    {
        lock (ConsoleLock)
        {
            if (_driver is not null)
            {
                try { _driver.Shutdown(); } catch { /* ignore */ }
                try { Tui.ConsoleInputPump.Current?.Dispose(); } catch { /* ignore */ }
            }
            try { _resizeTimer?.Dispose(); } catch { /* ignore */ }
            _resizeTimer = null;
            _driver = null;
            _tuiActive = false;
        }
    }

    private static void InstallTeardownHook_NoLock()
    {
        if (_teardownHooked) return;
        _teardownHooked = true;
        try
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _driver?.Shutdown(); } catch { /* ignore */ }
                try { Tui.ConsoleInputPump.Current?.Dispose(); } catch { /* ignore */ }
            };
            Console.CancelKeyPress += (_, _) =>
            {
                try { _driver?.Shutdown(); } catch { /* ignore */ }
                try { Tui.ConsoleInputPump.Current?.Dispose(); } catch { /* ignore */ }
            };
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Update + repaint the docked footer content (context meter + mode badges). Caches the
    /// values so subsequent commits keep the footer current. No-op when the driver is not
    /// active.
    /// </summary>
    public static void UpdateDockedFooter(uint tokens, uint threshold, bool plan, bool ultra, bool parallelSub, uint cached = 0, bool sub = false, bool giga = false)
    {
        _fTokens = tokens; _fThreshold = threshold; _fPlan = plan; _fUltra = ultra; _fPsub = parallelSub; _fSub = sub; _fCached = cached; _fGiga = giga;
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetFooter(tokens, threshold, plan, ultra, parallelSub, sub, cached, giga); }
    }

    /// <summary>Refresh ONLY the footer mode badges (plan / ultra / parallel-sub) immediately,
    /// reusing the last cached token/threshold values. Used by slash-command toggles like /ultra
    /// so the badge updates the instant the mode flips, instead of waiting for the next
    /// post-stream status push.</summary>
    public static void RefreshDockedFooterModes(bool plan, bool ultra, bool parallelSub, bool sub = false, bool giga = false)
    {
        _fPlan = plan; _fUltra = ultra; _fPsub = parallelSub; _fSub = sub; _fGiga = giga;
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetFooter(_fTokens, _fThreshold, plan, ultra, parallelSub, sub, _fCached, giga); }
    }

    /// <summary>Start the loop clock (live "&#x25cf; m:ss" footer badge) - called when an agentic
    /// interface (/agent, /stateless, /swarm, /pswarm) is entered. No-op outside the driver.</summary>
    public static void StartTuiLoopClock()
    {
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.StartLoopClock(); }
    }

    /// <summary>Stop/clear the loop clock - called back at the top-level menu. No-op outside the driver.</summary>
    public static void StopTuiLoopClock()
    {
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.StopLoopClock(); }
    }

    /// <summary>Set/clear the active-session id badge shown in the docked footer.</summary>
    public static void SetTuiSessionId(string? sessionId)
    {
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetSessionId(sessionId); }
    }

    /// <summary>Push the static context-overhead breakdown (system-prompt + tool-schema token
    /// estimates) into the docked footer. Computed once per agent build so a fresh session\u0027s
    /// baseline context is explained to the user. No-op outside the driver path.</summary>
    public static void SetTuiTokenBreakdown(uint sysTokens, uint toolTokens)
    {
        if (!ViaDriver) return;
        lock (ConsoleLock) { _driver!.SetTokenBreakdown(sysTokens, toolTokens); }
    }

    /// <summary>Live-set the tool-result collapse threshold (lines) and push it to the active
    /// driver so the change takes effect immediately for subsequent tool results. Updates the
    /// runtime value used on the next driver activation too.</summary>
    public static void SetTuiCollapseThreshold(int lines)
    {
        CollapseToolLines = Math.Max(0, lines);
        if (!ViaDriver) return;
        lock (ConsoleLock) { _driver!.SetCollapseThreshold(CollapseToolLines); }
    }

    /// <summary>Set the delegation spacing (blank lines above each delegation block) live. Takes
    /// effect on the next delegation render; no driver state needed.</summary>
    /// <summary>Toggle input-field shading live (console.inputHighlight) + push to the driver.</summary>
    public static void SetTuiInputHighlight(bool on)
    {
        InputHighlight = on;
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetInputHighlight(on); }
    }

    /// <summary>Toggle opaque content backgrounds live (console.contentBackgrounds) + re-render the
    /// frame in place. Styling-only change (no geometry), so it just refreshes existing rows.</summary>
    public static void SetTuiContentBackgrounds(bool on)
    {
        ContentBackgrounds = on;
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.RefreshStyling(); }
    }

    /// <summary>Toggle muted-markdown card bodies live (console.cardMarkdown) + push to the driver.</summary>
    public static void SetTuiCardMarkdown(bool on)
    {
        CardMarkdown = on;
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetCardMarkdown(on); }
    }

    /// <summary>Toggle bracketed-paste capture live (console.bracketedPaste) + push to the driver.</summary>
    public static void SetTuiBracketedPaste(bool on)
    {
        BracketedPaste = on;
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetBracketedPaste(on); }
    }

    public static void SetTuiDelegationSpacing(int lines)
    {
        DelegationSpacing = Math.Max(0, lines);
        if (!ViaDriver) return;
        lock (ConsoleLock) { _driver!.SetBlockGap(DelegationSpacing); }
    }

    public static void SetTuiMouseTracking(string preset)
    {
        MouseTracking = preset is "off" or "buttons" ? preset : "wheel";
        if (!ViaDriver) return;
        lock (ConsoleLock) { _driver!.SetMouseTrackingPreset(MouseTracking); }
    }

    public static void SetTuiScrollSpeedRows(int rows)
    {
        ScrollSpeedRows = Math.Max(1, rows);
        if (!ViaDriver) return;
        lock (ConsoleLock) { _driver!.SetScrollSpeedRows(ScrollSpeedRows); }
    }

    /// <summary>True when the driver should handle this render (active and on the TUI path).</summary>
    private static bool ViaDriver => _tuiActive && _driver is not null && IsTui && !StdioMode;

    // --- streaming relays (called from MuxConsole.WriteStream / Begin/End) ----

    internal static void TuiBeginStream() { if (ViaDriver) lock (ConsoleLock) { _driver!.BeginStream(); } }
    internal static void TuiStreamChunk(string text, bool reasoning = false) { if (ViaDriver) lock (ConsoleLock) { _driver!.StreamChunk(text, reasoning); } }
    internal static void TuiEndStream() { if (ViaDriver) lock (ConsoleLock) { _driver!.EndStream(); } }

    /// <summary>Set/clear the driver's live "thinking/working" line (animated spinner).</summary>
    internal static void TuiSetThinking(string? text) { if (ViaDriver) lock (ConsoleLock) { _driver!.SetThinking(text); } }

    /// <summary>Clear resize/redraw artifacts and repaint the live region (Ctrl+L). No-op outside
    /// the TUI. Safe from the mid-turn key listener thread (serializes on the console lock).</summary>
    internal static void TuiForceRedraw() { if (ViaDriver) lock (ConsoleLock) { _driver!.ForceRedraw(); } }

    /// <summary>Mid-turn wheel scroll routed from the EscapeKeyListener thread: steps the frame
    /// viewport by net wheel notches through the driver's FrameScrollBy path + repaints. Serialized
    /// under ConsoleLock exactly like the streaming writers, so it cannot race a mid-frame present.</summary>
    internal static void TuiWheelScroll(int netWheelDir) { if (ViaDriver) lock (ConsoleLock) { _driver!.WheelScrollBy(netWheelDir); } }

    /// <summary>Toggle the team TaskBoard strip (v0.12.0 M2, Ctrl+T). No-op outside the TUI.</summary>
    internal static void TuiToggleTaskBoard() { if (ViaDriver) lock (ConsoleLock) { _driver!.ToggleTaskBoardRepaint(); } }

    /// <summary>Install (or clear) the board-snapshot provider that feeds the TaskBoard strip.
    /// Set by TeamController when a taskboard team launches; cleared when it ends.</summary>
    internal static void TuiSetTaskBoardProvider(
        Func<(int Total, int Done, int InProgress, int Blocked, int Failed,
            IReadOnlyList<(string Id, string Status, string? Owner, string Subject, int Artifacts)> Rows)?>? provider)
    {
        if (_driver is not null) _driver.TaskBoardProvider = provider;
    }

    /// <summary>Install (or clear) the Agent View 'm' message-log provider (M4 Mailbox). Set by
    /// TeamController when a team launches; cleared when it ends.</summary>
    internal static void TuiSetMessageLogProvider(Func<string, IReadOnlyList<string>>? provider)
    {
        if (_driver is not null) _driver.MessageLogProvider = provider;
    }

    /// <summary>Resize poll tick: detect a terminal size change and force a clean repaint. No-op
    /// outside the TUI. Called from the shared resize-poll timer.</summary>
    internal static void TuiPollResize() { if (ViaDriver) lock (ConsoleLock) { _driver!.PollResize(); } }

    /// <summary>True when the driver is active - used to route the thinking indicator.</summary>
    internal static bool TuiDriverActive => ViaDriver;

    /// <summary>Read a line through the driver's pinned input box. Caller guards with TuiActive.</summary>
    internal static string? TuiReadLine() => _driver?.ReadLine();

    /// <summary>/voice: queue transcript text for insertion into the live compose buffer.
    /// Thread-safe; no-op outside the TUI (voice is TUI-path-only in v1).</summary>
    internal static void InjectComposeText(string text) { if (ViaDriver) _driver!.VoiceInject(text); }

    /// <summary>/voice: request the compose buffer be submitted as if Enter was pressed.
    /// Thread-safe; no-op outside the TUI or when the buffer is empty.</summary>
    internal static void SubmitComposeBuffer() { if (ViaDriver) _driver!.VoiceSubmit(); }

    /// <summary>/voice: nudge the input area to repaint on the next poll tick (state dot).</summary>
    internal static void TuiRepaintSoon() { if (ViaDriver) _driver!.VoiceRepaintSoon(); }

    /// <summary>Commit a single markup line into scrollback via the driver (caller guards with ViaDriver).</summary>
    internal static void CommitToDriver(string markupLine) { if (ViaDriver) lock (ConsoleLock) { _driver!.CommitLine(markupLine); } }

    /// <summary>Commit multiple markup lines into scrollback atomically via the driver.</summary>
    internal static void CommitLinesToDriver(IReadOnlyList<string> markupLines) { if (ViaDriver) lock (ConsoleLock) { _driver!.Commit(markupLines); } }

    /// <summary>Clear the live region before a blocking external prompt; repaint resumes after.</summary>
    // Suspend the live view before a blocking external prompt / mode switch / exit. Serialized
    // through ConsoleLock so it cannot race the ~100ms resize/sub-agent ticker painting concurrently
    // (the driver's stated invariant is that its public methods run under the lock). The lock is
    // reentrant, so callers already holding it are unaffected.
    internal static void TuiSuspend() { if (ViaDriver) lock (ConsoleLock) { _driver!.Suspend(); } }

    /// <summary>Suspend specifically for a bare text prompt. Frame mode replays the newly-committed
    /// command context onto the restored primary buffer before Spectre draws the input line.</summary>
    internal static void TuiSuspendForPrompt() { if (ViaDriver) lock (ConsoleLock) { _driver!.SuspendForPrompt(); } }

    /// <summary>Resume the live view after a blocking external prompt. Frame engine: re-enter the
    /// alternate screen and repaint everything retained while suspended (the suspend-envelope fix
    /// for the sub-prompt shatter). Inline engine: no-op - the next status repaint restores the
    /// footer as before. Serialized under ConsoleLock like TuiSuspend.</summary>
    internal static void TuiResume() { if (ViaDriver) lock (ConsoleLock) { _driver!.Resume(); } }

    /// <summary>
    /// Expand the latest large tool result INLINE (full panel above the footer) without entering
    /// NAV. Safe to call mid-stream from the Esc/Ctrl+E listener thread; no-op outside TUI or when
    /// nothing is expandable. Returns true if a panel was committed.
    /// </summary>
    internal static bool TuiExpandLatestInline()
    {
        if (!ViaDriver) return false;
        // True toggle: if a sub-agent panel is already open, Ctrl+E CLOSES it (whatever it is) and
        // stops - instead of recomputing a possibly-different target and opening that one, which
        // read as "collapse just changed the content". Only when nothing is open do we compute a
        // fresh target below.
        lock (ConsoleLock) { if (_driver!.CollapseOpenSubAgentPanel()) return false; }
        // Ctrl+E quick-open targets, in priority order: (1) the agent the user last FOREGROUNDED
        // via the backslash dashboard (sticky focus, issue #1) when it is still running, else
        // (2) the most-recent still-running sub-agent. This expands its buffered-so-far transcript
        // inline so Ctrl+E works mid-stream, not only after the agent completes. Falls back to the
        // latest finished expandable block (large tool result / committed sub-agent) when none run.
        SubAgentCapture? live = null;
        string? focus = null;
        lock (ConsoleLock) { focus = _driver!.ForegroundAgent; }
        lock (_captureGate)
        {
            if (focus is not null)
                live = _activeCaptures.FindLast(c => string.Equals(c.Agent, focus, StringComparison.Ordinal));
            if (live is null && _activeCaptures.Count > 0) live = _activeCaptures[^1];
        }
        if (live is not null)
        {
            string body = live.Buffer.ToString().Trim();
            if (body.Length > 0)
                lock (ConsoleLock) { return _driver!.ToggleSubAgentExpanded(live.Agent, body); }
        }
        lock (ConsoleLock) { return _driver!.ExpandLatestInline(); }
    }

    /// <summary>
    /// Open the vim NAV (view) overlay on demand - safe to call mid-stream from the
    /// EscapeKeyListener thread (Ctrl+G). Holds the console lock for the whole overlay session,
    /// so any concurrent streaming commit blocks-and-defers (the driver also retains history
    /// while NAV owns the screen); on exit the driver repaints one fresh frame. No-op outside
    /// TUI / when nothing is retained yet. Returns true if the overlay was opened.
    /// </summary>
    internal static bool TuiEnterViewMode()
    {
        if (!ViaDriver) return false;
        lock (ConsoleLock) { return _driver!.EnterViewMode(); }
    }

    /// <summary>
    /// Foreground the inline Agent View dashboard (v0.12.0 M1, the backslash key). Safe to call
    /// mid-stream from the EscapeKeyListener thread. Builds the running-agent snapshot from the
    /// live capture registry and supplies a by-name body provider so Enter can attach the chosen
    /// agent's buffered-so-far transcript through the driver's sub-agent expand path. No-op (false)
    /// outside the TUI or when no agents are running. Holds the console lock for the session, like
    /// the Ctrl+G view overlay, so concurrent commits defer while the dashboard owns the screen.
    /// </summary>
    internal static bool TuiEnterAgentView()
    {
        if (!ViaDriver) return false;
        List<(string Agent, string Status, string Tint)> snapshot;
        Dictionary<string, string> bodies;
        lock (_captureGate)
        {
            if (_activeCaptures.Count == 0) return false;
            // The Agent View lists every LANE (including /hide'd ones, tagged "hidden" in the
            // status) keyed by the unique lane name, so duplicate same-name delegations are
            // individually selectable + attachable.
            snapshot = _activeCaptures
                .Select(c => (c.Lane,
                              c.Hidden ? $"hidden \u00b7 {c.LiveStatus}" : c.LiveStatus,
                              TuiComponents.AgentTint(c.Agent)))
                .ToList();
            bodies = _activeCaptures
                .ToDictionary(c => c.Lane, c => c.Buffer.ToString().Trim(), StringComparer.Ordinal);
        }
        lock (ConsoleLock)
        {
            return _driver!.EnterAgentView(snapshot,
                agent => bodies.TryGetValue(agent, out var b) ? b : null);
        }
    }

    /// <summary>Set/clear the reasoning-effort chip shown in the docked footer.</summary>
    public static void SetTuiEffort(string? effort) { if (ViaDriver) lock (ConsoleLock) { _driver!.SetEffort(effort); } }

    /// <summary>
    /// Register the Shift+Tab mode-cycle callback on the driver (e.g. cycle reasoning effort
    /// and apply it live). The callback returns the new footer-chip label, or null to hide it.
    /// Pass null to clear. No-op when the driver is not active.</summary>
    public static void SetTuiModeCycle(Func<string?>? onCycle)
    {
        if (_driver is not null) _driver.OnModeCycle = onCycle;
    }

    /// <summary>
    /// Set the driver's lane gutter for <paramref name="agent"/>: a per-agent colored bar for
    /// sub-agent / swarm-specialist output, or no gutter (null) for the primary agent. Keeps
    /// dense multi-agent transcripts visually attributable without a view-swap.
    /// </summary>
    private static void ApplyLaneTint(string agent)
    {
        if (_driver is null) return;
        bool isPrimary = string.IsNullOrEmpty(agent)
            || (_primaryAgent is not null && string.Equals(agent, _primaryAgent, StringComparison.OrdinalIgnoreCase))
            || string.Equals(agent, "Orchestrator", StringComparison.OrdinalIgnoreCase);
        _driver.SetLaneTint(isPrimary ? null : TuiComponents.AgentTint(agent));
    }

    // --- render helpers (driver path + inline fallback) ----------------------

    /// <summary>G2/G7 - session header card shown when a TUI interactive loop starts.</summary>
    public static void RenderTuiSessionHeader(string agentName, string model, string provider, int toolCount = 0)
    {
        if (!IsTui) return;
        _primaryAgent = string.IsNullOrWhiteSpace(agentName) ? _primaryAgent : agentName.Trim();
        if (ViaDriver) { lock (ConsoleLock) { _driver!.Commit(TuiComponents.SessionHeader(agentName, model, provider, toolCount)); } return; }
        WithConsole(() =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow($"[{TC.Muted}]agent[/]",    $"[{TC.Agent}]{Esc(agentName)}[/]");
            grid.AddRow($"[{TC.Muted}]model[/]",    $"[{TC.Text}]{Esc(model)}[/]");
            grid.AddRow($"[{TC.Muted}]provider[/]", $"[{TC.Text}]{Esc(provider)}[/]");
            var panel = new Panel(grid)
            {
                Header = new PanelHeader($"[{TC.Accent}] session [/]", Justify.Left),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(HexColor(TC.Border)),
                Padding = new Padding(1, 0)
            };
            AnsiConsole.Write(panel);
        });
    }

    /// <summary>
    /// G7 - context-meter + mode-badge status bar. With the driver active this updates the
    /// pinned footer in place; otherwise it prints an inline status line before the prompt.
    /// </summary>
    public static void RenderTuiStatusBar(uint tokens, uint threshold, bool plan, bool ultra, bool parallelSub, uint cached = 0, bool sub = false, bool giga = false)
    {
        if (!IsTui) return;
        if (TuiActive) { UpdateDockedFooter(tokens, threshold, plan, ultra, parallelSub, cached, sub, giga); return; }
        WithConsole(() =>
        {
            AnsiConsole.MarkupLine(TuiComponents.Footer(tokens, threshold, plan, ultra, parallelSub, sub));
        }, clearIndicator: false);
    }

    /// <summary>G4 - tool-call line with a running glyph.</summary>
    public static void RenderTuiToolCall(string agent, string tool, string? args)
    {
        if (ViaDriver) { lock (ConsoleLock) { ApplyLaneTint(agent); _driver!.BeginToolCall(tool, args); } return; }
        WithConsole(() =>
        {
            string argHint = string.IsNullOrWhiteSpace(args)
                ? ""
                : $"[{TC.Dim}]({Esc(Trunc(CollapseWhitespace(args!), 56))})[/]";
            AnsiConsole.MarkupLine($"  [{TC.Warn}]\u25cf[/] [{TC.Accent}]{Esc(tool)}[/]{argHint}");
        }, clearIndicator: false);
    }

    /// <summary>G4 - collapsed one-line tool result with an ok glyph.</summary>
    public static void RenderTuiToolResultSummary(string agent, string summary)
    {
        if (ViaDriver)
        {
            lock (ConsoleLock) { ApplyLaneTint(agent); _driver!.ResolveMergedToolResult(summary, LooksLikeError(summary)); }
            return;
        }
        WithConsole(() =>
        {
            string clean = Trunc(CollapseWhitespace(summary), 120);
            AnsiConsole.MarkupLine($"    [{TC.Dim}]\u23bf[/] [{TC.Muted}]{Esc(clean)}[/]");
        });
    }

    /// <summary>
    /// G4/G5 - full tool result. Diffs and errors always expand; otherwise compact mode
    /// collapses to a one-liner. Routes through the driver (commit to scrollback) when active.
    /// </summary>
    public static void RenderTuiToolResultPanel(string agent, string tool, string fullResult, bool swarm)
    {
        string text = Common.ExtractMcpText(fullResult);
        bool err = LooksLikeError(text);
        int width = TuiActive ? _driver!.Width : AnsiConsole.Profile.Width;

        // Python REPL tools (repl_shell_exec / check_python_status / send_python_input): build a
        // DISPLAY-ONLY expand body that shows the exact code the model ran ABOVE its output. The code
        // is read from the session (never sent back to the model - it generated the code). The
        // collapsed one-liner is unchanged; opening the card (Ctrl+E / Ctrl+G) reveals code + output,
        // following the normal expand truncation rules.
        string? expandBody = null;
        if (IsPythonReplTool(tool)
            && MuxSwarm.Utils.NativeTools.ReplShellTools.CurrentReplCode() is { Length: > 0 } code)
        {
            string codeBlock = code.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
            expandBody = $"Code:\n{codeBlock}\n\n\u2500\u2500\u2500 output \u2500\u2500\u2500\n{text}";
        }

        if (ViaDriver)
        {
            lock (ConsoleLock)
            {
                ApplyLaneTint(agent);
                // Compact mode (default) collapses BOTH ok and error results to a single merged
                // line - a green dot for success, a red cross + "failed" for errors - so failures
                // read clearly without a heavy bordered panel. The expanded red panel is reserved
                // for /verbose (ToolOutputCompact == false). Diffs always render as their own card.
                if (IsDiffTool(tool) && LooksLikeDiff(text)) { _driver!.CommitDiffCollapsible(tool, text); }
                else if (ToolOutputCompact) { _driver!.ResolveMergedToolResult(text, error: err, expandBody: expandBody); }
                else { _driver!.FlushPendingToolCall(); _driver!.Commit(TuiComponents.ToolResultPanel(tool, expandBody ?? text, err, width, swarm ? 500 : 2000)); }
            }
            return;
        }

        WithConsole(() =>
        {
            if (IsDiffTool(tool) && LooksLikeDiff(text)) { RenderDiffBody(tool, text); return; }
            if (ToolOutputCompact) { RenderCompactResult(text, err); return; }

            int cap = swarm ? 500 : 2000;
            string body = text.Length > cap ? Esc(text[..cap]) + $"\n[{TC.Dim}]\u2026 truncated[/]" : Esc(text);
            string glyph = err ? $"[{TC.Err}]\u2717[/]" : $"[{TC.Ok}]\u2713[/]";
            var panel = new Panel($"[{TC.Text}]{body}[/]")
            {
                Header = new PanelHeader($"{glyph} [{TC.Accent}]{Esc(tool)}[/]", Justify.Left),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(HexColor(err ? TC.Err : TC.Border)),
                Padding = new Padding(2, 0),
                Expand = false
            };
            AnsiConsole.Write(panel);
        });
    }

    /// <summary>G5 - public entry to render a diff.</summary>
    public static void RenderTuiDiff(string title, string diff)
    {
        if (!IsTui) return;
        if (ViaDriver) { lock (ConsoleLock) { _driver!.CommitDiffCollapsible(title, diff); } return; }
        WithConsole(() => RenderDiffBody(title, diff));
    }

    private static void RenderDiffBody(string title, string diff)
    {
        var lines = diff.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw;
            if (line.StartsWith("+++") || line.StartsWith("---"))
                sb.AppendLine($"[{TC.Muted}]{Esc(line)}[/]");
            else if (line.StartsWith("@@"))
                sb.AppendLine($"[{TC.Accent}]{Esc(line)}[/]");
            else if (line.StartsWith("+"))
                sb.AppendLine($"[{TC.DiffAdd}]{Esc(line)}[/]");
            else if (line.StartsWith("-"))
                sb.AppendLine($"[{TC.DiffDel}]{Esc(line)}[/]");
            else
                sb.AppendLine($"[{TC.Dim}]{Esc(line)}[/]");
        }
        var panel = new Panel(sb.ToString().TrimEnd())
        {
            Header = new PanelHeader($"[{TC.Accent}] diff \u00b7 {Esc(Trunc(title, 48))} [/]", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(HexColor(TC.Border)),
            Padding = new Padding(1, 0),
            Expand = false
        };
        AnsiConsole.Write(panel);
    }

    /// <summary>G8 - delegation rendered as a small from -> to tree.</summary>
    public static void RenderTuiDelegation(string fromAgent, string toAgent, string task, int truncLength)
    {
        if (ViaDriver)
        {
            lock (ConsoleLock)
            {
                ApplyLaneTint(fromAgent);
                if (CollapseDelegations)
                {
                    // Collapse the dispatch to one expandable summary row (the full prompt is retained
                    // behind Ctrl+E), matching how sub-agent results already collapse. Keeps dense
                    // parallel fanout scannable instead of printing every full prompt inline.
                    string summary = TuiComponents.DelegationSummary(fromAgent, toAgent, task);
                    _driver!.CommitCollapsed(summary, fromAgent, CollapseWhitespace(task));
                }
                else { _driver!.Commit(TuiComponents.Delegation(fromAgent, toAgent, task, truncLength)); }
            }
            return;
        }
        WithConsole(() =>
        {
            var tree = new Tree($"[{TC.Agent}]{Esc(fromAgent)}[/] [{TC.Dim}]delegates[/]")
            {
                Style = new Style(HexColor(TC.Border))
            };
            var child = tree.AddNode($"[{TC.Accent}]\u2192 {Esc(toAgent)}[/]");
            child.AddNode($"[{TC.Muted}]{Esc(Trunc(CollapseWhitespace(task), truncLength))}[/]");
            AnsiConsole.Write(tree);
        });
    }

    /// <summary>G2/G8 - agent turn header.</summary>
    public static void RenderTuiTurnHeader(string agentName)
    {
        if (ViaDriver) { lock (ConsoleLock) { ApplyLaneTint(agentName); _driver!.Commit(TuiComponents.TurnHeader(agentName, _driver.Width)); } return; }
        WithConsole(() =>
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[{TC.Agent}]\u25b8 {Esc(agentName)}[/]")
                .RuleStyle(new Style(HexColor(TC.Border)))
                .LeftJustified());
        });
    }

    /// <summary>G4 - task-complete line with an ok glyph.</summary>
    public static void RenderTuiTaskComplete(string agent, string summary)
    {
        if (ViaDriver) { lock (ConsoleLock) { ApplyLaneTint(agent); _driver!.Commit(TuiComponents.TaskComplete(agent, summary)); } return; }
        WithConsole(() =>
        {
            AnsiConsole.MarkupLine($"  [{TC.Ok}]\u2714[/] [{TC.Agent}]{Esc(agent)}[/] [{TC.Dim}]completed[/]  [{TC.Muted}]{Esc(Trunc(summary, 120))}[/]");
        });
    }

    /// <summary>
    /// G6 - slash-command palette / preview. With the driver active the palette renders
    /// live beneath the input box as you type "/"; this explicit call commits a one-shot
    /// palette card (used by the inline fallback and the bare-"/" submit path).
    /// </summary>
    public static void RenderTuiSlashPalette(string? filter = null)
    {
        if (!IsTui) return;
        if (ViaDriver)
        {
            lock (ConsoleLock) { _driver!.Commit(TuiComponents.SlashPalette(filter, SlashPaletteEntries)); }
            return;
        }
        WithConsole(() =>
        {
            var f = (filter ?? "").TrimStart('/').Trim().ToLowerInvariant();
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(HexColor(TC.Border)))
                .Title($"[{TC.Accent}] slash commands [/]")
                .AddColumn(new TableColumn($"[{TC.Muted}]command[/]"))
                .AddColumn(new TableColumn($"[{TC.Muted}]description[/]"));

            int shown = 0;
            foreach (var (cmd, desc) in SlashPaletteEntries)
            {
                if (f.Length > 0 && !cmd.ToLowerInvariant().Contains(f) && !desc.ToLowerInvariant().Contains(f))
                    continue;
                table.AddRow($"[{TC.Accent}]{Esc(cmd)}[/]", $"[{TC.Text}]{Esc(desc)}[/]");
                shown++;
            }
            if (shown == 0)
                table.AddRow($"[{TC.Dim}]-[/]", $"[{TC.Dim}]no commands match '{Esc(f)}'[/]");

            AnsiConsole.Write(table);
        });
    }

    /// <summary>
    /// Top-level (repl) slash-command palette - the mode-select commands relevant at the
    /// main menu, distinct from the in-session command set. Rendered inline (the menu is not
    /// driver-active). Mirrors the web app's context-aware command gating.
    /// </summary>
    public static void RenderReplSlashPalette(string? filter = null)
    {
        if (!IsTui) return;
        WithConsole(() =>
        {
            var f = (filter ?? "").TrimStart('/').Trim().ToLowerInvariant();
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(HexColor(TC.Border)))
                .Title($"[{TC.Accent}] commands [/]")
                .AddColumn(new TableColumn($"[{TC.Muted}]command[/]"))
                .AddColumn(new TableColumn($"[{TC.Muted}]description[/]"));
            int shown = 0;
            foreach (var (cmd, desc) in ReplPaletteEntries)
            {
                if (f.Length > 0 && !cmd.ToLowerInvariant().Contains(f) && !desc.ToLowerInvariant().Contains(f))
                    continue;
                table.AddRow($"[{TC.Accent}]{Esc(cmd)}[/]", $"[{TC.Text}]{Esc(desc)}[/]");
                shown++;
            }
            if (shown == 0)
                table.AddRow($"[{TC.Dim}]-[/]", $"[{TC.Dim}]no commands match '{Esc(f)}'[/]");
            AnsiConsole.Write(table);
        });
    }

    /// <summary>
    /// Render the loaded skills as a clean per-skill preview (name + a one-line, width-aware
    /// description) routed through the live-region driver when active, so it reads like the
    /// web app's skill list instead of one giant wrapped panel. Returns false when not on the
    /// TUI driver path so the caller can fall back to its classic panel.
    /// </summary>
    public static bool RenderTuiSkills(IReadOnlyList<(string Name, string Description)> skills)
    {
        if (!ViaDriver) return false;
        int width = _driver!.Width;
        int descBudget = Math.Max(24, width - 6 - skills.Max(s => s.Name.Length) - 3);
        var rows = new List<string> { "", $"  [{TC.Accent}]\u2503[/] [{TC.Accent}]skills[/] [{TC.Dim}]({skills.Count})[/]" };
        int nameW = skills.Max(s => s.Name.Length);
        foreach (var (name, desc) in skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            string oneLine = Tui.TuiMarkup.TruncatePlain(
                System.Text.RegularExpressions.Regex.Replace((desc ?? "").Trim(), @"\s+", " "), descBudget);
            rows.Add($"  [{TC.Agent}]{Esc(name.PadRight(nameW))}[/]  [{TC.Muted}]{Esc(oneLine)}[/]");
        }
        rows.Add("");
        lock (ConsoleLock) { _driver!.Commit(rows); }
        return true;
    }

    private static (string Cmd, string Desc)[] ReplPaletteEntries => Tui.TuiCommands.Repl;

    private static (string Cmd, string Desc)[] SlashPaletteEntries => Tui.TuiCommands.SessionUnified;

    /// <summary>Collapsed result: a status glyph + first informative line + "(+N lines)" hint.
    /// Errors get a red cross and a "failed" tag instead of the dim ok marker.</summary>
    private static void RenderCompactResult(string text, bool error = false)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
                        .Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0) return;
        int pick = error
            ? Array.FindIndex(lines, l =>
                {
                    var t = l.TrimStart();
                    return t.StartsWith("STDERR", StringComparison.OrdinalIgnoreCase)
                        || t.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                        || t.Contains("command not found", StringComparison.OrdinalIgnoreCase);
                })
            : Array.FindIndex(lines, l =>
                l.TrimStart().StartsWith("Command:", StringComparison.OrdinalIgnoreCase));
        if (pick < 0) pick = 0;
        string first = Trunc(CollapseWhitespace(lines[pick]), 110);
        int more = lines.Length - 1;
        string moreHint = more > 0 ? $" [{TC.Dim}](+{more} line{(more == 1 ? "" : "s")})[/]" : "";
        if (error)
        {
            AnsiConsole.MarkupLine($"  [{TC.Err}]\u2717[/] [{TC.Err}]failed[/] [{TC.Muted}]{Esc(first)}[/]{moreHint}");
        }
        else
        {
            AnsiConsole.MarkupLine($"    [{TC.Dim}]\u23bf[/] [{TC.Muted}]{Esc(first)}[/]{moreHint}");
        }
    }

    // --- small helpers -------------------------------------------------------

    private static Color HexColor(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return new Color(r, g, b);
    }

    private static string Trunc(string s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length > max ? s[..max] + "\u2026" : s);

    /// <summary>
    /// Tools whose output is an actual file edit/write patch and may be rendered as a diff card.
    /// Everything else (analyze_image, reads, shell, search, prose) is shown verbatim - free-form
    /// text that merely *starts* lines with -/+/@ must never be mistaken for a diff. The tool gate
    /// is the primary guard; <see cref="LooksLikeDiff"/> is a secondary structural confirmation.
    /// </summary>
    /// <summary>The python REPL tools that all report the SAME running/last job, so their result
    /// cards can echo the code that ran. Async shell jobs (execute_command_async) carry their own
    /// "Command:" line and are not included.</summary>
    private static bool IsPythonReplTool(string? tool)
        => tool is "repl_shell_exec" or "check_python_status" or "send_python_input";

    private static bool IsDiffTool(string? tool)
    {
        if (string.IsNullOrEmpty(tool)) return false;
        var t = tool.ToLowerInvariant();
        // Match on the verb so registry-prefixed names (Filesystem_edit_file, str_replace_editor,
        // apply_patch, write_file, create_file, etc.) are all covered without an exact list.
        return t.Contains("edit_file") || t.Contains("apply_patch") || t.Contains("str_replace")
            || t.Contains("write_file") || t.Contains("create_file") || t.EndsWith("_patch")
            || t == "edit" || t == "patch" || t == "write" || t == "apply_diff";
    }

    private static bool LooksLikeDiff(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        // Require a genuine structural marker. The density fallback was removed: prose from tools
        // like analyze_image (bullet lists, em-dashes, lines starting with `-`/`` ` ``) was tripping
        // it, producing a bogus diff card with a duplicate old|new line-number gutter (issue: wrong
        // content caught as diffs). A real diff always carries one of these.
        if (text.Contains("diff --git ")) return true;                 // git porcelain header
        if (text.Contains("@@ ") && text.Contains(" @@")) return true; // unified hunk header
        if (text.Contains("--- ") && text.Contains("+++ ")) return true; // paired file headers
        return false;
    }

    private static bool LooksLikeError(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        // Scan a generous head window (not just 60 chars): async-shell results lead with a
        // "Job ID:" + "Status:" preamble, so the failure signal ("Status: failed", "(code 1)",
        // "not recognized", etc.) lands well past the first line.
        var head = (text.Length > 400 ? text[..400] : text).ToLowerInvariant();

        // POSITIVE short-circuit FIRST: the async-shell wrapper ALWAYS emits "--- STDOUT ---"
        // and "--- STDERR ---" section headers plus a "Status:" line, even on success. So an
        // explicit success/completion status must NEVER read as an error, regardless of the
        // (always-present, often empty) STDERR header below it.
        if (System.Text.RegularExpressions.Regex.IsMatch(head,
                @"status\s*[:=]\s*(completed|complete|success|succeeded|ok|done|running|finished|exited)"))
            return false;
        // An explicit zero exit code is success too.
        if (System.Text.RegularExpressions.Regex.IsMatch(head, @"\b(?:exit\s*code|code)\s*[:=]?\s*0\b"))
            return false;

        // Now the real failure signals. NOTE: the bare presence of a "--- STDERR ---" header is
        // NOT a failure signal (it is always present) - only an explicit failed/error status, a
        // non-zero exit code, or a known "command not found" string counts.
        if (head.StartsWith("error") || head.Contains("exception") ||
            head.Contains("traceback") || head.Contains("failed:"))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(head, @"status\s*[:=]\s*(failed|error|fault)"))
            return true;
        if (head.Contains("(failed)"))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(head, @"\b(?:exit\s*code|code)\s*[:=]?\s*[1-9]\d*\b"))
            return true;
        if (head.Contains("is not recognized as an internal or external command") ||
            head.Contains("command not found") || head.Contains("no such file or directory"))
            return true;
        return false;
    }
}
