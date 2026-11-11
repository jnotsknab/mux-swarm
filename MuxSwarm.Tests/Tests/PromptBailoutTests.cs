using System.IO;
using MuxSwarm.Engine;

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
    public void MultiSelectChoice_OptionsAndCustom_KeepsBoth()
    {
        try
        {
            Script("1,3,=also this");
            var c = MuxConsole.MultiSelectChoice("pick", new[] { "a", "b", "c" });
            Assert.True(c.Custom);
            Assert.False(c.Cancelled);
            Assert.Equal("also this", c.Value);
            Assert.Equal("a, c", c.AlsoSelected);
        }
        finally { Reset(); }
    }

    [Fact]
    public void MultiSelectChoice_ExistingAnswerForms_Unchanged()
    {
        try
        {
            Script("=only custom");
            var c = MuxConsole.MultiSelectChoice("pick", new[] { "a", "b" });
            Assert.True(c.Custom);
            Assert.Null(c.AlsoSelected);
            Script("2");
            Assert.Equal(MuxConsole.PromptChoice.Picked("b"), MuxConsole.MultiSelectChoice("pick", new[] { "a", "b" }));
            Script("a=b");                                   // free text is not the combined form
            Assert.True(MuxConsole.MultiSelectChoice("pick", new[] { "a", "b" }).Cancelled);
        }
        finally { Reset(); }
    }

    [Fact]
    public void FrameModal_CheckedOptionsPlusCustom_KeepsBoth()
    {
        var list = new System.Collections.Generic.List<string> { "a", "b", "c" };
        var c = MuxConsole.MultiFromChecked(new[] { 2, 3, 0 }, list, () => "typed");   // row 3 = "enter custom"
        Assert.True(c.Custom);
        Assert.Equal("typed", c.Value);
        Assert.Equal("a, c", c.AlsoSelected);
        Assert.Null(MuxConsole.MultiFromChecked(new[] { 3 }, list, () => "only").AlsoSelected);
        Assert.True(MuxConsole.MultiFromChecked(new[] { 1, 3 }, list, () => "  ").Cancelled);
        Assert.Equal(MuxConsole.PromptChoice.Picked("a, b"), MuxConsole.MultiFromChecked(new[] { 0, 1 }, list, () => null));
    }

    [Fact]
    public void AskMultiSelect_OptionsAndCustom_ReportsBoth()
    {
        try
        {
            Script("2,=my own");
            var r = MuxConsole.AskMultiSelect("pick", "alpha|beta|gamma");
            Assert.Equal("User selected: beta; and entered a custom response (outside the offered options): my own", r);
            Script("=just mine");
            Assert.Equal("User entered a custom response (outside the offered options): just mine",
                MuxConsole.AskMultiSelect("pick", "alpha|beta"));
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
