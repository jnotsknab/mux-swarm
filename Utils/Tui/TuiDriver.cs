using System.Text;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// The interactive live-region TUI driver (v0.11.0, Workstream G Option B). Owns a single
/// <see cref="LiveRegion"/> and a <see cref="LineEditor"/>, and is the only object that
/// talks to the real console for the TUI. It gives the renderer its Claude-Code feel:
/// a pinned footer (context meter + mode badges) and an input box at the bottom, with the
/// transcript flowing up into the terminal's NATIVE scrollback above it - no DECSTBM scroll
/// region and no alternate screen buffer, so scrollback survives and nothing is stranded.
///
/// Threading: all public methods must be called under MuxConsole.ConsoleLock (the existing
/// global serialization point), matching how every other MuxConsole writer behaves.
///
/// Teardown is guaranteed: <see cref="Shutdown"/> resets the scroll region (none),
/// re-shows the cursor, restores Ctrl-C handling, and clears the live region. It is wired
/// to AppDomain.ProcessExit / Console.CancelKeyPress by the installer.
/// </summary>
internal sealed class TuiDriver
{
    private readonly ITuiTerminal _term;
    private readonly LiveRegion _region;
    private readonly LineEditor _editor = new();

    // footer state
    private uint _tokens, _threshold, _cached;
    // Static per-session overhead (system prompt + serialized tool schemas), shown as a
    // breakdown in the footer so a fresh session\u0027s baseline context is explained.
    private uint _sysTokens, _toolTokens;
    private bool _plan, _ultra, _psub, _sub;
    private string? _effort;   // reasoning-effort chip (low/med/high), null = hidden
    private string? _sessionId; // active session id badge, null = hidden

    // streaming state - partial (un-newlined) tail shown live above the footer
    private readonly StringBuilder _streamTail = new();
    private bool _streaming;
    // True when the current stream tail is reasoning content (rendered grey+italic to distinguish
    // it from the final answer). Flushed/reset on a type switch so reasoning and answer never blend.
    private bool _streamReasoning;

    // Buffer of contiguous GFM table source rows seen during streaming. Markdown tables need
    // the whole block to align columns, but the stream commits one line at a time - so table
    // rows are accumulated here and rendered as a single aligned block when a non-table line
    // (or the stream end) breaks the run. See FlushTableBuffer.
    private readonly List<string> _tableBuf = new();

    // Buffer for a fenced code block (``` ... ```). While inside a fence, raw lines are kept
    // verbatim and rendered as a code panel on close; this is the code analog of _tableBuf.
    private readonly List<string> _codeBuf = new();
    private bool _inFence;
    private string _fenceLang = "";

    // True after a tool call/result block commits, so the NEXT agent text block gets a single
    // blank-line separator above it (visual breathing room between a tool block and prose).
    // Consumed (and reset) the next time streamed assistant text is committed.
    private bool _pendingGap;

    // thinking/working spinner state - a single live line shown above the rule while the
    // agent is reasoning or calling tools (replaces the inline \r spinner, which would
    // fight the live region). Animated by the caller via SetThinking.
    private string? _thinkingText;
    private int _thinkFrame;   // spinner animation cell, advanced on each SetThinking tick

    // Consolidated active-sub-agent activity: one entry per running collapsed sub-agent, shown
    // as a compact stacked panel above the footer while delegation runs. Pushed by MuxConsole's
    // single shared ticker (no per-agent spinner), so concurrent parallel agents never flicker.
    private IReadOnlyList<(string Agent, string Status, string Tint)> _subAgents = System.Array.Empty<(string, string, string)>();
    private int _subAgentFrame;

    // Mid-turn EXPAND slot (generic). Any expandable block - a running sub-agent's buffered
    // transcript OR a finished large tool result - can be toggled open with Ctrl+E into a single
    // bounded panel rendered INSIDE the repaintable live region (see BuildLiveFrame), never
    // committed to scrollback. Toggling open/closed and live stream updates repaint in place with
    // zero append spam (the old approach appended a fresh panel per keypress, flooding scrollback).
    // Only one block is expanded at a time; opening another switches the slot. A second Ctrl+E on
    // the same block (or stream end / turn end) collapses it.
    private enum ExpandKind { None, SubAgent, ToolResult }
    private ExpandKind _expandKind = ExpandKind.None;
    private string _expandKey = "";       // identity for toggle-match (agent name, or "tool#<idx>")
    private string _expandTitle = "";     // panel header label
    private string _expandBody = "";      // current body (refreshed live for sub-agents)
    private string _expandTint = "";      // border tint
    private bool _expandError;            // render with error styling
    private bool _expandAnchorTail;       // true: keep newest (sub-agent); false: keep first (tool)
    private bool Expanded => _expandKind != ExpandKind.None && _expandBody.Length > 0;

    // pending tool call awaiting its result for a one-line merge. Shown live (running glyph)
    // above the footer while the tool runs, then committed as a single merged line when the
    // result lands. Flushed as its own committed line if any other content commits first.
    private (string Tool, string? Args)? _pendingTool;

    // Active sub-agent lane tint for gutter attribution on committed transcript lines. Null
    // for the primary agent (no gutter); set to a per-agent color while a sub-agent/specialist
    // is producing output so its lines carry a colored bar. Owned by the caller via SetLaneTint.
    private string? _laneTint;

    /// <summary>Informative-line threshold above which a collapsed result is Ctrl+E-expandable.
    /// Owned by the caller (config-driven); 0 disables the affordance entirely.</summary>
    private int _collapseToolLines = 6;
    public void SetCollapseThreshold(int lines) => _collapseToolLines = Math.Max(0, lines);

    /// <summary>Blank lines emitted below a tool/delegation block before following agent prose
    /// (docked-below separator). 0 = tight. Owned by the caller (console.delegationSpacing).</summary>
    private int _blockGap = 1;
    public void SetBlockGap(int lines) => _blockGap = Math.Max(0, lines);

    // In-memory transcript retained so vim NAV mode can scroll back through committed history.
    // The live region writes finished lines straight into native scrollback (which we cannot
    // read back), so we mirror them here as ENTRIES. Most entries are a single plain markup
    // line; a tool-result entry additionally carries the data to render its full panel, so the
    // NAV cursor can expand/collapse it in place. Capped to avoid unbounded growth.
    private const int TranscriptCap = 5000;

    /// <summary>One retained transcript unit: collapsed markup line(s) plus optional expand data.</summary>
    private sealed class Entry
    {
        public required string Collapsed;                          // the normally-shown markup line
        public (string Tool, string Text, bool Error)? Expandable; // non-null => Ctrl+E expandable
        public bool Expanded;                                      // current toggle state in NAV
    }

    private readonly List<Entry> _transcript = new();

    /// <summary>Mirror committed plain markup lines into the transcript (one entry per line).</summary>
    private void Retain(IReadOnlyList<string> markupLines)
    {
        foreach (var l in markupLines) _transcript.Add(new Entry { Collapsed = l });
        TrimTranscript();
    }

    /// <summary>Mirror a single expandable tool-result line, retaining its full-panel data.</summary>
    private void RetainExpandable(string collapsedLine, string tool, string text, bool error)
    {
        _transcript.Add(new Entry { Collapsed = collapsedLine, Expandable = (tool, text, error) });
        TrimTranscript();
    }

    private void TrimTranscript()
    {
        int over = _transcript.Count - TranscriptCap;
        if (over > 0) _transcript.RemoveRange(0, over);
    }

