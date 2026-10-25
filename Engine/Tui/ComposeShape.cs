namespace MuxSwarm.Engine.Tui;

/// <summary>
/// The compose box's shape (<c>compose.shape</c>): the chrome drawn around the input rows. Each shape wraps the
/// rows <see cref="TuiComponents.InputRowsWithCursor"/> builds for the inner width, so the caret, wrapping and
/// click-to-caret math stay in one place; the shape only reports how many columns its left chrome takes
/// (<see cref="Frame.LeftCols"/>) and which output lines hold input rows.
/// </summary>
internal static class ComposeShape
{
    /// <summary>Built-in shapes. <c>classic</c> is the stock look (rule above, <c>›</c> prompt).</summary>
    internal static readonly string[] Names = ["classic", "rule", "bracket", "box", "bubble", "smooth", "rail", "bare"];

    /// <summary>Border glyph sets (box shape, splash frame): top-left, top-right, bottom-left, bottom-right, horizontal, vertical.</summary>
    internal static readonly Dictionary<string, string[]> Borders = new()
    {
        ["rounded"] = ["\u256d", "\u256e", "\u2570", "\u256f", "\u2500", "\u2502"],
        ["square"] = ["\u250c", "\u2510", "\u2514", "\u2518", "\u2500", "\u2502"],
        ["double"] = ["\u2554", "\u2557", "\u255a", "\u255d", "\u2550", "\u2551"],
        ["thick"] = ["\u250f", "\u2513", "\u2517", "\u251b", "\u2501", "\u2503"],
        ["ascii"] = ["+", "+", "+", "+", "-", "|"],
    };

    /// <summary>Allowed <c>compose.border</c> values.</summary>
    internal static readonly string[] BorderNames = ["rounded", "square", "double", "thick", "ascii"];

    /// <summary>A shaped compose block: all lines, which of them are input rows, and the columns the
    /// shape adds before each input row's own lead.</summary>
    internal sealed record Frame(List<string> Lines, List<int> InputLines, int LeftCols, int TitleLine = -1, int TitleCol = 0);

    /// <summary>True when <paramref name="shape"/> has an edge a footer can sit in (<c>bubble</c>: a row inside the outline).</summary>
    internal static bool HasTitleEdge(string shape) => shape is "box" or "rule" or "bracket" or "bubble";

    /// <summary>Columns available for a title in the top edge of <paramref name="shape"/> at <paramref name="width"/>
    /// (<c>bubble</c>: the inner width of its footer row).</summary>
    internal static int TitleWidth(string shape, int width) => shape == "bubble"
        ? Math.Max(8, width - 2 * SideCols(shape).Left)
        : Math.Max(8, width - (shape == "box" ? 7 : 5));

    /// <summary>The active shape, or <c>classic</c>.</summary>
    internal static string Active => RegionStyle.Choice("compose.shape");

    /// <summary>True when the active shape draws its own rule above the input (so the stock input rule is skipped).</summary>
    internal static bool OwnsTopRule => Active != "classic";

    /// <summary>Columns the shape takes on the left and right of every input row at <paramref name="width"/>.</summary>
    internal static (int Left, int Right) SideCols(string shape)
    {
        int pad = RegionStyle.Number("compose.padX");
        return shape switch
        {
            "box" => (1 + pad, 1 + pad),
            "bubble" => (1 + pad, 1 + pad),
            "smooth" => (1 + pad, 1 + pad),
            "rail" => (1 + pad, 0),
            _ => (0, 0),
        };
    }

    /// <summary>Columns of the stock two-space input lead the shape replaces with its own chrome.</summary>
    internal static int DroppedLead(string shape) => shape is "box" or "bubble" or "smooth" or "rail" ? 2 : 0;

    /// <summary>Width to build the input rows at so the shaped rows fill exactly <paramref name="width"/>.</summary>
    internal static int InnerWidth(string shape, int width)
    {
        var (l, r) = SideCols(shape);
        return Math.Max(1, width - l - r + DroppedLead(shape));
    }

    /// <summary>
    /// Shape <paramref name="rows"/> (input rows already built for <c>width - left - right</c> columns, each
    /// starting with the stock two-space lead). <paramref name="width"/> is the full band width.
    /// </summary>
    internal static Frame Wrap(string shape, List<string> rows, int width) => Wrap(shape, rows, width, null, 0);

