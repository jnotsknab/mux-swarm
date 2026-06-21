using System.Linq;
using System.Text.RegularExpressions;

namespace MuxSwarm.Utils.Tui;

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
    private const string Border = "#3A3A3A";  // box-drawing lines (dim)
    private const string Head = "#64B4DC";    // header cell text (accent)

    private enum Align { Left, Center, Right }

    /// <summary>True when a line looks like a GFM table row (pipe-delimited).</summary>
    public static bool IsTableRow(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.Contains('|')) return false;
        string t = s.Trim();
        int pipes = t.Count(ch => ch == '|');
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
    public static List<string> Render(IReadOnlyList<string> rawRows, int width)
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

        // Budget: indent(2) + borders. Each column costs colW + 3 ("| " + " "), plus final "|".
        int pad = 2;
        int chrome = 1 + cols * 3;                 // leading "|" handled per-column as "│ "
        int avail = Math.Max(8, width) - pad - chrome;
        int natural = colW.Sum();
        if (natural > avail && natural > 0)
        {
            // Proportionally shrink columns that are wider than a soft floor.
            double scale = (double)avail / natural;
            for (int c = 0; c < cols; c++)
                colW[c] = Math.Max(3, (int)Math.Floor(colW[c] * scale));
        }

        string indent = new string(' ', pad);
        var outp = new List<string>();

        string topB = indent + $"[{Border}]\u256d" + string.Join("\u252c", colW.Select(w => new string('\u2500', w + 2))) + "\u256e[/]";
        string midB = indent + $"[{Border}]\u251c" + string.Join("\u253c", colW.Select(w => new string('\u2500', w + 2))) + "\u2524[/]";
        string botB = indent + $"[{Border}]\u2570" + string.Join("\u2534", colW.Select(w => new string('\u2500', w + 2))) + "\u256f[/]";

        outp.Add("");
        outp.Add(topB);
        // Header row (first data row) styled in accent.
        outp.Add(RenderRow(dataRows[0], colW, aligns, indent, headerStyle: true));
        outp.Add(midB);
        for (int i = 1; i < dataRows.Count; i++)
            outp.Add(RenderRow(dataRows[i], colW, aligns, indent, headerStyle: false));
        outp.Add(botB);
        outp.Add("");
        return outp;
    }

    private static string RenderRow(List<string> cells, int[] colW, List<Align> aligns, string indent, bool headerStyle)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(indent).Append($"[{Border}]\u2502[/] ");
        for (int c = 0; c < colW.Length; c++)
        {
            string plain = c < cells.Count ? cells[c] : "";
            // Truncate plain content to the column width, then re-style.
            if (TuiMarkup.Width(plain) > colW[c])
                plain = TuiMarkup.TruncatePlain(plain, colW[c]);
            int textW = TuiMarkup.Width(plain);
            int padTotal = Math.Max(0, colW[c] - textW);
            int left = aligns[c] switch
            {
                Align.Right => padTotal,
                Align.Center => padTotal / 2,
                _ => 0,
            };
            int right = padTotal - left;
            string styled = headerStyle
                ? $"[bold {Head}]{Esc(plain)}[/]"
                : Esc(plain);
            sb.Append(new string(' ', left)).Append(styled).Append(new string(' ', right));
            sb.Append($" [{Border}]\u2502[/] ");
        }
        return sb.ToString().TrimEnd();
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
        string t = row.Trim();
        if (t.StartsWith("|")) t = t[1..];
        if (t.EndsWith("|")) t = t[..^1];
        // Strip inline Markdown (**bold**, *italic*, `code`, ~~strike~~) to clean plain text so the
        // fixed-width column math is correct and no stray markers (e.g. "**") leak into cells.
        return t.Split('|').Select(c => TuiMarkdown.StripInline(c.Trim())).ToList();
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");
}
