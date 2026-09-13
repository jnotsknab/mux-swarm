using System.Reflection;
using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Batch 10: streamed tables survive resize (committed as width-parameterized layouts,
/// never re-wrapped frozen rows), the upgraded table grid, and TuiMarkdown edge cases from the
/// dogfood captures.</summary>
[Collection("ConsoleState")]
public class MarkdownTableHardeningTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 100;
        public int Height { get; set; } = 30;
        public StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly string[] TableSource =
    {
        "| Agent | Cycles | Result |",
        "|---|---|---|",
        "| WebAgent | 3 | done |",
        "| CodeAgent | 2 | done (noted cycles 2-3 text was less prominent) |",
    };

    private static void StreamTable(TuiDriver driver)
    {
        driver.BeginStream();
        foreach (var row in TableSource) driver.StreamChunk(row + "\n");
        driver.EndStream();
    }

    // ---- resize corruption fix ----

    [Fact]
    public void StreamedTable_CommitsAsLayout_ReRendersCleanAtNewWidth()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        StreamTable(driver);
        var transcript = (System.Collections.IList)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!;
        // The table entry carries a Layout (width-parameterized), not frozen rows.
        bool foundLayout = false;
        foreach (var entry in transcript)
            if (entry!.GetType().GetField("Layout")!.GetValue(entry) is not null) foundLayout = true;
        Assert.True(foundLayout, "streamed table must commit as a re-renderable layout");

        // Compose at full width, then shrink hard and recompose: every border row must be a
        // SINGLE well-formed run (one ╭, matching ╮, no stranded fragments from re-wrapping).
        var composeMethod = typeof(TuiDriver).GetMethod("ComposeFrameRows", PrivateInstance | BindingFlags.Public)!;
        _ = (List<string>)composeMethod.Invoke(driver, null)!;
        term.Width = 60;
        typeof(TuiDriver).GetMethod("InvalidateFrameRowCounts", PrivateInstance)!.Invoke(driver, null);
        var rows = ((List<string>)composeMethod.Invoke(driver, null)!)
            .Select(TuiMarkup.StripAnsi).ToList();
        var borderRows = rows.Where(r => r.Contains('\u256d') || r.Contains('\u256e')).ToList();
        Assert.NotEmpty(borderRows);
        foreach (var r in borderRows)
        {
            Assert.Equal(r.Count(ch => ch == '\u256d'), r.Count(ch => ch == '\u256e'));
            Assert.True(TuiMarkup.Width(r) <= 60, $"border row must fit the new width: '{r}'");
        }
        // No orphaned box-drawing fragments INSIDE the table block (between its ╭ and ╰ rows):
        // a bare dashes-only row there is a re-wrap artifact. Rows outside the block (the
        // footer's full-width rule) legitimately contain plain dash runs.
        int tableTop = rows.FindIndex(r => r.Contains('\u256d'));
        int tableBottom = rows.FindIndex(r => r.Contains('\u2570'));
        Assert.True(tableTop >= 0 && tableBottom > tableTop);
        for (int i = tableTop + 1; i < tableBottom; i++)
            if (rows[i].TrimStart().StartsWith('\u2500'))
                Assert.Fail($"stranded border fragment inside the table: '{rows[i]}'");
    }

    [Fact]
    public void StreamedTable_LaneTint_SurvivesLayoutReRender()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetLaneTint("#64B4DC");
        StreamTable(driver);
        driver.SetLaneTint(null);   // turn ended; the layout must re-render with the CAPTURED tint
        var transcript = (System.Collections.IList)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!;
        foreach (var entry in transcript)
            if (entry!.GetType().GetField("Layout")!.GetValue(entry) is Func<int, List<string>> layout)
            {
                var rows = layout(70);
                Assert.Contains(rows, r => r.Contains("[#64B4DC]\u258e[/]", StringComparison.Ordinal));
                return;
            }
        Assert.Fail("no layout entry found");
    }

    // ---- table grid visuals ----

    [Fact]
    public void TableRender_HeaderRule_IsDoubleLine_WithProperIntersections()
    {
        var rows = TuiTable.Render(TableSource, 100);
        string rule = rows.Single(r => r.Contains('\u255e'));
        Assert.Contains('\u2550', rule);   // double-line fill
        Assert.Contains('\u256a', rule);   // double-to-single column intersections
        Assert.EndsWith("\u2561[/]", rule);
        // Top/bottom stay single rounded.
        Assert.Contains(rows, r => r.Contains('\u256d') && r.Contains('\u252c'));
        Assert.Contains(rows, r => r.Contains('\u2570') && r.Contains('\u2534'));
    }

    [Fact]
    public void TableRender_EveryBodyRow_CarriesClosingBorder()
    {
        var rows = TuiTable.Render(TableSource, 100);
        foreach (var r in rows.Where(r => r.Contains('\u2502')))
            Assert.EndsWith("\u2502[/]", r);   // closing border present, not trimmed away
    }

    [Fact]
    public void TableRender_ColumnsAlign_AcrossAllRows()
    {
        var rows = TuiTable.Render(TableSource, 100)
            .Where(r => r.Contains('\u2502'))
            .Select(TuiMarkup.Plain).ToList();
        // The interior separators must sit at identical visible columns on every row.
        var positions = rows.Select(r => Enumerable.Range(0, r.Length).Where(i => r[i] == '\u2502').ToArray()).ToList();
        for (int i = 1; i < positions.Count; i++)
            Assert.Equal(positions[0], positions[i]);
    }

    // ---- markdown edge cases (from the dogfood captures + audit) ----

    [Fact]
    public void BoldItalic_TripleStar_NoLiteralAsterisksLeak()
    {
        string markup = TuiMarkdown.ToMarkup("***both***");
        Assert.DoesNotContain("*", TuiMarkup.Plain(markup));
        Assert.Contains("[bold italic]", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoLink_AngleBracketsDropped_UrlStyled()
    {
        string markup = TuiMarkdown.ToMarkup("see <https://example.com/x> now");
        string plain = TuiMarkup.Plain(markup);
        Assert.Contains("https://example.com/x", plain);
        Assert.DoesNotContain("<", plain);
        Assert.DoesNotContain(">", plain);
    }

    [Fact]
    public void EscapedMarkers_RenderCharacter_NotBackslash()
    {
        Assert.Equal("2 * 3 = 6", TuiMarkup.Plain(TuiMarkdown.ToMarkup(@"2 \* 3 = 6")));
        Assert.Equal("snake_case_name", TuiMarkup.Plain(TuiMarkdown.ToMarkup(@"snake\_case\_name")));
    }

    [Fact]
    public void StripInline_HandlesNewForms_ForTableCellMeasurement()
    {
        Assert.Equal("both", TuiMarkdown.StripInline("***both***"));
        Assert.Equal("https://x.io", TuiMarkdown.StripInline("<https://x.io>"));
        Assert.Equal("a*b", TuiMarkdown.StripInline(@"a\*b"));
    }
}
