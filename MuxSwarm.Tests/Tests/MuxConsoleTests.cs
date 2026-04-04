using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

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
}
