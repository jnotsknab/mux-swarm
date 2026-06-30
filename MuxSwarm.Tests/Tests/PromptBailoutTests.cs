using System.IO;
using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

// M-F: bailout-aware interactive prompts. Driven through MuxConsole.InputOverride (the scripted
// path), which the *Choice variants honor exactly like the live picker. Shares the ConsoleState
// collection so console-static state (InputOverride) is not mutated concurrently.
[Collection("ConsoleState")]
public class PromptBailoutTests
{
    private static void Script(string line)
    {
        MuxConsole.InputOverride = new StringReader(line + "\n");
    }

    private static void Reset()
    {
        MuxConsole.InputOverride = System.Console.In;
    }

    [Fact]
    public void SelectChoice_NumericIndex_PicksOption()
    {
        try
        {
            Script("2");
            var c = MuxConsole.SelectChoice("pick", new[] { "alpha", "beta", "gamma" });
            Assert.False(c.Cancelled);
            Assert.False(c.Custom);
            Assert.Equal("beta", c.Value);
        }
        finally { Reset(); }
    }

    [Fact]
    public void SelectChoice_Cancel_ReturnsCancelled()
    {
        try
        {
            Script("cancel");
            var c = MuxConsole.SelectChoice("pick", new[] { "alpha", "beta" });
            Assert.True(c.Cancelled);
            Assert.Equal(string.Empty, c.Value);
        }
        finally { Reset(); }
    }

    [Fact]
    public void SelectChoice_EmptyLine_ReturnsCancelled()
    {
        try
        {
            Script("");
            var c = MuxConsole.SelectChoice("pick", new[] { "alpha", "beta" });
            Assert.True(c.Cancelled);
        }
        finally { Reset(); }
    }

    [Fact]
    public void SelectChoice_EqualsPrefix_ReturnsCustomText()
    {
        try
        {
            Script("=my own answer");
            var c = MuxConsole.SelectChoice("pick", new[] { "alpha", "beta" });
            Assert.True(c.Custom);
            Assert.False(c.Cancelled);
            Assert.Equal("my own answer", c.Value);
        }
        finally { Reset(); }
    }

    [Fact]
    public void SelectChoice_FreeTextNonIndex_ReturnsCustomText()
    {
        try
        {
            Script("something else entirely");
            var c = MuxConsole.SelectChoice("pick", new[] { "alpha", "beta" });
            Assert.True(c.Custom);
            Assert.Equal("something else entirely", c.Value);
        }
        finally { Reset(); }
    }

    [Fact]
    public void ConfirmChoice_Yes_ReturnsYes()
    {
        try
        {
            Script("y");
            var c = MuxConsole.ConfirmChoice("ok?");
            Assert.False(c.Cancelled);
            Assert.Equal("yes", c.Value);
        }
        finally { Reset(); }
    }

    [Fact]
    public void ConfirmChoice_Cancel_ReturnsCancelled()
    {
        try
        {
            Script("cancel");
            var c = MuxConsole.ConfirmChoice("ok?");
            Assert.True(c.Cancelled);
        }
        finally { Reset(); }
    }

    [Fact]
    public void ConfirmChoice_EmptyUsesDefault()
    {
        try
        {
            Script("");
            var c = MuxConsole.ConfirmChoice("ok?", defaultValue: false);
            Assert.False(c.Cancelled);
            Assert.Equal("no", c.Value);
        }
        finally { Reset(); }
    }

    [Fact]
    public void MultiSelectChoice_Indices_PicksOptions()
    {
        try
        {
            Script("1,3");
            var c = MuxConsole.MultiSelectChoice("pick", new[] { "a", "b", "c" });
            Assert.False(c.Cancelled);
            Assert.Equal("a, c", c.Value);
        }
        finally { Reset(); }
    }

    [Fact]
    public void MultiSelectChoice_Cancel_ReturnsCancelled()
    {
        try
        {
            Script("cancel");
            var c = MuxConsole.MultiSelectChoice("pick", new[] { "a", "b" });
            Assert.True(c.Cancelled);
        }
        finally { Reset(); }
    }

    [Fact]
    public void MultiSelectChoice_Custom_ReturnsCustomText()
    {
        try
        {
            Script("=custom multi");
            var c = MuxConsole.MultiSelectChoice("pick", new[] { "a", "b" });
            Assert.True(c.Custom);
            Assert.Equal("custom multi", c.Value);
        }
        finally { Reset(); }
    }

    [Fact]
    public void AskSelect_Cancel_ReturnsCancelledMessage()
    {
        try
        {
            Script("cancel");
            var r = MuxConsole.AskSelect("pick", "alpha|beta");
            Assert.Contains("cancelled", r, System.StringComparison.OrdinalIgnoreCase);
        }
        finally { Reset(); }
    }

    [Fact]
    public void AskSelect_Custom_ReturnsCustomMessage()
    {
        try
        {
            Script("=typed answer");
            var r = MuxConsole.AskSelect("pick", "alpha|beta");
            Assert.Contains("custom", r, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("typed answer", r);
        }
        finally { Reset(); }
    }

    [Fact]
    public void AskSelect_Index_ReturnsSelectedMessage()
    {
        try
        {
            Script("1");
            var r = MuxConsole.AskSelect("pick", "alpha|beta");
            Assert.Contains("User selected: alpha", r);
        }
        finally { Reset(); }
    }
}
