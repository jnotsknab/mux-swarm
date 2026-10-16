using System.Text.RegularExpressions;

namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Renders a buffered GitHub-flavored Markdown table into aligned, box-bordered Spectre
/// markup rows. Unlike <see cref="TuiMarkdown"/> (which is strictly per-line and cannot
/// align columns across rows), this operates on the WHOLE table at once: the driver buffers
/// contiguous table rows during streaming and hands them here when the block ends.
///
/// Supports per-column alignment from the separator row (:--- left, :--: center, ---: right),
/// inline markdown inside cells (bold/italic/code via <see cref="TuiMarkdown"/>), and cell
/// truncation so a wide table never overflows the terminal. Pure string work - unit-testable.
/// </summary>
internal static class TuiTable
{
    private static string Border => Theme.Active.Border;  // box-drawing lines (dim)
    private static string Head => Theme.Active.MdHeading; // header cell text (accent)

    private enum Align { Left, Center, Right }

    /// <summary>True when a line looks like a GFM table row (pipe-delimited).</summary>
    public static bool IsTableRow(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.Contains('|')) return false;
        string t = s.Trim();
        int pipes = TuiMarkdown.SplitRow(t).Count - 1;
        return t.StartsWith("|") || t.EndsWith("|") || pipes >= 2;
    }

    /// <summary>True when a line is a GFM header-separator row (|---|:--:|---:|).</summary>
    public static bool IsSeparatorRow(string s)
        => !string.IsNullOrEmpty(s)
           && Regex.IsMatch(s.Trim(), @"^\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?$");

    /// <summary>
    /// Render a buffered set of raw table-row source lines into aligned bordered markup.
    /// A well-formed table is header row, separator row, then body rows; if the separator is
    /// missing the first row is still treated as the header. Returns one markup string per
    /// visual row (top border, header, header rule, body rows, bottom border).
    /// </summary>
    public static List<string> Render(IReadOnlyList<string> rawRows, int width, int indentCols = 2)
    {
        var rows = rawRows.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (rows.Count == 0) return new();

        // Separate the alignment/separator row from data rows.
        int sepIdx = rows.FindIndex(IsSeparatorRow);
        var aligns = sepIdx >= 0 ? ParseAligns(rows[sepIdx]) : new List<Align>();
        var dataRows = rows.Where((_, i) => i != sepIdx).Select(SplitCells).ToList();
        if (dataRows.Count == 0) return new();

        int cols = dataRows.Max(r => r.Count);
        // Normalize ragged rows to the column count.
        foreach (var r in dataRows)
            while (r.Count < cols) r.Add("");
        while (aligns.Count < cols) aligns.Add(Align.Left);

        // Natural column widths (plain text), then shrink to fit the terminal.
        var colW = new int[cols];
        for (int c = 0; c < cols; c++)
            colW[c] = Math.Max(1, dataRows.Max(r => TuiMarkup.Width(r[c])));

        // Budget: indent + borders. Each column costs colW + 3 ("| " + " "), plus final "|".
        int pad = Math.Max(0, indentCols);
        int chrome = 2 + cols * 3;                 // leading "│ " + per-column " │ "; +1 keeps the
                                                   // closing border out of the last terminal column (no soft-wrap).
        int avail = Math.Max(8, width) - pad - chrome;
        // Shrink to fit by repeatedly trimming the WIDEST column by one, down to a soft floor.
        // This preserves naturally-narrow columns (e.g. a short id/commit column) at full width
        // and steals space only from the genuinely wide cells, which then wrap (never truncate).
        const int floorW = 6;
        int Total() => colW.Sum();
        int guard = 0;
        while (Total() > avail && guard++ < 100000)
        {
            int widest = 0;
            for (int c = 1; c < cols; c++) if (colW[c] > colW[widest]) widest = c;
            if (colW[widest] <= floorW) break;   // everything at/under the floor: accept overflow
            colW[widest]--;
        }

        string indent = new string(' ', pad);
        var outp = new List<string>();
        // Even at the floor the grid does not fit: a wrapped grid would break its borders across
        // rows, so show each row as a stacked "header: value" record instead.
        if (Total() > avail) return Stacked(dataRows, width, indent);

        // Border glyphs from the markdown style: HEAVY by default (the boxy, thick-edged grid; light/
        // rounded read as "extremely thin" in dogfooding), or an extension's light/rounded/ascii set.
        var g = MarkdownStyle.Active.BorderGlyphs;
        string Line(int w) => string.Concat(Enumerable.Repeat(g[0], w + 2));
        string topB = indent + $"[{Border}]{g[2]}" + string.Join(g[3], colW.Select(Line)) + $"{g[4]}[/]";
        string midB = indent + $"[{Border}]{g[5]}" + string.Join(g[6], colW.Select(Line)) + $"{g[7]}[/]";
        string botB = indent + $"[{Border}]{g[8]}" + string.Join(g[9], colW.Select(Line)) + $"{g[10]}[/]";

        outp.Add("");
        outp.Add(topB);
        // Header row (first data row) styled in accent.
        outp.AddRange(RenderRow(dataRows[0], colW, aligns, indent, headerStyle: true));
        if (dataRows.Count > 1) outp.Add(midB);   // header-only table: no dangling separator
        for (int i = 1; i < dataRows.Count; i++)
            outp.AddRange(RenderRow(dataRows[i], colW, aligns, indent, headerStyle: false));
        outp.Add(botB);
        outp.Add("");
        return outp;
    }

    /// <summary>
    /// Render one logical table row as one or more PHYSICAL lines: each cell is word-wrapped
    /// (hard-breaking over-long tokens) to its column width rather than truncated, so no cell
    /// content is lost. The row is as tall as its tallest wrapped cell; shorter cells are blank-
    /// padded on the trailing physical lines, and every physical line carries the full borders.
    /// </summary>
    private static List<string> RenderRow(List<string> cells, int[] colW, List<Align> aligns, string indent, bool headerStyle)
    {
        // Wrap every cell to its column width up-front; the row height is the tallest cell.
        var wrapped = new List<List<string>>(colW.Length);
        for (int c = 0; c < colW.Length; c++)
        {
            string plain = c < cells.Count ? cells[c] : "";
            var lines = TuiMarkup.WrapPlain(plain, colW[c]);
            if (lines.Count == 0) lines.Add("");
            wrapped.Add(lines);
        }
        int height = wrapped.Max(l => l.Count);

        var outLines = new List<string>(height);
        for (int h = 0; h < height; h++)
        {
            var sb = new System.Text.StringBuilder();
            string v = MarkdownStyle.Active.BorderGlyphs[1];
            sb.Append(indent).Append($"[{Border}]{Esc(v)}[/] ");
            for (int c = 0; c < colW.Length; c++)
            {
                string seg = h < wrapped[c].Count ? wrapped[c][h] : "";
                // WrapPlain already fits to width; guard against any wide-rune overshoot.
                if (TuiMarkup.Width(seg) > colW[c])
                    seg = TuiMarkup.TruncatePlain(seg, colW[c]);
                int textW = TuiMarkup.Width(seg);
                int padTotal = Math.Max(0, colW[c] - textW);
                int left = aligns[c] switch
                {
                    Align.Right => padTotal,
                    Align.Center => padTotal / 2,
                    _ => 0,
                };
                int right = padTotal - left;
                string styled = headerStyle
                    ? $"[bold {Head}]{Esc(seg)}[/]"
                    : Esc(seg);
                sb.Append(new string(' ', left)).Append(styled).Append(new string(' ', right));
                // Interior separator between columns; the CLOSING border (last column) must not
                // carry a trailing space that a TrimEnd would need to eat - emit it exactly.
                sb.Append(c < colW.Length - 1 ? $" [{Border}]{Esc(v)}[/] " : $" [{Border}]{Esc(v)}[/]");
            }
            outLines.Add(sb.ToString());
        }
        return outLines;
    }

    /// <summary>
    /// Narrow-terminal fallback: one record per body row, each cell on its own line as
    /// <c>header: value</c> (values wrap under themselves), records separated by a dim rule.
    /// </summary>
    private static List<string> Stacked(List<List<string>> dataRows, int width, string indent)
    {
        var head = dataRows[0];
        int usable = Math.Max(12, width - 1 - indent.Length);
        int keyW = Math.Min(head.Max(h => TuiMarkup.Width(h)), Math.Max(4, usable / 3));
        int valW = Math.Max(6, usable - keyW - 2);
        var g = MarkdownStyle.Active.BorderGlyphs;
        string rule = indent + $"[{Border}]{Esc(string.Concat(Enumerable.Repeat(g[0], Math.Min(usable, 40))))}[/]";
        var outp = new List<string> { "" };
        var body = dataRows.Count > 1 ? dataRows.Skip(1).ToList() : new List<List<string>> { head };
        for (int r = 0; r < body.Count; r++)
        {
            outp.Add(rule);
            for (int c = 0; c < head.Count; c++)
            {
                string key = TuiMarkup.Width(head[c]) > keyW ? TuiMarkup.TruncatePlain(head[c], keyW) : head[c];
                string keyCell = $"[bold {Head}]{Esc(key)}[/]" + new string(' ', keyW - TuiMarkup.Width(key));
                var lines = TuiMarkup.WrapPlain(dataRows.Count > 1 && c < body[r].Count ? body[r][c] : "", valW);
                if (lines.Count == 0) lines.Add("");
                outp.Add(indent + keyCell + "  " + Esc(lines[0]));
                foreach (var more in lines.Skip(1)) outp.Add(indent + new string(' ', keyW + 2) + Esc(more));
            }
        }
        outp.Add(rule);
        outp.Add("");
        return outp;
    }

    private static List<Align> ParseAligns(string sepRow)
    {
        return SplitCells(sepRow).Select(c =>
        {
            string t = c.Trim();
            bool l = t.StartsWith(":");
            bool r = t.EndsWith(":");
            return l && r ? Align.Center : r ? Align.Right : Align.Left;
        }).ToList();
    }

    private static List<string> SplitCells(string row)
    {
        // Split on unescaped pipes outside code spans, then strip inline Markdown (**bold**, *italic*,
        // `code`, ~~strike~~, escapes, entities) to plain text so the column math is exact and no
        // stray markers leak into cells. Tabs become spaces (a raw tab moves the terminal cursor).
        return TuiMarkdown.SplitRow(row).Select(c => TuiMarkdown.StripInline(TuiMarkup.ExpandTabs(c).Trim())).ToList();
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");
}