    /// <summary>Retain + commit lines above the region (mirrors history for vim NAV scrollback).</summary>
    private void CommitMirrored(IReadOnlyList<string> lines)
    {
        Retain(lines);
        // While the NAV overlay owns the screen (possibly opened mid-turn), retain history but
        // do NOT physically write - NAV exit issues a full repaint that reflects everything.
        if (_navActive) return;
        _region.CommitAbove(lines, BuildLiveFrame(Width));
    }

    // input state - true only inside ReadLine's raw-mode loop
    private bool _inInput;
    private bool _shuttingDown;

    // True while the NAV (vim view) overlay owns the screen. NAV can be opened mid-turn (via
    // the EscapeKeyListener's Ctrl+G), so the streaming/commit path must NOT physically write
    // to the live region while this is set - it keeps mutating the data model + retaining into
    // _transcript, but defers the screen paint until NAV exits and issues one full Repaint.
    // Re-entrancy guard too: a second open request while NAV is active is a no-op.
    private volatile bool _navActive;
    // Saved NAV cursor (entry index, not display-line) so re-opening NAV restores roughly where
    // the user left off instead of snapping to the bottom. -1 = none yet (open at the latest).
    private int _navSavedEntry = -1;

    // Highlighted candidate index in the open autocomplete preview (palette / skill / session /
    // @file), or -1 when nothing is highlighted. Arrow keys move it while a preview is open;
    // Tab/Enter accept the highlighted row. Reset whenever the filter text changes.
    private int _paletteSel = -1;

    /// <summary>Palette entries shown as-you-type. Defaults to the in-session set; the
    /// caller may swap in the top-level (repl) set via <see cref="SetPaletteScope"/>.
    /// Both are filtered views of <see cref="TuiCommands.All"/> (the single canonical list
    /// kept in sync with App.cs's command switch + Help.cs), so no command is ever missing
    /// from the preview while a different one works.</summary>
    private (string Cmd, string Desc)[] _paletteEntries = TuiCommands.Session;

    /// <summary>Loaded skills catalog for the live "/skill" autocomplete preview.</summary>
    private IReadOnlyList<(string Name, string Desc)> _skills = Array.Empty<(string, string)>();

    /// <summary>Switch the as-you-type palette between session and top-level command sets.</summary>
    public void SetPaletteScope(bool topLevel) => _paletteEntries = topLevel ? TuiCommands.Repl : TuiCommands.Session;

    /// <summary>Set the skills catalog backing the live "/skill" autocomplete preview.</summary>
    public void SetSkillsCatalog(IReadOnlyList<(string Name, string Desc)> skills)
        => _skills = skills ?? Array.Empty<(string, string)>();

    /// <summary>Resumable sessions catalog for the live "/resume" autocomplete preview.</summary>
    private IReadOnlyList<(string Id, string Preview)> _sessions = Array.Empty<(string, string)>();

    /// <summary>Set the sessions catalog backing the live "/resume" autocomplete preview.</summary>
    public void SetSessionsCatalog(IReadOnlyList<(string Id, string Preview)> sessions)
        => _sessions = sessions ?? Array.Empty<(string, string)>();

    /// <summary>Relative-path file index backing the live "@" fuzzy file picker.</summary>
    private IReadOnlyList<string> _files = Array.Empty<string>();

    /// <summary>Set the file catalog backing the live "@" fuzzy file-reference picker.</summary>
    public void SetFilesCatalog(IReadOnlyList<string> files)
        => _files = files ?? Array.Empty<string>();

    /// <summary>Tool catalog (name + description) backing the live "/tools" scrollable list.</summary>
    private IReadOnlyList<(string Name, string Desc)> _tools = Array.Empty<(string, string)>();

    /// <summary>Set the tools catalog backing the live "/tools" palette (expandable badge view).</summary>
    public void SetToolsCatalog(IReadOnlyList<(string Name, string Desc)> tools)
        => _tools = tools ?? Array.Empty<(string, string)>();

    /// <summary>When true, the "@" picker is indexing the mux install dir (not a real project),
    /// so the preview surfaces a "--workspace" hint. Set by the console wiring at startup.</summary>
    private bool _filesAreInstallDir;
    public void SetFilesInstallDirHint(bool isInstallDir) => _filesAreInstallDir = isInstallDir;

    /// <summary>
    /// Optional callback invoked when the user presses Shift+Tab inside the input box. It
    /// should perform the mode cycle (e.g. advance reasoning effort + apply it live) and
    /// return the new short label to show in the footer chip (or null to hide it). The
    /// driver owns only the chip's display; the caller owns the actual mode state. When null,
    /// Shift+Tab is ignored. Set by the session loop; cleared at the top-level menu.</summary>
    public Func<string?>? OnModeCycle { get; set; }

    public TuiDriver(ITuiTerminal? term = null)
    {
        _term = term ?? new ConsoleTuiTerminal();
        _region = new LiveRegion(_term);
    }

    public int Width => Math.Max(20, _term.Width);

    /// <summary>Visible terminal height (rows), floored so panel-bounding math stays sane on
    /// tiny/again-unavailable terminals.</summary>
    public int Height => Math.Max(10, _term.Height);

    /// <summary>Update the context meter / mode badges and repaint the live region.</summary>
    public void SetFooter(uint tokens, uint threshold, bool plan, bool ultra, bool psub, bool sub = false, uint cached = 0)
    {
        _tokens = tokens; _threshold = threshold; _plan = plan; _ultra = ultra; _psub = psub; _sub = sub; _cached = cached;
        Repaint();
    }

    /// <summary>Set the static context-overhead breakdown (system prompt + tool schema tokens)
    /// shown as "sys"/"tools" chips in the footer. Computed once when the agent is built.</summary>
    public void SetTokenBreakdown(uint sysTokens, uint toolTokens)
    {
        if (_sysTokens == sysTokens && _toolTokens == toolTokens) return;
        _sysTokens = sysTokens; _toolTokens = toolTokens;
        Repaint();
    }

    /// <summary>Set (or clear, with null) the active-session id badge shown in the footer.</summary>
    public void SetSessionId(string? sessionId)
    {
        var s = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        if (s == _sessionId) return;
        _sessionId = s;
        Repaint();
    }

    /// <summary>Set (or clear, with null) the reasoning-effort chip shown in the footer.</summary>
    public void SetEffort(string? effort)
    {
        var e = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        if (e == _effort) return;
        _effort = e;
        Repaint();
    }

    /// <summary>
    /// Set (or clear, with null) the active sub-agent lane tint. While set, committed
    /// transcript lines are prefixed with a colored gutter bar so a dense multi-agent run is
    /// visually attributable at a glance. Cleared (null) for the primary agent.
    /// </summary>
    public void SetLaneTint(string? tintHex)
        => _laneTint = string.IsNullOrWhiteSpace(tintHex) ? null : tintHex.Trim();

    /// <summary>Apply the active lane gutter to a batch of built markup lines (no-op when unset).</summary>
    private IReadOnlyList<string> Lane(IReadOnlyList<string> lines)
    {
        if (_laneTint is null || lines.Count == 0) return lines;
        var outp = new List<string>(lines.Count);
        foreach (var l in lines) outp.Add(TuiComponents.Gutter(l, _laneTint));
        return outp;
    }

    /// <summary>Commit finished transcript markup lines into native scrollback above the region.</summary>
    public void Commit(IReadOnlyList<string> markupLines)
    {
        if (markupLines.Count == 0) { Repaint(); return; }
        FlushTableBuffer();
        FlushPendingToolCall();
        _thinkingText = null;
        // The retained transcript keeps any expandable tool-result entries armed for Ctrl+E /
        // NAV across subsequent commits, so nothing is cleared here.
        CommitMirrored(Lane(markupLines));
    }

