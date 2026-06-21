using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// A tiny, line-oriented Markdown -> Spectre-markup converter for the live TUI. Assistant
/// text is plain Markdown; the renderer pipeline (<see cref="TuiMarkup"/>) speaks a small
/// Spectre-compatible markup. This bridges the two so headings, bold/italic, inline code,
/// and list bullets render as styled terminal text instead of raw "**...**" / "# ...".
///
/// It is deliberately conservative and operates per visual line (the driver commits complete
/// lines, so block-level state never needs to span chunks). Literal Spectre brackets in the
/// source are escaped FIRST, which also fixes the prior behavior where raw assistant text
/// containing "[" was misparsed as markup. Pure string work - fully unit-testable.
/// </summary>
internal static class TuiMarkdown
{
    // Palette aligned with TuiComponents.
    private const string Head = "#64B4DC";   // headings (accent)
    private const string Code = "#D4A054";   // inline code (warn/amber)
    private const string Bullet = "#787878"; // list bullet (muted)

    private static readonly Regex BoldStar = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex BoldUnder = new(@"__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex ItalicStar = new(@"(?<![\*])\*(?!\s)(.+?)(?<!\s)\*(?![\*])", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`([^`]+?)`", RegexOptions.Compiled);
    private static readonly Regex Strike = new(@"~~(.+?)~~", RegexOptions.Compiled);

    /// <summary>Sentinels protect already-emitted markup tags from later escaping/regex.</summary>
    private const string LB = "\uE000";  // stands in for '['
    private const string RB = "\uE001";  // stands in for ']'

    /// <summary>Convert a single line of Markdown to Spectre markup understood by TuiMarkup.</summary>
    public static string ToMarkup(string line)
    {
        if (string.IsNullOrEmpty(line)) return line ?? "";

        // Detect block-level prefixes on the raw (untrimmed-for-content) line.
        string trimmed = line.TrimStart();
        int indent = line.Length - trimmed.Length;
        string pad = new string(' ', Math.Min(indent, 8));

        // ATX heading: #, ##, ### ... -> bold accent, hashes dropped.
        var h = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
        if (h.Success)
        {
            string body = Inline(h.Groups[2].Value);
            return $"{pad}[bold {Head}]{body}[/]";
        }

        // Unordered list: -, *, + -> bullet glyph.
        var ul = Regex.Match(trimmed, @"^[-*+]\s+(.*)$");
        if (ul.Success)
        {
            string body = Inline(ul.Groups[1].Value);
            return $"{pad}[{Bullet}]\u2022[/] {body}";
        }

        // Ordered list: keep the number, style the marker subtly.
        var ol = Regex.Match(trimmed, @"^(\d+)\.\s+(.*)$");
        if (ol.Success)
        {
            string body = Inline(ol.Groups[2].Value);
            return $"{pad}[{Bullet}]{ol.Groups[1].Value}.[/] {body}";
        }

        // Blockquote.
        var bq = Regex.Match(trimmed, @"^>\s?(.*)$");
        if (bq.Success)
            return $"{pad}[{Bullet}]\u2502[/] {Inline(bq.Groups[1].Value)}";

        // Thematic break: ---, ***, ___ (3+). Render a dim horizontal rule.
        if (Regex.IsMatch(trimmed, @"^([-*_])\1{2,}$"))
            return $"{pad}[{Bullet}]{new string('\u2500', 24)}[/]";

        // Table rows: lines that start and end with a pipe (GitHub-flavored). The header
        // separator (|---|:--:|) becomes a rule; data/header rows render styled cells joined
        // by a dim vertical bar. (Per-row styling only - cross-row column alignment needs the
        // whole table buffered, which the streaming line-commit model does not provide.)
        if (IsTableRow(trimmed))
        {
            // Separator row -> rule.
            if (Regex.IsMatch(trimmed, @"^\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?$"))
                return $"{pad}[{Bullet}]{new string('\u2500', 24)}[/]";
            var cells = SplitTableCells(trimmed);
            var sep = $" [{Bullet}]\u2502[/] ";
            return pad + string.Join(sep, cells.Select(c => Inline(c.Trim())));
        }

        // Plain paragraph line.
        return pad + Inline(trimmed);
    }

    /// <summary>True when a line looks like a GitHub table row (contains a pipe separator).</summary>
    private static bool IsTableRow(string s)
    {
        // Must contain at least one unescaped interior pipe and look pipe-delimited.
        if (!s.Contains('|')) return false;
        string t = s.Trim();
        // Require either leading/trailing pipe or >=2 pipes, to avoid eating prose with one "|".
        int pipes = t.Count(ch => ch == '|');
        return t.StartsWith("|") || t.EndsWith("|") || pipes >= 2;
    }

    /// <summary>Split a table row into cells on unescaped pipes, dropping leading/trailing empties.</summary>
    private static List<string> SplitTableCells(string row)
    {
        string t = row.Trim();
        if (t.StartsWith("|")) t = t[1..];
        if (t.EndsWith("|")) t = t[..^1];
        return t.Split('|').ToList();
    }

    /// <summary>Apply inline transforms (bold/italic/code) and escape stray brackets.</summary>
    private static string Inline(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        // 1) Inline code first (its content must not be further transformed). Emit with
        //    protected sentinel brackets so subsequent escaping leaves the tags intact.
        s = InlineCode.Replace(s, m => Tag("on #2A2A2A", Code, Escape(m.Groups[1].Value)));

        // 2) Bold then italic. Emit protected tags around escaped inner text.
        s = BoldStar.Replace(s, m => Tag2("bold", Escape(m.Groups[1].Value)));
        s = BoldUnder.Replace(s, m => Tag2("bold", Escape(m.Groups[1].Value)));
        s = ItalicStar.Replace(s, m => Tag2("italic", Escape(m.Groups[1].Value)));
        s = Strike.Replace(s, m => Tag2("strikethrough", Escape(m.Groups[1].Value)));

        // 3) Escape any remaining literal Spectre brackets in the plain runs, then restore
        //    the protected sentinel brackets to real markup brackets.
        s = s.Replace("[", "[[").Replace("]", "]]");
        s = s.Replace(LB, "[").Replace(RB, "]");
        return s;
    }

    /// <summary>
    /// Strip inline Markdown markers (**bold**, __bold__, *italic*, `code`, ~~strike~~) to clean
    /// PLAIN text, leaving the inner content. Used by the table renderer, whose fixed-width column
    /// math needs the true display text (otherwise stray "**" leak into cells). Does not emit any
    /// Spectre markup, so the result is safe to escape and measure with TuiMarkup.Width.
    /// </summary>
    public static string StripInline(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = InlineCode.Replace(s, m => m.Groups[1].Value);
        s = BoldStar.Replace(s, m => m.Groups[1].Value);
        s = BoldUnder.Replace(s, m => m.Groups[1].Value);
        s = ItalicStar.Replace(s, m => m.Groups[1].Value);
        s = Strike.Replace(s, m => m.Groups[1].Value);
        return s;
    }

    private static string Escape(string s) => (s ?? "").Replace("[", "[[").Replace("]", "]]");

    // Build a protected single-tag span: [open]text[/]
    private static string Tag2(string open, string inner) => $"{LB}{open}{RB}{inner}{LB}/{RB}";

    // Build a protected two-attr span (e.g. fg + bg) for inline code: [fg on bg]text[/]
    private static string Tag(string bg, string fg, string inner) => $"{LB}{fg} {bg}{RB}{inner}{LB}/{RB}";
}
