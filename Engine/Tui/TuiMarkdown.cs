using System.Text.RegularExpressions;

namespace MuxSwarm.Engine.Tui;

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
    // Palette resolves from the ACTIVE theme (Theme.cs) so /theme recolors rendered markdown too.
    // Code BACKGROUNDS stay fixed dark shades (legible across themes); foregrounds are themed.
    private static string Head => Theme.Active.MdHeading;   // headings
    private static string Code => Theme.Active.MdCode;      // inline code
    private static string Bullet => Theme.Active.Muted;     // list bullet
    private static string Link => Theme.Active.MdLink;      // links (underlined)
    private static string CodeBg => TuiComponents.ContentBackgrounds ? Theme.Active.CodeBg : "default";     // fenced code bg (themed; gated by console.contentBackgrounds)
    private static string DiffAdd => Theme.Active.Success;  // diff added line
    private static string DiffDel => Theme.Active.Error;    // diff removed line
    private static string DiffHunk => Theme.Active.Accent;  // diff hunk header @@
    private static string DiffMeta => Theme.Active.Muted;   // diff file/meta headers
    private const string DiffCtx = "#A0A0A0";               // diff context line (fixed neutral grey)

    private static readonly Regex BoldItalicStar = new(@"\*\*\*(.+?)\*\*\*", RegexOptions.Compiled);
    private static readonly Regex BoldStar = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex BoldUnder = new(@"__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex ItalicStar = new(@"(?<![\*])\*(?!\s)(.+?)(?<!\s)\*(?![\*])", RegexOptions.Compiled);
    // Code span: a run of N backticks closed by exactly N (CommonMark), so ``a`b`` keeps its inner tick.
    private static readonly Regex InlineCode = new(@"(?<!`)(`+)(?!`)(.+?)(?<!`)\1(?!`)", RegexOptions.Compiled);
    // Backslash escape of any ASCII punctuation (CommonMark): the backslash is dropped, the char kept literal.
    private static readonly Regex Escaped = new(@"\\([!-/:-@\[-`{-~])", RegexOptions.Compiled);
    // Placeholder for a code span or escaped char while emphasis runs: \uE004 index \uE005.
    private static readonly Regex Slot = new("\uE004(\\d+)\uE005", RegexOptions.Compiled);
    private static readonly Regex Strike = new(@"~~(.+?)~~", RegexOptions.Compiled);
    // [text](url) and ![alt](url). Optional "title" after the url is tolerated and dropped.
    // The URL may hold one level of balanced parentheses (wiki-style "Foo_(bar)").
    private static readonly Regex MdLink = new(@"(!?)\[([^\]]*)\]\(((?:[^()\s]|\([^()\s]*\))+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);
    private static readonly Regex AutoLink = new(@"<(https?://[^>\s]+)>", RegexOptions.Compiled);
    // Fenced code delimiter: ``` or ~~~ (3+), optional info string (language).
    private static readonly Regex FenceRe = new(@"^\s*(`{3,}|~{3,})\s*([A-Za-z0-9_+\-.#]*)[^`]*$", RegexOptions.Compiled);

    /// <summary>Sentinels protect already-emitted markup tags from later escaping/regex.</summary>
    private const string LB = "\uE000";  // stands in for '['
    private const string RB = "\uE001";  // stands in for ']'
    // Span content brackets: protected from the final plain-run escape, restored as "[["/"]]" so
    // every literal bracket is escaped exactly once (Escape() + the plain pass doubled them).
    private const string ELB = "\uE002"; // stands in for an escaped '[' ("[[")
    private const string ERB = "\uE003"; // stands in for an escaped ']' ("]]")

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

        // Task list: - [x] / - [ ] -> checkbox glyph (checked rendered before the plain UL rule,
        // otherwise the "- " prefix is consumed and the "[x]" leaks through as literal text).
        var task = Regex.Match(trimmed, @"^[-*+]\s+\[([ xX])\]\s+(.*)$");
        if (task.Success)
        {
            bool done = task.Groups[1].Value is "x" or "X";
            string glyph = done ? $"[{Head}]\u2713[/]" : $"[{Bullet}]\u2610[/]";
            string body = Inline(task.Groups[2].Value);
            return $"{pad}{glyph} {body}";
        }

        // Unordered list: -, *, + -> bullet glyph.
        var ul = Regex.Match(trimmed, @"^[-*+]\s+(.*)$");
        if (ul.Success)
        {
            string body = Inline(ul.Groups[1].Value);
            return $"{pad}[{Bullet}]{Escape(MarkdownStyle.Active.Bullet)}[/] {body}";
        }

        // Ordered list: keep the number, style the marker subtly.
        var ol = Regex.Match(trimmed, @"^(\d+)\.\s+(.*)$");
        if (ol.Success)
        {
            string body = Inline(ol.Groups[2].Value);
            return $"{pad}[{Bullet}]{ol.Groups[1].Value}.[/] {body}";
        }

        // Blockquote.
        var bq = Regex.Match(trimmed, @"^((?:>\s?)+)(.*)$");
        if (bq.Success)
        {
            int depth = bq.Groups[1].Value.Count(c => c == '>');
            return pad + string.Concat(Enumerable.Repeat($"[{Bullet}]\u2502[/] ", depth)) + Inline(bq.Groups[2].Value);
        }

        // Thematic break: ---, ***, ___ (3+). Render a dim horizontal rule.
        if (Regex.IsMatch(trimmed, @"^([-*_])\1{2,}$"))
            return $"{pad}[{Bullet}]{Escape(string.Concat(Enumerable.Repeat(MarkdownStyle.Active.Rule, 24)))}[/]";

        // Table rows: lines that start and end with a pipe (GitHub-flavored). The header
        // separator (|---|:--:|) becomes a rule; data/header rows render styled cells joined
        // by a dim vertical bar. (Per-row styling only - cross-row column alignment needs the
        // whole table buffered, which the streaming line-commit model does not provide.)
        if (IsTableRow(trimmed))
        {
            // Separator row -> rule.
            if (Regex.IsMatch(trimmed, @"^\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?$"))
                return $"{pad}[{Bullet}]{Escape(string.Concat(Enumerable.Repeat(MarkdownStyle.Active.Rule, 24)))}[/]";
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
        int pipes = SplitRow(t).Count - 1;
        return t.StartsWith("|") || t.EndsWith("|") || pipes >= 2;
    }

    /// <summary>Split a table row into cells on unescaped pipes, dropping leading/trailing empties.</summary>
    private static List<string> SplitTableCells(string row) => SplitRow(row);

    /// <summary>
    /// Split a GFM table row into raw cells. A pipe escaped as <c>\|</c> or inside a closed code span is
    /// cell text, not a separator (the escape is removed); the outer pipes are optional.
    /// </summary>
    internal static List<string> SplitRow(string row)
    {
        string t = (row ?? "").Trim();
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        for (int i = 0; i < t.Length; i++)
        {
            char ch = t[i];
            if (ch == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cur.Append('|'); i++; continue; }
            if (ch == '`')
            {
                int run = 1;
                while (i + run < t.Length && t[i + run] == '`') run++;
                int close = t.IndexOf(new string('`', run), i + run, StringComparison.Ordinal);
                if (close > 0)
                {
                    // GFM: "\|" inside a code span in a table cell is still a literal pipe.
                    cur.Append(t.Substring(i, close + run - i).Replace("\\|", "|"));
                    i = close + run - 1;
                    continue;
                }
                cur.Append(t, i, run);
                i += run - 1;
                continue;
            }
            if (ch == '|') { cells.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        cells.Add(cur.ToString());
        if (t.StartsWith('|')) cells.RemoveAt(0);
        if (t.EndsWith('|') && !t.EndsWith("\\|") && cells.Count > 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    /// <summary>Apply inline transforms (bold/italic/code) and escape stray brackets.</summary>
    private static string Inline(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        // 0) Strip sentinel characters from model text so they can never forge markup brackets.
        s = StripSentinels(s);

        // 1) Code spans and backslash escapes become slots first: their content is literal, so no
        //    emphasis, link or entity rule may touch it. Slots are filled in after the emphasis passes.
        var slots = new List<string>();
        string Hold(string markup) { slots.Add(markup); return $"\uE004{slots.Count - 1}\uE005"; }
        s = InlineCode.Replace(s, m => Hold(Tag($"on {(TuiComponents.ContentBackgrounds ? Theme.Active.InlineCodeBg : "default")}", Code, Protect(CodeText(m.Groups[2].Value)))));
        s = Escaped.Replace(s, m => Hold(Protect(m.Groups[1].Value)));
        s = System.Net.WebUtility.HtmlDecode(s);

        // 1b) Links/images [text](url) -> styled label (the URL is dropped in the terminal; an
        //     image ![alt](url) renders its alt text). Emitted with protected tags so the later
        //     bracket-escape pass leaves them intact instead of showing raw "[text](url)".
        s = MdLink.Replace(s, m =>
        {
            string label = m.Groups[2].Value;
            if (string.IsNullOrEmpty(label)) label = m.Groups[3].Value; // bare-URL / empty alt
            bool isImage = m.Groups[1].Value == "!";
            string inner = Protect(label);
            return isImage
                ? Tag2("italic", inner)                                  // image alt -> italic
                : $"{LB}{Link} underline{RB}{inner}{LB}/{RB}";           // link -> accent underline
        });

        // 1c) Autolinks <https://...> -> styled URL, angle brackets dropped.
        s = AutoLink.Replace(s, m => $"{LB}{Link} underline{RB}{Protect(m.Groups[1].Value)}{LB}/{RB}");

        // 2) Bold-italic (***text***) first - without it the bold pass eats two of the three
        //    stars and the italic marker leaks as a literal. Then bold, italic, strike.
        s = BoldItalicStar.Replace(s, m => Tag2("bold italic", Protect(m.Groups[1].Value)));
        s = BoldStar.Replace(s, m => Tag2("bold", Protect(m.Groups[1].Value)));
        s = BoldUnder.Replace(s, m => Tag2("bold", Protect(m.Groups[1].Value)));
        s = ItalicStar.Replace(s, m => Tag2("italic", Protect(m.Groups[1].Value)));
        s = Strike.Replace(s, m => Tag2("strikethrough", Protect(m.Groups[1].Value)));

        // 3) Escape any remaining literal Spectre brackets in the plain runs, then restore
        //    the protected sentinel brackets to real markup brackets.
        s = s.Replace("[", "[[").Replace("]", "]]");
        s = Slot.Replace(s, m => slots[int.Parse(m.Groups[1].Value)]);
        s = s.Replace(LB, "[").Replace(RB, "]").Replace(ELB, "[[").Replace(ERB, "]]");
        return s;
    }

    // CommonMark code span content: one leading and trailing space are stripped when both are present.
    private static string CodeText(string s) => s.Length >= 2 && s[0] == ' ' && s[^1] == ' ' && s.Trim().Length > 0 ? s[1..^1] : s;

    private static string StripSentinels(string s)
    {
        foreach (char c in s)
            if (c is >= '\uE000' and <= '\uE005')
                return new string(s.Where(ch => ch is < '\uE000' or > '\uE005').ToArray());
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
        s = StripSentinels(s);
        var slots = new List<string>();
        string Hold(string text) { slots.Add(text); return $"\uE004{slots.Count - 1}\uE005"; }
        s = InlineCode.Replace(s, m => Hold(CodeText(m.Groups[2].Value)));
        s = Escaped.Replace(s, m => Hold(m.Groups[1].Value));
        s = System.Net.WebUtility.HtmlDecode(s);
        s = MdLink.Replace(s, m => string.IsNullOrEmpty(m.Groups[2].Value) ? m.Groups[3].Value : m.Groups[2].Value);
        s = AutoLink.Replace(s, m => m.Groups[1].Value);
        s = BoldItalicStar.Replace(s, m => m.Groups[1].Value);
        s = BoldStar.Replace(s, m => m.Groups[1].Value);
        s = BoldUnder.Replace(s, m => m.Groups[1].Value);
        s = ItalicStar.Replace(s, m => m.Groups[1].Value);
        s = Strike.Replace(s, m => m.Groups[1].Value);
        return Slot.Replace(s, m => slots[int.Parse(m.Groups[1].Value)]);
    }

    private static string Escape(string s) => (s ?? "").Replace("[", "[[").Replace("]", "]]");

    // Escape span content for Inline(): brackets become sentinels the final pass restores as "[["/"]]".
    private static string Protect(string s) => (s ?? "").Replace("[", ELB).Replace("]", ERB);

    // Build a protected single-tag span: [open]text[/]
    private static string Tag2(string open, string inner) => $"{LB}{open}{RB}{inner}{LB}/{RB}";

    // Build a protected two-attr span (e.g. fg + bg) for inline code: [fg on bg]text[/]
    private static string Tag(string bg, string fg, string inner) => $"{LB}{fg} {bg}{RB}{inner}{LB}/{RB}";

    /// <summary>
    /// True when a line is a fenced-code delimiter (``` or ~~~, 3+). The caller buffers the
    /// lines between an opening and closing fence and renders them verbatim via <see cref="CodeLine"/>
    /// instead of through <see cref="ToMarkup"/> (which would mangle code as Markdown).
    /// </summary>
    public static bool IsFence(string line) => line is not null && FenceRe.IsMatch(line);

    /// <summary>
    /// True when <paramref name="line"/> closes a block opened by <paramref name="opener"/>: the same
    /// fence character, at least as many of them, and no info string (CommonMark).
    /// </summary>
    public static bool ClosesFence(string line, string opener)
    {
        var m = FenceRe.Match(line ?? "");
        var o = FenceRe.Match(opener ?? "");
        if (!m.Success || !o.Success) return false;
        string run = m.Groups[1].Value, open = o.Groups[1].Value;
        return run[0] == open[0] && run.Length >= open.Length && line!.Trim() == run;
    }

    /// <summary>Info string (language) of a fence line, or "" if none / not a fence.</summary>
    public static string FenceInfo(string line)
    {
        var m = FenceRe.Match(line ?? "");
        return m.Success ? m.Groups[2].Value : "";
    }

    /// <summary>
    /// Render one verbatim code line: monospace content on a dim background, indented two spaces,
    /// with NO Markdown inline transforms (code is literal). Spectre brackets are escaped so code
    /// containing "[" / "]" is shown as-is. Trailing pad keeps the background bar visually contiguous.
    /// </summary>
    public static string CodeLine(string raw)
    {
        string body = Escape(TuiMarkup.ExpandTabs(raw ?? ""));
        return $"  [{Code} on {CodeBg}] {body} [/]";
    }

    /// <summary>
    /// True when a buffered fenced block should render as a colored diff rather than a plain code
    /// band. Triggers on an explicit "diff"/"patch" info string, on canonical unified-diff markers
    /// (@@ hunk header, "diff --git", "--- a/", "+++ b/", "index <sha>"), or on a strong density of
    /// +/- prefixed lines (handles agent-emitted diffs that lack a real @@ header). Conservative on
    /// the density path so ordinary code with a stray +/- is not miscolored.
    /// </summary>
    public static bool LooksLikeDiff(IReadOnlyList<string> lines, string? lang)
    {
        if (!string.IsNullOrEmpty(lang) &&
            (lang.Equals("diff", StringComparison.OrdinalIgnoreCase) ||
             lang.Equals("patch", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (lines is null || lines.Count == 0) return false;

        int nonEmpty = 0, pm = 0;
        foreach (var raw in lines)
        {
            string l = raw ?? "";
            if (l.Length == 0) continue;
            nonEmpty++;
            if (l.StartsWith("@@ ") || l.StartsWith("diff --git ") ||
                l.StartsWith("--- a/") || l.StartsWith("+++ b/") ||
                Regex.IsMatch(l, @"^index [0-9a-f]{7,}"))
                return true;
            char c = l[0];
            // Count +/- prefixed lines, excluding the ++/-- file-marker doublets.
            if ((c == '+' && !l.StartsWith("++")) || (c == '-' && !l.StartsWith("--")))
                pm++;
        }
        // Density fallback: several +/- lines that make up a meaningful share of the block.
        return nonEmpty >= 3 && pm >= 3 && pm * 100 >= nonEmpty * 35;
    }

    /// <summary>
    /// Render one verbatim diff line on the dim code background, colored by its leading marker:
    /// additions green, deletions red, @@ hunk headers accent, file/meta headers muted, and
    /// context lines neutral grey. No Markdown inline transforms (diffs are literal); Spectre
    /// brackets are escaped so payloads containing "[" / "]" render as-is.
    /// </summary>
    public static string DiffLine(string raw)
    {
        string s = raw ?? "";
        string body = Escape(TuiMarkup.ExpandTabs(s));
        string fg;
        if (s.StartsWith("@@"))
            fg = DiffHunk;
        else if (s.StartsWith("+++") || s.StartsWith("---") ||
                 s.StartsWith("diff ") || s.StartsWith("index "))
            fg = DiffMeta;
        else if (s.StartsWith("+"))
            fg = DiffAdd;
        else if (s.StartsWith("-"))
            fg = DiffDel;
        else
            fg = DiffCtx;
        return $"  [{fg} on {CodeBg}] {body} [/]";
    }
}
