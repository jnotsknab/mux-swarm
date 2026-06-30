using MuxSwarm.Utils;
using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Theme system: named presets, lookup, live-apply, and that the theme-backed palette consumers
// (TuiComponents foreground roles) reflect the active theme. Runs in ConsoleState collection
// because Theme.Active is process-global.
[Collection("ConsoleState")]
public class ThemeTests
{
    [Fact]
    public void Presets_IncludeExpectedNames()
    {
        var names = Theme.Names;
        Assert.Contains("default", names);
        Assert.Contains("dark", names);
        Assert.Contains("light", names);
        Assert.Contains("mono", names);
        Assert.Contains("solarized", names);
        Assert.Contains("dracula", names);
        Assert.Contains("gruvbox", names);
    }

    [Fact]
    public void Default_ReproducesOriginalPalette()
    {
        Assert.Equal("#64B4DC", Theme.Default.Accent);
        Assert.Equal("#78C88C", Theme.Default.Success);
        Assert.Equal("#D46C6C", Theme.Default.Error);
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndNullForUnknown()
    {
        Assert.NotNull(Theme.Find("DRACULA"));
        Assert.Equal("dracula", Theme.Find("Dracula")!.Name);
        Assert.Null(Theme.Find("nope-not-a-theme"));
        Assert.Null(Theme.Find(null));
        Assert.Null(Theme.Find(""));
    }

    [Fact]
    public void Apply_SwapsActive_AndPaletteConsumersReflectIt()
    {
        var prev = Theme.Active;
        try
        {
            Assert.True(Theme.Apply("dracula"));
            Assert.Equal("dracula", Theme.Active.Name);
            // TuiComponents foreground roles read Theme.Active.
            Assert.Equal(Theme.Dracula.Accent, TuiComponents.Accent);
            Assert.Equal(Theme.Dracula.Success, TuiComponents.Ok);
            Assert.Equal(Theme.Dracula.Error, TuiComponents.Err);
        }
        finally { Theme.Set(prev); }
    }

    [Fact]
    public void Apply_UnknownLeavesActiveUntouched()
    {
        var prev = Theme.Active;
        try
        {
            Theme.Apply("gruvbox");
            Assert.False(Theme.Apply("bogus"));
            Assert.Equal("gruvbox", Theme.Active.Name);  // unchanged by the failed apply
        }
        finally { Theme.Set(prev); }
    }

    [Fact]
    public void Mono_UsesTerminalDefaultForeground()
    {
        // Accessibility theme inherits the terminal fg ("default") rather than a hue.
        Assert.Equal("default", Theme.Mono.Accent);
        Assert.Equal("default", Theme.Mono.Prompt);
    }
}
