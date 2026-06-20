using Spectre.Console;

namespace MuxSwarm.Utils;

/// <summary>
/// v0.11.0 Workstream G - the "tui" interactive render layer (Model A: enhanced inline
/// chrome, Claude-Code-familiar). These helpers are only reached when
/// <see cref="MuxConsole.IsTui"/> is true (capable interactive TTY, not stdio/serve).
/// The classic line renderer remains the fallback and the stdio/serve NDJSON path is
/// untouched - every caller branches on IsTui only AFTER the StdioMode short-circuit.
///
/// "Inline chrome" means: the normal scrolling transcript is preserved (native terminal
/// scrollback, G9), but tool calls, results, diffs, delegations and the input prompt are
/// rendered as rich bordered cards with status glyphs, plus a context-meter status bar.
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

    /// <summary>G2/G7 - session header card shown when a TUI interactive loop starts.</summary>
    public static void RenderTuiSessionHeader(string agentName, string model, string provider)
    {
        if (!IsTui) return;
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
    /// G7 - context-meter + mode-badge status bar. Rendered inline just before an input
    /// prompt in TUI mode so the user always sees context usage and active modes, the way
    /// Claude Code pins a status line. Safe no-op outside TUI.
    /// </summary>
    public static void RenderTuiStatusBar(uint tokens, uint threshold, bool plan, bool ultra, bool parallelSub)
    {
        if (!IsTui) return;
        WithConsole(() =>
        {
            var badges = new List<string>();
            badges.Add($"[{TC.Dim}]tui[/]");
            if (plan)        badges.Add($"[{TC.Plan}]plan[/]");
            if (ultra)       badges.Add($"[{TC.Ultra}]ultra[/]");
            if (parallelSub) badges.Add($"[{TC.Accent}]psub[/]");

            string meter;
            if (threshold > 0)
            {
                double frac = Math.Clamp((double)tokens / threshold, 0, 1);
                const int width = 16;
                int filled = (int)Math.Round(frac * width);
                string colour = frac < 0.6 ? TC.Ok : frac < 0.85 ? TC.Warn : TC.Err;
                string bar = $"[{colour}]" + new string('\u2501', filled) + "[/]" +
                             $"[{TC.Dim}]" + new string('\u2501', width - filled) + "[/]";
                meter = $"{bar} [{TC.Muted}]{tokens:N0}/{threshold:N0} ({frac * 100:F0}%)[/]";
            }
            else
            {
                meter = $"[{TC.Muted}]{tokens:N0} tokens[/]";
            }

            AnsiConsole.MarkupLine($"  {string.Join($" [{TC.Dim}]\u00b7[/] ", badges)}   {meter}");
        }, clearIndicator: false);
    }

    /// <summary>G4 - tool-call line with a running glyph (compact, precedes the result panel).</summary>
    public static void RenderTuiToolCall(string agent, string tool, string? args)
    {
        WithConsole(() =>
        {
            string argHint = string.IsNullOrWhiteSpace(args)
                ? ""
                : $"  [{TC.Dim}]{Esc(Trunc(CollapseWhitespace(args!), 72))}[/]";
            AnsiConsole.MarkupLine($"  [{TC.Warn}]\u25cf[/] [{TC.Agent}]{Esc(agent)}[/] [{TC.Dim}]\u2192[/] [{TC.Accent}]{Esc(tool)}[/]{argHint}");
        }, clearIndicator: false);
    }

    /// <summary>G4 - collapsed one-line tool result with an ok glyph.</summary>
    public static void RenderTuiToolResultSummary(string agent, string summary)
    {
        WithConsole(() =>
        {
            string clean = Trunc(CollapseWhitespace(summary), 140);
            AnsiConsole.MarkupLine($"    [{TC.Ok}]\u2713[/] [{TC.Muted}]{Esc(clean)}[/]");
        });
    }

    /// <summary>
    /// G4/G5 - full tool result as a bordered panel with a status glyph. If the payload
    /// looks like a unified/git-style diff it is routed to the diff renderer (G5).
    /// </summary>
    public static void RenderTuiToolResultPanel(string agent, string tool, string fullResult, bool swarm)
    {
        WithConsole(() =>
        {
            string text = Common.ExtractMcpText(fullResult);
            if (LooksLikeDiff(text))
            {
                RenderDiffBody(tool, text);
                return;
            }

            int cap = swarm ? 500 : 2000;
            string body = text.Length > cap ? Esc(text[..cap]) + $"\n[{TC.Dim}]\u2026 truncated[/]" : Esc(text);
            bool err = LooksLikeError(text);
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

    /// <summary>G5 - public entry to render a diff as a syntax-tinted panel.</summary>
    public static void RenderTuiDiff(string title, string diff)
    {
        if (!IsTui) return;
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

    /// <summary>G8 - delegation rendered as a small tree so swarm/sub-agent fan-out is legible.</summary>
    public static void RenderTuiDelegation(string fromAgent, string toAgent, string task, int truncLength)
    {
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

    /// <summary>G2/G8 - agent turn header card.</summary>
    public static void RenderTuiTurnHeader(string agentName)
    {
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
        WithConsole(() =>
        {
            AnsiConsole.MarkupLine($"  [{TC.Ok}]\u2714[/] [{TC.Agent}]{Esc(agent)}[/] [{TC.Dim}]completed[/]  [{TC.Muted}]{Esc(Trunc(summary, 120))}[/]");
        });
    }

    /// <summary>
    /// G6 - slash-command palette / preview. Renders a categorized, filterable card of
    /// available commands. Invoked when the user submits a bare "/" or "/?" (Model A's
    /// robust alternative to fragile raw-keystroke interception under blocking ReadInput).
    /// </summary>
    public static void RenderTuiSlashPalette(string? filter = null)
    {
        if (!IsTui) return;
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

    private static readonly (string Cmd, string Desc)[] SlashPaletteEntries =
    {
        ("/plan",     "Toggle plan mode (confirm before exec)"),
        ("/ultra",    "Deep-reasoning mode (plan + max reasoning + steering)"),
        ("/classic",  "Switch to the classic line renderer"),
        ("/tui",      "Switch to the live TUI renderer"),
        ("/compact",  "Compact current session context"),
        ("/tokens",   "Show context/token usage"),
        ("/undo",     "Undo the last exchange"),
        ("/retry",    "Retry the last turn"),
        ("/resume",   "Resume a previous single-agent session"),
        ("/swap",     "Swap the active single-agent model"),
        ("/skills",   "List available local skills"),
        ("/tools",    "List available MCP tools"),
        ("/status",   "Show system status"),
        ("/help",     "Full command reference"),
        ("/qc",       "Exit the agent loop"),
    };

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
        if (text.Contains("@@ ") && text.Contains("@@")) return true;
        // git-style header produced by the edit_file tool.
        if (text.Contains("--- ") && text.Contains("+++ ")) return true;
        return false;
    }

    private static bool LooksLikeError(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var head = text.Length > 60 ? text[..60] : text;
        head = head.ToLowerInvariant();
        return head.StartsWith("error") || head.Contains("exception") ||
               head.Contains("traceback") || head.Contains("failed:");
    }
}
