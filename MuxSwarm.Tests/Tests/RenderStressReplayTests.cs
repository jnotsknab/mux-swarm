using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Render stress replay: the exact markdown a model produced in the 2026-10-16 renderer stress run
/// (RenderCorpus/*.md), streamed through the real driver in BOTH engines at several widths. Each
/// case pins a defect seen on screen: split escaped pipes, emphasis inside code, raw tabs, glyph
/// widths that disagree with the terminal, grids wrapped past the terminal edge, dangling separators.
/// </summary>
[Collection("ConsoleState")]
public class RenderStressReplayTests
{
    private sealed class Term : ITuiTerminal
    {
        public int Width { get; set; } = 100;
        public int Height { get; set; } = 400;
        public void Write(string text) { }
        public void Flush() { }
    }

    private const BindingFlags PI = BindingFlags.Instance | BindingFlags.NonPublic;

    private static string Corpus(string name, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "RenderCorpus", name + ".md")).Replace("\r\n", "\n");

    // Border glyphs of the active markdown style: [2] top-left, [8] bottom-left, [1] vertical.
    private static string[] G => MarkdownStyle.Active.BorderGlyphs;

    private static List<string> Render(string md, int width, bool frame)
    {
        var term = new Term { Width = width };
        var d = new TuiDriver(term, frameEngine: frame);
        d.BeginStream();
        d.StreamChunk(md.EndsWith('\n') ? md : md + "\n");
        d.EndStream();
        if (frame)
        {
            var entries = ((IEnumerable)typeof(TuiDriver).GetField("_transcript", PI)!.GetValue(d)!).Cast<object>();
            var render = typeof(TuiDriver).GetMethod("RenderEntryRows", PI)!;
            return entries.SelectMany(e => (List<string>)render.Invoke(d, new[] { e, (object)d.Width })!)
                .Select(TuiMarkup.StripAnsi).ToList();
        }
        return ((List<string>)typeof(TuiDriver).GetMethod("BuildReflowWindow", PI)!.Invoke(d, null)!)
            .Select(TuiMarkup.StripAnsi).ToList();
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var name in new[] { "t01_escapes", "t02_wide", "t03_status", "t04_unicode", "t05_padding", "t06_widetable", "t07_longcells", "t08_ragged", "t09_blocks", "t10_code", "t11_mixed",
            "u01_cells_md", "u02_align", "u03_narrow_long", "u04_lists_code", "u07_diff_json", "u08_unicode_prose", "u09_table_edge" })
            foreach (int w in new[] { 130, 90, 60, 40 })
                foreach (bool frame in new[] { false, true })
                    yield return new object[] { name, w, frame };
    }

    // Every grid row of a table is one line with the same width as its top border: no row is wider
    // (a glyph measured narrower than drawn) or wrapped onto the next line (grid wider than the screen).
    [Theory, MemberData(nameof(Cases))]
    public void EveryCase_FitsTheWidth_AndGridRowsShareTheBorderWidth(string name, int width, bool frame)
    {
        var rows = Render(Corpus(name), width, frame);
        Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= width - 1, $"{name}@{width}: row wider than the screen: '{r}'"));
        Assert.DoesNotContain(rows, r => r.Contains('\t'));
        Assert.DoesNotContain(rows, r => r.Any(c => c is >= '\uE000' and <= '\uE005'));
        int i = 0;
        while ((i = rows.FindIndex(i, r => r.TrimStart().StartsWith(G[2]))) >= 0)
        {
            int end = rows.FindIndex(i, r => r.TrimStart().StartsWith(G[8]));
            Assert.True(end > i, $"{name}@{width}: table without a bottom border");
            int w = TuiMarkup.Width(rows[i].TrimEnd());
            for (int k = i + 1; k <= end; k++)
            {
                string r = rows[k].TrimEnd();
                Assert.True(TuiMarkup.Width(r) == w, $"{name}@{width} frame={frame}: grid row {k - i} is {TuiMarkup.Width(r)} wide, border {w}: '{r}'");
                string first = r.TrimStart()[..1];
                Assert.True(first == G[1] || first == G[5] || first == G[8], $"{name}@{width}: wrapped grid fragment: '{r}'");
            }
            i = end + 1;
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void EscapedPipes_AndPipesInCode_StayInTheirCell(bool frame)
    {
        var rows = Render(Corpus("t01_escapes"), 130, frame);
        string header = rows.First(r => r.Contains("Expression"));
        Assert.Equal(4, header.Split(G[1]).Length - 1);   // 3 columns, not a phantom 4th
        Assert.Contains(rows, r => r.Contains("a | b") && r.Contains("pipe inside code"));
        Assert.Contains(rows, r => r.Contains("a | b") && r.Contains("escaped pipe in text"));
        Assert.Contains(rows, r => r.Contains("*not italic*"));
        Assert.Contains(rows, r => r.Contains("\\escaped\\") && !r.Contains("\\\\escaped"));
        Assert.Contains(rows, r => r.Contains("[brackets]"));
        Assert.Contains(rows, r => r.Contains("[hidden]"));
        Assert.Contains(rows, r => r.Contains("[red]x[/]"));
        Assert.Contains(rows, r => r.Contains("<tag> and &"));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void CodeSpans_AreLiteral_AndNestedQuotesNest(bool frame)
    {
        var rows = Render(Corpus("t09_blocks"), 130, frame);
        string inline = rows.First(r => r.Contains("Inline:"));
        Assert.Contains("**not bold**", inline);
        Assert.Contains("[x]", inline);
        Assert.Contains("a|b", inline);
        Assert.Contains(rows, r => r.Contains("\u2502 \u2502 nested quote"));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Tabs_Expand_AndLongerFenceIsNotClosedByShorter(bool frame)
    {
        var rows = Render(Corpus("t10_code"), 130, frame);
        Assert.Contains(rows, r => r.Contains("```nested fence```"));
        Assert.Contains(rows, r => r.Contains("    tab-indented line"));
        var pad = Render(Corpus("t05_padding"), 130, frame);
        Assert.Contains(pad, r => r.Contains("tabs") && r.Contains("inside") && !r.Contains('\t'));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void TooWideTable_StacksRecords_InsteadOfWrappingTheGrid(bool frame)
    {
        var rows = Render(Corpus("t06_widetable"), 90, frame);
        Assert.DoesNotContain(rows, r => r.Contains(G[2]));
        Assert.Contains(rows, r => r.TrimStart().StartsWith("c03") && r.Contains("charlie"));
        Assert.Contains(rows, r => r.TrimStart().StartsWith("c14") && r.Contains("november"));
        // Wide enough: still a grid.
        Assert.Contains(Render(Corpus("t06_widetable"), 140, frame), r => r.Contains(G[2]));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void HeaderOnlyTable_HasNoDanglingSeparator(bool frame)
    {
        var rows = Render(Corpus("t11_mixed"), 130, frame);
        int top = rows.FindLastIndex(r => r.Contains("Header only"));
        Assert.StartsWith(G[8], rows[top + 1].TrimStart());
    }

    [Theory, InlineData(false), InlineData(true)]
    public void TablesAndCode_ShareTheProseTextMargin(bool frame)
    {
        var rows = Render(Corpus("t11_mixed"), 130, frame);
        int prose = rows.First(r => r.Contains("After the table")).IndexOf("After", StringComparison.Ordinal);
        int table = rows.First(r => r.Contains(G[2])).IndexOf(G[2], StringComparison.Ordinal);
        Assert.Equal(prose, table);
        var code = Render(Corpus("t10_code"), 130, frame);
        int text = code.First(r => r.Contains("Text right after")).IndexOf("Text", StringComparison.Ordinal);
        // Code keeps its one-cell band padding, so its text sits one column right of the margin.
        Assert.Equal(text + 1, code.First(r => r.Contains("def f(x):")).IndexOf("def", StringComparison.Ordinal));
    }

    // Widths measured in Windows Terminal with cursor-position reports (DSR) on 2026-10-16.
    [Theory]
    [InlineData("\u26A0", 1)] [InlineData("\u26A0\uFE0F", 2)] [InlineData("\u2610", 1)] [InlineData("\u2611", 1)]
    [InlineData("\u2600", 1)] [InlineData("\u2605", 1)] [InlineData("\u2665", 1)] [InlineData("\u2699", 1)]
    [InlineData("\u27A1", 1)] [InlineData("\u2318", 1)] [InlineData("\u2764", 1)] [InlineData("\u2764\uFE0F", 2)]
    [InlineData("\u2705", 2)] [InlineData("\u274C", 2)] [InlineData("\u2753", 2)] [InlineData("\u2B50", 2)]
    [InlineData("\u26A1", 2)] [InlineData("\u23F0", 2)] [InlineData("\u231A", 2)] [InlineData("\u2728", 2)]
    [InlineData("\u2713", 1)] [InlineData("\u25B6", 1)] [InlineData("\u25B6\uFE0F", 2)] [InlineData("\u4F60", 2)]
    [InlineData("\uFF21", 2)] [InlineData("\uFF71", 1)] [InlineData("e\u0301", 1)] [InlineData("\U0001F1E9\U0001F1EA", 2)]
    [InlineData("\U0001F44D\U0001F3FF", 2)] [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467", 2)] [InlineData("\U0001F6C8", 1)]
    [InlineData("\u0E44", 1)] [InlineData("\uAC00", 2)] [InlineData("\u3042", 2)]
    public void GlyphWidths_MatchTheTerminal(string s, int cells) => Assert.Equal(cells, TuiMarkup.Width(s));

    [Theory, InlineData(false), InlineData(true)]
    public void LinkWithParensInUrl_KeepsItsLabel(bool frame)
    {
        var rows = Render(Corpus("u01_cells_md"), 130, frame);
        Assert.Contains(rows, r => r.Contains("docs") && r.Contains("paren url") && !r.Contains("docs)"));
        Assert.Contains(rows, r => r.Contains("x[0] | y"));
    }

    [Fact]
    public void Strikethrough_RendersSgr9()
        => Assert.Contains("\u001b[9m", TuiMarkup.ToAnsi(TuiMarkdown.ToMarkup("~~gone~~")));

    [Fact]
    public void SentinelCharacters_InModelText_CannotForgeMarkup()
        => Assert.Equal("xboldy", TuiMarkup.Plain(TuiMarkdown.ToMarkup("x\uE000bold\uE001y")));

}
