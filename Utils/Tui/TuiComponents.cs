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

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");

    private static string CollapseWs(string s)
        => string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");

    private static string Trunc(string s, int max)
        => TuiMarkup.TruncatePlain(s ?? "", max);

    /// <summary>Session header card (committed once when an interactive TUI loop starts).</summary>
    public static List<string> SessionHeader(string agent, string model, string provider) => new()
    {
        "",
        $"  [{Accent}]\u2503[/] [{Accent}]session[/]  [{Dim}]\u00b7[/]  [{Agent}]{Esc(agent)}[/]  [{Dim}]\u00b7[/]  [{Text}]{Esc(model)}[/]  [{Dim}]\u00b7[/]  [{Muted}]{Esc(provider)}[/]",
        ""
    };

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

    /// <summary>Collapsed one-line tool result (Claude-Code style: first line + "(+N lines)").</summary>
    public static List<string> ToolResultCompact(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0) return new();
        string first = Trunc(CollapseWs(lines[0]), 110);
        int more = lines.Length - 1;
        string moreHint = more > 0 ? $" [{Dim}](+{more} line{(more == 1 ? "" : "s")})[/]" : "";
        return new() { $"    [{Dim}]\u23bf[/] [{Muted}]{Esc(first)}[/]{moreHint}" };
    }

    /// <summary>Expanded tool result as a bordered card with a status glyph.</summary>
    public static List<string> ToolResultPanel(string tool, string text, bool error, int width, int cap = 2000)
    {
        string glyph = error ? $"[{Err}]\u2717[/]" : $"[{Ok}]\u2713[/]";
        string col = error ? Err : Border;
        var body = (text ?? "");
        if (body.Length > cap) body = body[..cap] + "\n\u2026 truncated";

        int inner = Math.Max(8, width - 4);
        var outp = new List<string> { $"  [{col}]\u256d\u2500[/] {glyph} [{Accent}]{Esc(tool)}[/]" };
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
            foreach (var w in TuiMarkup.WrapPlain(raw, inner))
                outp.Add($"  [{col}]\u2502[/] [{Text}]{Esc(w)}[/]");
        outp.Add($"  [{col}]\u2570{new string('\u2500', Math.Min(inner, 40))}[/]");
        return outp;
    }

    /// <summary>Unified/git diff rendered with +/- tinting inside a card.</summary>
    public static List<string> Diff(string title, string diff, int width)
    {
        int inner = Math.Max(8, width - 4);
        var outp = new List<string> { $"  [{Border}]\u256d\u2500[/] [{Accent}]diff[/] [{Dim}]\u00b7 {Esc(Trunc(title, 48))}[/]" };
        foreach (var raw in (diff ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            string col =
                raw.StartsWith("+++") || raw.StartsWith("---") ? Muted :
                raw.StartsWith("@@") ? Accent :
                raw.StartsWith("+") ? DiffAdd :
                raw.StartsWith("-") ? DiffDel : Dim;
            foreach (var w in TuiMarkup.WrapPlain(raw, inner))
                outp.Add($"  [{Border}]\u2502[/] [{col}]{Esc(w)}[/]");
        }
        outp.Add($"  [{Border}]\u2570{new string('\u2500', Math.Min(inner, 40))}[/]");
        return outp;
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
    public static string Footer(uint tokens, uint threshold, bool plan, bool ultra, bool psub, string? effort = null)
    {
        var badges = new List<string> { $"[{Dim}]tui[/]" };
        if (plan)  badges.Add($"[{Plan}]plan[/]");
        if (ultra) badges.Add($"[{Ultra}]ultra[/]");
        if (psub)  badges.Add($"[{Accent}]psub[/]");

        // Context meter: full bar+percent when a threshold is known; a bare token count when
        // tokens have accrued without a threshold; and NOTHING at all when idle (0 tokens, no
        // threshold) so the footer never shows a noisy, meaningless "0 tokens".
        string meter;
        if (threshold > 0)
        {
            double frac = Math.Clamp((double)tokens / threshold, 0, 1);
            const int width = 16;
            int filled = (int)Math.Round(frac * width);
            string colour = frac < 0.6 ? Ok : frac < 0.85 ? Warn : Err;
            string bar = $"[{colour}]" + new string('\u2501', filled) + "[/]" +
                         $"[{Dim}]" + new string('\u2501', Math.Max(0, width - filled)) + "[/]";
            meter = $"{bar} [{Muted}]{tokens:N0}/{threshold:N0} ({frac * 100:F0}%)[/]";
        }
        else if (tokens > 0)
        {
            meter = $"[{Muted}]{tokens:N0} tokens[/]";
        }
        else
        {
            meter = "";
        }

        string left = string.Join($" [{Dim}]\u00b7[/] ", badges);
        string effortChip = string.IsNullOrEmpty(effort) ? "" : $"  [{Dim}]\u00b7[/]  [{Warn}]\u25d0 {Esc(effort)}[/] [{Dim}]/effort[/]";
        string right = string.IsNullOrEmpty(meter) ? "" : $"   {meter}";
        return $"  {left}{right}{effortChip}";
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
    {
        const string cur = "#E0E0E0";
        if (string.IsNullOrEmpty(buffer))
            return $"  [{Accent}]\u203a[/] [black on {cur}] [/][{Dim}]type a message, or / for commands\u2026[/]";

        cursor = Math.Clamp(cursor, 0, buffer.Length);
        string before = Esc(buffer[..cursor]);
        if (cursor >= buffer.Length)
            return $"  [{Accent}]\u203a[/] [{Text}]{before}[/][black on {cur}] [/]";
        string at = Esc(buffer[cursor].ToString());
        string after = Esc(buffer[(cursor + 1)..]);
        return $"  [{Accent}]\u203a[/] [{Text}]{before}[/][black on {cur}]{at}[/][{Text}]{after}[/]";
    }

    /// <summary>Slash-command palette rows filtered by the current as-you-type token.</summary>
    public static List<string> SlashPalette(string? filter, IReadOnlyList<(string Cmd, string Desc)> entries)
    {
        var f = (filter ?? "").TrimStart('/').Trim().ToLowerInvariant();
        var rows = new List<string>();
        int shown = 0;
        foreach (var (cmd, desc) in entries)
        {
            if (f.Length > 0 && !cmd.ToLowerInvariant().Contains(f) && !desc.ToLowerInvariant().Contains(f))
                continue;
            rows.Add($"    [{Accent}]{Esc(cmd),-14}[/] [{Muted}]{Esc(desc)}[/]");
            if (++shown >= 8) break;
        }
        if (shown == 0) rows.Add($"    [{Dim}]no commands match '{Esc(f)}'[/]");
        return rows;
    }
}