    /// <summary>Commit a single markup line above the region.</summary>
    public void CommitLine(string markupLine) => Commit(new[] { markupLine });

    /// <summary>
    /// Commit a collapsed sub-agent summary line that retains its full buffered transcript as
    /// expandable data, so Ctrl+E / NAV can open it in place (same mechanism as a large tool
    /// result). The collapsed line is shown in live scrollback; the transcript lives in memory.
    /// </summary>
    public void CommitCollapsed(string collapsedLine, string agent, string fullTranscript)
    {
        // Caller (MuxConsole.CommitCapturedSubAgent) holds ConsoleLock, matching the other
        // driver commit methods which are not internally synchronized.
        FlushPendingToolCall();
        RetainExpandable(collapsedLine, agent, fullTranscript, error: false);
        if (!_navActive) _region.CommitAbove(new[] { collapsedLine }, BuildLiveFrame(Width));
        _pendingGap = true;
    }

    // --- tool call/result merge ---------------------------------------------

    /// <summary>
    /// Begin a tool call that may merge with its result into a single line. The call is held
    /// (shown live above the footer with a running glyph) until <see cref="ResolveMergedToolResult"/>
    /// or <see cref="FlushPendingToolCall"/> is invoked. Any previously-pending call is flushed
    /// first so calls never silently drop.
    /// </summary>
    public void BeginToolCall(string tool, string? args)
    {
        FlushPendingToolCall();
        _pendingTool = (tool, args);
        _thinkingText = null;
        Repaint();
    }

    /// <summary>
    /// Resolve the pending tool call by committing ONE merged "call + compact result" line.
    /// No-op fallback: if there is no pending call, commits the merged line as-is so the
    /// result is never lost.
    /// </summary>
    public void ResolveMergedToolResult(string resultText, bool error = false)
    {
        var (tool, args) = _pendingTool ?? ("", null);
        _pendingTool = null;
        _thinkingText = null;
        // "Large" results (informative-line count above the configured threshold) become
        // Ctrl+E-expandable: the merged line advertises the affordance and we retain the full
        // text in memory so Ctrl+E can re-commit it as a full panel below.
        int infoLines = (resultText ?? "").Replace("\r\n", "\n").Split('\n').Count(l => l.Trim().Length > 0);
        bool expandable = _collapseToolLines > 0 && infoLines > _collapseToolLines;
        string toolName = string.IsNullOrEmpty(tool) ? "tool" : tool;
        var merged = Lane(TuiComponents.ToolCallResultMerged(tool, args, resultText, error, expandable));
        if (expandable && merged.Count > 0)
        {
            // Retain the collapsed line WITH its full-panel data so the NAV cursor can toggle
            // it open/closed in place; commit only the collapsed line to live scrollback.
            RetainExpandable(merged[0], toolName, resultText ?? "", error);
            for (int k = 1; k < merged.Count; k++) _transcript.Add(new Entry { Collapsed = merged[k] });
            TrimTranscript();
            if (!_navActive) _region.CommitAbove(merged, BuildLiveFrame(Width));
        }
        else
        {
            CommitMirrored(merged);
        }
        _pendingGap = true;
    }

    /// <summary>
    /// Expand the most-recent collapsed (Ctrl+E-eligible) tool result into a full bordered
    /// panel committed below the current transcript. No-op when nothing is expandable. The
    /// retained block is cleared after expansion so repeated presses don't duplicate it.
    /// </summary>
    /// <summary>
    /// Open the vim NAV (view) overlay on demand - safe to call MID-TURN from the
    /// EscapeKeyListener thread (Ctrl+G). Opens focused on the latest expandable block if one
    /// exists, else the plain transcript. While open, the concurrent streaming/commit path
    /// retains history but defers screen writes (see <see cref="_navActive"/>); NAV exit issues
    /// one full repaint. No-op when the transcript is empty or NAV is already active (re-entrancy
    /// guard). Returns true if the overlay was opened. The caller holds the console lock.
    /// </summary>
    public bool EnterViewMode()
    {
        if (_navActive || _transcript.Count == 0) return false;
        int idx = -1;
        for (int i = _transcript.Count - 1; i >= 0; i--)
            if (_transcript[i].Expandable is not null) { idx = i; break; }
        if (idx >= 0) _transcript[idx].Expanded = true;
        EnterNavMode(focusEntry: idx);
        return true;
    }

    public bool ExpandLastBlock()
    {
        // Find the most-recent expandable transcript entry and open NAV positioned on it,
        // pre-expanded. Expansion is a reversible in-overlay toggle (not a one-way commit),
        // so the user can collapse it again or scroll to other blocks.
        int idx = -1;
        for (int i = _transcript.Count - 1; i >= 0; i--)
            if (_transcript[i].Expandable is not null) { idx = i; break; }
        if (idx < 0) return false;
        _transcript[idx].Expanded = true;
        EnterNavMode(focusEntry: idx);
        return true;
    }

    /// <summary>
    /// TOGGLE the most-recent large tool result into the bounded live-expand slot (Ctrl+E mid-turn
    /// or at the prompt). The full result renders as a bounded panel IN the repaintable live region
    /// - never appended to scrollback - so repeated presses flip it open/closed instead of stacking
    /// duplicate panels (the prior append-based ExpandLatestInline was the source of the spam bug).
    /// Head-anchored: shows the start of the result with a "+N more (ctrl+g for full)" footer, since
    /// a static result is read top-down (Ctrl+G / NAV still opens the complete untruncated block).
    /// Returns true if a panel is now shown. Caller holds the console lock.
    /// </summary>
    public bool ExpandLatestInline()
    {
        int idx = -1;
        for (int i = _transcript.Count - 1; i >= 0; i--)
            if (_transcript[i].Expandable is not null) { idx = i; break; }
        if (idx < 0) return false;
        var blk = _transcript[idx].Expandable!.Value;
        string key = $"tool#{idx}";
        // Same block already expanded -> collapse (reversible toggle, no spam).
        if (_expandKind == ExpandKind.ToolResult && string.Equals(_expandKey, key, StringComparison.Ordinal))
            { ClearExpanded(); return false; }
        OpenExpand(ExpandKind.ToolResult, key, blk.Tool, blk.Text ?? "",
            TuiComponents.Border, blk.Error, anchorTail: false);
        return Expanded;
    }

    /// <summary>Populate + open the generic live-expand slot, then repaint. Caller holds the lock.</summary>
    private void OpenExpand(ExpandKind kind, string key, string title, string body, string tint, bool error, bool anchorTail)
    {
        _expandKind = kind; _expandKey = key; _expandTitle = title; _expandBody = body;
        _expandTint = tint; _expandError = error; _expandAnchorTail = anchorTail;
        Repaint();
    }

    /// <summary>Collapse whatever is in the live-expand slot. No-op when empty. Holds the lock.</summary>
    private void ClearExpanded()
    {
        if (_expandKind == ExpandKind.None) return;
        _expandKind = ExpandKind.None; _expandKey = ""; _expandTitle = ""; _expandBody = "";
        _expandTint = ""; _expandError = false; _expandAnchorTail = false;
        Repaint();
    }

