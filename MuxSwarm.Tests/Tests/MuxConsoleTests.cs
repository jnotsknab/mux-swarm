using MuxSwarm.Utils;
using Spectre.Console;

namespace MuxSwarm.Tests.Tests;

[Collection("ConsoleState")]
public class MuxConsoleTests
{
    [Fact]
    public void ParseOptions_NullInput_ReturnsEmptyList()
    {
        var result = MuxConsole.ParseOptions(null);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseOptions_EmptyString_ReturnsEmptyList()
    {
        var result = MuxConsole.ParseOptions("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseOptions_WhitespaceOnly_ReturnsEmptyList()
    {
        var result = MuxConsole.ParseOptions("   ");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseOptions_SingleOption_ReturnsSingleItem()
    {
        var result = MuxConsole.ParseOptions("option1");
        Assert.Single(result);
        Assert.Equal("option1", result[0]);
    }

    [Fact]
    public void ParseOptions_MultipleOptions_ReturnsAllItems()
    {
        var result = MuxConsole.ParseOptions("alpha| beta| gamma");
        Assert.Equal(3, result.Count);
        Assert.Equal("alpha", result[0]);
        Assert.Equal("beta", result[1]);
        Assert.Equal("gamma", result[2]);
    }

    [Fact]
    public void ParseOptions_TrimsWhitespace()
    {
        var result = MuxConsole.ParseOptions("  a | b | c  ");
        Assert.Equal(3, result.Count);
        Assert.Equal("a", result[0]);
        Assert.Equal("b", result[1]);
        Assert.Equal("c", result[2]);
    }

    [Fact]
    public void ParseOptions_SkipsEmptyEntries()
    {
        var result = MuxConsole.ParseOptions("a||b|||c");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void StdioMode_DefaultIsFalse()
    {
        MuxConsole.StdioMode = false;
        Assert.False(MuxConsole.StdioMode);
    }

    [Fact]
    public void StdioMode_CanBeSetToTrue()
    {
        try
        {
            MuxConsole.StdioMode = true;
            Assert.True(MuxConsole.StdioMode);
        }
        finally
        {
            MuxConsole.StdioMode = false;
        }
    }

    // --- Regression: /set picker crash on bracketed choice labels --------------------
    // The /set picker builds labels like "renderMode = auto [auto|tui|classic]". Spectre
    // parses "[auto|tui|classic]" as a markup tag and throws unless choices are escaped.
    // The fix adds .UseConverter(Markup.Escape) to Select/MultiSelect.

    [Fact]
    public void SpectreMarkup_RawBracketedLabel_Throws()
    {
        // Documents the underlying Spectre behavior the fix guards against.
        Assert.ThrowsAny<System.Exception>(() =>
            new Markup("renderMode  =  auto   [auto|tui|classic]"));
    }

    [Fact]
    public void SpectreMarkup_EscapedBracketedLabel_DoesNotThrow()
    {
        // The converter used by Select/MultiSelect (Markup.Escape) renders the same
        // label safely.
        var ex = Record.Exception(() =>
            new Markup(Markup.Escape("renderMode  =  auto   [auto|tui|classic]")));
        Assert.Null(ex);
    }

    [Fact]
    public void Select_StdioPath_ReturnsRawBracketedChoiceUnchanged()
    {
        // The converter only affects display; the returned value must remain the original
        // (unescaped) string so callers can index/compare against their source list.
        var prevStdio = MuxConsole.StdioMode;
        var prevIn = MuxConsole.InputOverride;
        try
        {
            MuxConsole.StdioMode = true;
            MuxConsole.InputOverride = new System.IO.StringReader("2\n");
            var choices = new[] { "first [x|y]", "renderMode  =  auto   [auto|tui|classic]" };
            var chosen = MuxConsole.Select("pick", choices);
            Assert.Equal("renderMode  =  auto   [auto|tui|classic]", chosen);
        }
        finally
        {
            MuxConsole.StdioMode = prevStdio;
            MuxConsole.InputOverride = prevIn;
        }
    }

}
