using System.Text;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// A styled run of text: the literal string plus its resolved style. Produced by
/// <see cref="TuiMarkup.Parse"/>. Pure data so layout/wrapping can be unit-tested.
/// </summary>
internal readonly record struct Span(string Text, TuiStyle Style);

/// <summary>Resolved visual style for a <see cref="Span"/>.</summary>
internal readonly record struct TuiStyle(
    (byte R, byte G, byte B)? Fg,
    bool Bold,
    bool Dim,
    bool Italic,
    bool Underline,
    (byte R, byte G, byte B)? Bg = null)
{
    public static readonly TuiStyle None = new(null, false, false, false, false, null);

    /// <summary>SGR prefix emitting this style (empty when no attributes are set).</summary>
    public string ToAnsi()
    {
        if (Fg is null && Bg is null && !Bold && !Dim && !Italic && !Underline) return "";
        var sb = new StringBuilder();
        if (Bold) sb.Append(Ansi.Bold);
        if (Dim) sb.Append(Ansi.Dim);
        if (Italic) sb.Append(Ansi.Italic);
        if (Underline) sb.Append(Ansi.Underline);
        if (Fg is { } c) sb.Append(Ansi.Fg(c.R, c.G, c.B));
        if (Bg is { } bc) sb.Append(Ansi.Bg(bc.R, bc.G, bc.B));
        return sb.ToString();
    }
}

/// <summary>
/// Parses the small Spectre-compatible markup subset the codebase emits
/// (<c>[#RRGGBB]</c>, named colors like <c>grey</c>/<c>dim</c>, attributes
/// <c>bold</c>/<c>italic</c>/<c>underline</c>, nested tags, and the closing <c>[/]</c>)
/// into a flat list of styled <see cref="Span"/>s, then renders/wraps them as ANSI.
/// <c>[[</c> and <c>]]</c> are literal brackets (Spectre escape convention). This is the
/// renderer's text pipeline; it never touches the console so it is fully unit-testable.
/// </summary>
internal static class TuiMarkup
{
    /// <summary>Parse markup into styled spans. Unknown tags are ignored (style unchanged).</summary>
    public static List<Span> Parse(string markup)
    {
        var spans = new List<Span>();
        if (string.IsNullOrEmpty(markup)) return spans;

        var stack = new Stack<TuiStyle>();
        stack.Push(TuiStyle.None);
        var buf = new StringBuilder();

        void Flush()
        {
            if (buf.Length > 0)
            {
                spans.Add(new Span(buf.ToString(), stack.Peek()));
                buf.Clear();
            }
        }

        for (int i = 0; i < markup.Length; i++)
        {
            char ch = markup[i];

            // Escaped literal brackets: [[ -> [ and ]] -> ]
            if (ch == '[' && i + 1 < markup.Length && markup[i + 1] == '[') { buf.Append('['); i++; continue; }
            if (ch == ']' && i + 1 < markup.Length && markup[i + 1] == ']') { buf.Append(']'); i++; continue; }

            if (ch == '[')
            {
                int close = markup.IndexOf(']', i + 1);
                if (close < 0) { buf.Append(ch); continue; } // dangling '[' -> literal
                string tag = markup.Substring(i + 1, close - i - 1);
                i = close;

                Flush();
                if (tag == "/")
                {
                    if (stack.Count > 1) stack.Pop();
                }
                else
                {
                    stack.Push(ResolveTag(tag, stack.Peek()));
                }
                continue;
            }

            buf.Append(ch);
        }
        Flush();
        return spans;
    }

    /// <summary>Render markup to an ANSI string (each span prefixed by its SGR, reset at end).</summary>
    public static string ToAnsi(string markup)
    {
        var spans = Parse(markup);
        var sb = new StringBuilder();
        bool any = false;
        foreach (var s in spans)
        {
            string sgr = s.Style.ToAnsi();
            if (sgr.Length > 0) { sb.Append(sgr); any = true; }
            sb.Append(s.Text);
            if (sgr.Length > 0) sb.Append(Ansi.Reset);
        }
        if (any) sb.Append(Ansi.Reset);
        return sb.ToString();
    }

