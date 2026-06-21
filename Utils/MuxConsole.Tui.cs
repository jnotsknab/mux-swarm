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
    /// Informative-line threshold above which a collapsed tool result is Ctrl+E-expandable in
    /// the live TUI. Set from console.collapseToolLines; pushed to the driver on activation.
    /// 0 disables the expand affordance. Default 6.
    /// </summary>
    public static int CollapseToolLines { get; set; } = 6;

    /// <summary>
    /// When true (default) a delegated sub-agent's live output is captured and collapsed into a
    /// single expandable transcript line instead of streaming inline (Claude-Code Task style).
    /// The sub-agent's thinking spinner still animates while it works. Set from
    /// console.collapseSubAgents; toggled at runtime by /subagentview (alias /sav). Only affects
    /// the live TUI - stdio/serve always streams (the web app demultiplexes sub-agent streams).
    /// </summary>
    public static bool CollapseSubAgents { get; set; } = true;

    // --- sub-agent output capture (collapse-by-default) ----------------------
    // While a delegated sub-agent runs, its streamed text / reasoning / tool-result summaries are
    // buffered here instead of being committed to the transcript; on completion one collapsed,
    // expandable line is emitted. AsyncLocal so each (possibly concurrent, parallel-swarm) sub-
    // agent flow has its own isolated capture and siblings never cross-contaminate.

    private sealed class SubAgentCapture
    {
        public required string Agent;
        public readonly System.Text.StringBuilder Buffer = new();
        public int ToolCalls;
        public string? Status;   // set from signal_task_complete (success/failure/partial)
    }

    private static readonly AsyncLocal<SubAgentCapture?> _capture = new();

    /// <summary>True when the current async flow is a captured (collapsed) sub-agent.</summary>
    private static bool Capturing => _capture.Value is not null;

    /// <summary>
    /// Begin capturing the calling sub-agent's live output so it collapses to one expandable
    /// line. Returns a scope to dispose when the sub-agent finishes (commits the collapsed line),
    /// or null when capture does not apply (collapse disabled, stdio/serve, or non-TUI) - in
    /// which case the sub-agent streams inline as before. Safe to <c>using</c> the nullable.
    /// </summary>
    public static IDisposable? BeginSubAgentCapture(string agent)
    {
        if (!CollapseSubAgents || StdioMode || !IsTui) return null;
        var cap = new SubAgentCapture { Agent = agent };
        _capture.Value = cap;
        return new CaptureScope(cap);
    }

    /// <summary>Record the sub-agent's completion status (from signal_task_complete) on the
    /// active capture, so the collapsed line can show success/failure. No-op when not capturing.</summary>
    public static void SetCapturedStatus(string? status)
    {
        if (_capture.Value is { } cap && !string.IsNullOrEmpty(status)) cap.Status = status;
    }

    private sealed class CaptureScope : IDisposable
    {
        private readonly SubAgentCapture _cap;
        private bool _done;
        public CaptureScope(SubAgentCapture cap) => _cap = cap;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _capture.Value = null;
            CommitCapturedSubAgent(_cap);
        }
    }

    /// <summary>Append captured text/reasoning to the active sub-agent buffer.</summary>
    private static void CaptureAppend(string text) => _capture.Value?.Buffer.Append(text);

    /// <summary>Record a captured tool-result summary line + bump the tool counter.</summary>
    private static void CaptureToolResult(string summary)
    {
        if (_capture.Value is not { } cap) return;
        cap.ToolCalls++;
        string clean = CollapseWhitespace(summary ?? "");
        if (clean.Length > 0)
            cap.Buffer.Append('\n').Append("[tool] ").Append(clean.Length > 200 ? clean[..200] + "\u2026" : clean);
    }

    /// <summary>
    /// Emit the collapsed, expandable summary line for a finished sub-agent capture. The full
    /// buffered transcript is retained behind the line so Ctrl+O / NAV can expand it in place,
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
                // Retain the full transcript expandable when there is anything to expand; otherwise
                // commit a bare collapsed line (an empty sub-agent turn).
                if (body.Length > 0)
                    _driver!.CommitCollapsed(collapsed, cap.Agent, body);
                else
                    _driver!.CommitLine(collapsed);
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
    private static bool _fPlan, _fUltra, _fPsub;

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
                    _driver = new TuiDriver();
                    _tuiActive = true;
                    InstallTeardownHook_NoLock();
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
                _driver.SetFooter(_fTokens, _fThreshold, _fPlan, _fUltra, _fPsub);
            }
            catch
            {
                _tuiActive = false;
                _driver = null;
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
            }
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
            };
            Console.CancelKeyPress += (_, _) =>
            {
                try { _driver?.Shutdown(); } catch { /* ignore */ }
            };
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Update + repaint the docked footer content (context meter + mode badges). Caches the
    /// values so subsequent commits keep the footer current. No-op when the driver is not
    /// active.
    /// </summary>
    public static void UpdateDockedFooter(uint tokens, uint threshold, bool plan, bool ultra, bool parallelSub, uint cached = 0)
    {
        _fTokens = tokens; _fThreshold = threshold; _fPlan = plan; _fUltra = ultra; _fPsub = parallelSub; _fCached = cached;
        if (!TuiActive) return;
        lock (ConsoleLock) { _driver!.SetFooter(tokens, threshold, plan, ultra, parallelSub, cached); }
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

    /// <summary>True when the driver should handle this render (active and on the TUI path).</summary>
    private static bool ViaDriver => _tuiActive && _driver is not null && IsTui && !StdioMode;

    // --- streaming relays (called from MuxConsole.WriteStream / Begin/End) ----

    internal static void TuiBeginStream() { if (ViaDriver) lock (ConsoleLock) { _driver!.BeginStream(); } }
    internal static void TuiStreamChunk(string text) { if (ViaDriver) lock (ConsoleLock) { _driver!.StreamChunk(text); } }
    internal static void TuiEndStream() { if (ViaDriver) lock (ConsoleLock) { _driver!.EndStream(); } }

    /// <summary>Set/clear the driver's live "thinking/working" line (animated spinner).</summary>
    internal static void TuiSetThinking(string? text) { if (ViaDriver) lock (ConsoleLock) { _driver!.SetThinking(text); } }

    /// <summary>True when the driver is active - used to route the thinking indicator.</summary>
    internal static bool TuiDriverActive => ViaDriver;

    /// <summary>Read a line through the driver's pinned input box. Caller guards with TuiActive.</summary>
    internal static string? TuiReadLine() => _driver?.ReadLine();

    /// <summary>Commit a single markup line into scrollback via the driver (caller guards with ViaDriver).</summary>
    internal static void CommitToDriver(string markupLine) { if (ViaDriver) lock (ConsoleLock) { _driver!.CommitLine(markupLine); } }

    /// <summary>Commit multiple markup lines into scrollback atomically via the driver.</summary>
    internal static void CommitLinesToDriver(IReadOnlyList<string> markupLines) { if (ViaDriver) lock (ConsoleLock) { _driver!.Commit(markupLines); } }

    /// <summary>Clear the live region before a blocking external prompt; repaint resumes after.</summary>
    internal static void TuiSuspend() { if (ViaDriver) _driver!.Suspend(); }

    /// <summary>
    /// Expand the latest large tool result INLINE (full panel above the footer) without entering
    /// NAV. Safe to call mid-stream from the Esc/Ctrl+E listener thread; no-op outside TUI or when
    /// nothing is expandable. Returns true if a panel was committed.
    /// </summary>
    internal static bool TuiExpandLatestInline()
    {
        if (!ViaDriver) return false;
        lock (ConsoleLock) { return _driver!.ExpandLatestInline(); }
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
    public static void RenderTuiStatusBar(uint tokens, uint threshold, bool plan, bool ultra, bool parallelSub, uint cached = 0)
    {
        if (!IsTui) return;
        if (TuiActive) { UpdateDockedFooter(tokens, threshold, plan, ultra, parallelSub, cached); return; }
        WithConsole(() =>
        {
            AnsiConsole.MarkupLine(TuiComponents.Footer(tokens, threshold, plan, ultra, parallelSub));
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

        if (ViaDriver)
        {
            lock (ConsoleLock)
            {
                ApplyLaneTint(agent);
                // Compact mode (default) collapses BOTH ok and error results to a single merged
                // line - a green dot for success, a red cross + "failed" for errors - so failures
                // read clearly without a heavy bordered panel. The expanded red panel is reserved
                // for /verbose (ToolOutputCompact == false). Diffs always render as their own card.
                if (LooksLikeDiff(text)) { _driver!.FlushPendingToolCall(); _driver!.Commit(TuiComponents.Diff(tool, text, width)); }
                else if (ToolOutputCompact) { _driver!.ResolveMergedToolResult(text, error: err); }
                else { _driver!.FlushPendingToolCall(); _driver!.Commit(TuiComponents.ToolResultPanel(tool, text, err, width, swarm ? 500 : 2000)); }
            }
            return;
        }

        WithConsole(() =>
        {
            if (LooksLikeDiff(text)) { RenderDiffBody(tool, text); return; }
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
        if (ViaDriver) { lock (ConsoleLock) { _driver!.Commit(TuiComponents.Diff(title, diff, _driver.Width)); } return; }
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
        if (ViaDriver) { lock (ConsoleLock) { ApplyLaneTint(fromAgent); _driver!.Commit(TuiComponents.Delegation(fromAgent, toAgent, task, truncLength)); } return; }
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

    private static (string Cmd, string Desc)[] SlashPaletteEntries => Tui.TuiCommands.Session;

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

    private static bool LooksLikeDiff(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        // git porcelain header (from shell `git diff` / `git apply` / `patch` output).
        if (text.Contains("diff --git ")) return true;
        // unified-diff hunk header.
        if (text.Contains("@@ ") && text.Contains("@@")) return true;
        // unified-diff file headers (the edit-tool / `diff -u` form).
        if (text.Contains("--- ") && text.Contains("+++ ")) return true;
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