    /// <summary>
    /// As <see cref="Wrap(string, List{string}, int)"/>, embedding <paramref name="title"/> (the footer row, built for
    /// <see cref="TitleWidth"/> columns) in the top border of <c>box</c>, on the top rule of <c>rule</c>/<c>bracket</c>,
    /// or as the last row inside the <c>bubble</c> outline.
    /// <see cref="Frame.TitleLine"/>/<see cref="Frame.TitleCol"/> report where it landed (title columns start there).
    /// </summary>
    internal static Frame Wrap(string shape, List<string> rows, int width, string? title, int titleCols)
    {
        var lines = new List<string>();
        var input = new List<int>();
        var (left, right) = SideCols(shape);
        int dropLead = DroppedLead(shape);
        int inner = Math.Max(1, width - left - right);
        string border = RegionStyle.Color("compose.borderFg");
        string Rule(string glyph, int n) => $"[{border}]{Spectre.Console.Markup.Escape(string.Concat(Enumerable.Repeat(glyph, Math.Max(0, n))))}[/]";
        // Framed shapes supply their own inset (border + padX), so the stock two-space lead each input row carries
        // is dropped there; open shapes (rule, bracket, bare) keep it so text stays at its usual column.
        string Fit(string row)
        {
            if (dropLead > 0 && row.StartsWith("  ", StringComparison.Ordinal)) row = row[dropLead..];
            return row + new string(' ', Math.Max(0, inner - TuiMarkup.MarkupWidth(row)));
        }
        int padY = RegionStyle.Number("compose.padY");
        void Body(Func<string, string> frame)
        {
            for (int p = 0; p < padY; p++) lines.Add(frame(new string(' ', inner)));
            foreach (var r in rows) { input.Add(lines.Count); lines.Add(frame(Fit(r))); }
            for (int p = 0; p < padY; p++) lines.Add(frame(new string(' ', inner)));
        }
        string ruleGlyph = RegionStyle.PlainGlyph("compose.rule");
        int titleLine = -1, titleCol = 0;
        // A top edge of `n` rule cells, optionally carrying the title after a short lead-in: "── title ─────".
        string Edge(string glyph, int n, int lead)
        {
            if (title is null || titleCols <= 0 || n < lead + titleCols + 3) return Rule(glyph, n);
            titleLine = lines.Count;
            titleCol = (width - n) / 2 + lead + 1;   // 0-based column of the title: corner (box) + lead-in + one space
            return Rule(glyph, lead) + " " + title + Rule(glyph, n - lead - titleCols - 1);
        }
        switch (shape)
        {
            case "rule":
                lines.Add(Edge(ruleGlyph, width, 2));
                Body(r => r);
                break;
            case "bracket":
                lines.Add(Edge(ruleGlyph, width, 2));
                Body(r => r);
                lines.Add(Rule(ruleGlyph, width));
                break;
            case "box":
            {
                var g = Borders[RegionStyle.Choice("compose.border")];
                string pad = new(' ', left - 1);
                lines.Add($"[{border}]{Spectre.Console.Markup.Escape(g[0])}[/]" + Edge(g[4], width - 2, 1) + $"[{border}]{Spectre.Console.Markup.Escape(g[1])}[/]");
                string side = $"[{border}]{Spectre.Console.Markup.Escape(g[5])}[/]";
                Body(r => side + pad + r + pad + side);
                lines.Add($"[{border}]{Spectre.Console.Markup.Escape(g[2])}[/]" + Rule(g[4], width - 2) + $"[{border}]{Spectre.Console.Markup.Escape(g[3])}[/]");
                break;
            }
            case "bubble":
            {
                // A rounded outline all the way round the input; the footer (if given) is its last inner row, so the
                // prompt and the status line read as one bubble.
                var g = Borders["rounded"];
                string pad = new(' ', left - 1);
                string side = $"[{border}]{Spectre.Console.Markup.Escape(g[5])}[/]";
                lines.Add($"[{border}]{g[0]}{Spectre.Console.Markup.Escape(string.Concat(Enumerable.Repeat(g[4], Math.Max(0, width - 2))))}{g[1]}[/]");
                Body(r => side + pad + r + pad + side);
                if (title is not null && titleCols > 0 && titleCols <= inner)
                {
                    titleLine = lines.Count;
                    titleCol = left;
                    lines.Add(side + pad + title + new string(' ', inner - titleCols) + pad + side);
                }
                lines.Add($"[{border}]{g[2]}{Spectre.Console.Markup.Escape(string.Concat(Enumerable.Repeat(g[4], Math.Max(0, width - 2))))}{g[3]}[/]");
                break;
            }
            case "smooth":
            {
                // A filled field with rounded end caps (half-circle glyphs drawn in the fill colour on the terminal bg).
                string fill = RegionStyle.Color("compose.fill");
                string l = $"[{fill}]{Spectre.Console.Markup.Escape(RegionStyle.PlainGlyph("compose.capLeft"))}[/]";
                string r_ = $"[{fill}]{Spectre.Console.Markup.Escape(RegionStyle.PlainGlyph("compose.capRight"))}[/]";
                string pad = new(' ', left - 1);
                Body(r => l + $"[on {fill}]{pad}{r}{pad}[/]" + r_);
                break;
            }
            case "rail":
            {
                string fill = RegionStyle.Color("compose.fill");
                string rail = $"[{RegionStyle.Color("compose.railFg")}]{RegionStyle.Glyph("compose.rail")}[/]";
                string pad = new(' ', left - 1);
                Body(r => rail + $"[on {fill}]{pad}{r}[/]");
                break;
            }
            default:   // bare (and classic, which the driver never routes here)
                Body(r => r);
                break;
        }
        return new Frame(lines, input, left - dropLead, titleLine, titleCol);
    }
}