    /// <summary>
    /// TOGGLE the mid-turn expansion of a still-running sub-agent's buffered transcript. The
    /// expanded view is a bounded panel rendered INSIDE the repaintable live region (see
    /// BuildLiveFrame), not committed to scrollback - so pressing Ctrl+E repeatedly just flips
    /// it open/closed and live stream updates repaint in place, with zero append spam (the prior
    /// inline-commit approach appended a fresh panel per keypress, flooding scrollback). Passing a
    /// different agent while already expanded switches the target rather than collapsing. Returns
    /// true if a panel is now shown. Caller holds the console lock.
    /// </summary>
    public bool ToggleSubAgentExpanded(string agent, string body)
    {
        string key = $"sub:{agent}";
        // Same agent already open -> collapse (reversible toggle, no spam).
        if (_expandKind == ExpandKind.SubAgent && string.Equals(_expandKey, key, StringComparison.Ordinal))
            { ClearExpanded(); return false; }
        if (string.IsNullOrEmpty(body)) return false;
        OpenExpand(ExpandKind.SubAgent, key, agent, body, TuiComponents.AgentTint(agent), error: false, anchorTail: true);
        return Expanded;
    }

    /// <summary>Refresh the buffered body of the currently-expanded sub-agent so the bounded live
    /// panel grows in place as the agent streams. No-op when nothing is expanded or the update is
    /// for a different agent. Repaints only when the body actually changed (avoids ticker thrash).
    /// Caller holds the console lock.</summary>
    public void UpdateSubAgentExpandedBody(string agent, string body)
    {
        if (_expandKind != ExpandKind.SubAgent || !string.Equals(_expandKey, $"sub:{agent}", StringComparison.Ordinal)) return;
        if (string.Equals(_expandBody, body, StringComparison.Ordinal)) return;
        _expandBody = body;
        Repaint();
    }

    /// <summary>Collapse any active sub-agent expansion (e.g. when the agent finishes and its
    /// collapsed line commits). Leaves a tool-result expansion untouched. Caller holds the lock.</summary>
    public void ClearSubAgentExpanded()
    {
        if (_expandKind == ExpandKind.SubAgent) ClearExpanded();
    }

    /// <summary>True if <paramref name="agent"/> is the sub-agent currently expanded in the live
    /// region. Lets the completion path keep a user-opened panel open through finish (instead of
    /// snapping it collapsed) while still committing the expandable collapsed line. Caller holds
    /// the console lock.</summary>
    public bool IsSubAgentExpanded(string agent) =>
        _expandKind == ExpandKind.SubAgent && string.Equals(_expandKey, $"sub:{agent}", StringComparison.Ordinal);

    /// <summary>
    /// Flush a pending tool call as its own committed line (used when the result will render
    /// as a separate block - diff/error/expanded - or when other content commits first).
    /// Idempotent: no-op when nothing is pending.
    /// </summary>
    public void FlushPendingToolCall()
    {
        if (_pendingTool is not ( {} pend)) return;
        _pendingTool = null;
        CommitMirrored(Lane(TuiComponents.ToolCall(pend.Tool, pend.Args)));
        _pendingGap = true;
    }

    // --- streaming -----------------------------------------------------------

    public void BeginStream() { FlushPendingToolCall(); FlushTableBuffer(); if (_inFence) FlushCodeBuffer(); _streaming = true; _thinkingText = null; _streamTail.Clear(); _streamReasoning = false; Repaint(); }

    /// <summary>
    /// Feed a chunk of streamed assistant text. Complete lines (split on '\n') are committed
    /// into scrollback; the trailing partial line is shown live just above the footer so the
    /// user watches it type in real time.
    /// </summary>
    public void StreamChunk(string text, bool reasoning = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Switching between reasoning and answer text: flush whatever partial tail we have under
        // the OLD style first, so the two never share a rendered line.
        if (reasoning != _streamReasoning && _streamTail.Length > 0)
        {
            var pending = _streamTail.ToString();
            _streamTail.Clear();
            CommitStreamLine(pending);
        }
        _streamReasoning = reasoning;
        _streamTail.Append(text);
        FlushCompleteStreamLines();
        Repaint();
    }

    /// <summary>
    /// Set (or clear, with null) the live "thinking/working" line shown above the rule.
    /// Each call advances the spinner frame so repeated calls animate. Cleared automatically
    /// when streaming begins or the next transcript content is committed.
    /// </summary>
    public void SetThinking(string? text)
    {
        if (text is null) { if (_thinkingText is null) return; _thinkingText = null; Repaint(); return; }
        var t = text.Trim();
        // Advance the spinner each tick so the indicator animates even when the working text
        // is unchanged (the caller pings SetThinking on a timer).
        _thinkFrame++;
        _thinkingText = t;
        Repaint();
    }

    /// <summary>
    /// Set the consolidated active-sub-agent activity snapshot (one entry per running collapsed
    /// sub-agent). Rendered as a compact stacked panel above the footer; <paramref name="frame"/>
    /// drives the shared spinner. An empty list clears the panel. Repaints only when the snapshot
    /// actually changed, so the ticker does not thrash the region when nothing moved.
    /// </summary>
    public void SetSubAgentActivity(IReadOnlyList<(string Agent, string Status, string Tint)> agents, int frame)
    {
        bool changed = frame != _subAgentFrame || agents.Count != _subAgents.Count;
        if (!changed)
            for (int i = 0; i < agents.Count; i++)
                if (agents[i].Agent != _subAgents[i].Agent || agents[i].Status != _subAgents[i].Status)
                    { changed = true; break; }
        if (!changed) return;
        _subAgents = agents;
        _subAgentFrame = frame;
        Repaint();
    }

    public void EndStream()
    {
        bool hadTail = _streamTail.Length > 0;
        string tail = _streamTail.ToString();
        _streamTail.Clear();
        _streaming = false;
        if (hadTail)
            CommitStreamLine(tail);
        // An unterminated fence (model omitted the closing ```) is flushed so code still renders.
        if (_inFence) FlushCodeBuffer();
        // Any table rows buffered up to the very end of the turn are rendered now.
        FlushTableBuffer();
        if (!hadTail) Repaint();
    }

    private void FlushCompleteStreamLines()
    {
        string s = _streamTail.ToString();
        int nl;
        var commit = new List<string>();
        while ((nl = s.IndexOf('\n')) >= 0)
        {
            commit.Add(s[..nl].TrimEnd('\r'));
            s = s[(nl + 1)..];
        }
        if (commit.Count > 0)
        {
            _streamTail.Clear();
            _streamTail.Append(s);
            foreach (var raw in commit) CommitStreamLine(raw);
        }
    }

    /// <summary>
    /// Commit one finished line of streamed assistant text, routing contiguous GFM table rows
    /// through a buffer so the whole table can be column-aligned (a single line cannot align
    /// against its neighbours). A non-table line first flushes any pending table block, then
    /// commits itself as normal Markdown-to-markup.
    /// </summary>
    private void CommitStreamLine(string raw)
    {
        // Fenced code takes precedence over every other block rule: while inside a fence, lines are
        // captured verbatim (a closing fence flushes the block); an opening fence starts capture.
        if (_inFence)
        {
            if (TuiMarkdown.IsFence(raw)) { FlushCodeBuffer(); return; }
            _codeBuf.Add(raw);
            return;
        }
        if (TuiMarkdown.IsFence(raw))
        {
            FlushTableBuffer();
            ConsumeGap();
            _inFence = true;
            _fenceLang = TuiMarkdown.FenceInfo(raw);
            _codeBuf.Clear();
            return;
        }

        if (TuiTable.IsTableRow(raw))
        {
            ConsumeGap();
            _tableBuf.Add(raw);
            return;
        }
        FlushTableBuffer();
        // A blank line between a preceding tool block and this agent prose gives the two
        // visually distinct bands (requested density tweak). Skip if the line is itself blank.
        if (!string.IsNullOrWhiteSpace(raw)) ConsumeGap();
        // Reasoning streams as grey+italic so it is visually distinct from the answer. It is NOT
        // run through the markdown renderer (reasoning is free-form thought, not formatted output).
        if (_streamReasoning)
        {
            CommitMirrored(Lane(new[] { $"[grey italic]{Spectre.Console.Markup.Escape(raw)}[/]" }));
            return;
        }
        CommitMirrored(Lane(new[] { TuiMarkdown.ToMarkup(raw) }));
    }