    /// <summary>Strip all markup tags, returning the plain visible text.</summary>
    public static string Plain(string markup)
    {
        var sb = new StringBuilder();
        foreach (var s in Parse(markup)) sb.Append(s.Text);
        return sb.ToString();
    }

    private static TuiStyle ResolveTag(string tag, TuiStyle parent)
    {
        var style = parent;
        var toks = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool bgNext = false;
        foreach (var tokRaw in toks)
        {
            var tok = tokRaw.Trim().ToLowerInvariant();
            if (bgNext)
            {
                var bg = ResolveColor(tok);
                if (bg is { } bc) style = style with { Bg = bc };
                bgNext = false;
                continue;
            }
            switch (tok)
            {
                case "on": bgNext = true; break;
                case "bold": case "b": style = style with { Bold = true }; break;
                case "dim": style = style with { Dim = true }; break;
                case "italic": case "i": style = style with { Italic = true }; break;
                case "underline": case "u": style = style with { Underline = true }; break;
                default:
                    var col = ResolveColor(tok);
                    if (col is { } c) style = style with { Fg = c };
                    break;
            }
        }
        return style;
    }

    private static (byte, byte, byte)? ResolveColor(string token)
    {
        if (token.StartsWith('#') && (token.Length == 7))
        {
            try
            {
                byte r = Convert.ToByte(token.Substring(1, 2), 16);
                byte g = Convert.ToByte(token.Substring(3, 2), 16);
                byte b = Convert.ToByte(token.Substring(5, 2), 16);
                return (r, g, b);
            }
            catch { return null; }
        }
        return token switch
        {
            "black" => ((byte)0, (byte)0, (byte)0),
            "white" => ((byte)255, (byte)255, (byte)255),
            "red" => ((byte)215, (byte)95, (byte)95),
            "green" => ((byte)120, (byte)200, (byte)140),
            "yellow" => ((byte)212, (byte)160, (byte)84),
            "blue" => ((byte)100, (byte)180, (byte)220),
            "grey" or "gray" => ((byte)150, (byte)150, (byte)150),
            "grey23" or "gray23" => ((byte)59, (byte)59, (byte)59),
            "grey35" or "gray35" => ((byte)90, (byte)90, (byte)90),
            "grey37" or "gray37" => ((byte)94, (byte)94, (byte)94),
            _ => null
        };
    }

    // --- display width + wrapping -------------------------------------------

    /// <summary>
    /// Visible width of a single rune. Combining marks count 0, common wide ranges
    /// (CJK, fullwidth, most emoji) count 2, everything else 1. Good enough for terminal
    /// layout without pulling in a full Unicode width table.
    /// </summary>
    public static int RuneWidth(int cp)
    {
        if (cp == 0) return 0;
        // C0/C1 control: 0 (callers strip control chars; defensive here).
        if (cp < 32 || (cp >= 0x7f && cp < 0xa0)) return 0;
        // Combining marks.
        if ((cp >= 0x0300 && cp <= 0x036F) || (cp >= 0x1AB0 && cp <= 0x1AFF) ||
            (cp >= 0x1DC0 && cp <= 0x1DFF) || (cp >= 0x20D0 && cp <= 0x20FF) ||
            (cp >= 0xFE20 && cp <= 0xFE2F)) return 0;
        // Wide ranges.
        if ((cp >= 0x1100 && cp <= 0x115F) ||  // Hangul Jamo
            (cp >= 0x2E80 && cp <= 0x303E) ||  // CJK radicals, Kangxi
            (cp >= 0x3041 && cp <= 0x33FF) ||  // Hiragana..CJK symbols
            (cp >= 0x3400 && cp <= 0x4DBF) ||  // CJK Ext A
            (cp >= 0x4E00 && cp <= 0x9FFF) ||  // CJK Unified
            (cp >= 0xA000 && cp <= 0xA4CF) ||  // Yi
            (cp >= 0xAC00 && cp <= 0xD7A3) ||  // Hangul syllables
            (cp >= 0xF900 && cp <= 0xFAFF) ||  // CJK compat
            (cp >= 0xFE30 && cp <= 0xFE4F) ||  // CJK compat forms
            (cp >= 0xFF00 && cp <= 0xFF60) ||  // Fullwidth forms
            (cp >= 0xFFE0 && cp <= 0xFFE6) ||
            (cp >= 0x1F300 && cp <= 0x1FAFF) || // emoji / pictographs
            (cp >= 0x20000 && cp <= 0x3FFFD))   // CJK Ext B+
            return 2;
        return 1;
    }

