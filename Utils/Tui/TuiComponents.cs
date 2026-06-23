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
    // Palette (kept in sync with MuxConsole.Tui TC).
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
    // Calm "working" cyan for the thinking/spinner line (NOT Warn - orange reads as an error).
    public const string Think    = "#7AA2C0";
    // Dim blue fill for the cached portion of the context meter (vs the bright live portion).
    public const string CacheFill = "#3E5A6E";
    // Elevated "card" body fill (GitHub-dark canvas-subtle feel) so tool/diff panels read as a
    // solid block distinct from the airy prose on the terminal's base background.
    public const string CardBg  = "#161B22";
    // Diff line backgrounds: faint green/red bands + a neutral context fill on the card.
    public const string DiffAddBg = "#16261C";
    public const string DiffDelBg = "#2A1A1C";
    public const string DiffHunkBg = "#16202C";
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
        $"  [{Accent}]\u258e[/] [{Text}]{Esc(line ?? "")}[/]"
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

    /// <summary>Compact token count: 30000 -> "30k", 1500 -> "1.5k", &lt;1000 stays exact.</summary>
    private static string Fmt(uint n)
        => n >= 1000 ? (n % 1000 == 0 ? $"{n / 1000}k" : $"{n / 1000.0:0.0}k") : n.ToString();

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

    /// <summary>Tool-call line: a running glyph, tool name, and a compact arg hint.</summary>
    public static List<string> ToolCall(string tool, string? args)
    {
        string hint = string.IsNullOrWhiteSpace(args)
            ? ""
            : $" [{Dim}]({Esc(Trunc(CollapseWs(args!), 56))})[/]";
        return new() { $"  [{Warn}]\u25cf[/] [{Accent}]{Esc(tool)}[/]{hint}" };
    }

    /// <summary>
    /// Tool call + its compact result merged into ONE line (density win on command-heavy
    /// turns): a completed glyph, tool name + arg hint, then the first informative result
    /// line and a "(+N lines)" hint. Used when a call resolves to a short, non-error,
    /// non-diff result; otherwise the call and result render as separate blocks.
    /// </summary>
    public static List<string> ToolCallResultMerged(string tool, string? args, string resultText, bool error = false, bool expandable = false)
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
        string glyph = error ? $"[{Err}]\u2717[/]" : $"[{Ok}]\u25cf[/]";
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
        // Prefer the most informative line: for async-shell dispatches the result leads with an
        // opaque "Job ID:" GUID, so surface the "Command:" line instead (Claude-Code style - show
        // what is actually running, not the bookkeeping id).
        int pick = Array.FindIndex(lines, l =>
            l.TrimStart().StartsWith("Command:", StringComparison.OrdinalIgnoreCase));
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
        string spin = SubAgentFrames[((frame % SubAgentFrames.Length) + SubAgentFrames.Length) % SubAgentFrames.Length];
        foreach (var (agent, status, tint) in agents)
        {
            string st = string.IsNullOrWhiteSpace(status) ? "working" : CollapseWs(status);
            if (st.Length > 60) st = st[..59] + "\u2026";
            // The ctrl+e affordance is shown live (not just after completion) so the user knows the
            // still-running sub-agent's buffered output can be expanded inline at any time.
            outp.Add($"  [{tint}]{spin}[/] [{Agent}]{Esc(agent)}[/] [{Dim}]\u00b7[/] [{Think} italic]{Esc(st)}\u2026[/] [{Dim}](ctrl+e)[/]");
        }
        return outp;
    }

    /// <summary>Expanded tool result as a bordered card with a status glyph.</summary>
    public static List<string> ToolResultPanel(string tool, string text, bool error, int width, int cap = 2000, bool expanded = false)
    {
        string glyph = error ? $"[{Err}]\u2717[/]" : $"[{Ok}]\u2713[/]";
        string col = error ? Err : Border;
        var body = (text ?? "");
        // When expanded (NAV cursor opened the card) show the FULL result - no truncation,
        // since the whole point of expanding is to read everything. Only the compact inline
        // path applies the cap.
        if (!expanded && body.Length > cap) body = body[..cap] + "\n\u2026 truncated";

        int inner = Math.Max(8, width - 5);
        var outp = new List<string> { $"  [{col}]\u256d\u2500[/] {glyph} [{Accent}]{Esc(tool)}[/]" };
        foreach (var raw in TrimTrailingBlankLines(body).Split('\n'))
            foreach (var w in TuiMarkup.WrapPlain(raw, inner))
                outp.Add(ShadedRow(col, w, Text, CardBg, inner));
        outp.Add($"  [{col}]\u2570{new string('\u2500', inner + 1)}[/]");
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
        string title, string body, string tintHex, int width, int maxRows, bool anchorTail, bool error = false)
    {
        string tint = error ? Err : tintHex;
        int inner = Math.Max(8, width - 4);
        int cap = Math.Max(1, maxRows);
        var wrapped = new List<string>();
        foreach (var raw in TrimTrailingBlankLines(body ?? "").Split('\n'))
            foreach (var w in TuiMarkup.WrapPlain(raw, inner))
                wrapped.Add(w);
        int hidden = 0;
        if (wrapped.Count > cap)
        {
            hidden = wrapped.Count - cap;
            wrapped = anchorTail ? wrapped.GetRange(hidden, cap) : wrapped.GetRange(0, cap);
        }
        var outp = new List<string>
        {
            $"  [{tint}]\u256d\u2500[/] [{Accent}]{Esc(title)}[/] [{Dim}](live \u00b7 ctrl+e collapse)[/]"
        };
        // Top elision marker (tail-anchored views only).
        if (hidden > 0 && anchorTail)
            outp.Add($"  [{tint}]\u2502[/] [{Dim}]\u2026 +{hidden} earlier line{(hidden == 1 ? "" : "s")}[/]");
        foreach (var w in wrapped)
            outp.Add($"  [{tint}]\u2502[/] [{Text}]{Esc(w)}[/]");
        // Bottom elision marker (head-anchored views only) - points at Ctrl+G for the full block.
        if (hidden > 0 && !anchorTail)
            outp.Add($"  [{tint}]\u2502[/] [{Dim}]\u2026 +{hidden} more line{(hidden == 1 ? "" : "s")} (ctrl+g for full)[/]");
        outp.Add($"  [{tint}]\u2570{new string('\u2500', inner)}[/]");
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
                string codeCell = (mk + wrapped[wi]).PadRight(codeW + 1);
                outp.Add($"  [{Border}]\u2502[/] [{GutterFg} on {bg}]{gOld} {gNew} [/][{fg} on {bg}]{Esc(codeCell)}[/]");
            }
        }
        outp.Add($"  [{Border}]\u2570{new string('\u2500', gutterCols + codeW + 1)}[/]");
        return outp;
    }

    private const string DiffCtxFg = "#A0A0A0"; // diff context line (neutral grey)

    /// <summary>Shade one card body row: rail + a full-width background band of <paramref name="text"/>.</summary>
    private static string ShadedRow(string railCol, string content, string fg, string bg, int inner)
        => $"  [{railCol}]\u2502[/] [{fg} on {bg}]{Esc(content.PadRight(inner))}[/]";

    private static bool IsMeta(string s)
        => s.StartsWith("+++") || s.StartsWith("---") || s.StartsWith("diff ") || s.StartsWith("index ");

    private static string MetaRow(string raw, int gw, int gutterCols, int codeW)
    {
        string blank = new string(' ', gw);
        string codeCell = Esc(Trunc(raw, codeW + 1).PadRight(codeW + 1));
        return $"  [{Border}]\u2502[/] [{GutterFg} on {CardBg}]{blank} {blank} [/][{Muted} on {CardBg}]{codeCell}[/]";
    }

    private static string HunkRow(string raw, int gw, int gutterCols, int codeW)
    {
        string blank = new string(' ', gw);
        string codeCell = Esc(Trunc(raw, codeW + 1).PadRight(codeW + 1));
        return $"  [{Border}]\u2502[/] [{GutterFg} on {DiffHunkBg}]{blank} {blank} [/][{Accent} on {DiffHunkBg}]{codeCell}[/]";
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
    /// The pinned footer: mode badges + a context meter. Lives at the bottom of the live
    /// region and is repainted every frame, so it never strands or scrolls away.
    /// </summary>
    public static string Footer(uint tokens, uint threshold, bool plan, bool ultra, bool psub, bool sub = false, string? effort = null, bool modeCycleHint = false, string? sessionId = null, uint cached = 0, uint sysTokens = 0, uint toolTokens = 0)
    {
        // No standing "tui" badge - it is noise. Only show active modes.
        // Ultra implies plan + max reasoning (and is typically run with psub), so when ultra is
        // active the three mode badges are redundant noise - collapse them to a single "ultra"
        // chip. Otherwise show whichever discrete modes are on.
        var badges = new List<string>();
        if (ultra)
        {
            badges.Add($"[{Ultra}]ultra[/]");
        }
        else
        {
            if (plan) badges.Add($"[{Plan}]plan[/]");
            if (psub) badges.Add($"[{Accent}]psub[/]");
            if (sub) badges.Add($"[{Ok}]sub[/]");
        }

        // Context meter: full bar+percent when a threshold is known; a bare token count when
        // tokens have accrued without a threshold; and NOTHING at all when idle (0 tokens, no
        // threshold) so the footer never shows a noisy, meaningless "0 tokens".
        // The meter plots TOTAL context (live + cached) against the threshold, because
        // compaction fires on the total crossing it - so the bar reaching full genuinely
        // means compaction is imminent. The filled span is split into a dim cached segment
        // and a bright live segment so the user sees how much headroom is real (live) vs
        // already-pinned (cached). `tokens` here is the live (uncached) count.
        string meter;
        if (threshold > 0)
        {
            uint total = tokens + cached;
            double fracTotal = Math.Clamp((double)total / threshold, 0, 1);
            const int width = 16;
            int filled = (int)Math.Round(fracTotal * width);
            int cachedCells = Math.Clamp((int)Math.Round((double)cached / threshold * width), 0, filled);
            int liveCells = filled - cachedCells;
            int empty = Math.Max(0, width - filled);
            string liveColour = fracTotal < 0.6 ? Ok : fracTotal < 0.85 ? Warn : Err;
            string bar = $"[{CacheFill}]" + new string('\u2501', cachedCells) + "[/]" +
                         $"[{liveColour}]" + new string('\u2501', liveCells) + "[/]" +
                         $"[{Dim}]" + new string('\u2501', empty) + "[/]";
            string cachedHint = cached > 0 ? $"  [{Dim}]\u00b7[/]  [{Dim}]{cached:N0} cached[/]" : "";
            meter = $"{bar} [{Muted}]{total:N0}/{threshold:N0} ({fracTotal * 100:F0}%)[/]{cachedHint}";
        }
        else if (tokens > 0)
        {
            meter = $"[{Muted}]{tokens:N0} tokens[/]";
        }
        else
        {
            meter = "";
        }

        // Static overhead breakdown: on a FRESH session the context is dominated by the system
        // prompt + the serialized tool/MCP schemas, not the conversation. Surfacing "sys" and
        // "tools" chips explains why a brand-new session already reads e.g. 30k tokens. Shown only
        // when a breakdown is known and there is meaningful overhead to explain.
        string breakdownChip = "";
        if (sysTokens > 0 || toolTokens > 0)
        {
            var parts = new List<string>();
            if (sysTokens > 0)  parts.Add($"[{Muted}]sys[/] [{Text}]{Fmt(sysTokens)}[/]");
            if (toolTokens > 0) parts.Add($"[{Muted}]tools[/] [{Text}]{Fmt(toolTokens)}[/]");
            breakdownChip = $"  [{Dim}]\u00b7[/]  " + string.Join($" [{Dim}]\u00b7[/] ", parts);
        }

        string left = string.Join($" [{Dim}]\u00b7[/] ", badges);
        // The effort chip always gets a leading dot-separator so it never butts up against the
        // meter / cached-tokens text (the "cached\u25d0" run-together bug).
        string effortChip = string.IsNullOrEmpty(effort)
            ? ""
            : $"  [{Dim}]\u00b7[/]  [{Warn}]\u25d0 {Esc(effort)}[/] [{Dim}]/effort[/]";
        // Shift+Tab discoverability hint, shown only when a mode-cycle is wired up.
        string hint = modeCycleHint ? $"  [{Dim}]\u21e7\u21b9 cycle[/]" : "";
        // Active-session id badge (Claude-Code style), shown beside the hint when present.
        string sess = string.IsNullOrEmpty(sessionId) ? "" : $"  [{Dim}]\u00b7[/]  [{Agent}]session[/] [{Muted}]{Esc(sessionId!)}[/]";
        string right = string.IsNullOrEmpty(meter) ? "" : $"   {meter}";
        // Avoid a leading double-space when there are no badges at all.
        string body = $"{left}{right}{breakdownChip}{effortChip}{hint}{sess}";
        return $"  {body.TrimStart()}";
    }

    /// <summary>A horizontal rule that spans the FULL terminal width (Claude-Code style),
    /// used to separate the transcript from the docked footer/input band. Width is computed
    /// at paint time so it tracks terminal resizes.</summary>
    public static string FullRule(int width)
    {
        int w = Math.Max(8, width);
        return $"[{Border}]{new string('\u2500', w)}[/]";
    }

    /// <summary>The input row shown in the live region while awaiting/editing input.</summary>
    public static string InputRow(string buffer)
        => $"  [{Accent}]\u203a[/] {(string.IsNullOrEmpty(buffer) ? $"[{Dim}]type a message, or / for commands\u2026[/]" : Esc(buffer))}";

    /// <summary>
    /// Input row with a synthetic block cursor at <paramref name="cursor"/>. The real
    /// terminal cursor is hidden while the live region is painted, so we draw a reverse-video
    /// cell at the edit position (Claude-Code style) to show where typing lands.
    /// </summary>
    public static string InputRowWithCursor(string buffer, int cursor)
        => InputRowWithCursor(buffer, cursor, EditorMode.Insert);

    /// <summary>
    /// Input row with a synthetic block cursor and a vim-mode prompt marker. Normal mode swaps
    /// the cyan "\u203a" prompt for a distinct "-- NORMAL --" badge + bold block prompt so the
    /// active mode is unmistakable; Insert mode is unchanged from the modeless renderer.
    /// </summary>
    public static string InputRowWithCursor(string buffer, int cursor, EditorMode mode)
        => string.Join("\n", InputRowsWithCursor(buffer, cursor, mode));

    /// <summary>
    /// Render the input/compose area as one or more markup lines. A buffer containing embedded
    /// newlines (Alt+Enter / Ctrl+J multiline compose) renders as a prompt line plus continuation
    /// lines, each gutter-aligned under the prompt, with the synthetic block cursor placed on the
    /// correct visual line. Single-line buffers return exactly one row (unchanged behaviour).
    /// </summary>
    public static List<string> InputRowsWithCursor(string buffer, int cursor, EditorMode mode, int width = 0)
    {
        const string cur = "#E0E0E0";
        string prompt = mode == EditorMode.Normal
            ? $"[{Warn}]\u25c6[/] [black on {Warn}] NORMAL [/]"
            : $"[{Accent}]\u203a[/]";
        // Continuation gutter for wrapped/multiline rows - dim vertical bar aligned under the prompt.
        string contGutter = $"[{Dim}]\u2502[/]";
        string promptLead = $"  {prompt} ";
        string contLead   = $"  {contGutter} ";
        // Visible column cost of each lead ("  " + glyph + " "). Used to size the wrap width so a
        // wrapped row's text never runs under the terminal's right edge and soft-wraps with no gutter.
        int promptLeadCols = 2 + TuiMarkup.MarkupWidth(prompt) + 1;
        int contLeadCols   = 2 + TuiMarkup.MarkupWidth(contGutter) + 1;

        if (string.IsNullOrEmpty(buffer))
            return new List<string>
            {
                mode == EditorMode.Normal
                    ? $"{promptLead}[black on {cur}] [/]"
                    : $"{promptLead}[black on {cur}] [/][{Dim}]type a message, or / for commands\u2026[/]"
            };

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
        return rows;
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
}