    /// <summary>Emit the one-line separator owed after a tool block, if any.</summary>
    private void ConsumeGap()
    {
        if (!_pendingGap) return;
        _pendingGap = false;
        if (_blockGap <= 0) return;                       // tight: no separator
        CommitMirrored(Enumerable.Repeat("", _blockGap).ToList());
    }

    /// <summary>Render and commit any buffered table rows as one aligned, bordered block.</summary>
    /// <summary>Render and commit a buffered fenced code block as a verbatim, dim-background panel.</summary>
    private void FlushCodeBuffer()
    {
        _inFence = false;
        var code = new List<string>(_codeBuf);
        _codeBuf.Clear();
        var outl = new List<string>(code.Count == 0 ? 1 : code.Count);
        if (code.Count == 0)
            outl.Add(TuiMarkdown.CodeLine(""));   // empty fence still shows a thin band
        else if (TuiMarkdown.LooksLikeDiff(code, _fenceLang))
            foreach (var ln in code) outl.Add(TuiMarkdown.DiffLine(ln));  // git-style diff: per-line +/- coloring
        else
            foreach (var ln in code) outl.Add(TuiMarkdown.CodeLine(ln));
        CommitMirrored(Lane(outl));
        _pendingGap = true;
    }

    private void FlushTableBuffer()
    {
        if (_tableBuf.Count == 0) return;
        var rows = new List<string>(_tableBuf);
        _tableBuf.Clear();
        // A lone table-looking row (e.g. stray prose with pipes, no separator) is not really a
        // table; fall back to per-line markdown so we never swallow non-table content.
        bool looksTable = rows.Count >= 2 || rows.Exists(TuiTable.IsSeparatorRow);
        if (looksTable)
            CommitMirrored(Lane(TuiTable.Render(rows, Width)));
        else
            CommitMirrored(Lane(rows.Select(TuiMarkdown.ToMarkup).ToList()));
    }

    // --- live-region composition (pure, testable) ----------------------------

    /// <summary>
    /// Build the live-region markup lines from current state: optional streaming tail, a
    /// thin rule, the footer, and (during input) the input row + slash palette. Pure given
    /// the inputs - unit-tested without a console.
    /// </summary>
    internal List<string> BuildLiveFrame(int width)
    {
        var lines = new List<string>();

        if (_streaming && _streamTail.Length > 0)
            lines.Add(_streamReasoning
                ? $"[grey italic]{Spectre.Console.Markup.Escape(_streamTail.ToString())}[/]"
                : TuiMarkdown.ToMarkup(_streamTail.ToString()));

        // Mid-turn EXPANDED sub-agent: a bounded, tail-anchored panel rendered IN the live region
        // (toggled by Ctrl+E, never committed to scrollback). Bounded to a slice of the viewport so
        // the footer + input always stay on screen no matter how long the sub-agent transcript gets;
        // this is the whole point of model B (paintable buffer) - long output can't shove the footer
        // off-screen the way an unbounded inline commit would. The compact activity panel still
        // renders below it so the user keeps the at-a-glance status for ALL running agents.
        if (Expanded)
        {
            // Reserve room for footer/rule/input + the activity panel; cap the body to the rest so
            // the footer + input always stay on screen no matter how long the expanded block is.
            int reserved = 6 + Math.Min(_subAgents.Count, 4);
            int maxRows = Math.Max(3, Height - reserved);
            lines.AddRange(TuiComponents.BoundedLivePanel(
                _expandTitle, _expandBody, _expandTint, width, maxRows, _expandAnchorTail, _expandError));
        }

        // Consolidated sub-agent activity panel takes precedence over the single thinking line:
        // while one or more collapsed sub-agents run, show one animated line each (no flicker).
        if (_subAgents.Count > 0)
            lines.AddRange(TuiComponents.SubAgentActivity(_subAgents, _subAgentFrame));
        else if (!_streaming && !string.IsNullOrEmpty(_thinkingText))
            lines.Add(TuiComponents.ThinkingLine(_thinkingText, _thinkFrame));

        // A pending (unresolved) tool call is shown live with a running glyph until its
        // result lands and the two merge into a single committed line.
        if (!_streaming && _pendingTool is { } pt)
            lines.AddRange(Lane(TuiComponents.ToolCall(pt.Tool, pt.Args)));

        // Full-width rule separates the transcript from the docked footer (Claude-Code feel).
        lines.Add(TuiComponents.FullRule(width));
        lines.Add(TuiComponents.Footer(_tokens, _threshold, _plan, _ultra, _psub, _sub, _effort,
            modeCycleHint: OnModeCycle is not null, sessionId: _sessionId, cached: _cached,
            sysTokens: _sysTokens, toolTokens: _toolTokens));

        if (_inInput)
        {
            // A second full-width rule gives the input/compose area its own visible band,
            // clearly separated from the footer above it.
            lines.Add(TuiComponents.FullRule(width));
            lines.AddRange(TuiComponents.InputRowsWithCursor(_editor.Buffer, _editor.Cursor, _editor.Mode));
            // "/skill[s]" gets a live, web-app-style skills autocomplete; any other "/" token
            // gets the command palette. Skills check first so "/skills" isn't eaten by the
            // generic slash filter.
            if (_editor.IsAtFilter)
                lines.AddRange(TuiComponents.FilesPreview(_editor.AtFilter, _files, width, _paletteSel, _filesAreInstallDir));
            else if (_editor.IsSkillsFilter)
                lines.AddRange(TuiComponents.SkillsPreview(_editor.SkillsFilter, _skills, width, _paletteSel));
            else if (_editor.IsToolsFilter)
                lines.AddRange(TuiComponents.ToolsPreview(_editor.ToolsFilter, _tools, width, _paletteSel));
            else if (_editor.IsResumeFilter)
                lines.AddRange(TuiComponents.SessionsPreview(_editor.ResumeFilter, _sessions, width, _paletteSel));
            else if (_editor.IsSlashFilter)
                lines.AddRange(TuiComponents.SlashPalette(_editor.SlashFilter, _paletteEntries, _paletteSel));
        }
        return lines;
    }

    private void Repaint()
    {
        if (_shuttingDown) return;
        if (_navActive) return;   // NAV overlay owns the screen; defer paint until it exits
        _region.SetLive(BuildLiveFrame(Width));
    }

    // --- input ---------------------------------------------------------------

