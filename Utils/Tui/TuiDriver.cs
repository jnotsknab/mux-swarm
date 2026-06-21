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
    private bool _plan, _ultra, _psub;
    private string? _effort;   // reasoning-effort chip (low/med/high), null = hidden
    private string? _sessionId; // active session id badge, null = hidden

    // streaming state - partial (un-newlined) tail shown live above the footer
    private readonly StringBuilder _streamTail = new();
    private bool _streaming;

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
        _region.CommitAbove(lines, BuildLiveFrame(Width));
    }

    // input state - true only inside ReadLine's raw-mode loop
    private bool _inInput;
    private bool _shuttingDown;

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

    /// <summary>Update the context meter / mode badges and repaint the live region.</summary>
    public void SetFooter(uint tokens, uint threshold, bool plan, bool ultra, bool psub, uint cached = 0)
    {
        _tokens = tokens; _threshold = threshold; _plan = plan; _ultra = ultra; _psub = psub; _cached = cached;
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
            _region.CommitAbove(merged, BuildLiveFrame(Width));
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
    /// Expand the most-recent large tool result INLINE (commit its full panel above the live
    /// region) without entering the NAV overlay. Used mid-stream - the agent is still producing
    /// output, so we cannot take over the screen with NAV; instead we drop the full panel into
    /// scrollback above the footer. Thread-safe via the console lock at the call site.
    /// </summary>
    public bool ExpandLatestInline()
    {
        Entry? target = null;
        for (int i = _transcript.Count - 1; i >= 0; i--)
            if (_transcript[i].Expandable is not null) { target = _transcript[i]; break; }
        if (target?.Expandable is not { } blk) return false;
        // Commit the full panel above the region (mirrors into transcript so NAV still has it).
        CommitMirrored(Lane(TuiComponents.ToolResultPanel(blk.Tool, blk.Text, blk.Error, Width, expanded: true)));
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

    // --- streaming -----------------------------------------------------------

    public void BeginStream() { FlushPendingToolCall(); FlushTableBuffer(); if (_inFence) FlushCodeBuffer(); _streaming = true; _thinkingText = null; _streamTail.Clear(); Repaint(); }

    /// <summary>
    /// Feed a chunk of streamed assistant text. Complete lines (split on '\n') are committed
    /// into scrollback; the trailing partial line is shown live just above the footer so the
    /// user watches it type in real time.
    /// </summary>
    public void StreamChunk(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
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
        CommitMirrored(Lane(new[] { TuiMarkdown.ToMarkup(raw) }));
    }

    /// <summary>Emit the one-line separator owed after a tool block, if any.</summary>
    private void ConsumeGap()
    {
        if (!_pendingGap) return;
        _pendingGap = false;
        CommitMirrored(new[] { "" });
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
            lines.Add(TuiMarkdown.ToMarkup(_streamTail.ToString()));

        if (!_streaming && !string.IsNullOrEmpty(_thinkingText))
            lines.Add(TuiComponents.ThinkingLine(_thinkingText, _thinkFrame));

        // A pending (unresolved) tool call is shown live with a running glyph until its
        // result lands and the two merge into a single committed line.
        if (!_streaming && _pendingTool is { } pt)
            lines.AddRange(Lane(TuiComponents.ToolCall(pt.Tool, pt.Args)));

        // Full-width rule separates the transcript from the docked footer (Claude-Code feel).
        lines.Add(TuiComponents.FullRule(width));
        lines.Add(TuiComponents.Footer(_tokens, _threshold, _plan, _ultra, _psub, _effort,
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

        int viewH = Math.Max(3, _term.Height - 3);   // transcript rows shown at once

        // Build the flat DISPLAY-line list + a per-line owning-entry map, expanding open
        // entries into their full panels. Rebuilt whenever an entry is toggled.
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
            return (disp, owner);
        }

        /// <summary>First display-line index of entry e in the freshly-built list.</summary>
        int FirstLineOf(List<int> owner, int e)
        {
            for (int i = 0; i < owner.Count; i++) if (owner[i] == e) return i;
            return Math.Max(0, owner.Count - 1);
        }

        var (disp0, owner0) = Build();
        // The cursor is a DISPLAY-LINE index, so j/k step line-by-line - including through the
        // body of an expanded card (fixing the "jumps to the next card / no side-scroll" bug).
        int cursor = focusEntry >= 0 ? FirstLineOf(owner0, focusEntry) : disp0.Count - 1;
        int top = 0;

        void Paint(List<string> disp, List<int> owner)
        {
            if (cursor < 0) cursor = 0;
            if (cursor >= disp.Count) cursor = disp.Count - 1;
            int maxTop = Math.Max(0, disp.Count - viewH);
            if (cursor < top) top = cursor;
            else if (cursor >= top + viewH) top = cursor - viewH + 1;
            if (top > maxTop) top = maxTop;
            if (top < 0) top = 0;

            var frame = new List<string>();
            int endLine = Math.Min(disp.Count, top + viewH);
            for (int i = top; i < endLine; i++)
                frame.Add(i == cursor
                    ? $"[{TuiComponents.Warn}]\u2503[/]{disp[i]}"   // highlight the exact cursor line
                    : $" {disp[i]}");
            frame.Add(TuiComponents.FullRule(Width));
            var ent = _transcript[owner[cursor]];
            string expandHint = ent.Expandable is not null
                ? (ent.Expanded ? "  [#787878]ctrl+e/enter collapse[/]" : "  [#787878]ctrl+e/enter expand[/]")
                : "";
            frame.Add($"  [{TuiComponents.Warn}]\u25c6[/] [black on {TuiComponents.Warn}] NAV [/] "
                + $"[{TuiComponents.Dim}]{cursor + 1}/{disp.Count}  j/k move  ctrl+d/u page  g/G ends  q exit[/]{expandHint}");
            _region.SetLive(frame);
        }

        var (disp, owner) = (disp0, owner0);
        Paint(disp, owner);
        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { break; }
            bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
            bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
            int last = disp.Count - 1;
            int half = Math.Max(1, viewH / 2);

            if (key.Key == ConsoleKey.Q || key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.I)
                break;
            else if (key.Key == ConsoleKey.J || key.Key == ConsoleKey.DownArrow) cursor = Math.Min(last, cursor + 1);
            else if (key.Key == ConsoleKey.K || key.Key == ConsoleKey.UpArrow)   cursor = Math.Max(0, cursor - 1);
            else if (ctrl && key.Key == ConsoleKey.D) cursor = Math.Min(last, cursor + half);
            else if (ctrl && key.Key == ConsoleKey.U) cursor = Math.Max(0, cursor - half);
            else if ((ctrl && key.Key == ConsoleKey.F) || key.Key == ConsoleKey.PageDown) cursor = Math.Min(last, cursor + viewH);
            else if ((ctrl && key.Key == ConsoleKey.B) || key.Key == ConsoleKey.PageUp)   cursor = Math.Max(0, cursor - viewH);
            else if (key.Key == ConsoleKey.Home || (key.Key == ConsoleKey.G && !shift)) cursor = 0;
            else if (key.Key == ConsoleKey.End || (key.Key == ConsoleKey.G && shift)) cursor = last;
            else if ((ctrl && key.Key == ConsoleKey.E) || key.Key == ConsoleKey.Enter)
            {
                int e = owner[cursor];
                var ent = _transcript[e];
                if (ent.Expandable is null) continue;
                ent.Expanded = !ent.Expanded;
                // Rebuild and re-anchor the cursor to the toggled entry's first line so the
                // view stays put on the card the user just opened/closed.
                (disp, owner) = Build();
                cursor = FirstLineOf(owner, e);
                Paint(disp, owner);
                continue;
            }
            else continue;
            Paint(disp, owner);
        }

        // Leaving NAV: collapse all entries again (keep live scrollback compact) and return to
        // Insert mode at the live input prompt.
        foreach (var e in _transcript) e.Expanded = false;
        _editor.SetMode(EditorMode.Insert);
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
