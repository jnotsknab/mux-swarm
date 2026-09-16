using System.Text.RegularExpressions;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Regression coverage for committed agent-prose wrapping (v0.14.1 edge-alignment fix). The
/// streamed answer path renders markdown -&gt; markup -&gt; <see cref="LiveRegion.WrapMarkupLine"/>,
/// and dogfooding showed three edge defects in that wrap: continuation rows of a markdown bullet
/// fell back to column 0, a row could open with a stray leading space when the break landed on a
/// styled-span boundary, and rows broke many columns short of the right margin because each span
/// was pre-wrapped in isolation. These lock the fixed behaviour.
/// </summary>
public class WrapAlignmentTests
{
    /// <summary>Visible text of a rendered row (ANSI stripped), as the terminal would show it.</summary>
    private static string Vis(string row) => Regex.Replace(row, "\u001b\\[[0-9;]*m", "");

    private static List<string> Wrap(string markup, int cols) =>
        LiveRegion.WrapMarkupLine(markup, cols).Select(Vis).ToList();

    [Theory]
    [InlineData("- Header marks the delegation-wait closeout and then some more text here", 2)]
    [InlineData("- Build section records `prod = 771db47` and the rest of a long sentence", 2)]
    [InlineData("1. Next steps tightened: dogfood then push slash PR with full scope", 3)]
    [InlineData("  - Nested bullet body text that is long enough to require wrapping here", 4)]
    public void MarkdownListContinuationRows_AlignUnderTheMarkerText(string md, int hang)
    {
        // A list marker puts its body at a deeper column; every wrapped continuation row must
        // re-apply that column. Before the fix ComputeHangIndent only knew the streamed lead dot
        // (U+25CF), so markdown bullets (U+2022) and ordered "N." markers wrapped back to col 0.
        var rows = Wrap(TuiMarkdown.ToMarkup(md), 40);
        Assert.True(rows.Count >= 2, "input must actually wrap at this width");
        foreach (var r in rows.Skip(1))
        {
            Assert.StartsWith(new string(' ', hang), r);
            Assert.NotEqual(' ', r[hang]);   // exactly the hang indent, not one column more
        }
    }

    [Fact]
    public void WrappedRow_NeverOpensWithStraySpace_WhenBreakLandsOnSpanBoundary()
    {
        // The space between two styled spans must be dropped when the row breaks there, otherwise
        // the continuation starts one column right of the hang indent (the visible +1 stagger).
        const string markup = "  \u25cf [#C8C8C8]each of those would have fired[/] [#64B4DC]signal_task_complete[/] now";
        var rows = Wrap(markup, 40);
        Assert.True(rows.Count >= 2);
        foreach (var r in rows.Skip(1))
        {
            Assert.StartsWith("    ", r);          // 4-col hang (dot at col 2 => text at col 4)
            Assert.NotEqual(' ', r[4]);            // and nothing extra beyond it
        }
        Assert.All(rows, r => Assert.Equal(r.TrimEnd(), r));   // no trailing space overhang either
    }

    [Fact]
    public void WrappedRows_FillTowardTheRightMargin_AcrossStyledSpans()
    {
        // Each span used to be pre-wrapped to a fixed budget and then re-checked during assembly,
        // so a span starting mid-row could neither be re-split nor fit and the row broke early -
        // the ragged right edge. Wrapping is now column-aware: no row may break so short that the
        // first word of the NEXT row would have fit on it.
        const int cols = 40;
        const string markup = "  \u25cf alpha beta [#64B4DC]771db47[/] gamma delta epsilon zeta eta theta iota";
        var rows = Wrap(markup, cols);
        Assert.True(rows.Count >= 2);
        for (int i = 0; i < rows.Count - 1; i++)
        {
            string next = rows[i + 1].TrimStart();
            int firstWordW = TuiMarkup.Width(next.Split(' ')[0]);
            Assert.True(TuiMarkup.Width(rows[i]) + 1 + firstWordW > cols,
                $"row {i} broke early: '{rows[i]}' (w={TuiMarkup.Width(rows[i])}) could have taken '{next.Split(' ')[0]}'");
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void EveryWrappedRow_StaysWithinTheWidth(int cols)
    {
        // Overflow past the margin makes the terminal soft-wrap the spill back to col 0, which is
        // how a stray fragment ends up orphaned at the left edge.
        var inputs = new[]
        {
            TuiMarkdown.ToMarkup("- Build/deployment section (updated last turn) records `prod = 771db47` and more"),
            TuiMarkdown.ToMarkup("1. Next steps tightened: dogfood then push/PR with the full release-notes scope"),
            "  \u25cf alpha beta [#64B4DC]771db47[/] gamma delta epsilon zeta eta theta iota kappa",
            "    plain stream-indented continuation prose that runs on well past any margin here",
        };
        foreach (var markup in inputs)
            foreach (var r in Wrap(markup, cols))
                Assert.True(TuiMarkup.Width(r) <= cols, $"row exceeds {cols}: '{r}' (w={TuiMarkup.Width(r)})");
    }

    [Fact]
    public void LongUnbrokenToken_HardBreaksAndFillsRemainingColumns()
    {
        // A path/hash/URL longer than a row must fill the columns left on the current row rather
        // than starting a fresh one, and no row may exceed the width.
        const int cols = 30;
        var rows = Wrap("  \u25cf see " + new string('x', 70), cols);
        Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= cols));
        Assert.Equal(70, rows.Sum(r => r.Count(ch => ch == 'x')));
        // The token starts on the SAME row as the lead-in text (remaining columns are used).
        Assert.Contains('x', rows[0]);
    }

    [Fact]
    public void PlainProse_WithoutMarker_KeepsContinuationFlushLeft()
    {
        // Unindented prose has no hang: continuations must stay at col 0 (no invented indent).
        var rows = Wrap("alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu", 30);
        Assert.True(rows.Count >= 2);
        Assert.All(rows, r => Assert.NotEqual(' ', r[0]));
    }

    [Fact]
    public void WideGlyphs_AreNeverSplitMidCluster()
    {
        // Cluster-accurate fill: a wide CJK glyph must not be cut in half by the remaining-columns
        // split, and the visible text must survive the wrap intact.
        const string body = "\u6f22\u5b57\u6f22\u5b57\u6f22\u5b57\u6f22\u5b57\u6f22\u5b57\u6f22\u5b57\u6f22\u5b57\u6f22\u5b57";
        var rows = Wrap("  \u25cf " + body, 12);
        Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= 12));
        Assert.Equal(body, string.Concat(rows.Select(r => r.Replace(" ", "").Replace("\u25cf", ""))));
    }
}