    /// <summary>Visible (display) width of plain text, summing rune widths.</summary>
    public static int Width(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return 0;
        int w = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(plain);
        while (e.MoveNext())
        {
            string el = (string)e.Current;
            w += RuneWidth(char.ConvertToUtf32(el, 0));
        }
        return w;
    }

    /// <summary>Visible width of the plain text underlying a markup string.</summary>
    public static int MarkupWidth(string markup) => Width(Plain(markup));

    /// <summary>
    /// Hard-truncate plain text to a maximum display width, appending an ellipsis when
    /// truncation occurs (the ellipsis is included within <paramref name="maxWidth"/>).
    /// </summary>
    public static string TruncatePlain(string plain, int maxWidth, string ellipsis = "\u2026")
    {
        if (maxWidth <= 0) return "";
        if (Width(plain) <= maxWidth) return plain;
        int ellW = Width(ellipsis);
        int budget = Math.Max(0, maxWidth - ellW);
        var sb = new StringBuilder();
        int w = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(plain);
        while (e.MoveNext())
        {
            string el = (string)e.Current;
            int ew = RuneWidth(char.ConvertToUtf32(el, 0));
            if (w + ew > budget) break;
            sb.Append(el);
            w += ew;
        }
        sb.Append(ellipsis);
        return sb.ToString();
    }

    /// <summary>
    /// Word-wrap plain text to the given width, breaking over-long words. Existing newlines
    /// are honored. Returns at least one (possibly empty) line.
    /// </summary>
    public static List<string> WrapPlain(string plain, int width)
    {
        var outLines = new List<string>();
        if (width <= 0) { outLines.Add(plain); return outLines; }

        foreach (var rawLine in (plain ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (Width(rawLine) <= width) { outLines.Add(rawLine); continue; }

            var cur = new StringBuilder();
            int curW = 0;
            foreach (var word in SplitKeepingSpaces(rawLine))
            {
                int ww = Width(word);
                if (ww > width)
                {
                    // Flush current, then hard-break the long word.
                    if (cur.Length > 0) { outLines.Add(cur.ToString()); cur.Clear(); curW = 0; }
                    foreach (var chunk in HardBreak(word, width)) { outLines.Add(chunk); }
                    // Last chunk may continue accumulating - re-seed from it.
                    if (outLines.Count > 0)
                    {
                        cur.Append(outLines[^1]); curW = Width(outLines[^1]); outLines.RemoveAt(outLines.Count - 1);
                    }
                    continue;
                }
                if (curW + ww > width)
                {
                    outLines.Add(cur.ToString().TrimEnd());
                    cur.Clear(); curW = 0;
                    if (string.IsNullOrWhiteSpace(word)) continue; // don't lead a line with the wrapped space
                }
                cur.Append(word); curW += ww;
            }
            outLines.Add(cur.ToString().TrimEnd());
        }
        if (outLines.Count == 0) outLines.Add("");
        return outLines;
    }

    private static IEnumerable<string> SplitKeepingSpaces(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            bool space = s[i] == ' ';
            int j = i;
            while (j < s.Length && (s[j] == ' ') == space) j++;
            yield return s.Substring(i, j - i);
            i = j;
        }
    }

    private static IEnumerable<string> HardBreak(string word, int width)
    {
        var sb = new StringBuilder();
        int w = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(word);
        while (e.MoveNext())
        {
            string el = (string)e.Current;
            int ew = RuneWidth(char.ConvertToUtf32(el, 0));
            if (w + ew > width) { yield return sb.ToString(); sb.Clear(); w = 0; }
            sb.Append(el); w += ew;
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
