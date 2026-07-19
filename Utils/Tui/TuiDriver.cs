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
    private bool _plan, _ultra, _psub, _sub, _giga;
    private string? _effort;   // reasoning-effort chip (low/med/high), null = hidden
    private string? _sessionId; // active session id badge, null = hidden

    // streaming state - partial (un-newlined) tail shown live above the footer
    private readonly StringBuilder _streamTail = new();
    private bool _streaming;
    // True until the FIRST answer line of the current streamed assistant block is committed, so a
    // single grey lead dot (Claude-Code style) is stamped once per OUTPUT BLOCK - not per line and
    // not per turn. Set in BeginStream, cleared when the first non-reasoning line commits.
    private bool _streamBlockDotPending;
    // Left margin for streamed agent prose/reasoning so every line aligns under the lead dot's TEXT.
    // The output dot is rendered "  *" (dot at col 2, matching the turn-header marker and tool-call
    // dot), so its text begins at col 4. The first line of a block uses the dot prefix; later lines
    // use this 4-space indent so the whole block reads as one column under "* <agent>".
    private const string StreamIndent = "    ";
    // Stream repaint coalescing: model tokens arrive many-per-second and a full live-frame rebuild
    // per token (BuildLiveFrame re-runs the markdown renderer over the whole growing tail) is what
    // makes non-ACP streaming look chunky. We mark the live tail dirty per chunk but only actually
    // repaint on a ~30fps budget; completed-line commits + EndStream still flush immediately, so no
    // content is ever lost - only the intra-frame live-tail preview is throttled.
    private long _lastStreamPaintTicks;
    private const long StreamPaintIntervalTicks = TimeSpan.TicksPerMillisecond * 33;
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

    // v0.12.0 M1 - inline Agent View dashboard. The backslash key foregrounds a keyboard-
    // navigable session list over the running sub-agents (the existing _subAgents snapshot);
    // Enter attaches the selected agent's buffered stream through the same sub-agent expand
    // machinery used by Ctrl+E. Rendered INSIDE the live region (BuildLiveFrame) so scrollback
    // is preserved - never an alt-screen takeover. Bypassed entirely when not open, so the
    // off-dashboard path is byte-identical to today's frame.
    private readonly AgentView _agentView = new();
    private volatile bool _agentViewActive;
    // The sub-agent the user last FOREGROUNDED via the backslash dashboard. Ctrl+E sticks to
    // this agent (toggling its panel) instead of snapping back to the latest-registered
    // capture, so the quick-open key tracks the user's chosen focus. Null = no explicit
    // focus yet (Ctrl+E falls back to the most-recent running sub-agent).
    private volatile string? _foregroundAgent;

    // v0.12.0 M2 - team TaskBoard strip (Ctrl+T). A decoupled provider supplies a point-in-time
    // board snapshot (tally + flattened rows) so the driver never references State/Teams directly.
    // Null provider or null snapshot => no board => the strip never renders (off-team byte-identical).
    private volatile bool _taskBoardVisible;
    // Scroll offset into the (possibly long) task list while the Ctrl+T strip is open. Up/Down at
    // the idle prompt (empty buffer) adjust it; reset to 0 whenever the strip is toggled.
    private int _taskBoardOffset;
    // Rows shown in the strip viewport - kept in sync with TaskBoardStrip's maxRows so the driver
    // clamps the offset against the same window the component renders.
    private const int TaskBoardWindow = 5;
    public bool TaskBoardVisible => _taskBoardVisible;
    public Func<(int Total, int Done, int InProgress, int Blocked, int Failed,
        IReadOnlyList<(string Id, string Status, string? Owner, string Subject, int Artifacts)> Rows)?>? TaskBoardProvider { get; set; }

    // M4 Mailbox: per-agent message-log provider for the Agent View 'm' key. Given an agent name,
    // returns pre-formatted markup rows of that agent's inbox history (oldest-first), or empty.
    // Null when no team mailbox is active (the 'm' key is then a no-op).
    public Func<string, IReadOnlyList<string>>? MessageLogProvider { get; set; }

    // Opens the inline Agent View dashboard from the IDLE prompt (the backslash key). MuxConsole
    // wires this to TuiEnterAgentView(), which builds the running-agent snapshot + body provider
    // from the live capture registry. Null or returns false => no agents running => backslash falls
    // through to the editor as a literal char. Mirrors the mid-turn EscapeKeyListener onAgents path.
    public Func<bool>? AgentViewOpener { get; set; }

    /// <summary>
    /// Optional callback to expand/collapse the live sub-agent panel at the idle prompt, mirroring
    /// the mid-turn EscapeKeyListener Ctrl+E path (MuxConsole.TuiExpandLatestInline). When set and
    /// sub-agents are running, the prompt's Ctrl+E targets the live panel (toggle open/closed)
    /// instead of the transcript NAV overlay, so a /background or /swarm panel can be closed from
    /// the prompt. Returns true if it opened a panel. Set by the console wiring alongside
    /// AgentViewOpener; null at the top-level menu.
    /// </summary>
    public Func<bool>? OnSubAgentExpand { get; set; }

    /// <summary>
    /// Optional idle-prompt picker for DETACHED interactive sessions (v0.12.0 /detach). When the
    /// backslash key is pressed at the prompt and no sub-agents are running to foreground, this is
    /// invoked; if it returns a non-null command string (e.g. "/attach sess2") the ReadLine loop
    /// returns that line so it routes through the normal /attach dispatch. Returns null when the
    /// user cancels or there is nothing to attach (then backslash inserts literally). Set by the
    /// console wiring; null at construction.
    /// </summary>
    public Func<string?>? AttachPicker { get; set; }

    // Mid-turn EXPAND slot (generic). Any expandable block - a running sub-agent's buffered
    // transcript OR a finished large tool result - can be toggled open with Ctrl+E into a single
    // bounded panel rendered INSIDE the repaintable live region (see BuildLiveFrame), never
    // committed to scrollback. Toggling open/closed and live stream updates repaint in place with
    // zero append spam (the old approach appended a fresh panel per keypress, flooding scrollback).
    // Only one block is expanded at a time; opening another switches the slot. A second Ctrl+E on
    // the same block (or stream end / turn end) collapses it.
    private enum ExpandKind { None, SubAgent, ToolResult, Diff }
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

    // Most-recent RESOLVED tool result, HELD in the live region so its completion dot can pulse
    // SLOWLY (a "settling" beat, ~3x slower than the in-flight dot). It is flushed down to static
    // scrollback the instant any other content commits (next tool call / stream / line), so only
    // ONE completion dot ever pulses at a time, just above the footer. Stores everything needed to
    // re-emit the merged line + retain its expandable block on flush. Null = nothing settling.
    private (string Tool, string? Args, string Result, bool Error, bool Expandable, string? ExpandBody)? _settling;

    // Wall-clock timers surfaced as footer badges. _sessionStart is fixed at construction
    // (total session age, shown by the session timer); _loopStart is set when an agentic
    // interface (/agent, /stateless, /swarm, /pswarm) is entered and cleared when it exits, so
    // the loop clock ticks continuously the whole time the user is inside a loop.
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    private DateTime? _loopStart;

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

    /// <summary>Rows scrolled per Ctrl+U / Ctrl+D step while paging the frame viewport at the idle
    /// prompt (console.scrollSpeedRows). PgUp/PgDn and Ctrl+B/Ctrl+F stay full-page. Min 1.</summary>
    private int _scrollSpeedRows = 1;
    public void SetScrollSpeedRows(int rows) => _scrollSpeedRows = Math.Max(1, rows);

    /// <summary>Shade the user input/compose field on a band (console.inputHighlight).</summary>
    private bool _inputHighlight = true;
    public void SetInputHighlight(bool on) => _inputHighlight = on;

    /// <summary>Render expanded tool-result card bodies as muted markdown (console.cardMarkdown).</summary>
    private bool _cardMarkdown = true;
    public void SetCardMarkdown(bool on)
    {
        if (_cardMarkdown == on) return;
        _cardMarkdown = on;
        InvalidateFrameRowCounts();
    }

    /// <summary>Invalidate cached frame-row renders + force a repaint after a live change that only
    /// alters row STYLING, not geometry (e.g. console.contentBackgrounds). Row widths/counts are
    /// unchanged — background SGR is zero visible width — so this just re-renders in place.</summary>
    public void RefreshStyling()
    {
        InvalidateFrameRowCounts();
        ForceRedraw();
    }

    /// <summary>Capture a multi-line paste as one literal block (console.bracketedPaste, DECSET 2004)
    /// instead of submitting on the first embedded newline.</summary>
    private bool _bracketedPaste = true;
    public void SetBracketedPaste(bool on)
    {
        _bracketedPaste = on;
        try { _term.Write(on ? Ansi.BracketedPasteOn : Ansi.BracketedPasteOff); _term.Flush(); } catch { /* ignore */ }
    }
    // Keys consumed while probing an ESC sequence that turned out NOT to be a paste marker are
    // stashed here and replayed through the normal edit path so nothing is dropped.
    private readonly Queue<ConsoleKeyInfo> _ungetq = new();

    // --- /voice: transcripts + submit requests from the voice worker thread. Drained by the
    // input thread inside ReadLine's poll loop (active only while voice is on), so the line
    // editor is NEVER mutated cross-thread. The poll tick doubles as the animation clock for
    // the compose-field voice indicator (pulsing dot replaces the prompt caret).
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _voiceInject = new();
    private volatile bool _voiceSubmit;
    private volatile bool _voiceDirty;

    /// <summary>Queue transcript text for insertion into the compose buffer (thread-safe).</summary>
    public void VoiceInject(string text) { if (!string.IsNullOrEmpty(text)) { _voiceInject.Enqueue(text); _voiceDirty = true; } }

    /// <summary>Request the current compose buffer be submitted as if Enter was pressed (thread-safe).</summary>
    public void VoiceSubmit() { _voiceSubmit = true; }

    /// <summary>Request an input-area repaint on the next voice poll tick (thread-safe).</summary>
    public void VoiceRepaintSoon() { _voiceDirty = true; }

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
        public bool DiffKind;                                      // expandable body is a unified diff
        public long Sequence;                                      // monotonic prompt-replay watermark
    }

    private readonly List<Entry> _transcript = new();
    private long _transcriptSequence;
    private long _promptContextAfterSequence;

    // Exact wrapped-row metrics for the frame scroll marker. Counts are cached per Entry at the
    // active width; appends update the cache incrementally, while width/style/expansion changes
    // invalidate it. This avoids an O(history) walk on every spinner tick or streamed line.
    private readonly Dictionary<Entry, int> _frameRowCountCache = new();
    private int _frameRowCountWidth = -1;
    private int _frameTotalRowsCache;
    private bool _frameRowCountValid;
    private bool _frameRowCountCardMarkdown;

    private void AddTranscriptEntry(Entry entry)
    {
        entry.Sequence = ++_transcriptSequence;
        _transcript.Add(entry);
        if (_frameRowCountValid && _frameRowCountWidth == Width
            && _frameRowCountCardMarkdown == _cardMarkdown)
        {
            int count = RenderEntryRows(entry, _frameRowCountWidth).Count;
            _frameRowCountCache[entry] = count;
            _frameTotalRowsCache += count;
        }
        else
        {
            InvalidateFrameRowCounts();
        }
    }

    private void InvalidateFrameRowCounts()
    {
        _frameRowCountValid = false;
        _frameRowCountCache.Clear();
        _frameTotalRowsCache = 0;
    }

    private int GetFrameTotalRows(int wrapWidth)
    {
        if (_frameRowCountValid && _frameRowCountWidth == wrapWidth
            && _frameRowCountCardMarkdown == _cardMarkdown)
            return _frameTotalRowsCache;

        _frameRowCountCache.Clear();
        int total = 0;
        foreach (var entry in _transcript)
        {
            int count = RenderEntryRows(entry, wrapWidth).Count;
            _frameRowCountCache[entry] = count;
            total += count;
        }
        _frameRowCountWidth = wrapWidth;
        _frameRowCountCardMarkdown = _cardMarkdown;
        _frameTotalRowsCache = total;
        _frameRowCountValid = true;
        return total;
    }

    /// <summary>Mirror committed plain markup lines into the transcript (one entry per line).</summary>
    private void Retain(IReadOnlyList<string> markupLines)
    {
        foreach (var l in markupLines) AddTranscriptEntry(new Entry { Collapsed = l });
        TrimTranscript();
    }

    /// <summary>Mirror a single expandable tool-result line, retaining its full-panel data.</summary>
    private void RetainExpandable(string collapsedLine, string tool, string text, bool error)
    {
        AddTranscriptEntry(new Entry { Collapsed = collapsedLine, Expandable = (tool, text, error) });
        TrimTranscript();
    }

    /// <summary>
    /// Commit a diff as a COLLAPSED one-liner that retains the full unified-diff body, so it joins
    /// the Ctrl+E / NAV collapse system exactly like a large tool result instead of permanently
    /// exploding into scrollback. NAV / inline expand render it through the production diff card.
    /// </summary>
    public void CommitDiffCollapsible(string title, string diff)
    {
        FlushPendingToolCall();
        var body = diff ?? "";
        int adds = 0, dels = 0;
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("+") && !raw.StartsWith("+++")) adds++;
            else if (raw.StartsWith("-") && !raw.StartsWith("---")) dels++;
        }
        string shortTitle = string.IsNullOrEmpty(title) ? "diff" : title;
        if (shortTitle.Length > 44) shortTitle = shortTitle[..43] + "\u2026";
        string collapsed = Lane(new List<string>
        {
            $"  [{TuiComponents.Accent}]\u270e diff[/] [{TuiComponents.Dim}]\u00b7 {Spectre.Console.Markup.Escape(shortTitle)}[/]  "
            + $"[{TuiComponents.DiffAdd}]+{adds}[/] [{TuiComponents.DiffDel}]\u2212{dels}[/] [{TuiComponents.Dim}](ctrl+e expand)[/]"
        })[0];
        AddTranscriptEntry(new Entry { Collapsed = collapsed, Expandable = (title ?? "diff", body, false), DiffKind = true });
        TrimTranscript();
        if (!_navActive) CommitPaint(new[] { collapsed });
        _pendingGap = true;
    }

    private void TrimTranscript()
    {
        int over = _transcript.Count - TranscriptCap;
        if (over <= 0) return;
        var removed = _transcript.GetRange(0, over);
        _transcript.RemoveRange(0, over);
        if (_frameRowCountValid)
        {
            foreach (var entry in removed)
            {
                if (_frameRowCountCache.Remove(entry, out int count)) _frameTotalRowsCache -= count;
                else { InvalidateFrameRowCounts(); break; }
            }
        }
    }

    /// <summary>Retain + commit lines above the region (mirrors history for vim NAV scrollback).</summary>
    private void CommitMirrored(IReadOnlyList<string> lines)
    {
        Retain(lines);
        // While the NAV overlay owns the screen (possibly opened mid-turn), retain history but
        // do NOT physically write - NAV exit issues a full repaint that reflects everything.
        if (_navActive) return;
        CommitPaint(lines);
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
    private (string Cmd, string Desc)[] _paletteEntries = TuiCommands.SessionUnified;

    /// <summary>Loaded skills catalog for the live "/skill" autocomplete preview.</summary>
    private IReadOnlyList<(string Name, string Desc)> _skills = Array.Empty<(string, string)>();

    /// <summary>Switch the as-you-type palette between session and top-level command sets.</summary>
    public void SetPaletteScope(bool topLevel) => _paletteEntries = topLevel ? TuiCommands.ReplUnified : TuiCommands.SessionUnified;

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

    // v0.12.4 full-frame renderer (console.renderEngine = "frame"). When _engineFrame is set the
    // driver takes complete alternate-screen ownership through this owner instead of the inline
    // native-scrollback live region: every paint re-composes the WHOLE viewport (transcript tail
    // re-wrapped at the current width + the pinned live/footer/input band) from retained state and
    // presents one atomic diffed frame. Off (the default) the frame renderer is never touched and
    // the inline path is byte-identical to today.
    private readonly FrameRenderer _frame;
    private readonly bool _engineFrame;

    // Frame-engine SUSPEND latch (the fix for the sub-prompt shatter that killed the first frame
    // engine). Suspend() leaves the alternate screen AND latches this flag; while latched, every
    // frame-mode paint DEFERS (state is still retained) so the ~100ms sub-agent ticker / resize
    // poll can never re-enter the alt screen while a blocking Spectre prompt owns the primary
    // buffer. Resume() (paired TuiResume after each prompt, or ReadLine reclaiming the screen)
    // unlatches, invalidates, and fully repaints. Volatile: set on prompt threads, read by timers.
    private volatile bool _suspended;

    public TuiDriver(ITuiTerminal? term = null, bool frameEngine = false)
    {
        _term = term ?? new ConsoleTuiTerminal();
        _region = new LiveRegion(_term);
        _frame = new FrameRenderer(_term);
        _engineFrame = frameEngine;
    }

    /// <summary>True when the driver is running the v0.12.4 full-frame (alternate-screen) renderer
    /// rather than the inline native-scrollback live region. Test/wiring hook.</summary>
    public bool FrameEngine => _engineFrame;

    /// <summary>True while the frame engine is suspended for a blocking external prompt (alt screen
    /// left, presents deferred). Always false in inline mode. Test hook.</summary>
    public bool Suspended => _engineFrame && _suspended;

    // Frame mode lays out at (cols - 1): the last column is RESERVED - components never touch it,
    // so a full-width rule/panel row can never soft-wrap when re-wrapped in ComposeFrameRows (the
    // stranded "-" fragment / split-card bug), and the reserved column holds the tiny passive
    // scroll-position marker while paging. Inline mode keeps the true terminal width - byte-identical to before.
    public int Width => _engineFrame ? Math.Max(20, _term.Width - 1) : Math.Max(20, _term.Width);

    /// <summary>Visible terminal height (rows), floored so panel-bounding math stays sane on
    /// tiny/again-unavailable terminals.</summary>
    public int Height => Math.Max(10, _term.Height);

    /// <summary>Update the context meter / mode badges and repaint the live region.</summary>
    public void SetFooter(uint tokens, uint threshold, bool plan, bool ultra, bool psub, bool sub = false, uint cached = 0, bool giga = false)
    {
        _tokens = tokens; _threshold = threshold; _plan = plan; _ultra = ultra; _psub = psub; _sub = sub; _cached = cached; _giga = giga;
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

    /// <summary>Start the loop clock - the live "&#x25cf; m:ss" footer badge that ticks the whole
    /// time the user is inside an agentic interface. Idempotent: re-entering a loop while one is
    /// already running keeps the original start (does not reset the clock).</summary>
    public void StartLoopClock() { _loopStart ??= DateTime.UtcNow; }

    /// <summary>Stop and clear the loop clock (back at the top-level menu, no loop active).</summary>
    public void StopLoopClock() { _loopStart = null; }

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
        FlushSettlingResult();
        FlushPendingToolCall();
        _thinkingText = null;
        // The retained transcript keeps any expandable tool-result entries armed for Ctrl+E /
        // NAV across subsequent commits, so nothing is cleared here.
        CommitMirrored(Lane(markupLines));
    }

    /// <summary>Seed frame-mode startup content and open it at the oldest/top edge when the full
    /// splash is taller than the available transcript pane. A normal command submission resets the
    /// viewport to the live tail. Used only during initial frame activation.</summary>
    public void CommitStartup(IReadOnlyList<string> markupLines)
    {
        if (markupLines.Count == 0) return;
        Retain(markupLines);
        _frameScroll = int.MaxValue; // ComposeFrameRows clamps this to the exact oldest position.
        CommitPaint(markupLines);
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
        if (!_navActive) CommitPaint(new[] { collapsedLine });
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
        FlushSettlingResult();
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
    public void ResolveMergedToolResult(string resultText, bool error = false, string? expandBody = null)
    {
        var (tool, args) = _pendingTool ?? ("", null);
        _pendingTool = null;
        _thinkingText = null;
        int infoLines = (resultText ?? "").Replace("\r\n", "\n").Split('\n').Count(l => l.Trim().Length > 0);
        // An expandBody override (e.g. repl_shell_exec code shown above its output) makes the result
        // expandable even when the visible result is short, so the user can always open the card to
        // read the exact code that ran. The collapsed one-liner stays lean either way.
        bool expandable = (_collapseToolLines > 0 && infoLines > _collapseToolLines) || expandBody is not null;
        // Flush any PRIOR settling result to static scrollback, then HOLD this newest one in the
        // live region so its completion dot pulses slowly until the next event supersedes it. This
        // is the only way the most-recent completed dot can animate (committed scrollback can't be
        // repainted). The held line still retains its expandable block on flush (see FlushSettling).
        FlushSettlingResult();
        _settling = (tool, args, resultText ?? "", error, expandable, expandBody);
        Repaint();
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
        FlushSettlingResult();
        if (_navActive || _transcript.Count == 0) return false;
        int idx = -1;
        for (int i = _transcript.Count - 1; i >= 0; i--)
            if (_transcript[i].Expandable is not null) { idx = i; break; }
        if (idx >= 0) { _transcript[idx].Expanded = true; InvalidateFrameRowCounts(); }
        EnterNavMode(focusEntry: idx);
        return true;
    }

    public bool ExpandLastBlock()
    {
        // Flush any still-settling result into the transcript so it is openable in NAV.
        FlushSettlingResult();
        // Find the most-recent expandable transcript entry and open NAV positioned on it,
        // pre-expanded. Expansion is a reversible in-overlay toggle (not a one-way commit),
        // so the user can collapse it again or scroll to other blocks.
        int idx = -1;
        for (int i = _transcript.Count - 1; i >= 0; i--)
            if (_transcript[i].Expandable is not null) { idx = i; break; }
        if (idx < 0) return false;
        _transcript[idx].Expanded = true;
        InvalidateFrameRowCounts();
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
        // A just-resolved result may still be HELD in the settling slot (pulsing) and not yet in
        // the transcript - flush it first so its expandable block exists to open.
        FlushSettlingResult();
        int idx = -1;
        for (int i = _transcript.Count - 1; i >= 0; i--)
            if (_transcript[i].Expandable is not null) { idx = i; break; }
        if (idx < 0) return false;
        var blk = _transcript[idx].Expandable!.Value;
        bool isDiff = _transcript[idx].DiffKind;
        var wantKind = isDiff ? ExpandKind.Diff : ExpandKind.ToolResult;
        string key = $"tool#{idx}";
        // Same block already expanded -> collapse (reversible toggle, no spam).
        if (_expandKind == wantKind && string.Equals(_expandKey, key, StringComparison.Ordinal))
            { ClearExpanded(); return false; }
        OpenExpand(wantKind, key, blk.Tool, blk.Text ?? "",
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

    /// <summary>If a sub-agent panel is currently open, collapse it and return true; else return
    /// false. Lets Ctrl+E act as a true toggle - close whatever is open - instead of recomputing a
    /// (possibly different) target agent and opening THAT, which read as "collapse changed the
    /// content instead of closing" when focus had drifted. Caller holds the console lock.</summary>
    public bool CollapseOpenSubAgentPanel()
    {
        if (_expandKind != ExpandKind.SubAgent) return false;
        ClearExpanded();
        return true;
    }

    /// <summary>True if <paramref name="agent"/> is the sub-agent currently expanded in the live
    /// region. Lets the completion path keep a user-opened panel open through finish (instead of
    /// snapping it collapsed) while still committing the expandable collapsed line. Caller holds
    /// the console lock.</summary>
    public bool IsSubAgentExpanded(string agent) =>
        _expandKind == ExpandKind.SubAgent && string.Equals(_expandKey, $"sub:{agent}", StringComparison.Ordinal);

    /// <summary>The sub-agent most recently foregrounded through the backslash dashboard, or
    /// null when none has been chosen this turn. Lets the Ctrl+E quick-open prefer the user's
    /// selected focus over the latest-registered capture. Caller holds the console lock.</summary>
    public string? ForegroundAgent => _foregroundAgent;

    /// <summary>Clear the sticky foreground focus (e.g. when its capture finishes). Caller
    /// holds the console lock.</summary>
    public void ClearForegroundAgent(string agent)
    {
        if (string.Equals(_foregroundAgent, agent, StringComparison.Ordinal)) _foregroundAgent = null;
    }

    /// <summary>
    /// Foreground the inline Agent View dashboard (v0.12.0 M1, the backslash key). Seeds the
    /// session list from the current live sub-agent snapshot and runs a small key loop ON THE
    /// CALLING THREAD (the mid-turn EscapeKeyListener thread, exactly like the Ctrl+G view
    /// overlay) so there is never a second concurrent key reader. Up/Down move the selection,
    /// Enter foregrounds the selected agent's buffered stream via <paramref name="bodyProvider"/>
    /// + the existing sub-agent expand machinery, Esc/backslash/q close. Rendered inline through
    /// the live region (BuildLiveFrame) - scrollback preserved, no alt-screen takeover. The caller
    /// holds the console lock for the whole session (so concurrent commits defer), and the
    /// snapshot is supplied by the caller because the driver does not own the capture buffers.
    /// No-op (returns false) when there are no running agents or NAV already owns the screen.
    /// </summary>
    public bool EnterAgentView(
        IReadOnlyList<(string Agent, string Status, string Tint)> snapshot,
        Func<string, string?> bodyProvider)
    {
        if (_navActive || _agentViewActive) return false;
        if (snapshot.Count == 0) return false;

        _agentView.SetRows(snapshot, DateTime.UtcNow);
        _agentView.Open();
        _agentViewActive = true;
        try
        {
            Repaint();
            while (true)
            {
                ConsoleKeyInfo key;
                try { key = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException) { break; }

                if (key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.K)
                    { _agentView.Move(-1, DateTime.UtcNow); Repaint(); continue; }
                if (key.Key == ConsoleKey.DownArrow || key.Key == ConsoleKey.J)
                    { _agentView.Move(+1, DateTime.UtcNow); Repaint(); continue; }

                // m: audit the selected agent's mailbox (M4). Commits its message-log rows to the
                // transcript so cross-agent chatter is visible on demand (off by default - nothing
                // shows until the user presses m). No-op when no team mailbox is active.
                if ((key.Key == ConsoleKey.M || key.KeyChar == 'm') && MessageLogProvider is { } mlog)
                {
                    string? sel = _agentView.SelectedAgent(DateTime.UtcNow);
                    if (sel is not null)
                    {
                        var rows = mlog(sel);
                        _agentViewActive = false;
                        _agentView.Close();
                        if (rows.Count == 0)
                            Commit(new[] { $"[{TuiComponents.Dim}]\u00b7 no messages for {sel}[/]" });
                        else
                            Commit(rows);
                        Repaint();
                        return true;
                    }
                    continue;
                }

                // Esc / backslash / q: close the dashboard and return to the foregrounded stream.
                if (key.Key == ConsoleKey.Escape || key.KeyChar == '\\' || key.Key == ConsoleKey.Q)
                    break;

                // Enter: foreground (attach) the selected agent's buffered transcript. Reuses the
                // proven ToggleSubAgentExpanded bounded-panel path so the attached stream renders
                // in-region and keeps growing live; closing the dashboard hands the screen back.
                if (key.Key == ConsoleKey.Enter)
                {
                    string? agent = _agentView.SelectedAgent(DateTime.UtcNow);
                    if (agent is not null)
                    {
                        string body = bodyProvider(agent) ?? "";
                        _agentViewActive = false;
                        _agentView.Close();
                        // Stick Ctrl+E to this agent from now on (issue #1).
                        _foregroundAgent = agent;
                        if (body.Length > 0 && !IsSubAgentExpanded(agent))
                            ToggleSubAgentExpanded(agent, body);
                        Repaint();
                        return true;
                    }
                    break;
                }
            }
        }
        finally
        {
            _agentViewActive = false;
            _agentView.Close();
            Repaint();
        }
        return true;
    }

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

    /// <summary>Flush the held "settling" tool result down to static scrollback (dot frozen) and
    /// retain its expandable block. No-op when nothing is settling. Called from every commit
    /// chokepoint so the next event supersedes the pulse. Caller holds the console lock.</summary>
    public void FlushSettlingResult()
    {
        if (_settling is not ( {} s)) return;
        _settling = null;
        string toolName = string.IsNullOrEmpty(s.Tool) ? "tool" : s.Tool;
        var merged = Lane(TuiComponents.ToolCallResultMerged(s.Tool, s.Args, s.Result, s.Error, s.Expandable, -1));
        if (s.Expandable && merged.Count > 0)
        {
            RetainExpandable(merged[0], toolName, s.ExpandBody ?? s.Result, s.Error);
            for (int k = 1; k < merged.Count; k++) AddTranscriptEntry(new Entry { Collapsed = merged[k] });
            TrimTranscript();
            if (!_navActive) CommitPaint(merged);
        }
        else CommitMirrored(merged);
        _pendingGap = true;
    }

    // --- streaming -----------------------------------------------------------

    public void BeginStream() { FlushSettlingResult(); FlushPendingToolCall(); FlushTableBuffer(); if (_inFence) FlushCodeBuffer(); _streaming = true; _thinkingText = null; _streamTail.Clear(); _streamReasoning = false; _streamBlockDotPending = true; _lastStreamPaintTicks = 0; Repaint(); }

    /// <summary>
    /// Feed a chunk of streamed assistant text. Complete lines (split on '\n') are committed
    /// into scrollback; the trailing partial line is shown live just above the footer so the
    /// user watches it type in real time.
    /// </summary>
    public void StreamChunk(string text, bool reasoning = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Switching between reasoning and answer text: flush whatever partial tail we have under
        // the OLD style first, so the two never share a rendered line. A switch must paint
        // immediately (bypass the stream throttle) so the boundary is shown without delay.
        bool forcePaint = false;
        if (reasoning != _streamReasoning && _streamTail.Length > 0)
        {
            var pending = _streamTail.ToString();
            _streamTail.Clear();
            CommitStreamLine(pending);
            forcePaint = true;
        }
        _streamReasoning = reasoning;
        _streamTail.Append(text);
        bool committed = FlushCompleteStreamLines();
        // A completed line repainted via CommitMirrored already; only the leftover live tail needs
        // a frame. Coalesce those tail-only repaints to a ~30fps budget so a burst of tokens does
        // not trigger a full live-frame rebuild each. A type switch (forcePaint) and EndStream are
        // unthrottled so no boundary or final token is ever left unpainted.
        long now = DateTime.UtcNow.Ticks;
        if (!forcePaint && !committed && now - _lastStreamPaintTicks < StreamPaintIntervalTicks) return;
        _lastStreamPaintTicks = now;
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
    /// <summary>Toggle the team TaskBoard strip (Ctrl+T). No-op visual when no board is active.</summary>
    public bool ToggleTaskBoard()
    {
        _taskBoardVisible = !_taskBoardVisible;
        _taskBoardOffset = 0;
        return _taskBoardVisible;
    }

    /// <summary>Toggle the TaskBoard strip and repaint IN PLACE (Ctrl+T). The strip changes the
    /// live frame height, so this drives a normal diff/erase+repaint via <see cref="Repaint"/>
    /// (the same path the mid-turn Ctrl+E expand uses) rather than a full ClearScreen redraw -
    /// a ClearScreen would re-anchor the frame at the top of the viewport and strand the footer
    /// in scrollback as streaming resumed (the buffer-artifact bug). Safe from the listener
    /// thread: the caller holds the console lock, exactly like every other repaint.</summary>
    public void ToggleTaskBoardRepaint()
    {
        _taskBoardVisible = !_taskBoardVisible;
        _taskBoardOffset = 0;
        Repaint();
    }

    /// <summary>Scroll the open TaskBoard strip by <paramref name="delta"/> rows (Up/Down at the
    /// idle prompt). Clamped to [0, rows-window]; a no-op when the strip is closed or there is no
    /// board. Returns true if it consumed the key (so the caller skips history nav).</summary>
    public bool ScrollTaskBoard(int delta)
    {
        if (!_taskBoardVisible) return false;
        if (TaskBoardProvider?.Invoke() is not { } bd) return false;
        int maxOffset = Math.Max(0, bd.Rows.Count - TaskBoardWindow);
        if (maxOffset == 0) return false;   // nothing to scroll - let the key fall through
        int next = Math.Clamp(_taskBoardOffset + delta, 0, maxOffset);
        if (next == _taskBoardOffset) return true;  // consumed (at an edge) but no repaint needed
        _taskBoardOffset = next;
        Repaint();
        return true;
    }

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
        // Driven by the ~100ms BACKGROUND ticker thread (under ConsoleLock via
        // PushSubAgentActivity). Repaint() now serializes on that same lock, so painting live is
        // safe even while the idle-prompt ReadLine loop is active (_inInput) - the spinner/activity
        // strip animates at the prompt instead of freezing. (g12.25 deferred this paint to dodge a
        // torn-frame race; the real fix was locking the idle-prompt repaints - see Repaint().)
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
        // Always repaint at end-of-stream: the per-chunk live-tail repaint is throttled, so the
        // final partial frame may be stale; this guarantees the last tokens are on screen.
        Repaint();
    }

    private bool FlushCompleteStreamLines()
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
            return true;
        }
        return false;
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
            // Indent reasoning to the same col-2 text margin as the turn header / output dot so the
            // whole agent block reads as one aligned column (no flush-left stagger). Blanks stay bare.
            string r = string.IsNullOrWhiteSpace(raw)
                ? $"[grey italic]{Spectre.Console.Markup.Escape(raw)}[/]"
                : StreamIndent + $"[grey italic]{Spectre.Console.Markup.Escape(raw)}[/]";
            CommitMirrored(Lane(new[] { r }));
            return;
        }
        string built = TuiMarkdown.ToMarkup(raw);
        // Align every answer line to the col-2 text margin (matching the turn header and the dot's
        // text), so continuation lines sit under the message instead of falling back to col 0. The
        // FIRST non-blank line of the block carries the grey lead dot IN PLACE OF the indent (the
        // dot sits at col 0, its text at col 2 - same margin); later lines get a plain 2-col indent.
        // Claude-Code style: one quiet marker per block, never on reasoning, blanks, code, or tables.
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (_streamBlockDotPending)
            {
                _streamBlockDotPending = false;
                built = $"  [{TuiComponents.Muted}]\u25cf[/] " + built;
            }
            else
            {
                built = StreamIndent + built;
            }
        }
        CommitMirrored(Lane(new[] { built }));
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
        {
            // Live partial-tail preview must use the SAME col-2 margin the committed line will get,
            // so the text does not jump left->right when the line finalizes. First non-blank answer
            // line of the block shows the lead dot (its text at col 2); reasoning + later lines get
            // the plain 2-col indent; blanks stay bare.
            string t = _streamTail.ToString();
            string previewBody = _streamReasoning
                ? $"[grey italic]{Spectre.Console.Markup.Escape(t)}[/]"
                : TuiMarkdown.ToMarkup(t);
            string preview;
            if (string.IsNullOrWhiteSpace(t))
                preview = previewBody;
            else if (!_streamReasoning && _streamBlockDotPending)
                preview = $"  [{TuiComponents.Muted}]\u25cf[/] " + previewBody;
            else
                preview = StreamIndent + previewBody;
            lines.Add(preview);
        }

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
            if (_expandKind == ExpandKind.Diff)
            {
                // Render the production diff card, then bound it to the viewport slice (head-anchored:
                // a diff is read top-down) with a "+N more (ctrl+g for full)" footer when it overflows.
                var full = TuiComponents.Diff(_expandTitle, _expandBody, width);
                if (full.Count > maxRows)
                {
                    int shown = Math.Max(1, maxRows - 1);
                    lines.AddRange(full.GetRange(0, shown));
                    lines.Add($"  [{TuiComponents.Dim}]\u2026 +{full.Count - shown} more line(s) (ctrl+g for full)[/]");
                }
                else lines.AddRange(full);
            }
            else
                lines.AddRange(TuiComponents.BoundedLivePanel(
                    _expandTitle, _expandBody, _expandTint, width, maxRows, _expandAnchorTail, _expandError,
                    // Inline Ctrl+E tool-result expansion renders markdown to match the NAV path
                    // (ToolResultPanel markdown:_cardMarkdown) and RenderEntryRows. Diffs use a
                    // separate branch; sub-agent panels already rendered markdown.
                    markdown: _expandKind == ExpandKind.SubAgent
                        || (_expandKind == ExpandKind.ToolResult && _cardMarkdown)));
        }

        // Consolidated sub-agent activity panel takes precedence over the single thinking line:
        // while one or more collapsed sub-agents run, show one animated line each (no flicker).
        // When the backslash dashboard is foregrounded it becomes the SOLE session list, so the
        // compact strip is suppressed to avoid duplicate per-agent rows (issue #2).
        if (_agentViewActive)
        {
            // dashboard owns the agent list this frame; no compact strip / thinking line.
        }
        else if (_subAgents.Count > 0)
            lines.AddRange(TuiComponents.SubAgentActivity(_subAgents, _subAgentFrame));

        // A pending (unresolved) tool call is shown live with a running glyph until its
        // result lands and the two merge into a single committed line.
        if (!_streaming && _pendingTool is { } pt)
            lines.AddRange(Lane(TuiComponents.ToolCall(pt.Tool, pt.Args, _thinkFrame)));

        // Most-recent RESOLVED tool result held live so its completion dot pulses SLOWLY (~1/3 the
        // in-flight cadence). Flushed to static scrollback the instant anything else commits.
        if (!_streaming && _settling is { } sr)
            lines.AddRange(Lane(TuiComponents.ToolCallResultMerged(
                sr.Tool, sr.Args, sr.Result, sr.Error, sr.Expandable, _thinkFrame / 3)));

        // The italic "thinking" indicator renders BELOW the live dot line(s) - the dot is the
        // primary action, the spinner+status is the running tail beneath it (rendering it above the
        // dot looked off when both animate). Only when no sub-agent strip owns the line.
        if (!_agentViewActive && _subAgents.Count == 0 && !_streaming && !string.IsNullOrEmpty(_thinkingText))
            lines.Add(TuiComponents.ThinkingLine(_thinkingText, _thinkFrame));

        // v0.12.0 M1 Agent View: when foregrounded (backslash), the keyboard-navigable session
        // dashboard renders inline just above the rule/footer. The always-on activity strip above
        // still shows, so the dashboard is an expansion of it rather than a replacement. Off (the
        // default) this adds nothing, keeping the frame byte-identical to today's.
        if (_agentViewActive)
            lines.AddRange(_agentView.RenderDashboard(width, DateTime.UtcNow, _subAgentFrame, _foregroundAgent));

        // v0.12.0 M2: the team TaskBoard strip (Ctrl+T). Renders below the agent activity/dashboard
        // and above the rule when toggled on AND a board snapshot is available. Off (or no team) it
        // adds nothing, keeping the frame identical to today.
        if (_taskBoardVisible && TaskBoardProvider?.Invoke() is { } bd)
            lines.AddRange(TuiComponents.TaskBoardStrip(
                bd.Total, bd.Done, bd.InProgress, bd.Blocked, bd.Failed, bd.Rows,
                maxRows: TaskBoardWindow, offset: _taskBoardOffset));

        // Full-width rule separates the transcript from the docked footer (Claude-Code feel).
        lines.Add(TuiComponents.FullRule(width));
        lines.Add(TuiComponents.Footer(_tokens, _threshold, _plan, _ultra, _psub, _sub, _effort,
            modeCycleHint: OnModeCycle is not null, sessionId: _sessionId, cached: _cached,
            sysTokens: _sysTokens, toolTokens: _toolTokens,
            sessionElapsed: DateTime.UtcNow - _sessionStart,
            loopElapsed: _loopStart is { } ls ? DateTime.UtcNow - ls : null,
            giga: _giga));

        if (_inInput)
        {
            // A second full-width rule gives the input/compose area its own visible band,
            // clearly separated from the footer above it.
            lines.Add(TuiComponents.FullRule(width));
            // While reverse-incremental search (Ctrl+R) is active, the readline-style search row
            // replaces the normal input row; accepting/cancelling restores the input row.
            if (_editor.IsSearching)
            {
                lines.Add(TuiComponents.ReverseSearchRow(_editor.SearchQuery, _editor.SearchMatch, width));
                return lines;
            }
            lines.AddRange(TuiComponents.InputRowsWithCursor(_editor.Buffer, _editor.Cursor, _editor.Mode, width, highlight: _inputHighlight));
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
        // Serialize against the ~100ms sub-agent ticker thread (which paints through
        // MuxConsole.ConsoleLock via PushSubAgentActivity). The idle-prompt ReadLine loop runs
        // UNLOCKED (TuiReadLine holds no lock so blocking input never starves the ticker), so its
        // Repaint() previously raced the ticker's SetLive and stranded duplicate footers. The lock
        // is reentrant, so the many callers already under ConsoleLock are unaffected.
        lock (MuxConsole.ConsoleLock)
            PaintNow();
    }

    // --- render-engine routing (inline live-region vs full-frame) -----------
    // These helpers are the ONLY seam between the two renderers. In inline mode they call the
    // native-scrollback LiveRegion exactly as before (byte-identical). In frame mode they route to
    // the alternate-screen FrameRenderer, which re-composes and presents the WHOLE viewport from
    // retained state each time - so a "commit above" is not a distinct physical operation, it is
    // just another present of the (already-Retained) transcript. While the frame engine is
    // SUSPENDED for a blocking prompt (see Suspend/Resume) every present is deferred: retained
    // state still accrues, and Resume()'s invalidate+repaint reflects it all at once.

    /// <summary>Repaint the live view: inline live band, or a full recomposed frame.</summary>
    private void PaintNow()
    {
        if (_engineFrame) PresentFrame();
        else _region.SetLive(BuildLiveFrame(Width));
    }

    /// <summary>Commit finished lines: inline pushes them into native scrollback and repaints the
    /// live band; frame mode just presents (the lines are already retained in <c>_transcript</c>).</summary>
    private void CommitPaint(IReadOnlyList<string> committedLines)
    {
        if (_engineFrame) PresentFrame();
        else _region.CommitAbove(committedLines, BuildLiveFrame(Width));
    }

    /// <summary>Force a clean full repaint (resize / Ctrl+L): inline reflows the visible transcript
    /// window; frame mode invalidates its cache so the next present is a full clear+redraw.</summary>
    private void ForcePaint()
    {
        if (_engineFrame) { _frame.Invalidate(); PresentFrame(); }
        else ReflowNow();
    }

    /// <summary>Hand the terminal back cleanly before a blocking external prompt / mode switch /
    /// exit: inline erases the live band and shows the cursor; frame mode leaves the alternate
    /// screen (restoring the primary buffer + native scrollback verbatim) and LATCHES suspended so
    /// no timer-driven present can re-enter the alt screen while the prompt owns the terminal.</summary>
    private void HandBack()
    {
        if (_engineFrame) { _suspended = true; _frame.Leave(); }
        else _region.Clear();
    }

    /// <summary>Present one full-frame viewport (frame engine only). Deferred while suspended.</summary>
    private void PresentFrame()
    {
        // Hard gate: NOTHING may re-enter the alternate screen while a blocking prompt owns the
        // primary buffer (suspended) or during teardown. The ticker/resize timers race prompt
        // windows, and a single stray present paints the alt screen over a half-drawn Spectre
        // list (the "cut-off prompt" artifact).
        if (_suspended || _shuttingDown) return;
        _frame.Present(ComposeFrameRows());
    }

    /// <summary>
    /// Frame engine only: return from a blocking external prompt. Unlatches the suspend, discards
    /// the cached frame (the prompt drew arbitrary content on the primary buffer; the next present
    /// re-enters the alt screen from a clean slate), and repaints everything retained while
    /// suspended. Inline mode is a no-op - the next status repaint restores its footer as before.
    /// </summary>
    public void Resume()
    {
        if (!_engineFrame || !_suspended) return;
        _suspended = false;
        _promptContextAfterSequence = _transcriptSequence;
        _frame.Invalidate();
        Repaint();
    }

    /// <summary>
    /// Compose the full-frame viewport: the visible transcript tail (retained <c>_transcript</c>
    /// entries re-wrapped at the CURRENT width, bottom-anchored) topped up to fill the space above
    /// the live band, then the live stream/tool/footer/input band pinned at the bottom. Every row is
    /// rendered to ANSI through the SAME wrapper the inline region uses, so frame output is visually
    /// identical to inline output. Honors <see cref="_frameScroll"/>: when the user has paged back,
    /// the transcript window slides up by that many physical rows (a real viewport over retained
    /// history - the frame engine's replacement for native scrollback). Returns exactly
    /// <c>height</c> physical rows.
    /// </summary>
    internal List<string> ComposeFrameRows()
    {
        int h = Math.Max(1, _term.Height);
        // Layout and wrap BOTH use the driver's Width, which in frame mode is already (cols - 1)
        // with the last physical column reserved. Using one width for component layout AND the wrap
        // pass is what keeps full-width rules/panels to exactly one physical row each (the earlier
        // layout-at-cols/wrap-at-cols-1 mismatch split every full-width row and stranded "-"
        // fragments at the left margin).
        int wrapW = Width;

        // Live band (bottom): built and wrapped at the same width.
        var liveRows = new List<string>();
        foreach (var ml in BuildLiveFrame(wrapW))
            liveRows.AddRange(LiveRegion.WrapMarkupLine(ml, wrapW));
        if (liveRows.Count > h)
            liveRows = liveRows.GetRange(liveRows.Count - h, h);  // keep the freshest tail on screen

        int transcriptRoom = h - liveRows.Count;
        var transcriptRows = new List<string>();
        int totalRows = transcriptRoom > 0 ? GetFrameTotalRows(wrapW) : 0;
        if (transcriptRoom > 0)
        {
            // Render retained entries bottom-up until we have enough rows to satisfy the visible
            // window PLUS the requested scroll offset, then slide the window up by the offset.
            // The marker/clamp use the exact cached total above, not this deliberately-bounded
            // render window (the old code treated the partial count as the total and pinned the
            // marker at the top after the first PgUp).
            int maxScroll = Math.Max(0, totalRows - transcriptRoom);
            if (_frameScroll > maxScroll) _frameScroll = maxScroll;
            int want = transcriptRoom + Math.Max(0, _frameScroll);
            for (int e = _transcript.Count - 1; e >= 0 && transcriptRows.Count < want; e--)
                transcriptRows.InsertRange(0, RenderEntryRows(_transcript[e], wrapW));
            int skipTail = Math.Max(0, _frameScroll);
            int end = transcriptRows.Count - skipTail;
            int start = Math.Max(0, end - transcriptRoom);
            transcriptRows = transcriptRows.GetRange(start, Math.Max(0, end - start));
        }

        var rows = new List<string>(h);
        // Anchor transcript content to the BOTTOM of the pane (directly above the live band), so
        // short startup/early-session content sits just over the input box instead of being stranded
        // at the top with a large empty gap below it. Unused rows are inserted ABOVE the transcript;
        // the footer/input stay bottom-pinned. Once history fills the pane this is naturally
        // tail-anchored, and a scrolled-back window (_frameScroll > 0) already carries its own slice.
        for (int i = transcriptRows.Count; i < transcriptRoom; i++) rows.Add("");
        rows.AddRange(transcriptRows);
        rows.AddRange(liveRows);
        if (rows.Count > h) rows = rows.GetRange(rows.Count - h, h);
        while (rows.Count < h) rows.Add("");

        // Keyboard-only scrollback gets a tiny passive position marker in the reserved last
        // column. It has a fixed one-cell size and only moves vertically; there is no full rail,
        // dynamic thumb sizing, mouse hit target, or wheel/click/drag interaction.
        PaintFrameScrollIndicator(rows, transcriptRoom, totalRows);
        return rows;
    }

    /// <summary>Render one retained entry to physical rows at <paramref name="wrapW"/>: expanded
    /// entries get their full panel (diff or tool-result card - the same rendering NAV uses),
    /// collapsed entries their one-line summary, wrapped.</summary>
    private List<string> RenderEntryRows(Entry ent, int wrapW)
    {
        if (ent.Expandable is { } x && ent.Expanded)
        {
            var panel = ent.DiffKind
                ? TuiComponents.Diff(x.Tool, x.Text, wrapW)
                : TuiComponents.ToolResultPanel(x.Tool, x.Text, x.Error, wrapW, expanded: true, markdown: _cardMarkdown);
            var outRows = new List<string>();
            foreach (var l in panel) outRows.AddRange(LiveRegion.WrapMarkupLine(l, wrapW));
            return outRows;
        }
        return LiveRegion.WrapMarkupLine(ent.Collapsed, wrapW);
    }

    private const int FrameScrollIndicatorSize = 1;
    internal static string FrameScrollIndicatorCell()
        => TuiMarkup.ToAnsi($"[{TuiComponents.Accent}]▏[/]");

    /// <summary>Pure placement math for the passive frame-scroll marker. Offset 0 is the live
    /// tail (bottom); <paramref name="maxScroll"/> is the oldest retained position (top).</summary>
    internal static (int Top, int Length) FrameScrollIndicatorPlacement(int scroll, int maxScroll, int trackRows)
    {
        int length = Math.Min(FrameScrollIndicatorSize, Math.Max(0, trackRows));
        if (length == 0) return (0, 0);
        int travel = Math.Max(0, trackRows - length);
        double fraction = Math.Clamp((double)scroll / Math.Max(1, maxScroll), 0.0, 1.0);
        int top = (int)Math.Round((1.0 - fraction) * travel);
        return (top, length);
    }

    /// <summary>Paint a fixed-size passive marker in the reserved physical column. Every transcript
    /// row is padded to the content width and receives an explicit final cell in both marker and
    /// no-marker states, so moving/hiding the marker always overwrites its previous cells.</summary>
    private void PaintFrameScrollIndicator(List<string> rows, int transcriptRoom, int totalRows)
    {
        int trackRows = Math.Min(transcriptRoom, rows.Count);
        int maxScroll = Math.Max(0, totalRows - transcriptRoom);
        bool visible = _userScrolled && _frameScroll > 0 && maxScroll > 0 && trackRows > 0;
        var placement = FrameScrollIndicatorPlacement(_frameScroll, maxScroll, trackRows);

        for (int i = 0; i < trackRows; i++)
        {
            string plain = System.Text.RegularExpressions.Regex.Replace(rows[i], "\u001b\\[[0-9;?]*[A-Za-z]", "");
            int pad = Width - TuiMarkup.Width(plain);
            if (pad > 0) rows[i] += new string(' ', pad);
            if (pad < 0) continue;

            bool marker = visible && i >= placement.Top && i < placement.Top + placement.Length;
            rows[i] += marker ? FrameScrollIndicatorCell() : " ";
        }
    }

    // Frame-engine viewport scroll offset, in physical rows above the live tail (0 = pinned to the
    // newest content). PgUp/PgDn / Ctrl+U/Ctrl+D at the prompt adjust it; any commit/stream keeps
    // the offset (the user is reading history) until they page back to 0 or press End/Esc. Clamped
    // in ComposeFrameRows to the oldest retained row.
    private int _frameScroll;

    // True once the user has actively paged the frame viewport (Ctrl+U/D, PgUp/Dn, Ctrl+B/F) this
    // session. Gates the passive scroll marker and the Esc/End snap-to-tail so a seeded startup
    // offset (a tall splash opened at its top) never lights the marker or swallows the first Esc.
    // Reset when the viewport returns to the live tail (snap or submit).
    private bool _userScrolled;

    /// <summary>Frame engine: scroll the viewport by <paramref name="rows"/> physical rows
    /// (positive = back in history). Returns true when the offset changed (caller repaints).</summary>
    internal bool FrameScrollBy(int rows)
    {
        if (!_engineFrame) return false;
        int prev = _frameScroll;
        _frameScroll = Math.Max(0, _frameScroll + rows);
        // Upper clamp happens in ComposeFrameRows (needs the wrapped row count). Return the actual
        // post-clamp movement so paging at the oldest boundary does not trigger redundant repaints.
        if (rows > 0) _ = ComposeFrameRows();
        // A key-driven page into history arms the passive marker + Esc/End snap. A seeded startup
        // offset (CommitStartup, which bypasses this method) never sets it, so a tall splash opened
        // at its top shows no marker and does not swallow the first Esc.
        if (_frameScroll > 0 && _frameScroll != prev) _userScrolled = true;
        return _frameScroll != prev;
    }

    /// <summary>
    /// v0.12.4 Option A - build the viewport-bounded window of RECENT transcript, re-wrapped at the
    /// CURRENT terminal width, that a resize repaint reflows. This is the reflow: <c>_transcript</c>
    /// holds the logical markup lines (width-independent); <see cref="TuiMarkup.WrapMarkup"/> re-wraps
    /// each at the live width, so a narrow-&gt;wide (or wide-&gt;narrow) drag re-lays-out the on-screen
    /// text instead of preserving its old hard-wrap geometry. Only the last viewport-worth of entries
    /// is wrapped (older history stays in immutable native scrollback, which no terminal can reflow),
    /// so the work is bounded regardless of transcript length.
    /// </summary>
    private List<string> BuildReflowWindow()
    {
        int wrapW = Math.Max(1, Width - 1);   // never render into the last column (no soft-wrap)
        int room = Math.Max(1, Height);       // at most a viewport of transcript rows is ever visible
        var rows = new List<string>();
        // Walk entries newest-first, wrapping each at the current width, until we have a viewport's
        // worth of physical rows; then keep the freshest `room` rows. Bounded work: we stop early.
        // Use LiveRegion.WrapMarkupLine (markup -> wrapped ANSI rows) - the SAME renderer the live
        // band uses - so the reflowed transcript is real ANSI, not literal "[#RRGGBB]" markup tokens.
        // (TuiMarkup.WrapMarkup returns MARKUP slices for NAV, which re-parses them; writing those
        // straight to the terminal printed the raw tags and bloated every row's width.)
        for (int e = _transcript.Count - 1; e >= 0 && rows.Count < room; e--)
            rows.InsertRange(0, LiveRegion.WrapMarkupLine(_transcript[e].Collapsed, wrapW));
        if (rows.Count > room) rows = rows.GetRange(rows.Count - room, room);
        return rows;
    }

    /// <summary>Reflow repaint for a resize / manual redraw: re-wrap the recent transcript window at
    /// the new width and repaint it plus the live band as one bottom-anchored atomic frame.</summary>
    private void ReflowNow()
    {
        lock (MuxConsole.ConsoleLock)
            _region.ReflowRepaint(BuildReflowWindow(), BuildLiveFrame(Width));
    }

    // Last terminal size observed by the resize poll (see PollResize). -1 until first checked.
    private int _lastSeenWidth = -1;
    private int _lastSeenHeight = -1;

    /// <summary>
    /// Resize poll: called on a timer (~100ms). Detects a terminal width/height change and, when
    /// one is seen, forces a clean full repaint of the live region - the only reliable way to clear
    /// the artifacts a buffer reflow leaves on a width change (conhost + Windows Terminal both
    /// reflow already-emitted rows). No-op when the size is unchanged, during NAV (which owns the
    /// screen), or while shutting down, so it is cheap to call frequently.
    /// </summary>
    public void PollResize()
    {
        if (_shuttingDown || _navActive) return;
        if (_engineFrame && _suspended) return;   // a blocking prompt owns the terminal; defer
        int w = _term.Width, h = _term.Height;
        if (w == _lastSeenWidth && h == _lastSeenHeight) return;
        bool first = _lastSeenWidth < 0;
        _lastSeenWidth = w;
        _lastSeenHeight = h;
        if (first) return;        // just record the baseline on the first tick; nothing to redraw
        // Inline: v0.12.4 Option A reflow of the visible transcript window at the new width.
        // Frame: invalidate + full recomposed present at the new geometry (no reflow seam at all).
        ForcePaint();
    }

    /// <summary>
    /// Manual redraw (Ctrl+L): clear the viewport and repaint the live region from scratch,
    /// discarding any artifacts. Never cancels the turn. Safe mid-stream or at the prompt.
    /// </summary>
    public void ForceRedraw()
    {
        if (_shuttingDown || _navActive) return;
        // Re-sync the size baseline so the poll does not immediately re-fire after a manual redraw.
        _lastSeenWidth = _term.Width;
        _lastSeenHeight = _term.Height;
        ForcePaint();
    }

    // --- bracketed paste -----------------------------------------------------

    private static readonly char[] _pasteOpenTail = { '[', '2', '0', '0', '~' };
    private static readonly char[] _pasteCloseTail = { '[', '2', '0', '1', '~' };

    // Called right after an ESC was read. Probe the next queued chars for the "[200~" opener tail.
    // On a full match the chars are consumed (returns true). On any mismatch the probed keys are
    // pushed to the unget queue (replayed as normal input) and the ESC keeps its normal meaning.
    private bool TryConsumePasteOpen() => MatchTail(_pasteOpenTail);

    private bool MatchTail(char[] tail)
    {
        var probed = new List<ConsoleKeyInfo>(tail.Length);
        for (int i = 0; i < tail.Length; i++)
        {
            ConsoleKeyInfo k;
            if (_ungetq.Count > 0) k = _ungetq.Dequeue();
            else if (TryReadKeyNonBlocking(out k)) { }
            else { foreach (var p in probed) _ungetq.Enqueue(p); return false; }
            probed.Add(k);
            if (k.KeyChar != tail[i]) { foreach (var p in probed) _ungetq.Enqueue(p); return false; }
        }
        return true;
    }

    private static bool TryReadKeyNonBlocking(out ConsoleKeyInfo key)
    {
        try
        {
            if (Console.KeyAvailable) { key = Console.ReadKey(intercept: true); return true; }
        }
        catch { /* not a real console */ }
        key = default;
        return false;
    }

    private string DrainBracketedPaste()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo k;
            if (_ungetq.Count > 0) k = _ungetq.Dequeue();
            else if (!TryReadKeyNonBlocking(out k))
            {
                System.Threading.Thread.Sleep(2);   // settle wait for the rest of a large paste
                if (!TryReadKeyNonBlocking(out k)) break;
            }
            if (k.KeyChar == '\u001b' && MatchTail(_pasteCloseTail)) break;   // ESC[201~ terminator
            if (k.Key == ConsoleKey.Enter || k.KeyChar == '\r' || k.KeyChar == '\n') { sb.Append('\n'); continue; }
            if (k.KeyChar != '\0') sb.Append(k.KeyChar);
        }
        return sb.ToString().Replace("\r\n", "\n").Replace("\r", "\n");
    }

    // Drain the remainder of a burst (raw-keystroke paste with no DECSET markers). Reads only what
    // is ALREADY buffered - it never blocks waiting for a human - so it stops the instant the burst
    // ends. Enters become literal newlines; printables append; control keys are dropped. A short
    // settle wait bridges the tiny gap between chunks of a large paste still streaming in.
    private string DrainBurstPaste()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo k;
            if (!TryReadKeyNonBlocking(out k))
            {
                System.Threading.Thread.Sleep(2);   // bridge inter-chunk gap of a large paste
                if (!TryReadKeyNonBlocking(out k)) break;
            }
            if (k.Key == ConsoleKey.Enter || k.KeyChar == '\r' || k.KeyChar == '\n') { sb.Append('\n'); continue; }
            if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        return sb.ToString().Replace("\r\n", "\n").Replace("\r", "\n");
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
        BeginPromptContext();
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
                if (_ungetq.Count > 0) { key = _ungetq.Dequeue(); }
                else if (Voice.VoiceSession.IsActive)
                {
                    // /voice on: poll instead of blocking so the loop can service transcript
                    // injects, voice-driven submits, and the indicator animation between keys.
                    ConsoleKeyInfo? polled = null;
                    int frame = 0;
                    while (true)
                    {
                        // Drain voice work first so dictation lands even while no key arrives.
                        bool changed = DrainVoice(out var submitted);
                        if (submitted is not null) return submitted;
                        try { if (Console.KeyAvailable) { polled = Console.ReadKey(intercept: true); break; } }
                        catch (InvalidOperationException)
                        {
                            _inInput = false;
                            HandBack();
                            return Console.ReadLine();
                        }
                        // ~10 fps indicator animation while listening/hearing/transcribing.
                        if (changed || (++frame % 3) == 0) Repaint();
                        Thread.Sleep(33);
                        if (!Voice.VoiceSession.IsActive)
                        {
                            Repaint();   // voice turned off elsewhere - restore the normal caret
                            break;
                        }
                    }
                    if (polled is null) continue;
                    key = polled.Value;
                }
                else
                {
                    try { key = Console.ReadKey(intercept: true); }
                    catch (InvalidOperationException)
                    {
                        // stdin not a real console (shouldn't happen in TUI) - fall back.
                        _inInput = false;
                        HandBack();
                        return Console.ReadLine();
                    }
                }

                // Bracketed paste (DECSET 2004): the terminal brackets a paste with ESC[200~ ... 
                // ESC[201~. On a confirmed opener we drain the body (newlines kept literal) until
                // the closer and insert it all at once, so a multi-line paste no longer submits on
                // its first newline. A bare Esc (no paste body) falls through to the editor unchanged.
                if (_bracketedPaste && key.KeyChar == '\u001b' && TryConsumePasteOpen())
                {
                    string pasted = DrainBracketedPaste();
                    if (pasted.Length > 0)
                    {
                        _editor.InsertText(pasted);
                        _paletteSel = -1;
                        if (!KeyQueued() && _ungetq.Count == 0) Repaint();
                    }
                    continue;
                }

                // Burst-paste heuristic (cross-platform fallback to DECSET 2004). When an Enter is
                // read and more input is ALREADY buffered, it is the interior of a fast burst - a
                // paste, not a human keystroke (a person cannot have the next key queued in the same
                // instant). Treat it as a literal newline and absorb the rest of the burst into the
                // compose buffer; a standalone Enter (nothing queued) still submits normally. Only
                // active when bracketedPaste is enabled and the editor is in plain Insert mode.
                if (_bracketedPaste && key.Key == ConsoleKey.Enter
                    && (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) == 0
                    && _editor.Mode == EditorMode.Insert && !_editor.IsSearching
                    && _ungetq.Count == 0 && KeyQueued())
                {
                    string burst = DrainBurstPaste();
                    _editor.InsertText("\n" + burst);
                    _paletteSel = -1;
                    if (!KeyQueued()) Repaint();
                    continue;
                }

                // Reverse-incremental history search (Ctrl+R, readline/bash style). While active it
                // OWNS every keystroke: printable keys refine the query, Ctrl+R steps to older
                // matches, Enter accepts+submits, Esc accepts into the buffer, Ctrl+C/Ctrl+G cancel.
                if (_editor.IsSearching)
                {
                    var rs = _editor.SearchFeed(key);
                    switch (rs)
                    {
                        case ReverseSearchSignal.AcceptAndSubmit:
                        {
                            string line = _editor.Buffer;
                            _editor.Remember(line);
                            _inInput = false;
                            _pendingGap = false;
                            if (!TuiCommands.OpensInteractivePrompt(line))
                                CommitMirrored(TuiComponents.UserEcho(line));
                            return line;
                        }
                        case ReverseSearchSignal.Accept:
                        case ReverseSearchSignal.Cancel:
                            _paletteSel = -1;
                            Repaint();
                            continue;
                        case ReverseSearchSignal.Redraw:
                        default:
                            Repaint();
                            continue;
                    }
                }

                // Ctrl+R at the prompt: enter reverse-incremental history search. No-op (falls
                // through) when there is no history yet, so the key is never silently swallowed.
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.R
                    && _editor.History.Count > 0)
                {
                    _editor.BeginReverseSearch();
                    if (_editor.IsSearching) { _paletteSel = -1; Repaint(); continue; }
                }

                // Palette navigation intercept: when an autocomplete preview is open, Up/Down
                // move the highlighted candidate (instead of browsing command history), and
                // Enter on a highlighted row accepts it (instead of submitting the line). Tab
                // always accepts. This is the bridge toward full vim-style nav.
                //
                // EXCEPTION: when the buffer holds a line just RECALLED from history (e.g. a
                // previously-run slash command), Up/Down must keep browsing history rather than be
                // captured by the palette - otherwise history gets stuck the moment a recalled entry
                // starts with '/'. The palette re-engages as soon as the recalled line is edited.
                if (PaletteOpen && !_editor.RecalledFromHistory)
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

                // Up/Down while the Ctrl+T TaskBoard strip is open AND the input buffer is empty:
                // scroll the board's windowed task list instead of browsing command history, so a
                // long board's lower tasks become reachable. Only consumes the key when the strip
                // actually scrolled (more rows than the window); otherwise falls through to normal
                // history nav. Closed board / non-empty buffer => arrows behave exactly as before.
                if (_taskBoardVisible && _editor.IsEmpty
                    && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift)) == 0)
                {
                    if (key.Key == ConsoleKey.UpArrow && ScrollTaskBoard(-1)) continue;
                    if (key.Key == ConsoleKey.DownArrow && ScrollTaskBoard(+1)) continue;
                    if (key.Key == ConsoleKey.PageUp && ScrollTaskBoard(-TaskBoardWindow)) continue;
                    if (key.Key == ConsoleKey.PageDown && ScrollTaskBoard(+TaskBoardWindow)) continue;
                }

                // Ctrl+E at the prompt: when sub-agents are live (e.g. a /background or /swarm
                // panel), target THAT live panel - expand/collapse it in place like the mid-turn
                // EscapeKeyListener path - so the running panel can be closed from the prompt
                // instead of falling into the transcript NAV overlay. Otherwise open the NAV overlay
                // on the most-recent large tool result, pre-expanded (reversible). Falls through to
                // the editor's emacs Ctrl+E (end-of-line) when neither applies.
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.E)
                {
                    if (_subAgents.Count > 0 && OnSubAgentExpand is { } expandSub)
                    {
                        expandSub();
                        Repaint();
                        continue;
                    }
                    if (_transcript.Any(e => e.Expandable is not null))
                    {
                        ExpandLastBlock();
                        Repaint();
                        continue;
                    }
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

                // Ctrl+L at the prompt: clear any resize/redraw artifacts and repaint the live
                // region from scratch. Does not cancel or submit.
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.L)
                {
                    ForceRedraw();
                    continue;
                }

                // Frame engine only: page a REAL viewport over the retained transcript - the frame
                // engine's replacement for the native scrollback the alt screen forfeits. Binds
                // MIRROR the standard NAV pager so muscle memory carries over: Ctrl+U/Ctrl+D step
                // by console.scrollSpeedRows rows (default 1), Ctrl+B/Ctrl+F = full page, PgUp/PgDn
                // = full page (fallback), and Esc/End snap back to the live tail while paged. A tiny fixed-size passive marker
                // moves in the reserved last column to show position. Inline mode ignores all of
                // these
                // (native scrollback already works there). Ctrl+U keeps its kill-to-start editing
                // role whenever the input buffer is non-empty - paging only owns it on an empty
                // buffer, same as the Esc-opens-NAV convention.
                if (_engineFrame)
                {
                    bool ctrlMod = (key.Modifiers & ConsoleModifiers.Control) != 0;
                    int full = Math.Max(1, Height - 2);
                    int delta = 0;
                    if (key.Key == ConsoleKey.PageUp || (ctrlMod && key.Key == ConsoleKey.B)) delta = full;
                    else if (key.Key == ConsoleKey.PageDown || (ctrlMod && key.Key == ConsoleKey.F)) delta = -full;
                    else if (ctrlMod && key.Key == ConsoleKey.U && _editor.Buffer.Length == 0) delta = _scrollSpeedRows;
                    else if (ctrlMod && key.Key == ConsoleKey.D && _editor.Buffer.Length == 0) delta = -_scrollSpeedRows;
                    else if (_userScrolled && _frameScroll > 0 && (key.Key == ConsoleKey.End || key.Key == ConsoleKey.Escape))
                    {
                        _frameScroll = 0;
                        _userScrolled = false;
                        Repaint();
                        continue;
                    }
                    if (delta != 0)
                    {
                        if (FrameScrollBy(delta)) Repaint();
                        continue;
                    }
                }

                // Ctrl+T at the prompt: toggle the team TaskBoard strip (v0.12.0 M2). No-op visual
                // when no team board is active. Does not cancel or submit.
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.T)
                {
                    ToggleTaskBoard();
                    Repaint();
                    continue;
                }

                // Backslash at the prompt opens the Agent View dashboard - but ONLY when it is the
                // FIRST char (empty buffer), so the user can still type a literal '\\' mid-line.
                // No-op fall-through (insert the char) when no agents are running or the opener is
                // unset, so the key is never silently swallowed. Mirrors the mid-turn '\\' path.
                if (key.KeyChar == '\\' && _editor.IsEmpty && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0)
                {
                    if (AgentViewOpener is { } open && open()) { Repaint(); continue; }
                    // No live sub-agents to foreground: offer the detached-session picker. If it
                    // yields an /attach command, submit it as the line so the menu's /attach
                    // dispatch re-enters that parked session.
                    if (AttachPicker is { } pick && pick() is { } attachCmd)
                    {
                        _inInput = false;
                        return attachCmd;
                    }
                    // else: fall through and insert '\\' as a normal character.
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
                        _frameScroll = 0;   // submitting always returns the frame viewport to the live tail
                        _userScrolled = false;
                        // Erase input box, then echo the submitted line into scrollback with a
                        // leading blank + accent gutter so each turn is clearly delimited.
                        _pendingGap = false;
                        // Suppress the echo for bare commands that open a blocking interactive
                        // picker (e.g. /set, /swap): the picker draws its own UI and the handler
                        // prints a confirmation, so echoing the bare command line just leaves
                        // residue above the prompt. All other lines echo normally.
                        if (!TuiCommands.OpensInteractivePrompt(line))
                            CommitMirrored(TuiComponents.UserEcho(line));
                        return line;
                    }
                    case LineEditSignal.Cancel:
                        _inInput = false;
                        HandBack();
                        return null;
                    case LineEditSignal.Eof:
                        _inInput = false;
                        HandBack();
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

    /// <summary>
    /// Drain queued voice transcripts into the line editor and honor a pending voice submit.
    /// MUST run on the input thread (mutates the editor). Returns true when the buffer changed;
    /// when a submit fires with a non-empty buffer, <paramref name="submitted"/> carries the line
    /// exactly as the Enter path would return it (echoed + remembered) and ReadLine returns it.
    /// </summary>
    private bool DrainVoice(out string? submitted)
    {
        submitted = null;
        bool changed = _voiceDirty;
        _voiceDirty = false;
        while (_voiceInject.TryDequeue(out var text))
        {
            // Separate from existing content with a space, like the web app's mic append.
            if (!_editor.IsEmpty && !_editor.Buffer.EndsWith(' ') && !_editor.Buffer.EndsWith('\n'))
                _editor.InsertText(" ");
            _editor.InsertText(text);
            changed = true;
        }
        if (_voiceSubmit)
        {
            _voiceSubmit = false;
            string line = _editor.Buffer.Trim();
            if (line.Length > 0)
            {
                _editor.Remember(line);
                _inInput = false;
                _pendingGap = false;
                if (!TuiCommands.OpensInteractivePrompt(line))
                    CommitMirrored(TuiComponents.UserEcho(line));
                submitted = line;
                return true;
            }
        }
        return changed;
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
                {
                    var panel = ent.DiffKind
                        ? TuiComponents.Diff(x.Tool, x.Text, Width)
                        : TuiComponents.ToolResultPanel(x.Tool, x.Text, x.Error, Width, expanded: true, markdown: _cardMarkdown);
                    foreach (var l in panel) { disp.Add(l); owner.Add(e); }
                }
                else
                {
                    // Wrap long prose rows to the viewport so they are fully readable in NAV
                    // (expanded tool panels are already pre-wrapped to Width by ToolResultPanel;
                    // unexpandable prose was emitted as one long row and clipped at the edge).
                    foreach (var wl in TuiMarkup.WrapMarkup(ent.Collapsed, Math.Max(1, Width - 1)))
                    { disp.Add(wl); owner.Add(e); }
                }
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
            // In frame mode the FrameRenderer already OWNS the alternate screen, so NAV must not
            // nest another EnterAltScreen (?1049h is not a stack - a later single LeaveAltScreen
            // would then pop us to the primary buffer while the frame renderer still believed it
            // owned the alt screen). NAV just paints over the shared alt screen and, on exit,
            // invalidates the frame so it fully redraws. Inline mode enters/leaves as before.
            if (!_engineFrame) _term.Write(Ansi.EnterAltScreen);
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
                    InvalidateFrameRowCounts();
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
            InvalidateFrameRowCounts();
            _editor.SetMode(EditorMode.Insert);
        }
        finally
        {
            // Restore the primary screen buffer (scrollback intact) and repaint one fresh live
            // frame reflecting everything that streamed/committed (deferred) while NAV was open.
            _term.Write(Ansi.AutoWrapOn);
            // Inline mode pops back to the primary buffer; frame mode STAYS on the shared alt
            // screen (the frame renderer owns it) and force-invalidates so the deferred stream/
            // commit history that accrued while NAV was open is fully redrawn on the next present.
            if (_engineFrame) _frame.Invalidate();
            else _term.Write(Ansi.LeaveAltScreen);
            try { Console.TreatControlCAsInput = prevCtrlC; } catch { /* ignore */ }
            _navActive = false;
            Repaint();
        }
    }

    /// <summary>
    /// Clear the live region and hand the terminal back cleanly (cursor shown, no residue).
    /// Called before any blocking external prompt, mode switch, or exit. Idempotent.
    /// </summary>
    public void Suspend() { FlushSettlingResult(); FlushPendingToolCall(); HandBack(); }

    /// <summary>Mark the transcript boundary for context produced by the next submitted command or
    /// turn. A subsequent bare text prompt can replay only that newly-produced context after frame
    /// mode leaves the alternate screen.</summary>
    internal void BeginPromptContext() => _promptContextAfterSequence = _transcriptSequence;

    /// <summary>Frame-mode bridge for a blocking bare Spectre text prompt. Leaves the alternate
    /// screen, then writes transcript entries committed since the current prompt-context watermark
    /// onto the restored primary buffer before Spectre draws its input line. This preserves lists
    /// and explanatory panels from commands such as /setmodel, /swap and /provider. Inline mode is
    /// identical to Suspend().</summary>
    public void SuspendForPrompt()
    {
        FlushSettlingResult();
        FlushPendingToolCall();
        HandBack();
        if (!_engineFrame) return;

        var replay = new List<string>();
        foreach (var entry in _transcript)
        {
            if (entry.Sequence <= _promptContextAfterSequence) continue;
            replay.AddRange(RenderEntryRows(entry, Width));
        }
        _promptContextAfterSequence = _transcriptSequence;
        if (replay.Count == 0) return;
        int maxReplayRows = Math.Max(20, Height * 4);
        if (replay.Count > maxReplayRows)
        {
            replay = replay.GetRange(replay.Count - maxReplayRows + 1, maxReplayRows - 1);
            replay.Insert(0, TuiMarkup.ToAnsi($"[{TuiComponents.Dim}]… earlier prompt context omitted[/]"));
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(Ansi.AutoWrapOff);
        // Each replayed row is framed CR..CRLF: the leading \r returns to column 0 (the cursor is
        // wherever the restored primary buffer left it after HandBack), EraseLine clears the row, and
        // the trailing \r\n prevents column drift/stair-stepping on stacks where LF is not implicitly
        // CR+LF (raw ssh/tmux, some VT passthrough).
        foreach (var row in replay) sb.Append('\r').Append(Ansi.EraseLine).Append(row).Append("\r\n");
        sb.Append(Ansi.AutoWrapOn);
        _term.Write(sb.ToString());
        _term.Flush();
    }

    /// <summary>
    /// Full teardown for process exit / mode switch: clear the live region, show the cursor.
    /// Idempotent and exception-safe so it is safe on ProcessExit / CancelKeyPress.
    /// </summary>
    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try { if (_bracketedPaste) { _term.Write(Ansi.BracketedPasteOff); _term.Flush(); } } catch { /* ignore */ }
        try { HandBack(); } catch { /* ignore */ }
    }
}