    /// <summary>
    /// Read a line of input with a live-edited input box pinned at the bottom (raw-mode key
    /// loop). Returns the submitted text, or null on EOF/cancel (caller treats like Ctrl-C).
    /// The submitted line is echoed into scrollback so history reads naturally.
    /// </summary>
    public string? ReadLine()
    {
        _editor.Reset();
        _paletteSel = -1;
        _inInput = true;
        // Collapse any mid-turn live-expand panel before the idle prompt so a stale tool-result /
        // sub-agent expansion never lingers into the input frame. The block stays Ctrl+E/NAV
        // expandable from its committed collapsed line in scrollback.
        ClearExpanded();
        bool prevCtrlC;
        try { prevCtrlC = Console.TreatControlCAsInput; } catch { prevCtrlC = false; }
        try
        {
            try { Console.TreatControlCAsInput = true; } catch { /* ignore */ }
            Repaint();

            while (true)
            {
                ConsoleKeyInfo key;
                try { key = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException)
                {
                    // stdin not a real console (shouldn't happen in TUI) - fall back.
                    _inInput = false;
                    _region.Clear();
                    return Console.ReadLine();
                }

                // Palette navigation intercept: when an autocomplete preview is open, Up/Down
                // move the highlighted candidate (instead of browsing command history), and
                // Enter on a highlighted row accepts it (instead of submitting the line). Tab
                // always accepts. This is the bridge toward full vim-style nav.
                if (PaletteOpen)
                {
                    if (key.Key == ConsoleKey.UpArrow)   { MovePaletteSelection(-1); Repaint(); continue; }
                    if (key.Key == ConsoleKey.DownArrow) { MovePaletteSelection(+1); Repaint(); continue; }
                    if (key.Key == ConsoleKey.Enter && _paletteSel >= 0)
                    {
                        AcceptCompletion();
                        _paletteSel = -1;
                        Repaint();
                        continue;
                    }
                }

                // Ctrl+E at the prompt: open the NAV overlay on the most-recent large tool
                // result, pre-expanded. Inside NAV the cursor can toggle it closed again or
                // move to other blocks (reversible). Falls through to the editor's emacs
                // Ctrl+E (end-of-line) when there is no expandable result.
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.E
                    && _transcript.Any(e => e.Expandable is not null))
                {
                    ExpandLastBlock();
                    Repaint();
                    continue;
                }

                // Ctrl+G at the prompt: open the transcript / expand view unconditionally - a
                // secondary affordance to Esc-on-empty for users whose terminal binds Esc to
                // cancel-the-turn. Opens NAV focused on the latest expandable block if one exists,
                // else the plain transcript overlay. Never cancels.
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.G)
                {
                    if (!ExpandLastBlock()) EnterNavMode();
                    Repaint();
                    continue;
                }

                var sig = _editor.Feed(key);
                // Any buffer/cursor edit invalidates the highlighted candidate (the filtered
                // list just changed); a fresh preview starts with nothing highlighted.
                if (sig == LineEditSignal.Continue) _paletteSel = -1;
                switch (sig)
                {
                    case LineEditSignal.Submit:
                    {
                        string line = _editor.Buffer;
                        _editor.Remember(line);
                        _inInput = false;
                        // Erase input box, then echo the submitted line into scrollback with a
                        // leading blank + accent gutter so each turn is clearly delimited.
                        _pendingGap = false;
        CommitMirrored(TuiComponents.UserEcho(line));
                        return line;
                    }
                    case LineEditSignal.Cancel:
                        _inInput = false;
                        _region.Clear();
                        return null;
                    case LineEditSignal.Eof:
                        _inInput = false;
                        _region.Clear();
                        return null;
                    case LineEditSignal.ModeCycle:
                        if (OnModeCycle is not null)
                        {
                            try { SetEffort(OnModeCycle()); } catch { /* ignore */ }
                        }
                        break;
                    case LineEditSignal.Complete:
                        AcceptCompletion();
                        _paletteSel = -1;
                        Repaint();
                        break;
                    case LineEditSignal.ModeChanged:
                        // Vim Insert<->Normal toggle: repaint so the mode badge/prompt updates.
                        _paletteSel = -1;
                        Repaint();
                        break;
                    case LineEditSignal.NavEnter:
                        // Normal-mode scroll chord: browse the retained transcript in a NAV
                        // overlay, then resume input where we left off.
                        EnterNavMode();
                        Repaint();
                        break;
                    case LineEditSignal.Continue:
                        // Coalesce repaints: when more keys are already queued (fast typing,
                        // held key, or a paste), skip the repaint and let the next iteration
                        // process the buffered key - only the LAST key in the burst triggers a
                        // paint. This is the key fix for multiline-input lag, where each
                        // keystroke would otherwise force a full wrapped-frame re-render.
                        if (!KeyQueued()) Repaint();
                        break;
                    case LineEditSignal.Ignored:
                    default:
                        break;
                }
            }
        }
        finally
        {
            _inInput = false;
            try { Console.TreatControlCAsInput = prevCtrlC; } catch { /* ignore */ }
        }
    }

    /// <summary>True when more console key events are already buffered (fast typing / paste /
    /// held key). Used to coalesce repaints so a burst of input renders once, not per keystroke -
    /// the fix for laggy multiline editing. Safe-guards against non-console stdin.</summary>
    private static bool KeyQueued()
    {
        try { return Console.KeyAvailable; }
        catch { return false; }
    }

    /// <summary>Resolve a workspace-relative file path (as shown in the @-file autocomplete) to
    /// its absolute form so the committed @reference is unambiguous to the agent. Falls back to
    /// the input unchanged if resolution fails.</summary>
    private static string ToAbsoluteWorkspacePath(string relative)
    {
        try
        {
            var root = PlatformContext.WorkspaceRoot;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            return full;
        }
        catch { return relative; }
    }

    /// <summary>True when an autocomplete preview is currently open (and thus arrow keys
    /// should drive its selection rather than command history).</summary>
    private bool PaletteOpen => _inInput && (_editor.IsAtFilter || _editor.IsSkillsFilter
        || _editor.IsToolsFilter || _editor.IsResumeFilter || _editor.IsSlashFilter);

    /// <summary>The ranked candidate list backing the currently open preview (empty if none).</summary>
    private IReadOnlyList<string> PaletteCandidates()
    {
        if (_editor.IsAtFilter)    return TuiComponents.RankFiles(_editor.AtFilter, _files);
        if (_editor.IsSkillsFilter) return TuiComponents.RankSkills(_editor.SkillsFilter, _skills);
        if (_editor.IsToolsFilter)  return TuiComponents.RankTools(_editor.ToolsFilter, _tools);
        if (_editor.IsResumeFilter) return TuiComponents.RankSessions(_editor.ResumeFilter, _sessions);
        if (_editor.IsSlashFilter)  return TuiComponents.RankCommands(_editor.SlashFilter, _paletteEntries).Select(e => e.Cmd).ToList();
        return Array.Empty<string>();
    }

    /// <summary>Move the highlighted palette candidate by <paramref name="delta"/> (wraps within
    /// the visible 8-row window). No-op when no preview is open. Returns true if it consumed the key.</summary>
    private bool MovePaletteSelection(int delta)
    {
        if (!PaletteOpen) return false;
        int count = PaletteCandidates().Count;   // full list - the preview window scrolls to follow
        if (count == 0) { _paletteSel = -1; return true; }
        int next = _paletteSel < 0 ? (delta > 0 ? 0 : count - 1) : _paletteSel + delta;
        // wrap around the whole list (top<->bottom)
        if (next < 0) next = count - 1;
        if (next >= count) next = 0;
        _paletteSel = next;
        return true;
    }

    /// <summary>
    /// Accept the highlighted (or, when none highlighted, the top) autocomplete candidate for
    /// the active input context. Resolves which live list is showing - @file / /skill / /resume /
    /// command palette - and applies the replacement into the editor buffer. No-op when nothing
    /// is completable.
    /// </summary>
    private void AcceptCompletion()
    {
        int sel = _paletteSel;
        // @-file reference: replace the current @token with the chosen fuzzy match.
        if (_editor.IsAtFilter)
        {
            var cands = TuiComponents.RankFiles(_editor.AtFilter, _files);
            var pick = Pick(cands, sel) ?? TuiComponents.TopFileMatch(_editor.AtFilter, _files);
            // Insert the ABSOLUTE path on selection so the agent knows exactly where the file
            // lives (the autocomplete preview shows relative paths to save space, but the
            // committed reference must be unambiguous regardless of the agent's working dir).
            if (pick is not null) _editor.ReplaceCurrentToken("@" + ToAbsoluteWorkspacePath(pick));
            return;
        }
        // /skill[s] <filter>: complete to "/skill <name>".
        if (_editor.IsSkillsFilter)
        {
            var cands = TuiComponents.RankSkills(_editor.SkillsFilter, _skills);
            var pick = Pick(cands, sel);
            if (pick is not null) _editor.SetBuffer($"/skill {pick}");
            return;
        }
        // /resume <filter>: complete to "/resume <id>".
        if (_editor.IsResumeFilter)
        {
            var cands = TuiComponents.RankSessions(_editor.ResumeFilter, _sessions);
            var pick = Pick(cands, sel);
            if (pick is not null) _editor.SetBuffer($"/resume {pick}");
            return;
        }
        // bare slash command: complete to the chosen (or best-ranked) command, so "/age" ->
        // "/agent", not "/swarm" (whose description contains "multi-agent"). Commands that take
        // an inline argument get a trailing space (ready for the arg); arg-less commands are
        // completed bare, because the dispatcher exact-matches and "/agent " != "/agent".
        if (_editor.IsSlashFilter)
        {
            var cands = TuiComponents.RankCommands(_editor.SlashFilter, _paletteEntries).Select(e => e.Cmd).ToList();
            var pick = Pick(cands, sel) ?? TuiComponents.TopCommandMatch(_editor.SlashFilter, _paletteEntries);
            if (pick is not null)
                _editor.SetBuffer(TuiCommands.TakesArgument(pick) ? pick + " " : pick);
        }
    }

    /// <summary>The highlighted candidate when <paramref name="sel"/> is a valid index, else the
    /// first candidate, else null.</summary>
    private static string? Pick(IReadOnlyList<string> cands, int sel)
    {
        if (cands.Count == 0) return null;
        if (sel >= 0 && sel < cands.Count) return cands[sel];
        return cands[0];
    }

    /// <summary>
    /// Enter the vim transcript-NAV overlay: a scrollable viewport over the retained transcript
    /// rendered in the live region. Runs its own key loop (j/k + arrows, Ctrl+D/U half-page,
    /// Ctrl+F/B + PgUp/PgDn full-page, g/G + Home/End to ends, q/Esc/i to exit). NAV is only
    /// ever entered from the idle input prompt (agent not streaming), so there is no concurrent
    /// writer to the screen and no render race. On exit the editor returns to Insert mode and
    /// the normal input frame repaints.
    /// </summary>
    /// <summary>
    /// Enter the vim transcript-NAV overlay: a scrollable viewport over the retained transcript
    /// with a MOVABLE CURSOR. j/k (+ arrows) move the cursor through entries (view scrolls to
    /// follow); the focused entry is highlighted. When the cursor sits on an expandable tool
    /// result, Ctrl+E or Enter toggles its full panel open/closed in place (reversible). Other
    /// keys: Ctrl+D/U half-page, Ctrl+F/B + PgUp/PgDn full-page, g/G + Home/End to ends,
    /// q/Esc/i to exit. NAV is only entered from the idle prompt, so there is no render race.
    /// </summary>
    /// <param name="focusEntry">Entry index to place the cursor on at entry (-1 = last).</param>
    private void EnterNavMode(int focusEntry = -1)
    {
        if (_transcript.Count == 0) { _editor.SetMode(EditorMode.Insert); return; }

        // Mark the overlay active so the (possibly concurrent, mid-turn) streaming/commit path
        // defers its physical writes while NAV owns the screen. Cleared in the finally below.
        _navActive = true;

        // Build the flat DISPLAY-line list + a per-line owning-entry map, expanding open entries
        // into their full panels. Rebuilt whenever an entry is toggled. Returned lines are MARKUP;
        // the cursor model strips them to plain text for unambiguous column/selection math.
        (List<string> Disp, List<int> Owner) Build()
        {
            var disp = new List<string>();
            var owner = new List<int>();
            for (int e = 0; e < _transcript.Count; e++)
            {
                var ent = _transcript[e];
                if (ent.Expandable is { } x && ent.Expanded)
                    foreach (var l in TuiComponents.ToolResultPanel(x.Tool, x.Text, x.Error, Width, expanded: true))
                    { disp.Add(l); owner.Add(e); }
                else
                { disp.Add(ent.Collapsed); owner.Add(e); }
            }
            if (disp.Count == 0) { disp.Add(""); owner.Add(0); }
            return (disp, owner);
        }

        int FirstLineOf(List<int> owner, int e)
        {
            for (int i = 0; i < owner.Count; i++) if (owner[i] == e) return i;
            return Math.Max(0, owner.Count - 1);
        }

        var (disp, owner) = Build();
        var model = new NavCursorModel(disp);
        int startEntry = focusEntry >= 0 ? focusEntry
            : (_navSavedEntry >= 0 && _navSavedEntry < _transcript.Count ? _navSavedEntry : -1);
        if (startEntry >= 0) model.SeekRow(FirstLineOf(owner, startEntry));
        else model.Bottom();

        int top = 0;
        string status = "";   // transient status line (e.g. "copied N chars")
        // Last-painted physical rows (ANSI) for diff repaint - only changed rows are rewritten,
        // so moving the cursor does NOT clear+redraw the whole screen (kills the flicker). null
        // until the first full paint; reset to force a full repaint after a resize/expand.
        List<string>? lastRows = null;

        // Render one transcript row to a full-width ANSI string at the current model state: the
        // styled markup is preserved (color is NOT lost - we parse spans and re-emit their SGR),
        // and the cursor cell + any visual selection are overlaid with reverse-video on top of the
        // underlying style. Padded to terminal width so a diff-rewrite fully overwrites the prior
        // row with no residue.
        string RenderRow(int r)
        {
            var sb = new System.Text.StringBuilder();
            var spans = TuiMarkup.Parse(model.DisplayLine(r));
            int col = 0;
            int rowCap = Math.Max(1, _term.Width - 1);   // never render into the last column
            foreach (var span in spans)
            {
                string sgr = span.Style.ToAnsi();
                foreach (char ch in span.Text)
                {
                    if (col >= rowCap) break;
                    bool sel = model.InSelection(r, col);
                    bool cur = r == model.Row && col == model.Col;
                    if (sel || cur) sb.Append(Ansi.Reset).Append(Ansi.Invert).Append(ch).Append(Ansi.Reset);
                    else { if (sgr.Length > 0) sb.Append(sgr); sb.Append(ch); if (sgr.Length > 0) sb.Append(Ansi.Reset); }
                    col++;
                }
                if (col >= rowCap) break;
            }
            // Cursor parked just past the last char: show an inverted space.
            if (model.Row == r && model.Col >= col) { sb.Append(Ansi.Invert).Append(' ').Append(Ansi.Reset); col++; }
            // Pad to width-1 (never touch the last column) - combined with AutoWrapOff this
            // guarantees the row cannot wrap and strand a stray line below the footer.
            int cap = Math.Max(1, _term.Width - 1);
            int pad = Math.Max(0, cap - col);
            if (pad > 0) sb.Append(new string(' ', pad));
            return sb.ToString();
        }

        // Diff repaint into the ALT SCREEN: compute every visible row's ANSI, compare against what
        // is already on screen, and rewrite ONLY the rows that changed (cursor-addressed). The
        // status/help rows are likewise only redrawn when their text changes. No full clear, so the
        // view does not flicker as the cursor moves.
        void Paint()
        {
            int viewH = Math.Max(1, _term.Height - 2);   // rows for transcript; 2 for status+help
            if (model.Row < top) top = model.Row;
            else if (model.Row >= top + viewH) top = model.Row - viewH + 1;
            int maxTop = Math.Max(0, model.LineCount - viewH);
            if (top > maxTop) top = maxTop;
            if (top < 0) top = 0;

            // Build the desired physical rows: viewH transcript rows + 1 help + 1 status.
            var rows = new List<string>(viewH + 2);
            int endLine = Math.Min(model.LineCount, top + viewH);
            for (int r = top; r < endLine; r++) rows.Add(RenderRow(r));
            while (rows.Count < viewH) rows.Add(new string(' ', _term.Width)); // blank filler rows

            var ent = _transcript[owner[Math.Clamp(model.Row < owner.Count ? model.Row : owner.Count - 1, 0, owner.Count - 1)]];
            string expandHint = ent.Expandable is not null
                ? (ent.Expanded ? "ctrl+e/enter collapse  " : "ctrl+e/enter expand  ")
                : "";
            string selHint = model.Select switch
            {
                NavSelect.Char => "[char-select] y copy  Esc clear  ",
                NavSelect.Line => "[line-select] y copy  Esc clear  ",
                _ => "v char-sel  V line-sel  ",
            };
            string help = $"{Ansi.Invert} NAV {Ansi.Reset} "
                + $"{model.Row + 1}/{model.LineCount}  hjkl/arrows move  ctrl+d/u page  g/G ends  "
                + expandHint + selHint + "q exit";
            rows.Add(help);
            rows.Add(status.Length > 0 ? status : "");

            var sb = new System.Text.StringBuilder();
            if (lastRows is null || lastRows.Count != rows.Count)
            {
                // First paint (or geometry changed): full clear + draw, then record.
                sb.Append(Ansi.ClearScreen).Append(Ansi.Home);
                for (int i = 0; i < rows.Count; i++)
                    sb.Append(Ansi.MoveTo(i + 1, 1)).Append(rows[i]);
            }
            else
            {
                // Diff: rewrite only the rows whose ANSI changed.
                for (int i = 0; i < rows.Count; i++)
                    if (!string.Equals(rows[i], lastRows[i], StringComparison.Ordinal))
                        sb.Append(Ansi.MoveTo(i + 1, 1)).Append(Ansi.EraseLine).Append(rows[i]);
            }
            lastRows = rows;
            if (sb.Length > 0) { _term.Write(sb.ToString()); _term.Flush(); }
        }

        bool prevCtrlC;
        try { prevCtrlC = Console.TreatControlCAsInput; } catch { prevCtrlC = false; }
        try
        {
            try { Console.TreatControlCAsInput = true; } catch { /* ignore */ }
            _region.HideCursor();
            _term.Write(Ansi.EnterAltScreen);
            _term.Write(Ansi.AutoWrapOff);   // full-width rows must not wrap (kills the stray line below the footer)
            _term.Write(Ansi.HideCursor);
            Paint();

            while (true)
            {
                ConsoleKeyInfo key;
                try { key = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException) { break; }
                bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
                bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
                int viewH = Math.Max(1, _term.Height - 2);
                int half = Math.Max(1, viewH / 2);
                status = "";

                if (key.Key == ConsoleKey.Q || key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.I)
                {
                    // Esc/q exits; but if a selection is active, Esc first clears it (vim-like).
                    if (key.Key == ConsoleKey.Escape && model.Select != NavSelect.None) { model.ClearSelect(); Paint(); continue; }
                    break;
                }
                else if (key.Key == ConsoleKey.J || key.Key == ConsoleKey.DownArrow) model.MoveDown();
                else if (key.Key == ConsoleKey.K || key.Key == ConsoleKey.UpArrow)   model.MoveUp();
                else if (key.Key == ConsoleKey.H || key.Key == ConsoleKey.LeftArrow) model.MoveLeft();
                else if (key.Key == ConsoleKey.L || key.Key == ConsoleKey.RightArrow) model.MoveRight();
                else if (ctrl && key.Key == ConsoleKey.D) model.Page(half);
                else if (ctrl && key.Key == ConsoleKey.U) model.Page(-half);
                else if ((ctrl && key.Key == ConsoleKey.F) || key.Key == ConsoleKey.PageDown) model.Page(viewH);
                else if ((ctrl && key.Key == ConsoleKey.B) || key.Key == ConsoleKey.PageUp)   model.Page(-viewH);
                else if (key.Key == ConsoleKey.Home) model.LineStart();
                else if (key.Key == ConsoleKey.End)  model.LineEnd();
                else if (key.Key == ConsoleKey.G && !shift) model.Top();
                else if (key.Key == ConsoleKey.G && shift)  model.Bottom();
                else if (key.Key == ConsoleKey.V && !shift) model.ToggleSelect(NavSelect.Char);
                else if (key.Key == ConsoleKey.V && shift)  model.ToggleSelect(NavSelect.Line);
                else if (key.Key == ConsoleKey.Y)
                {
                    string text = model.SelectedText();
                    if (text.Length > 0)
                    {
                        TuiClipboard.CopyViaTerminal(_term, text);   // OSC 52 -> local clipboard
                        TuiClipboard.CopyViaShell(text);             // fallback -> OS clipboard
                        model.ClearSelect();
                        int chars = text.Length;
                        status = $"{Ansi.Invert} copied {chars} char{(chars == 1 ? "" : "s")} {Ansi.Reset}";
                    }
                    else status = "(nothing selected - press v then move, then y)";
                }
                else if ((ctrl && key.Key == ConsoleKey.E) || key.Key == ConsoleKey.Enter)
                {
                    int e = owner[Math.Clamp(model.Row, 0, owner.Count - 1)];
                    var ent2 = _transcript[e];
                    if (ent2.Expandable is null) { Paint(); continue; }
                    ent2.Expanded = !ent2.Expanded;
                    (disp, owner) = Build();
                    model.Load(disp);
                    model.SeekRow(FirstLineOf(owner, e));   // stay on the toggled card, no top-snap
                    Paint();
                    continue;
                }
                else continue;
                Paint();
            }

            // Remember where the cursor was (as an entry index) so the next NAV open restores it.
            _navSavedEntry = owner[Math.Clamp(model.Row, 0, owner.Count - 1)];
            // Leaving NAV: collapse all entries again (keep live scrollback compact) and return to
            // Insert mode at the live input prompt.
            foreach (var e in _transcript) e.Expanded = false;
            _editor.SetMode(EditorMode.Insert);
        }
        finally
        {
            // Restore the primary screen buffer (scrollback intact) and repaint one fresh live
            // frame reflecting everything that streamed/committed (deferred) while NAV was open.
            _term.Write(Ansi.AutoWrapOn);
            _term.Write(Ansi.LeaveAltScreen);
            try { Console.TreatControlCAsInput = prevCtrlC; } catch { /* ignore */ }
            _navActive = false;
            Repaint();
        }
    }

    /// <summary>
    /// Clear the live region and hand the terminal back cleanly (cursor shown, no residue).
    /// Called before any blocking external prompt, mode switch, or exit. Idempotent.
    /// </summary>
    public void Suspend() { FlushPendingToolCall(); _region.Clear(); }

    /// <summary>
    /// Full teardown for process exit / mode switch: clear the live region, show the cursor.
    /// Idempotent and exception-safe so it is safe on ProcessExit / CancelKeyPress.
    /// </summary>
    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try { _region.Clear(); } catch { /* ignore */ }
    }
}
