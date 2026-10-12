using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Regression coverage for two markdown-renderer bugs: brackets inside styled inline spans were
/// escaped twice (`a[0]` rendered "a[[0]]"), and the buffered table grid hardcoded its border and
/// header colors instead of following the active theme. Theme.Active is process-global, hence
/// the ConsoleState collection.
/// </summary>
[Collection("ConsoleState")]
public class MarkdownBracketThemeTests
{
    [Theory]
    [InlineData("see `a[0]` here", "see a[0] here")]
    [InlineData("**[b]** text", "[b] text")]
    [InlineData("__[b]__ text", "[b] text")]
    [InlineData("***[bi]*** text", "[bi] text")]
    [InlineData("an *[i]* word", "an [i] word")]
    [InlineData("~~[s]~~ gone", "[s] gone")]
    [InlineData("[x[1](https://example.com) link", "x[1 link")]
    [InlineData("![alt[2](img.png)", "alt[2")]
    [InlineData("<https://example.com/a[0]>", "https://example.com/a[0]")]
    [InlineData("plain [p] and `c[3]`", "plain [p] and c[3]")]
    public void Inline_BracketsInsideSpans_RenderSingle(string md, string expectedPlain)
    {
        Assert.Equal(expectedPlain, TuiMarkup.Plain(TuiMarkdown.ToMarkup(md)));
    }

    [Fact]
    public void Inline_BracketInsideCodeSpan_EscapedExactlyOnceInMarkup()
    {
        var m = TuiMarkdown.ToMarkup("`a[0]`");
        Assert.Contains("a[[0]]", m);
        Assert.DoesNotContain("[[[[", m);
        Assert.DoesNotContain("]]]]", m);
    }

    [Fact]
    public void Table_BracketsInCells_RenderSingle()
    {
        var rows = TuiTable.Render(new[] { "| Key | Val |", "|---|---|", "| `a[0]` | **[b]** [c] |" }, 80);
        var plain = string.Join("\n", rows.Select(TuiMarkup.Plain));
        Assert.Contains("a[0]", plain);
        Assert.Contains("[b] [c]", plain);
        Assert.DoesNotContain("[[", plain);
    }

    [Fact]
    public void Table_BorderAndHeader_FollowActiveTheme()
    {
        var prev = Theme.Active;
        try
        {
            Assert.True(Theme.Apply("dracula"));
            var rows = TuiTable.Render(new[] { "| H1 | H2 |", "|---|---|", "| a | b |" }, 80);
            string all = string.Join("\n", rows);
            Assert.Contains("[#44475A]", all);                         // dracula table border
            Assert.Contains($"[bold {Theme.Dracula.MdHeading}]H1[/]", all);
            Assert.DoesNotContain("#3A3A3A", all);
            Assert.DoesNotContain("#64B4DC", all);
        }
        finally { Theme.Set(prev); }
    }

    [Fact]
    public void Table_DefaultTheme_KeepsOriginalColors()
    {
        var prev = Theme.Active;
        try
        {
            Theme.Set(Theme.Default);
            string all = string.Join("\n", TuiTable.Render(new[] { "| H |", "|---|", "| a |" }, 80));
            Assert.Contains("[#3A3A3A]", all);
            Assert.Contains("[bold #64B4DC]H[/]", all);
        }
        finally { Theme.Set(prev); }
    }
}
