using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>/layout: layouts are installed extensions whose tui block sets compose.shape; switching enables one and disables
/// the other layouts (never other extensions), so two layouts never merge.</summary>
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

    private void Add(string name, string tui)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"name\": \"" + name + "\"" + tui + " }");
    }

    private ExtensionRegistry.Result Load(ExtensionsConfig cfg) => ExtensionRegistry.Load(cfg);

    [Fact]
    public void Layouts_AreExtensionsThatSetAComposeShape_EnabledOrNot()
    {
        Add("bubble", ", \"tui\": { \"regions\": { \"compose\": { \"shape\": \"bubble\" } } }");
        Add("smooth", ", \"tui\": { \"regions\": { \"compose\": { \"shape\": \"smooth\" } } }");
        Add("chips", ", \"tui\": { \"regions\": { \"notice\": { \"okMark\": \"+\" } } }");   // styles, but not a layout
        Add("plain", "");
        var cfg = new ExtensionsConfig { Enabled = true, Dirs = [_root], Disabled = ["smooth"] };
        var loaded = Load(cfg);
        var layouts = LayoutCommand.Layouts(loaded);
        Assert.Equal(["bubble", "smooth"], layouts);
        Assert.Equal("bubble", LayoutCommand.Active(loaded, layouts));
    }

    [Fact]
    public void Apply_EnablesTheChoice_DisablesOtherLayouts_AndLeavesOtherExtensionsAlone()
    {
        Add("bubble", ", \"tui\": { \"regions\": { \"compose\": { \"shape\": \"bubble\" } } }");
        Add("smooth", ", \"tui\": { \"regions\": { \"compose\": { \"shape\": \"smooth\" } } }");
        Add("chips", ", \"tui\": { \"regions\": { \"notice\": { \"okMark\": \"+\" } } }");
        var cfg = new ExtensionsConfig { Enabled = true, Dirs = [_root], Disabled = ["chips"] };
        var layouts = LayoutCommand.Layouts(Load(cfg));

        Assert.True(LayoutCommand.Apply(cfg, layouts, "smooth"));
        Assert.Equal(["chips", "bubble"], cfg.Disabled);
        var loaded = Load(cfg);
        Assert.Equal("smooth", LayoutCommand.Active(loaded, layouts));
        Assert.Equal("smooth", Assert.Single(loaded.Extensions).Name);

        Assert.False(LayoutCommand.Apply(cfg, layouts, "smooth"));   // already there
        Assert.True(LayoutCommand.Apply(cfg, layouts, LayoutCommand.Standard));
        Assert.Equal(LayoutCommand.Standard, LayoutCommand.Active(Load(cfg), layouts));
        Assert.Equal(["chips", "bubble", "smooth"], cfg.Disabled);
    }
}
