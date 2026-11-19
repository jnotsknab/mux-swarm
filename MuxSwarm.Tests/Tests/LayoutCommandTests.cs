using System.Text.Json;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>/layout: the choices are the fixed built-in compose shapes (never extensions); picking one writes only
/// console.tui.regions.compose.shape, which wins over extensions; standard removes it.</summary>
[Collection("ConsoleState")]
public sealed class LayoutCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-layout-tests-" + Guid.NewGuid().ToString("N"));
    public LayoutCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Choices_AreTheBuiltInShapes_WithStandardFirst()
    {
        Assert.Equal(["standard", "rule", "bracket", "box", "bubble", "smooth", "rail", "bare"], LayoutCommand.Choices());
    }

    [Fact]
    public void Apply_WritesOnlyTheShape_KeepsOtherUserStyles_AndStandardRemovesIt()
    {
        var console = new ConsoleConfig
        {
            Tui = JsonSerializer.Deserialize<TuiManifest>("{ \"regions\": { \"footer\": { \"position\": \"above\" }, \"compose\": { \"padX\": 2 } } }"),
        };
        Assert.True(LayoutCommand.Apply(console, "bubble"));
        Assert.Equal("bubble", console.Tui!.Regions!["compose"]["shape"].GetString());
        Assert.Equal(2, console.Tui.Regions["compose"]["padX"].GetInt32());
        Assert.Equal("above", console.Tui.Regions["footer"]["position"].GetString());
        Assert.False(LayoutCommand.Apply(console, "bubble"));

        Assert.True(LayoutCommand.Apply(console, LayoutCommand.Standard));
        Assert.False(console.Tui.Regions["compose"].ContainsKey("shape"));
        Assert.True(console.Tui.Regions["compose"].ContainsKey("padX"));
        Assert.False(LayoutCommand.Apply(console, LayoutCommand.Standard));

        var empty = new ConsoleConfig();
        Assert.True(LayoutCommand.Apply(empty, "rule"));
        Assert.True(LayoutCommand.Apply(empty, LayoutCommand.Standard));
        Assert.False(empty.Tui!.Regions!.ContainsKey("compose"));
    }

    [Fact]
    public void UserShape_WinsOverAnExtensionShape_AndActiveReportsIt()
    {
        var dir = Path.Combine(_root, "hearth");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName),
            "{ \"tui\": { \"regions\": { \"compose\": { \"shape\": \"box\" }, \"footer\": { \"position\": \"border\" } } } }");
        var cfg = new ExtensionsConfig { Enabled = true, Dirs = [_root] };
        ExtensionRegistry.Load(cfg);
        Assert.Equal("box", LayoutCommand.Active());

        var console = new ConsoleConfig();
        LayoutCommand.Apply(console, "smooth");
        ExtensionRegistry.Load(cfg, console.Tui);
        Assert.Equal("smooth", LayoutCommand.Active());
        Assert.Equal("border", RegionStyle.Choice("footer.position"));   // the extension's other styles still apply

        ExtensionRegistry.Load(null);
        Assert.Equal(LayoutCommand.Standard, LayoutCommand.Active());
    }

    [Theory]
    [InlineData(false)]   // no shape of the user's own yet
    [InlineData(true)]    // the user had picked one before
    public void Standard_OverridesAnExtensionShape_BackToTheStockLook(bool userHadShape)
    {
        var dir = Path.Combine(_root, "hearth");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": { \"compose\": { \"shape\": \"box\" } } } }");
        var cfg = new ExtensionsConfig { Enabled = true, Dirs = [_root] };
        var console = new ConsoleConfig();
        if (userHadShape) LayoutCommand.Apply(console, "rule");
        ExtensionRegistry.Load(cfg, console.Tui);
        Assert.Equal("box", LayoutCommand.ExtensionShape());   // the user's own pick is not the extension's

        Assert.True(LayoutCommand.Apply(console, LayoutCommand.Standard, LayoutCommand.ExtensionShape()));   // reports a change
        ExtensionRegistry.Load(cfg, console.Tui);
        Assert.Equal(LayoutCommand.Standard, LayoutCommand.Active());   // stock look, not the extension's box
        Assert.False(LayoutCommand.Apply(console, LayoutCommand.Standard, LayoutCommand.ExtensionShape()));
    }
}
