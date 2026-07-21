namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Pure builders that turn render events (session header, tool call/result, diff,
/// delegation, status footer, etc.) into lists of Spectre-compatible markup lines for the
/// <see cref="TuiDriver"/> to commit into scrollback or pin in the live region. No console
/// I/O and no Spectre objects, so every component's layout is unit-testable. Colors mirror
/// the palette used by the classic renderer for visual continuity.
/// </summary>
internal static class TuiComponents
{
    // Palette: foreground accent/semantic roles AND the background shades (card/input/diff fills)
    // resolve from the ACTIVE theme (Theme.cs) so /theme fully recolors the live footer/badges/panels
    // and the docked band. Remaining structural fg (Dim/Border/badge tints/gutter/lane colors below)
    // stay fixed UI semantics. Default theme reproduces the prior palette + shades exactly.
    public static string Accent  => Theme.Active.Accent;
    public static string Agent   => Theme.Active.Agent;
    public static string Ok      => Theme.Active.Success;
    public static string Warn    => Theme.Active.Warning;
    public static string Err     => Theme.Active.Error;
    public static string Muted   => Theme.Active.Muted;
    public const string Dim     = "#5A5A5A";
    public static string Text    => Theme.Active.Prompt;
    public const string Plan    = "#B48EAD";
    public const string Ultra   = "#D08770";
    public const string Giga    = "#B48EAD";
    public static string DiffAdd => Theme.Active.Success;
    public static string DiffDel => Theme.Active.Error;
    public const string Border  = "#3A3A3A";
    // Calm "working" cyan for the thinking/spinner line (NOT Warn - orange reads as an error).
    public const string Think    = "#7AA2C0";
    // Dim blue fill for the cached portion of the context meter (vs the bright live portion).
    public const string CacheFill = "#3E5A6E";
    // Elevated "card" body fill (GitHub-dark canvas-subtle feel) so tool/diff panels read as a
    // solid block distinct from the airy prose on the terminal's base background. Themed (v0.12.1).
    // Content background fills (card/diff/code). When ContentBackgrounds is false these resolve to
    // "default" -> ResolveColor returns null -> NO background SGR is emitted, so the terminal's own
    // (possibly translucent/glassy) background shows through. Foreground + layout are unchanged
    // (bg tags are zero visible width). InputBg + cursor/badge fills are intentionally NOT gated
    // here (input has its own inputHighlight toggle; cursor/mode fills are functional, not chrome).
    public static bool ContentBackgrounds { get; set; } = true;
    private static string Bg(string role) => ContentBackgrounds ? role : "default";
    public static string CardBg  => Bg(Theme.Active.CardBg);
    public static string InputBg => Theme.Active.InputBg;   // shade behind the compose field (gated by inputHighlight)
    // Diff line backgrounds: faint green/red bands + a neutral context fill on the card.
    public static string DiffAddBg => Bg(Theme.Active.DiffAddBg);
    public static string DiffDelBg => Bg(Theme.Active.DiffDelBg);
    public static string DiffHunkBg => Bg(Theme.Active.DiffHunkBg);
    public const string GutterFg = "#5A6675"; // line-number gutter (dim slate)

    /// <summary>
    /// Per-agent lane tints (stable, readable) used to gutter sub-agent / swarm-specialist
    /// transcript lines so the eye can follow one agent's stream in a dense multi-agent run.
    /// </summary>
    public static readonly string[] AgentLane =
    {
        "#7FB3D5", "#82C49B", "#C9A26B", "#B49ACA", "#6FBFB8", "#D49AA6", "#9DB668", "#C58F8F",
    };

    /// <summary>Stable agent-&gt;lane-color map (deterministic by name, never random per run).</summary>
    public static string AgentTint(string? agent)
    {
        var n = (agent ?? "").Trim();
        if (n.Length == 0) return Agent;
        uint h = 2166136261u;
        foreach (char c in n) { h ^= c; h *= 16777619u; }
        return AgentLane[(int)(h % (uint)AgentLane.Length)];
    }

    /// <summary>
    /// Prefix a built transcript line with a colored lane bar (sub-agent attribution). The
    /// line's leading indent (two spaces) is replaced by "&#x258e; " in the agent's tint so the
    /// gutter aligns with the rest of the transcript. No-op when <paramref name="tintHex"/> is null.
    /// </summary>
    public static string Gutter(string line, string? tintHex)
    {
        if (string.IsNullOrEmpty(tintHex) || string.IsNullOrEmpty(line)) return line;
        // Lines from these builders lead with two spaces; swap the first space for a tinted bar
        // (keeps the second space + any deeper indent so nested rows stay aligned).
        if (line.StartsWith("  "))
            return $"[{tintHex}]\u258e[/]" + line[1..];
        return $"[{tintHex}]\u258e[/] " + line;
    }

    /// <summary>
    /// The submitted user line echoed into scrollback. A leading blank line + a distinct
    /// accent gutter bar visually delimits each turn so dense back-to-back commands and their
    /// results don't run together.
    /// </summary>
    public static List<string> UserEcho(string line) => new()
    {
        "",
        // Echo glyph at col 0 (like the live input bar and the turn-header dot), text at col 2 - so
        // the submitted line aligns with the agent output column instead of sitting 2 cols deeper.
        $"[{Accent}]\u258e[/] [{Text}]{Esc(line ?? "")}[/]"
    };

    /// <summary>Braille spinner frames for the live "thinking/working" indicator
    /// (Claude-Code feel): a single dot-wheel that pulses. Advanced by the driver each
    /// repaint. Used only as a fallback when the status text does not already carry its
    /// own leading spinner glyph (the ThinkingIndicator bakes one in).</summary>
    public static readonly string[] ThinkFrames =
        { "\u2802", "\u2806", "\u2826", "\u2836", "\u2837", "\u2827", "\u2807", "\u2803" };

    /// <summary>Distinct spinner for delegated sub-agents - a rotating half-circle, set apart
    /// from the main agent's Braille dots so concurrent sub-agent activity reads as its own lane.</summary>
    public static readonly string[] SubAgentFrames =
        { "\u25D0", "\u25D3", "\u25D1", "\u25D2" };   // half-circle: left, top, right, bottom

    /// <summary>The single glyph used for BOTH the live pulsing dot and the static "done" dot, so
    /// the only signal is motion (alive) vs stillness (done). One fixed width-1 glyph: the pulse is
    /// a BRIGHTNESS animation (dim -> normal -> bold -> normal), never a glyph-shape change, so the
    /// rendered row width can never oscillate (the earlier ·/•/● frames were East-Asian-Width
    /// "Ambiguous" and rendered 1- or 2-cells inconsistently across terminals, leaving far-right
    /// residue as the dot pulsed). Rides the shared ~100ms ticker frame.</summary>
    public const string PulseGlyph = "\u25CF";   // ● - width-1 in our model AND the static done dot

    // Brightness cycle applied to the pulse glyph. Spectre decorations dim/bold change WEIGHT, not
    // width, so every frame is exactly one cell wide regardless of terminal.
    private static readonly string[] PulseDecos = { "dim", "", "bold", "" };

    /// <summary>The pulsing-dot cell for <paramref name="frame"/> (safe for any int incl. negative):
    /// the fixed glyph. Callers colour it; use <see cref="PulsingDot"/> for the breathing effect.</summary>
    public static string PulseDot(int frame) => PulseGlyph;

    /// <summary>Fully-styled pulsing dot in <paramref name="colorRole"/>: a fixed-width breathing dot
    /// (dim/normal/bold cycle) for the live lane head. <paramref name="frame"/> is safe for any int.</summary>
    public static string PulsingDot(int frame, string colorRole)
    {
        string deco = PulseDecos[((frame % PulseDecos.Length) + PulseDecos.Length) % PulseDecos.Length];
        string style = string.IsNullOrEmpty(deco) ? colorRole : $"{colorRole} {deco}";
        return $"[{style}]{PulseGlyph}[/]";
    }

    /// <summary>True if <paramref name="s"/> already begins with a Braille spinner glyph
    /// (U+2800..U+28FF), optionally after leading whitespace. The ThinkingIndicator composes
    /// its own spinner into the status text, so we must not prepend a second one.</summary>
    private static bool HasLeadingSpinner(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i < s.Length && s[i] >= '\u2800' && s[i] <= '\u28FF';
    }

    /// <summary>
    /// Claude-style working indicator: a single Braille spinner + dimmed italic status text
    /// (e.g. "\u2836 Conjuring\u2026"). The live ThinkingIndicator already bakes a Braille
    /// spinner into the status text, so when one is present we just style it and do NOT add a
    /// second glyph (fixes the double-icon). When absent (e.g. bare/empty text), a fallback
    /// spinner cell selected by <paramref name="frame"/> is prepended so the line still animates.
    /// Pure + width-agnostic.
    /// </summary>
    public static string ThinkingLine(string text, int frame)
    {
        string body = string.IsNullOrWhiteSpace(text) ? "Working\u2026" : CollapseWs(text);
        // Calm Think color (never the amber Warn) so the working indicator reads as quiet
        // activity, not an alert.
        if (HasLeadingSpinner(body))
            return $"  [{Think} italic]{Esc(body)}[/]";
        string spin = ThinkFrames[((frame % ThinkFrames.Length) + ThinkFrames.Length) % ThinkFrames.Length];
        return $"  [{Think}]{spin}[/] [{Think} italic]{Esc(body)}[/]";
    }

    /// <summary>
    /// The reverse-incremental-history-search prompt row (Ctrl+R), rendered above the input box
    /// in the readline/bash style: <c>(reverse-i-search)`query': matched line</c>. When nothing
    /// matches yet the match slot reads <c>(failed)</c> so the user knows to refine the query.
    /// </summary>
    public static string ReverseSearchRow(string query, string? match, int width)
    {
        string q = Esc(query ?? "");
        string label = $"(reverse-i-search)`{q}': ";
        if (string.IsNullOrEmpty(match))
            return $"  [{Accent}]{label}[/][{Dim}](no match)[/]";
        int room = Math.Max(8, width - TuiMarkup.Width(label) - 2);
        return $"  [{Accent}]{label}[/][{Text}]{Esc(Trunc(match, room))}[/]";
    }

    /// <summary>Compact token count: 30000 -> "30k", 1500 -> "1.5k", &lt;1000 stays exact.</summary>
    private static string Fmt(uint n)
        => n >= 1000 ? (n % 1000 == 0 ? $"{n / 1000}k" : $"{n / 1000.0:0.0}k") : n.ToString();

    /// <summary>Compact m:ss / h:mm:ss clock for the loop-clock badge (no leading "0:" hours).</summary>
    private static string ClockMS(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>Coarse session-timer label: "12m", "1h 04m" (no seconds - it is a slow wall clock).</summary>
    private static string ClockHM(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m";

    /// <summary>Short duration for the idle last-turn chip: "12s" under a minute, m:ss above.</summary>
    private static string ShortDur(TimeSpan t)
        => t.TotalSeconds < 60 ? $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))}s" : ClockMS(t);

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");

    private static string CollapseWs(string s)
        => string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");

    private static string Trunc(string s, int max)
        => TuiMarkup.TruncatePlain(s ?? "", max);

    /// <summary>Max candidate rows shown at once in an autocomplete preview window.</summary>
    public const int PreviewWindow = 8;

    /// <summary>
    /// Compute the [start,end) slice of a candidate list to render so that <paramref name="selected"/>
    /// stays visible inside an 8-row window: the window scrolls as the selection moves past the
    /// top/bottom edge. Returns the window start index (end = min(count, start+PreviewWindow)).
    /// </summary>
    public static int WindowStart(int count, int selected, int window = PreviewWindow)
    {
        if (count <= window || selected < 0) return 0;
        // Keep selection within the window; clamp so we never scroll past the last full page.
        int start = selected - window / 2;
        if (start < 0) start = 0;
        if (start > count - window) start = count - window;
        return start;
    }

    /// <summary>Normalize CRLF and strip trailing blank lines so bordered cards (diff/result)
    /// don't render a stack of empty interior rows before their closing border.</summary>
    private static string TrimTrailingBlankLines(string s)
    {
        var norm = (s ?? "").Replace("\r\n", "\n").TrimEnd('\n');
        return norm;
    }

    /// <summary>Session header card (committed once when an interactive TUI loop starts).</summary>
    public static List<string> SessionHeader(string agent, string model, string provider, int toolCount = 0)
    {
        // Trailing tool-count badge (folded in from the old standalone "N tools available"
        // line): a dim "N tools" chip on the session header row. Hidden when count <= 0.
        string toolsBadge = toolCount > 0
            ? $"  [{Dim}]\u00b7[/]  [{Ok}]{toolCount} tool{(toolCount == 1 ? "" : "s")}[/]"
            : "";
        return new()
        {
            "",
            $"  [{Accent}]\u2503[/] [{Accent}]session[/]  [{Dim}]\u00b7[/]  [{Agent}]{Esc(agent)}[/]  [{Dim}]\u00b7[/]  [{Text}]{Esc(model)}[/]  [{Dim}]\u00b7[/]  [{Muted}]{Esc(provider)}[/]{toolsBadge}"
        };
    }

    /// <summary>Agent turn header (a left rule with the agent name).</summary>
    public static List<string> TurnHeader(string agent, int width)
    {
        int w = Math.Max(8, width);
        string label = $"\u25b8 {agent}";
        int dashes = Math.Max(0, w - TuiMarkup.Width(label) - 3);
        return new()
        {
            "",
            $"  [{Agent}]{Esc(label)}[/] [{Border}]{new string('\u2500', dashes)}[/]"
        };
    }

    /// <summary>Tool-call line: a running glyph, a human ACTION label (verb-derived from the tool
    /// id so it reads "Running command" not "ReplShellMcp_execute_command_async"), and a compact
    /// arg hint. Falls back to a humanized name then "Working" - never a raw identifier.</summary>
    public static List<string> ToolCall(string tool, string? args) => ToolCall(tool, args, -1);

    /// <summary>Tool-call line; when <paramref name="frame"/> &gt;= 0 the head dot PULSES (live
    /// in-flight call), else a static running dot (committed/flushed line). Action label as above.</summary>
    public static List<string> ToolCall(string tool, string? args, int frame)
    {
        string hint = string.IsNullOrWhiteSpace(args)
            ? ""
            : $" [{Dim}]({Esc(Trunc(CollapseWs(args!), 56))})[/]";
        string dot = frame >= 0 ? PulsingDot(frame, Warn) : $"[{Warn}]{PulseGlyph}[/]";
        return new() { $"  {dot} [{Accent}]{Esc(ToolActionLabel.Describe(tool))}[/]{hint}" };
    }

    /// <summary>
    /// Tool call + its compact result merged into ONE line (density win on command-heavy
    /// turns): a completed glyph, tool name + arg hint, then the first informative result
    /// line and a "(+N lines)" hint. Used when a call resolves to a short, non-error,
    /// non-diff result; otherwise the call and result render as separate blocks.
    /// </summary>
    public static List<string> ToolCallResultMerged(string tool, string? args, string resultText, bool error = false, bool expandable = false)
        => ToolCallResultMerged(tool, args, resultText, error, expandable, -1);

    /// <summary>As above; when <paramref name="frame"/> &gt;= 0 the OK head dot pulses (the most-
    /// recent completed call, held in the live region) - the red failure glyph never pulses.</summary>
    public static List<string> ToolCallResultMerged(string tool, string? args, string resultText, bool error, bool expandable, int frame)
    {
        var lines = (resultText ?? "").Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0).ToArray();
        string first = "";
        int more = 0;
        if (lines.Length > 0)
        {
            // For OK results prefer the "Command:" line (skip async "Job ID:" bookkeeping);
            // for failures surface the most informative error line instead.
            int pick;
            if (error)
            {
                // Prefer the most specific failure line (STDERR body / "not recognized" /
                // "command not found") over a generic "Status: failed" preamble line.
                pick = Array.FindIndex(lines, l =>
                {
                    var t = l.TrimStart();
                    return t.StartsWith("STDERR", StringComparison.OrdinalIgnoreCase)
                        || t.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                        || t.Contains("command not found", StringComparison.OrdinalIgnoreCase)
                        || t.Contains("no such file", StringComparison.OrdinalIgnoreCase);
                });
                if (pick < 0)
                    pick = Array.FindIndex(lines, l =>
                    {
                        var t = l.TrimStart();
                        return t.Contains("error", StringComparison.OrdinalIgnoreCase)
                            || t.Contains("failed", StringComparison.OrdinalIgnoreCase);
                    });
            }
            else
            {
                pick = Array.FindIndex(lines, l =>
                    l.TrimStart().StartsWith("Command:", StringComparison.OrdinalIgnoreCase));
            }
            if (pick < 0) pick = 0;
            first = Trunc(CollapseWs(lines[pick]), 90);
            more = lines.Length - 1;
        }
        string hint = string.IsNullOrWhiteSpace(args)
            ? ""
            : $" [{Dim}]({Esc(Trunc(CollapseWs(args!), 48))})[/]";
        // The line-count hint is shown ONLY when the result is large enough to be Ctrl+E-
        // expandable; it doubles as the expand affordance (Claude Code's "(ctrl+o to expand)"
        // pattern). Short, fully-shown results get no "(+N lines)" noise.
        string moreHint = (expandable && more > 0)
            ? $" [{Dim}](+{more} line{(more == 1 ? "" : "s")}, ctrl+e expand)[/]"
            : "";
        // Failed calls get a red glyph + a dim "failed" tag so a non-zero result never reads
        // as success (the old path always painted a green dot regardless of exit status).
        // Most-recent completed OK call (frame>=0, held live) pulses; failures + flushed lines static.
        string glyph = error
            ? $"[{Err}]\u2717[/]"
            : (frame >= 0 ? PulsingDot(frame, Ok) : $"[{Ok}]{PulseGlyph}[/]");
        string failTag = error ? $" [{Err}]failed[/]" : "";
        string resultPart = first.Length > 0
            ? $"  [{Dim}]\u23bf[/] [{Muted}]{Esc(first)}[/]{moreHint}"
            : "";
        return new() { $"  {glyph} [{Accent}]{Esc(tool)}[/]{hint}{failTag}{resultPart}" };
    }

    /// <summary>Collapsed one-line tool result (Claude-Code style: first line + "(+N lines)").</summary>
    public static List<string> ToolResultCompact(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0) return new();
        // Prefer the most informative line: shell/REPL dispatches lead with bookkeeping ("Job ID:"
        // GUID or "Status:"), so surface the line that shows WHAT actually ran instead (Claude-Code
        // style) - "Command:" for async shell jobs. (The Python REPL no longer emits a "Code:" line;
        // the code is shown to the user in the expanded card, so the collapsed line falls back to the
        // Status line - the legacy "Code:" match is kept only for any older buffered output.)
        int pick = Array.FindIndex(lines, l =>
        {
            var t = l.TrimStart();
            return t.StartsWith("Command:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Code:", StringComparison.OrdinalIgnoreCase);
        });
        if (pick < 0) pick = 0;
        string first = Trunc(CollapseWs(lines[pick]), 110);
        int more = lines.Length - 1;
        string moreHint = more > 0 ? $" [{Dim}](+{more} line{(more == 1 ? "" : "s")})[/]" : "";
        return new() { $"    [{Dim}]\u23bf[/] [{Muted}]{Esc(first)}[/]{moreHint}" };
    }

    /// <summary>
    /// Collapsed one-line summary for a finished delegated sub-agent (Claude-Code Task style):
    /// a triangle glyph, the agent name in its lane tint, an optional status glyph, and a
    /// "(N lines, M tools — ctrl+o expand)" hint. The full transcript is retained expandable by
    /// the driver. <paramref name="status"/> is the signal_task_complete status when known.
    /// </summary>
    public static string SubAgentCollapsed(string agent, string? status, int lines, int tools, string tintHex)
    {
        string stat = (status ?? "").Trim().ToLowerInvariant();
        string glyph = stat switch
        {
            "success" or "complete" or "completed" or "done" => $"[{Ok}]\u2713[/]",
            "failure" or "failed" or "error"                 => $"[{Err}]\u2717[/]",
            "partial"                                         => $"[{Warn}]\u2052[/]",
            _                                                  => $"[{Muted}]\u2713[/]"
        };
        var bits = new List<string>();
        if (lines > 0) bits.Add($"{lines} line{(lines == 1 ? "" : "s")}");
        if (tools > 0) bits.Add($"{tools} tool{(tools == 1 ? "" : "s")}");
        bits.Add("ctrl+e expand");
        string hint = $" [{Dim}]({string.Join(", ", bits)})[/]";
        return $"  [{tintHex}]\u25b8[/] [{Agent}]{Esc(agent)}[/] {glyph}{hint}";
    }

    /// <summary>
    /// Consolidated live activity for the currently-running collapsed sub-agents: one compact
    /// line each (animated spinner in the agent's lane tint + name + concise status). Driven by
    /// a single shared ticker so concurrent parallel agents animate in lockstep without the
    /// flicker that competing per-agent spinners on one shared line produced. <paramref name="frame"/>
    /// advances the spinner. Returns an empty list when no sub-agents are active.
    /// </summary>
    public static List<string> SubAgentActivity(
        IReadOnlyList<(string Agent, string Status, string Tint)> agents, int frame)
    {
        var outp = new List<string>(agents.Count);
        if (agents.Count == 0) return outp;
        // Pulsing dot marks the live lane head (motion = working); same dot vocabulary as static rows.
        foreach (var (agent, status, tint) in agents)
        {
            string st = string.IsNullOrWhiteSpace(status) ? "working" : CollapseWs(status);
            if (st.Length > 60) st = st[..59] + "\u2026";
            string spin = PulsingDot(frame, tint);
            // The ctrl+e affordance is shown live (not just after completion) so the user knows the
            // still-running sub-agent's buffered output can be expanded inline at any time.
            outp.Add($"  {spin} [{Agent}]{Esc(agent)}[/] [{Dim}]\u00b7[/] [{Think} italic]{Esc(st)}\u2026[/] [{Dim}](ctrl+e)[/]");
        }
        return outp;
    }

    /// <summary>Expanded tool result as a bordered card with a status glyph.</summary>
    public static List<string> ToolResultPanel(string tool, string text, bool error, int width, int cap = 2000, bool expanded = false, bool markdown = false)
    {
        string glyph = error ? $"[{Err}]\u2717[/]" : $"[{Ok}]\u2713[/]";
        string col = error ? Err : Border;
        var body = (text ?? "");
        // When expanded (NAV cursor opened the card) show the FULL result - no truncation,
        // since the whole point of expanding is to read everything. Only the compact inline
        // path applies the cap.
        if (!expanded && body.Length > cap) body = body[..cap] + "\n\u2026 truncated";

        int inner = Math.Max(8, width - 4);
        // One continuous filled card: header band, body rows and footer band all share CardBg,
        // with the accent rail painted ON the fill (no unshaded gap, so blank rows can't notch the
        // right edge and the block reads as a single solid card rather than a floating rectangle).
        string headMarkup = $"{glyph} [{Accent} on {CardBg}]{Esc(tool)}[/]";
        string headPlain   = $"\u2713 {tool}";   // glyph(1)+space(1)+tool; width drives the fill pad
        var outp = new List<string> { ShadedHeader(col, headPlain, headMarkup, CardBg, inner) };
        // markdown=true renders the BODY as muted markdown (uniform with sub-agent panels): headings/
        // bold/inline-code styled but subordinate, no literal ###/**. Tool name/status stay literal.
        foreach (var raw in TrimTrailingBlankLines(body).Split('\n'))
        {
            if (markdown)
                foreach (var w in TuiMarkup.WrapMarkup(TuiMarkdown.ToMarkup(raw), inner))
                    outp.Add(ShadedMarkdownRow(col, w, CardBg, inner));
            else
                foreach (var w in TuiMarkup.WrapPlain(raw, inner))
                    outp.Add(ShadedRow(col, w, Text, CardBg, inner));
        }
        outp.Add(ShadedRow(col, "", Text, CardBg, inner));
        return outp;
    }

    /// <summary>
    /// Bounded live panel for any expandable block (running sub-agent transcript, large tool
    /// result, etc.) rendered INSIDE the repaintable live region - never committed to scrollback -
    /// so Ctrl+E toggles it open/closed and it updates in place with zero append spam. Shows at
    /// most <paramref name="maxRows"/> body rows. When <paramref name="anchorTail"/> is true the
    /// NEWEST lines are kept and older ones elided from the top ("+N earlier", for live-growing
    /// sub-agent output); when false the FIRST lines are kept and the rest elided from the bottom
    /// ("+N more", for a static result the user reads top-down). The header label and border tint
    /// are caller-supplied so tool results and sub-agents read distinctly.
    /// </summary>
    public static List<string> BoundedLivePanel(
        string title, string body, string tintHex, int width, int maxRows, bool anchorTail, bool error = false, bool markdown = false)
    {
        string tint = error ? Err : tintHex;
        int inner = Math.Max(8, width - 4);
        int cap = Math.Max(1, maxRows);
        // SubAgent panels render their transcript as MUTED markdown (headings/bold/inline-code
        // still differentiate, but subordinate to the main viewport); everything else stays plain.
        var wrapped = new List<string>();
        foreach (var raw in TrimTrailingBlankLines(body ?? "").Split('\n'))
        {
            if (markdown)
                foreach (var w in TuiMarkup.WrapMarkup(TuiMarkdown.ToMarkup(raw), inner)) wrapped.Add(w);
            else
                foreach (var w in TuiMarkup.WrapPlain(raw, inner)) wrapped.Add(w);
        }
        int hidden = 0;
        if (wrapped.Count > cap)
        {
            hidden = wrapped.Count - cap;
            wrapped = anchorTail ? wrapped.GetRange(hidden, cap) : wrapped.GetRange(0, cap);
        }
        // Same continuous filled-card treatment as ToolResultPanel so a LIVE mid-turn expand looks
        // identical to its NAV-scrollback counterpart: rail-on-fill, every band padded to `inner`.
        string headPlain = $"{title} (live \u00b7 ctrl+e collapse)";
        string headMarkup = $"[{Accent} on {CardBg}]{Esc(title)}[/] [{Dim} on {CardBg}](live \u00b7 ctrl+e collapse)[/]";
        var outp = new List<string> { ShadedHeader(tint, headPlain, headMarkup, CardBg, inner) };
        // Top elision marker (tail-anchored views only).
        if (hidden > 0 && anchorTail)
            outp.Add(ShadedRow(tint, $"\u2026 +{hidden} earlier line{(hidden == 1 ? "" : "s")}", Dim, CardBg, inner));
        foreach (var w in wrapped)
            outp.Add(markdown ? ShadedMarkdownRow(tint, w, CardBg, inner) : ShadedRow(tint, w, Text, CardBg, inner));
        // Bottom elision marker (head-anchored views only) - points at Ctrl+G for the full block.
        if (hidden > 0 && !anchorTail)
            outp.Add(ShadedRow(tint, $"\u2026 +{hidden} more line{(hidden == 1 ? "" : "s")} (ctrl+g for full)", Dim, CardBg, inner));
        outp.Add(ShadedRow(tint, "", Text, CardBg, inner));
        return outp;
    }

    /// <summary>Tail-anchored bounded live panel for a running sub-agent (thin wrapper over
    /// <see cref="BoundedLivePanel"/> with the agent's lane tint).</summary>
    public static List<string> SubAgentLivePanel(string agent, string body, int width, int maxRows)
        => BoundedLivePanel(agent, body, AgentTint(agent), width, maxRows, anchorTail: true);

    /// <summary>
    /// Unified/git diff rendered as a production diff card: a shaded body, an old|new line-number
    /// gutter parsed from the @@ hunk headers, per-line add/del/context background bands, and a
    /// "+adds -dels" summary in the header. Long code lines wrap with a blank gutter continuation.
    /// </summary>
    public static List<string> Diff(string title, string diff, int width)
    {
        var raws = TrimTrailingBlankLines(diff ?? "").Replace("\r\n", "\n").Split('\n');

        // First pass: count adds/dels and find the largest line number so the gutter is sized once.
        int adds = 0, dels = 0, maxLineNo = 0;
        int oldN0 = 0, newN0 = 0;
        foreach (var raw in raws)
        {
            if (TryParseHunk(raw, out var oh, out var nh)) { oldN0 = oh; newN0 = nh; continue; }
            if (IsMeta(raw)) continue;
            if (raw.StartsWith("+")) { adds++; maxLineNo = Math.Max(maxLineNo, newN0); newN0++; }
            else if (raw.StartsWith("-")) { dels++; maxLineNo = Math.Max(maxLineNo, oldN0); oldN0++; }
            else { maxLineNo = Math.Max(maxLineNo, Math.Max(oldN0, newN0)); oldN0++; newN0++; }
        }
        int gw = Math.Max(2, maxLineNo.ToString().Length);
        // prefix(2) + rail(1) + space(1) + gutter(old gw + space + new gw + space) + code
        int gutterCols = gw + 1 + gw + 1;
        int codeW = Math.Max(8, width - 5 - gutterCols);

        string summary = $"[{DiffAdd}]+{adds}[/] [{DiffDel}]\u2212{dels}[/]";
        var outp = new List<string>
        {
            $"  [{Border}]\u256d\u2500[/] [{Accent}]diff[/] [{Dim}]\u00b7 {Esc(Trunc(title, 40))}[/]  {summary}"
        };

        int oldN = 0, newN = 0;
        foreach (var raw in raws)
        {
            if (TryParseHunk(raw, out var oh2, out var nh2))
            {
                oldN = oh2; newN = nh2;
                outp.Add(HunkRow(raw, gw, gutterCols, codeW));
                continue;
            }
            if (IsMeta(raw)) { outp.Add(MetaRow(raw, gw, gutterCols, codeW)); continue; }

            string fg, bg, marker, oldS, newS;
            if (raw.StartsWith("+")) { fg = DiffAdd; bg = DiffAddBg; marker = "+"; oldS = ""; newS = newN.ToString(); newN++; }
            else if (raw.StartsWith("-")) { fg = DiffDel; bg = DiffDelBg; marker = "-"; oldS = oldN.ToString(); newS = ""; oldN++; }
            else { fg = DiffCtxFg; bg = CardBg; marker = " "; oldS = oldN.ToString(); newS = newN.ToString(); oldN++; newN++; }

            string code = raw.Length > 0 ? raw[1..] : "";   // strip the +/-/space marker column
            var wrapped = TuiMarkup.WrapPlain(code, codeW);
            for (int wi = 0; wi < wrapped.Count; wi++)
            {
                string gOld = (wi == 0 ? oldS : "").PadLeft(gw);
                string gNew = (wi == 0 ? newS : "").PadLeft(gw);
                string mk = wi == 0 ? marker : " ";
                // Pad by DISPLAY width (not UTF-16 length) so wide/CJK/emoji glyphs do not push
                // the shaded cell past codeW+1 and bleed the bg past the border.
                string cellText = mk + wrapped[wi];
                int cellPad = Math.Max(0, codeW + 1 - TuiMarkup.Width(cellText));
                outp.Add($"  [{Border} on {bg}]\u2502[/][{GutterFg} on {bg}] {gOld} {gNew} [/][{fg} on {bg}]{Esc(cellText)}{new string(' ', cellPad)}[/]");
            }
        }
        outp.Add($"  [{Border}]\u2570{new string('\u2500', gutterCols + codeW + 2)}[/]");  // +2 so the border spans the full shaded body width (prev +1 was one cell short -> bg read as bleeding past)
        return outp;
    }

    private const string DiffCtxFg = "#A0A0A0"; // diff context line (neutral grey)

    /// <summary>Shade one card body row: an accent rail painted ON the fill, then a full-width
    /// background band of <paramref name="content"/>. The rail-on-fill (vs a detached rail + gap)
    /// keeps the card a single continuous rectangle with no left notch or ragged blank rows.</summary>
    private static string ShadedRow(string railCol, string content, string fg, string bg, int inner)
    {
        // Pad by DISPLAY width so wide/CJK/emoji content keeps the shaded band exactly `inner` cells
        // (UTF-16 PadRight overshoots for double-width glyphs and bleeds the bg past the card edge).
        int pad = Math.Max(0, inner - TuiMarkup.Width(content));
        return $"  [{railCol} on {bg}]\u2502[/][{fg} on {bg}] {Esc(content)}{new string(' ', pad)}[/]";
    }

    /// <summary>Body row of a shaded card carrying PRE-STYLED markdown markup (from TuiMarkdown/
    /// WrapMarkup). The base fill is Muted so the sub-agent transcript reads as subordinate to the
    /// main viewport, while nested tags (headings/bold/inline-code) still apply on top. Padded by
    /// DISPLAY width so the band is exactly inner cells wide (matching the plain body rows).</summary>
    private static string ShadedMarkdownRow(string railCol, string markupContent, string bg, int inner)
    {
        int pad = Math.Max(0, inner - TuiMarkup.MarkupWidth(markupContent));
        return $"  [{railCol} on {bg}]\u2502[/][{Muted} on {bg}] {markupContent}{new string(' ', pad)}[/]";
    }

    /// <summary>Header band of a shaded card: same rail-on-fill treatment, carrying pre-styled
    /// markup (glyph + tool name) + a trailing fill computed from the header's DISPLAY width so the
    /// band is exactly `inner` cells wide (matching the body rows). Passing a full `inner` of spaces
    /// here was the overflow/soft-wrap regression - the row ran ~inner cells too wide and reflowed.</summary>
    private static string ShadedHeader(string railCol, string headerPlain, string headerMarkup, string bg, int inner)
    {
        int pad = Math.Max(0, inner - TuiMarkup.Width(headerPlain));
        return $"  [{railCol} on {bg}]\u2502[/][on {bg}] {headerMarkup}{new string(' ', pad)}[/]";
    }

    private static bool IsMeta(string s)
        => s.StartsWith("+++") || s.StartsWith("---") || s.StartsWith("diff ") || s.StartsWith("index ");

    private static string MetaRow(string raw, int gw, int gutterCols, int codeW)
    {
        string blank = new string(' ', gw);
        string txt = Trunc(raw, codeW + 1);
        int pad = Math.Max(0, codeW + 1 - TuiMarkup.Width(txt));
        return $"  [{Border} on {CardBg}]\u2502[/][{GutterFg} on {CardBg}] {blank} {blank} [/][{Muted} on {CardBg}]{Esc(txt)}{new string(' ', pad)}[/]";
    }

    private static string HunkRow(string raw, int gw, int gutterCols, int codeW)
    {
        string blank = new string(' ', gw);
        string txt = Trunc(raw, codeW + 1);
        int pad = Math.Max(0, codeW + 1 - TuiMarkup.Width(txt));
        return $"  [{Border} on {DiffHunkBg}]\u2502[/][{GutterFg} on {DiffHunkBg}] {blank} {blank} [/][{Accent} on {DiffHunkBg}]{Esc(txt)}{new string(' ', pad)}[/]";
    }

    /// <summary>Parse a "@@ -o,c +n,c @@" hunk header into the starting old/new line numbers.</summary>
    private static bool TryParseHunk(string raw, out int oldStart, out int newStart)
    {
        oldStart = 0; newStart = 0;
        if (raw is null || !raw.StartsWith("@@")) return false;
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"^@@+\s*-(\d+)(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s*@@");
        if (!m.Success) return false;
        oldStart = int.Parse(m.Groups[1].Value);
        newStart = int.Parse(m.Groups[2].Value);
        return true;
    }

    /// <summary>One-line collapsed delegation summary: "from -> to  <short task>  (ctrl+e expand)".
    /// The full prompt is retained as expandable data by the driver, mirroring sub-agent collapse.</summary>
    public static string DelegationSummary(string from, string to, string task)
        => $"  [{Agent}]{Esc(from)}[/] [{Dim}]delegates[/] [{Accent}]\u2192 {Esc(to)}[/]  "
         + $"[{Muted}]{Esc(Trunc(CollapseWs(task), 60))}[/]  [{Dim}](ctrl+e expand)[/]";

    /// <summary>Delegation rendered as a small from -> to tree.</summary>
    public static List<string> Delegation(string from, string to, string task, int truncLength) => new()
    {
        $"  [{Agent}]{Esc(from)}[/] [{Dim}]delegates[/] [{Accent}]\u2192 {Esc(to)}[/]",
        $"    [{Dim}]\u2514[/] [{Muted}]{Esc(Trunc(CollapseWs(task), truncLength))}[/]"
    };

    /// <summary>Task-complete line with an ok glyph.</summary>
    public static List<string> TaskComplete(string agent, string summary) => new()
    {
        $"  [{Ok}]\u2714[/] [{Agent}]{Esc(agent)}[/] [{Dim}]completed[/]  [{Muted}]{Esc(Trunc(summary, 120))}[/]"
    };

    /// <summary>
    /// The pinned footer: mode badges + timers + a context meter. Lives at the bottom of the
    /// live region and is repainted every frame, so it never strands or scrolls away.
    /// Responsive: when <paramref name="width"/> is known, lower-value chips are dropped (and
    /// the meter compacted) until the line fits on one row - it never wraps mid-chip. Drop
    /// order: Shift+Tab hint, sys/tools breakdown, cached text, session timer + calls, model,
    /// meter shrink, meter to bare percent, idle last-turn + effort chips.
    /// </summary>
    public static string Footer(uint tokens, uint threshold, bool plan, bool ultra, bool psub, bool sub = false, string? effort = null, bool modeCycleHint = false, uint cached = 0, uint sysTokens = 0, uint toolTokens = 0, TimeSpan? sessionElapsed = null, bool giga = false, TimeSpan? turnElapsed = null, TimeSpan? lastTurn = null, uint toolCalls = 0, string? model = null, int width = 0, int pulseFrame = -1)
    {
        // Mode chip: giga supersedes ultra (superset), ultra collapses plan/psub/sub, else the
        // discrete modes show individually. For a short window after activation the chip
        // "breathes" (dim/normal/bold cycle - weight-only, same trick as PulsingDot, so the
        // rendered width can never oscillate); pulseFrame < 0 renders it static.
        string ModeStyle(string colour)
        {
            if (pulseFrame < 0) return colour;
            string deco = PulseDecos[((pulseFrame % PulseDecos.Length) + PulseDecos.Length) % PulseDecos.Length];
            return string.IsNullOrEmpty(deco) ? colour : $"{colour} {deco}";
        }
        var modeBadges = new List<string>();
        if (giga) modeBadges.Add($"[{ModeStyle(Giga)}]giga[/]");
        else if (ultra) modeBadges.Add($"[{ModeStyle(Ultra)}]ultra[/]");
        else
        {
            if (plan) modeBadges.Add($"[{Plan}]plan[/]");
            if (psub) modeBadges.Add($"[{Accent}]psub[/]");
            if (sub) modeBadges.Add($"[{Ok}]sub[/]");
        }

        // Turn timer: bright + ticking while the model works the current turn; between turns
        // the LAST turn's duration shows dimmed (same glyph, dim = idle) so the cost of the
        // previous exchange stays visible until the next send resets it.
        string liveTurnChip = turnElapsed is { } te ? $"[{Warn}]\u25cf {ClockMS(te)}[/]" : "";
        string idleTurnChip = turnElapsed is null && lastTurn is { } lt ? $"[{Dim}]\u25cf {ShortDur(lt)}[/]" : "";

        // Session timer: total wall-clock since the session opened. Hidden under a minute so a
        // fresh session does not show a noisy "0m".
        string sessChip = sessionElapsed is { } se && se.TotalSeconds >= 60 ? $"[{Dim}]{ClockHM(se)}[/]" : "";

        // Context meter: bar+percent when a threshold is known (bar shrinks, then collapses to
        // a bare percent+total, under width pressure); a bare token count when tokens have
        // accrued without a threshold; nothing at all when idle so the footer never shows a
        // noisy, meaningless "0 tokens". The bar plots TOTAL context (live + cached) against
        // the threshold - compaction fires on the total crossing it - split into a dim cached
        // segment and a bright live segment. `tokens` here is the live (uncached) count.
        string Meter(int level)
        {
            if (threshold > 0)
            {
                uint total = tokens + cached;
                double fracTotal = Math.Clamp((double)total / threshold, 0, 1);
                if (level >= 6)
                    return $"[{Muted}]{fracTotal * 100:F0}% {Fmt(total)}[/]";
                int barWidth = level >= 5 ? 10 : 16;
                int filled = (int)Math.Round(fracTotal * barWidth);
                int cachedCells = Math.Clamp((int)Math.Round((double)cached / threshold * barWidth), 0, filled);
                int liveCells = filled - cachedCells;
                int empty = Math.Max(0, barWidth - filled);
                string liveColour = fracTotal < 0.6 ? Ok : fracTotal < 0.85 ? Warn : Err;
                string bar = $"[{CacheFill}]" + new string('\u2501', cachedCells) + "[/]" +
                             $"[{liveColour}]" + new string('\u2501', liveCells) + "[/]" +
                             $"[{Dim}]" + new string('\u2501', empty) + "[/]";
                return $"{bar} [{Muted}]{Fmt(total)}/{Fmt(threshold)} ({fracTotal * 100:F0}%)[/]";
            }
            return tokens > 0 ? $"[{Muted}]{Fmt(tokens)} tokens[/]" : "";
        }

        // Compose the footer at a degradation level (0 = everything). Uniform middot
        // separators throughout - no mixed double-space/triple-space seams.
        string Compose(int level)
        {
            var chips = new List<string>(modeBadges);
            if (liveTurnChip.Length > 0) chips.Add(liveTurnChip);
            if (idleTurnChip.Length > 0 && level < 7) chips.Add(idleTurnChip);
            if (sessChip.Length > 0 && level < 4) chips.Add(sessChip);
            string meter = Meter(level);
            if (meter.Length > 0) chips.Add(meter);
            if (cached > 0 && threshold > 0 && level < 3) chips.Add($"[{Dim}]{Fmt(cached)} cached[/]");
            if (sysTokens > 0 && level < 2) chips.Add($"[{Muted}]sys[/] [{Text}]{Fmt(sysTokens)}[/]");
            if (toolTokens > 0 && level < 2) chips.Add($"[{Muted}]tools[/] [{Text}]{Fmt(toolTokens)}[/]");
            if (toolCalls > 0 && level < 4) chips.Add($"[{Muted}]calls[/] [{Text}]{Fmt(toolCalls)}[/]");
            // Model chip: the resolved model id (path prefix stripped), high-value so it
            // survives to mid tiers - after a provider fallback/pin this is the fastest way
            // to see what the session is actually running on.
            if (!string.IsNullOrEmpty(model) && level < 5)
            {
                string m = model!;
                int slash = m.LastIndexOf('/');
                if (slash >= 0 && slash < m.Length - 1) m = m[(slash + 1)..];
                chips.Add($"[{Agent}]{Esc(m)}[/]");
            }
            if (!string.IsNullOrEmpty(effort) && level < 7) chips.Add($"[{Warn}]\u25d0 {Esc(effort)}[/]");
            if (modeCycleHint && level < 1) chips.Add($"[{Dim}]\u21e7\u21b9[/]");
            return "  " + string.Join($" [{Dim}]\u00b7[/] ", chips);
        }

        string line = Compose(0);
        if (width > 8)
            for (int lvl = 1; lvl <= 7 && TuiMarkup.MarkupWidth(line) > width; lvl++)
                line = Compose(lvl);
        return line;
    }

    /// <summary>A horizontal rule that spans the FULL terminal width (Claude-Code style),
    /// used to separate the transcript from the docked footer/input band. Width is computed
    /// at paint time so it tracks terminal resizes.</summary>
    public static string FullRule(int width)
    {
        int w = Math.Max(8, width);
        return $"[{Border}]{new string('\u2500', w)}[/]";
    }

    /// <summary>
    /// The /voice replacement for the prompt caret: a state-driven dot animated off the wall
    /// clock (the voice poll loop repaints ~10fps while active). Null when voice is off so the
    /// normal caret renders. warming = dim slow blink, listening = steady dot, hearing = fast
    /// accent pulse, transcribing = braille spinner, error = red cross.
    /// </summary>
    internal static string? VoicePromptGlyph()
    {
        var st = Voice.VoiceSession.State;
        if (st == Voice.VoiceState.Off) return null;
        long ms = Environment.TickCount64;
        return st switch
        {
            Voice.VoiceState.Warming      => (ms / 600) % 2 == 0 ? $"[{Dim}]\u25cf[/]" : $"[{Dim}]\u00b7[/]",
            Voice.VoiceState.Listening    => $"[{Accent}]\u25cf[/]",
            Voice.VoiceState.Hearing      => PulsingDot((int)(ms / 120), Accent),
            Voice.VoiceState.Transcribing => $"[{Warn}]{Spinner[(int)(ms / 100) % Spinner.Length]}[/]",
            Voice.VoiceState.Error        => $"[{Err}]\u2717[/]",
            _ => null,
        };
    }

    private static readonly string[] Spinner = { "\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f" };

    /// <summary>The input row shown in the live region while awaiting/editing input.</summary>
    public static string InputRow(string buffer)
        => $"  [{Accent}]\u203a[/] {(string.IsNullOrEmpty(buffer) ? $"[{Dim}]type a message, or / for commands\u2026[/]" : Esc(buffer))}";

    /// <summary>
    /// Input row with a synthetic block cursor at <paramref name="cursor"/>. The real
    /// terminal cursor is hidden while the live region is painted, so we draw a reverse-video
    /// cell at the edit position (Claude-Code style) to show where typing lands.
    /// </summary>
    public static string InputRowWithCursor(string buffer, int cursor)
        => string.Join("\n", InputRowsWithCursor(buffer, cursor));

    /// <summary>
    /// Render the input/compose area as one or more markup lines. A buffer containing embedded
    /// newlines (Alt+Enter / Ctrl+J multiline compose) renders as a prompt line plus continuation
    /// lines, each gutter-aligned under the prompt, with the synthetic block cursor placed on the
    /// correct visual line. Single-line buffers return exactly one row (unchanged behaviour).
    /// </summary>
    public static List<string> InputRowsWithCursor(string buffer, int cursor, int width = 0, bool highlight = false)
    {
        const string cur = "#E0E0E0";
        // When highlight is on, every input row is wrapped in a shaded band (InputBg) spanning the
        // full width so the compose field reads as a contained region. Applied as a final pass over
        // the rows so the cursor/markup math below is unchanged (highlight=false is byte-identical).
        // Subtle treatment (option B): a thin accent left-rail + a FAINT shade FILLING the field row.
        // The shade starts flush at the rail (the leading space is inside the band, no gap) and spans
        // the full width so the field reads as one contained region - the faint colour keeps it from
        // looking like a heavy strip. The 2-space lead each row already carries becomes rail(1)+
        // space(1), so the text stays at its original column. highlight=false stays byte-identical.
        List<string> Shade(List<string> rows)
        {
            if (!highlight || width <= 0) return rows;
            var outp = new List<string>(rows.Count);
            foreach (var r in rows)
            {
                int vis = TuiMarkup.MarkupWidth(r);          // includes the 2 leading spaces
                // Fill the rest of the row with the faint shade: rail(1) + " " + body + pad == width.
                int pad = Math.Max(0, width - vis);
                string body = r.StartsWith("  ") ? r.Substring(2) : r;
                outp.Add($"[{Accent}]\u2502[/][on {InputBg}] {body}{new string(' ', pad)}[/]");
            }
            return outp;
        }
        string prompt = VoicePromptGlyph() ?? $"[{Accent}]\u203a[/]";
        // Continuation gutter for wrapped/multiline rows - dim vertical bar aligned under the prompt.
        string contGutter = $"[{Dim}]\u2502[/]";
        string promptLead = $"  {prompt} ";
        string contLead   = $"  {contGutter} ";
        // Visible column cost of each lead ("  " + glyph + " "). Used to size the wrap width so a
        // wrapped row's text never runs under the terminal's right edge and soft-wraps with no gutter.
        int promptLeadCols = 2 + TuiMarkup.MarkupWidth(prompt) + 1;
        int contLeadCols   = 2 + TuiMarkup.MarkupWidth(contGutter) + 1;

        if (string.IsNullOrEmpty(buffer))
            return Shade(new List<string>
            {
                $"{promptLead}[black on {cur}] [/][{Dim}]type a message, or / for commands\u2026[/]"
            });

        cursor = Math.Clamp(cursor, 0, buffer.Length);

        // Split into logical lines on embedded newlines, tracking which line+offset the cursor is on.
        var segments = buffer.Split('\n');
        int cursorLine = 0, cursorCol = cursor;
        for (int s = 0; s < segments.Length; s++)
        {
            if (cursorCol <= segments[s].Length) { cursorLine = s; break; }
            cursorCol -= segments[s].Length + 1; // +1 for the consumed newline
            cursorLine = s + 1;
        }

        // Build visual rows. Each logical segment is hard-wrapped (by character, so the block
        // cursor maps exactly to (row,col)) to the content width left of the gutter. The prompt
        // lead is used only for the very first visual row; every other row gets the dim gutter so
        // wrapped/continuation lines hang-indent under the prompt instead of hugging column 0.
        var rows = new List<string>();
        int cursorVRow = -1, cursorVCol = 0;
        for (int s = 0; s < segments.Length; s++)
        {
            string seg = segments[s];
            int pos = 0;
            bool firstOfSeg = true;
            while (true)
            {
                bool isFirstRowOverall = s == 0 && firstOfSeg;
                string lead = isFirstRowOverall ? promptLead : contLead;
                int leadCols = isFirstRowOverall ? promptLeadCols : contLeadCols;
                int cap = width > 0 ? Math.Max(1, width - leadCols) : int.MaxValue;
                int take = Math.Min(cap, seg.Length - pos);
                string chunk = seg.Substring(pos, take);

                // Does the cursor fall on this visual chunk? A cursor sitting exactly at the chunk's
                // trailing boundary belongs here only when it is also the segment end (append spot);
                // otherwise it rolls to the start of the next wrapped row.
                if (s == cursorLine && cursorCol >= pos &&
                    (cursorCol < pos + take || (cursorCol == pos + take && pos + take == seg.Length)))
                {
                    cursorVRow = rows.Count;
                    cursorVCol = cursorCol - pos;
                }

                if (cursorVRow == rows.Count)
                {
                    int col = Math.Clamp(cursorVCol, 0, chunk.Length);
                    string before = Esc(chunk[..col]);
                    if (col >= chunk.Length)
                        rows.Add($"{lead}[{Text}]{before}[/][black on {cur}] [/]");
                    else
                    {
                        string at = Esc(chunk[col].ToString());
                        string after = Esc(chunk[(col + 1)..]);
                        rows.Add($"{lead}[{Text}]{before}[/][black on {cur}]{at}[/][{Text}]{after}[/]");
                    }
                }
                else
                {
                    rows.Add($"{lead}[{Text}]{Esc(chunk)}[/]");
                }

                pos += take;
                firstOfSeg = false;
                if (pos >= seg.Length) break;
            }
        }
        return Shade(rows);
    }

    /// <summary>
    /// Rank slash-command entries against an as-you-type token (lower score = better, -1 =
    /// no match). Command-NAME matches always beat description-only matches, and a name PREFIX
    /// beats a name substring - so "/age" ranks "/agent" (prefix) above "/swarm" (whose
    /// description "multi-agent" merely contains "age"). Pure + shared by the preview and Tab.
    /// </summary>
    public static int CommandScore(string cmd, string desc, string filter)
    {
        var f = (filter ?? "").TrimStart('/').Trim().ToLowerInvariant();
        if (f.Length == 0) return 0;
        string c = cmd.TrimStart('/').ToLowerInvariant();
        if (c.StartsWith(f)) return c.Length - f.Length;          // best: name prefix (tighter wins)
        int ci = c.IndexOf(f, StringComparison.Ordinal);
        if (ci >= 0) return 100 + ci;                             // next: name substring
        int di = (desc ?? "").ToLowerInvariant().IndexOf(f, StringComparison.Ordinal);
        if (di >= 0) return 1000 + di;                            // last: description match
        return -1;
    }

    /// <summary>Entries that match <paramref name="filter"/>, best-ranked first.</summary>
    public static List<(string Cmd, string Desc)> RankCommands(string? filter, IReadOnlyList<(string Cmd, string Desc)> entries)
    {
        var f = (filter ?? "").TrimStart('/').Trim();
        if (f.Length == 0) return entries.ToList();
        return entries
            .Select(e => (e, score: CommandScore(e.Cmd, e.Desc, f)))
            .Where(t => t.score >= 0)
            .OrderBy(t => t.score)
            .ThenBy(t => t.e.Cmd.Length)
            .ThenBy(t => t.e.Cmd, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.e)
            .ToList();
    }

    /// <summary>The top-ranked command for <paramref name="filter"/>, or null. Used by Tab.</summary>
    public static string? TopCommandMatch(string? filter, IReadOnlyList<(string Cmd, string Desc)> entries)
        => RankCommands(filter, entries).Select(e => e.Cmd).FirstOrDefault();

    /// <summary>Slash-command palette rows filtered + ranked by the current as-you-type token.</summary>
    public static List<string> SlashPalette(string? filter, IReadOnlyList<(string Cmd, string Desc)> entries, int selected = -1)
    {
        var f = (filter ?? "").TrimStart('/').Trim().ToLowerInvariant();
        var ranked = RankCommands(filter, entries);
        var rows = new List<string>();
        if (ranked.Count == 0) { rows.Add($"    [{Dim}]no commands match '{Esc(f)}'[/]"); return rows; }

        int start = WindowStart(ranked.Count, selected);
        int end = Math.Min(ranked.Count, start + PreviewWindow);
        if (start > 0) rows.Add($"    [{Dim}]\u2191 {start} more[/]");
        for (int i = start; i < end; i++)
        {
            var (cmd, desc) = ranked[i];
            rows.Add(i == selected
                ? $"  [{Accent}]\u203a[/] [{Text}]{Esc(cmd),-14}[/] [{Text}]{Esc(desc)}[/]"
                : $"    [{Accent}]{Esc(cmd),-14}[/] [{Muted}]{Esc(desc)}[/]");
        }
        if (end < ranked.Count) rows.Add($"    [{Dim}]\u2193 {ranked.Count - end} more[/]");
        return rows;
    }

    /// <summary>
    /// Live, web-app-style skills autocomplete shown beneath the input box while typing
    /// "/skill". Fuzzy-filters the loaded skill catalog by name OR description on the text
    /// after the command word, and renders each match as "name  description" (width-aware).
    /// Pure - no console I/O - so it is unit-testable.
    /// </summary>
    /// <summary>Skill names matching <paramref name="filter"/> (name or description), sorted.</summary>
    public static List<string> RankSkills(string? filter, IReadOnlyList<(string Name, string Desc)> skills)
    {
        var f = (filter ?? "").Trim().ToLowerInvariant();
        return skills
            .Where(s => f.Length == 0 || s.Name.ToLowerInvariant().Contains(f) || (s.Desc ?? "").ToLowerInvariant().Contains(f))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Name)
            .ToList();
    }

    public static List<string> SkillsPreview(string? filter, IReadOnlyList<(string Name, string Desc)> skills, int width, int selected = -1)
    {
        var f = (filter ?? "").Trim().ToLowerInvariant();
        var rows = new List<string>();
        if (skills.Count == 0)
        {
            rows.Add($"    [{Dim}]no skills loaded[/]");
            return rows;
        }

        var names = RankSkills(filter, skills);
        var descOf = skills.ToDictionary(s => s.Name, s => s.Desc, StringComparer.Ordinal);

        rows.Add($"  [{Accent}]\u2503[/] [{Accent}]skills[/] [{Dim}]({names.Count})[/]");
        if (names.Count == 0)
        {
            rows.Add($"    [{Dim}]no skills match '{Esc(f)}'[/]");
            return rows;
        }

        int nameW = names.Max(n => n.Length);
        int descBudget = Math.Max(16, Math.Max(8, width) - 6 - nameW - 2);
        int start = WindowStart(names.Count, selected);
        int end = Math.Min(names.Count, start + PreviewWindow);
        if (start > 0) rows.Add($"    [{Dim}]\u2191 {start} more[/]");
        for (int i = start; i < end; i++)
        {
            string name = names[i];
            string oneLine = Trunc(CollapseWs(descOf.GetValueOrDefault(name) ?? ""), descBudget);
            rows.Add(i == selected
                ? $"  [{Accent}]\u203a[/] [{Text}]{Esc(name.PadRight(nameW))}[/]  [{Text}]{Esc(oneLine)}[/]"
                : $"    [{Agent}]{Esc(name.PadRight(nameW))}[/]  [{Muted}]{Esc(oneLine)}[/]");
        }
        if (end < names.Count) rows.Add($"    [{Dim}]\u2193 {names.Count - end} more[/]");
        return rows;
    }

    /// <summary>Tool names matching <paramref name="filter"/> (name or description), sorted.</summary>
    public static List<string> RankTools(string? filter, IReadOnlyList<(string Name, string Desc)> tools)
    {
        var f = (filter ?? "").Trim().ToLowerInvariant();
        return tools
            .Where(t => f.Length == 0 || t.Name.ToLowerInvariant().Contains(f) || (t.Desc ?? "").ToLowerInvariant().Contains(f))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Name)
            .ToList();
    }

    /// <summary>
    /// Live, scrollable tools catalog shown beneath the input box while typing "/tools".
    /// Surfaces the available tool names (with a one-line description) in a paged window -
    /// the expandable view behind the session-header tool badge, without dumping the whole
    /// list inline. Arrow keys scroll the selection; pure/width-aware.
    /// </summary>
    public static List<string> ToolsPreview(string? filter, IReadOnlyList<(string Name, string Desc)> tools, int width, int selected = -1)
    {
        var f = (filter ?? "").Trim().ToLowerInvariant();
        var rows = new List<string>();
        if (tools.Count == 0)
        {
            rows.Add($"    [{Dim}]no tools available[/]");
            return rows;
        }

        var names = RankTools(filter, tools);
        var descOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tools) descOf[t.Name] = t.Desc ?? "";

        rows.Add($"  [{Accent}]\u2503[/] [{Accent}]tools[/] [{Dim}]({names.Count})[/]");
        if (names.Count == 0)
        {
            rows.Add($"    [{Dim}]no tools match '{Esc(f)}'[/]");
            return rows;
        }

        int nameW = names.Max(n => n.Length);
        int descBudget = Math.Max(16, Math.Max(8, width) - 6 - nameW - 2);
        int start = WindowStart(names.Count, selected);
        int end = Math.Min(names.Count, start + PreviewWindow);
        if (start > 0) rows.Add($"    [{Dim}]\u2191 {start} more[/]");
        for (int i = start; i < end; i++)
        {
            string name = names[i];
            string oneLine = Trunc(CollapseWs(descOf.GetValueOrDefault(name) ?? ""), descBudget);
            rows.Add(i == selected
                ? $"  [{Accent}]\u203a[/] [{Text}]{Esc(name.PadRight(nameW))}[/]  [{Text}]{Esc(oneLine)}[/]"
                : $"    [{Agent}]{Esc(name.PadRight(nameW))}[/]  [{Muted}]{Esc(oneLine)}[/]");
        }
        if (end < names.Count) rows.Add($"    [{Dim}]\u2193 {names.Count - end} more[/]");
        return rows;
    }

    /// <summary>
    /// Live, web-app-style sessions autocomplete shown beneath the input box while typing
    /// "/resume". Fuzzy-filters resumable sessions by id OR first-message preview on the text
    /// after the command word, and renders each match as "id  preview" (width-aware). Pure.
    /// </summary>
    /// <summary>Session ids matching <paramref name="filter"/> (id or preview), in catalog order.</summary>
    public static List<string> RankSessions(string? filter, IReadOnlyList<(string Id, string Preview)> sessions)
    {
        var f = (filter ?? "").Trim().ToLowerInvariant();
        return sessions
            .Where(se => f.Length == 0 || se.Id.ToLowerInvariant().Contains(f) || (se.Preview ?? "").ToLowerInvariant().Contains(f))
            .Select(se => se.Id)
            .ToList();
    }

    public static List<string> SessionsPreview(string? filter, IReadOnlyList<(string Id, string Preview)> sessions, int width, int selected = -1)
    {
        var f = (filter ?? "").Trim().ToLowerInvariant();
        var rows = new List<string>();
        if (sessions.Count == 0)
        {
            rows.Add($"    [{Dim}]no resumable sessions[/]");
            return rows;
        }

        var ids = RankSessions(filter, sessions);
        var prevOf = sessions.ToDictionary(se => se.Id, se => se.Preview, StringComparer.Ordinal);

        rows.Add($"  [{Accent}]\u2503[/] [{Accent}]resume[/] [{Dim}]({ids.Count})[/]");
        if (ids.Count == 0)
        {
            rows.Add($"    [{Dim}]no sessions match '{Esc(f)}'[/]");
            return rows;
        }

        int idW = ids.Max(i => i.Length);
        int prevBudget = Math.Max(16, Math.Max(8, width) - 6 - idW - 2);
        int start = WindowStart(ids.Count, selected);
        int end = Math.Min(ids.Count, start + PreviewWindow);
        if (start > 0) rows.Add($"    [{Dim}]\u2191 {start} more[/]");
        for (int i = start; i < end; i++)
        {
            string id = ids[i];
            string oneLine = Trunc(CollapseWs(prevOf.GetValueOrDefault(id) ?? ""), prevBudget);
            rows.Add(i == selected
                ? $"  [{Accent}]\u203a[/] [{Text}]{Esc(id.PadRight(idW))}[/]  [{Text}]{Esc(oneLine)}[/]"
                : $"    [{Agent}]{Esc(id.PadRight(idW))}[/]  [{Muted}]{Esc(oneLine)}[/]");
        }
        if (end < ids.Count) rows.Add($"    [{Dim}]\u2193 {ids.Count - end} more[/]");
        return rows;
    }

    /// <summary>
    /// Live fuzzy file picker shown beneath the input box while typing an "@" file reference
    /// (Claude-Code style). Filters the provided relative-path catalog by a subsequence match
    /// on the text after "@", ranking shorter / earlier matches first. Width-aware, pure.
    /// </summary>
    /// <summary>Files matching <paramref name="filter"/>, best fuzzy match first (capped).</summary>
    public static List<string> RankFiles(string? filter, IReadOnlyList<string> files)
    {
        var f = (filter ?? "").Trim();
        if (files.Count == 0) return new();
        if (f.Length == 0) return files.Take(64).ToList();
        return files
            .Select(path => (path, score: FuzzyScore(path, f)))
            .Where(t => t.score >= 0)
            .OrderBy(t => t.score).ThenBy(t => t.path.Length)
            .ThenBy(t => t.path, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.path)
            .ToList();
    }

    public static List<string> FilesPreview(string? filter, IReadOnlyList<string> files, int width, int selected = -1, bool installDirHint = false)
    {
        var f = (filter ?? "").Trim();
        var rows = new List<string>();
        if (files.Count == 0)
        {
            rows.Add($"    [{Dim}]no files indexed[/]");
            // When the only reason there are no useful files is that @ is indexing mux's own
            // install dir, tell the user how to point it at their project.
            if (installDirHint)
                rows.Add($"    [{Warn}]@ is indexing the mux install dir - launch with --workspace <path>[/]");
            return rows;
        }

        var matches = RankFiles(filter, files);
        string hint = installDirHint ? $" [{Warn}](install dir - use --workspace)[/]" : "";
        rows.Add($"  [{Accent}]\u2503[/] [{Accent}]files[/] [{Dim}](@{Esc(f)})[/]{hint}");
        if (matches.Count == 0)
        {
            rows.Add($"    [{Dim}]no files match '@{Esc(f)}'[/]");
            return rows;
        }

        int budget = Math.Max(16, Math.Max(8, width) - 6);
        int start = WindowStart(matches.Count, selected);
        int end = Math.Min(matches.Count, start + PreviewWindow);
        if (start > 0) rows.Add($"    [{Dim}]\u2191 {start} more[/]");
        for (int i = start; i < end; i++)
        {
            rows.Add(i == selected
                ? $"  [{Accent}]\u203a[/] [{Text}]{Esc(Trunc(matches[i], budget))}[/]"
                : $"    [{Agent}]{Esc(Trunc(matches[i], budget))}[/]");
        }
        if (end < matches.Count) rows.Add($"    [{Dim}]\u2193 {matches.Count - end} more[/]");
        return rows;
    }

    /// <summary>The best (top-ranked) file match for an "@" filter, or null when none match.
    /// Mirrors <see cref="FilesPreview"/> ranking so Tab accepts what the preview shows first.</summary>
    public static string? TopFileMatch(string? filter, IReadOnlyList<string> files)
        => RankFiles(filter, files).FirstOrDefault();

    /// <summary>
    /// Subsequence fuzzy score: lower is better, -1 = no match. Rewards matches against the
    /// file name (after the last '/') and contiguous runs; a plain substring beats a scattered
    /// subsequence. Case-insensitive.
    /// </summary>
    private static int FuzzyScore(string path, string filter)
    {
        if (filter.Length == 0) return 0;
        string p = path.ToLowerInvariant();
        string q = filter.ToLowerInvariant();

        // Strong preference: substring hit (in the file name first, then the full path).
        int slash = p.LastIndexOf('/');
        string name = slash >= 0 ? p[(slash + 1)..] : p;
        int nameHit = name.IndexOf(q, StringComparison.Ordinal);
        if (nameHit >= 0) return nameHit;                 // best: earlier in the file name
        int pathHit = p.IndexOf(q, StringComparison.Ordinal);
        if (pathHit >= 0) return 100 + pathHit;           // next: substring somewhere in path

        // Fallback: in-order subsequence; score by span length (tighter = better).
        int qi = 0, first = -1, last = -1;
        for (int i = 0; i < p.Length && qi < q.Length; i++)
            if (p[i] == q[qi]) { if (first < 0) first = i; last = i; qi++; }
        if (qi < q.Length) return -1;                     // not a subsequence at all
        return 1000 + (last - first);
    }

    /// <summary>
    /// The v0.12.0 M2 TaskBoard strip (Ctrl+T): a one-line progress bar over the shared team
    /// board plus up to a few color-coded task rows (pending=dim, in-progress=accent,
    /// blocked=warn, done=ok, failed=err). Pure - the driver passes a tally + pre-flattened
    /// rows (id, status, owner, subject) so this stays free of any State dependency and is
    /// unit-testable. Rendered with auto-wrap OFF by the live region like every other strip.
    /// </summary>
    public static List<string> TaskBoardStrip(
        int total, int done, int inProgress, int blocked, int failed,
        IReadOnlyList<(string Id, string Status, string? Owner, string Subject, int Artifacts)> rows,
        int maxRows = 5, int offset = 0)
    {
        var outRows = new List<string>();
        int segs = 6;
        int filled = total > 0 ? (int)System.Math.Round(segs * (double)done / total) : 0;
        filled = System.Math.Clamp(filled, 0, segs);
        string bar = new string('\u2593', filled) + new string('\u2591', segs - filled);
        string blockedNote = blocked > 0 ? $" [{Warn}]\u00b7 {blocked} blocked[/]" : "";
        string failedNote = failed > 0 ? $" [{Err}]\u00b7 {failed} failed[/]" : "";
        outRows.Add($"  [{Accent}]\u25a4 board[/] [{Dim}]{bar}[/] [{Text}]{done}/{total} done[/]{blockedNote}{failedNote}");

        if (total == 0)
        {
            outRows.Add($"    [{Dim}]no tasks yet[/]");
            return outRows;
        }

        // Window the (possibly long) task list through a maxRows-tall viewport scrollable with
        // Up/Down while the Ctrl+T strip is open (offset clamped by the driver). The "above" /
        // "more" affordances show how many rows are hidden in each direction so the user knows
        // there is more to scroll to.
        int maxOffset = System.Math.Max(0, rows.Count - maxRows);
        offset = System.Math.Clamp(offset, 0, maxOffset);
        if (offset > 0)
            outRows.Add($"    [{Dim}]\u2191 {offset} above[/]");
        int shown = System.Math.Min(maxRows, rows.Count - offset);
        for (int i = 0; i < shown; i++)
        {
            var (id, status, owner, subject, artifacts) = rows[offset + i];
            string tint = status switch
            {
                "InProgress" => Accent,
                "Blocked" => Warn,
                "Done" => Ok,
                "Failed" => Err,
                _ => Dim,
            };
            string glyph = status switch
            {
                "Done" => "\u2713",
                "Failed" => "\u2717",
                "InProgress" => "\u25cf",
                "Blocked" => "\u25cb",
                _ => "\u00b7",
            };
            string who = string.IsNullOrEmpty(owner) ? "" : $" [{Dim}]@{Esc(owner)}[/]";
            string subj = subject ?? "";
            if (subj.Length > 48) subj = subj[..47] + "\u2026";
            string files = artifacts > 0 ? $" [{Dim}]\U0001F4CE{artifacts}[/]" : "";
            outRows.Add($"    [{tint}]{glyph}[/] [{Dim}]{Esc(id)}[/] [{Text}]{Esc(subj)}[/]{who}{files}");
        }
        int below = rows.Count - offset - shown;
        if (below > 0)
            outRows.Add($"    [{Dim}]\u2193 +{below} more[/]");
        return outRows;
    }
}
