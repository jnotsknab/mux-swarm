using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Extension loading: manifest scan, validation, merge order, and the theme contribution.
// ConsoleState collection because Load() publishes into the process-global Theme list.
[Collection("ConsoleState")]
public sealed class ExtensionRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-ext-tests-" + Guid.NewGuid().ToString("N"));

    public ExtensionRegistryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string AddExtension(string folder, string manifestJson)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), manifestJson);
        return dir;
    }

    [Fact]
    public void Disabled_LoadsNothing_AndThemesAreTheBuiltIns()
    {
        var result = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = false, Dirs = [_root] });
        Assert.Empty(result.Extensions);
        Assert.Same(Theme.Presets, Theme.All);
    }

    [Fact]
    public void Scan_LoadsThemeOverBase_AndIgnoresFoldersWithoutManifest()
    {
        AddExtension("dusk", """
            { "name": "dusk", "version": "1.0", "themes": [ { "name": "dusk", "base": "nord", "accent": "#FF8800" } ] }
            """);
        Directory.CreateDirectory(Path.Combine(_root, "not-an-extension"));

        var result = ExtensionRegistry.Scan([_root]);

        var ext = Assert.Single(result.Extensions);
        Assert.Equal("dusk", ext.Name);
        var theme = Assert.Single(result.Themes);
        Assert.Equal("#FF8800", theme.Accent);
        Assert.Equal(Theme.Nord.Success, theme.Success);   // unset roles come from the base
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Scan_NameDefaultsToFolder_AndMissingDirsAreIgnored()
    {
        AddExtension("my-pack", "{ }");
        var result = ExtensionRegistry.Scan([_root, Path.Combine(_root, "missing")]);
        Assert.Equal("my-pack", Assert.Single(result.Extensions).Name);
    }

    [Theory]
    [InlineData("""{ "themes": [ { "name": "x1", "accent": "[red]boom" } ] }""", "not a valid color")]
    [InlineData("""{ "themes": [ { "name": "x1", "accent": "red on blue" } ] }""", "not a valid color")]
    [InlineData("""{ "themes": [ { "name": "x1", "nope": "#000000" } ] }""", "unknown color role")]
    [InlineData("""{ "themes": [ { "name": "x1", "base": "nope" } ] }""", "unknown base")]
    [InlineData("""{ "themes": [ { "name": "dracula" } ] }""", "shadow a built-in")]
    [InlineData("""{ "themes": [ { "name": "Bad Name" } ] }""", "invalid")]
    public void Scan_RejectsInvalidThemes_WithAProblem(string manifest, string expected)
    {
        AddExtension("pack", manifest);
        var result = ExtensionRegistry.Scan([_root]);
        Assert.Empty(result.Themes);
        Assert.Contains(result.Problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildTheme_AcceptsHexNamedAndDefaultColors()
    {
        var spec = new Dictionary<string, string> { ["name"] = "mixed", ["accent"] = "#1A2B3C", ["muted"] = "grey", ["prompt"] = "default" };
        var theme = ExtensionRegistry.BuildTheme(spec, out var error);
        Assert.NotNull(theme);
        Assert.Equal("", error);
        Assert.Equal("grey", theme!.Muted);
        Assert.Equal("default", theme.Prompt);
        Assert.Equal(Theme.Default.Success, theme.Success);   // base defaults to "default"
    }

    [Fact]
    public void Scan_BadJsonAndBadNames_AreSkippedWithProblems()
    {
        AddExtension("broken", "{ not json");
        AddExtension("ok", """{ "name": "Has Spaces" }""");
        var result = ExtensionRegistry.Scan([_root]);
        Assert.Empty(result.Extensions);
        Assert.Equal(2, result.Problems.Count);
    }

    [Fact]
    public void Scan_DuplicateNames_FirstSearchDirWins()
    {
        var second = Path.Combine(_root, "second");
        AddExtension("a", """{ "name": "same", "version": "1" }""");
        Directory.CreateDirectory(Path.Combine(second, "b"));
        File.WriteAllText(Path.Combine(second, "b", ExtensionRegistry.ManifestFileName), """{ "name": "same", "version": "2" }""");

        var result = ExtensionRegistry.Scan([_root, second]);

        Assert.Equal("1", Assert.Single(result.Extensions).Version);
        Assert.Contains(result.Problems, p => p.Contains("duplicate"));
    }

    [Fact]
    public void Scan_SameThemeInTwoExtensions_LaterNameWins()
    {
        AddExtension("aaa", """{ "themes": [ { "name": "shared", "accent": "#111111" } ] }""");
        AddExtension("bbb", """{ "themes": [ { "name": "shared", "accent": "#222222" } ] }""");
        var result = ExtensionRegistry.Scan([_root]);
        Assert.Equal("#222222", Assert.Single(result.Themes).Accent);
        Assert.Equal(new ExtensionRegistry.Conflict("theme 'shared'", "bbb", "aaa"), Assert.Single(result.Conflicts));
    }

    [Fact]
    public void Load_PublishesThemes_ToFindAndAll_AndClearsWhenDisabled()
    {
        AddExtension("dusk", """{ "themes": [ { "name": "dusk", "accent": "#FF8800" } ] }""");

        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        Assert.Equal("#FF8800", Theme.Find("DUSK")!.Accent);
        Assert.Contains("dusk", Theme.Names);
        Assert.Equal(Theme.Presets.Count + 1, Theme.All.Count);

        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = false });
        Assert.Null(Theme.Find("dusk"));
        Assert.Same(Theme.Presets, Theme.All);
    }

    [Fact]
    public void Scan_ReadsProcessAndEvents_AndRejectsEmptyCommand()
    {
        AddExtension("svc", """{ "process": { "command": "python", "args": ["main.py", "a b"] }, "events": ["tool_call", " ", "turn_end"] }""");
        AddExtension("bad", """{ "process": { "command": " " } }""");
        var result = ExtensionRegistry.Scan([_root]);

        var svc = Assert.Single(result.Extensions, e => e.Name == "svc");
        Assert.Equal("python", svc.Process!.Command);
        Assert.Equal(["main.py", "a b"], svc.Process.Args);
        Assert.Equal(["tool_call", "turn_end"], svc.Events);
        Assert.Null(Assert.Single(result.Extensions, e => e.Name == "bad").Process);
        Assert.Contains(result.Problems, p => p.Contains("process.command is empty"));
    }

    [Fact]
    public void Gate_IsSettableThroughSet_AndOffByDefault()
    {
        var cfg = new AppConfig();
        Assert.False(cfg.Extensions.Enabled);
        var leaf = Assert.Single(MuxSwarm.Engine.Tui.ConfigReflector.Walk(cfg, ""), l => l.Path == "extensions.enabled");
        Assert.True(leaf.Set("on").ok);
        Assert.True(cfg.Extensions.Enabled);
    }

    [Fact]
    public void SearchDirectories_StartsWithInstallFolder_AndDeduplicates()
    {
        var dirs = ExtensionRegistry.SearchDirectories(new ExtensionsConfig { Dirs = [_root, _root + Path.DirectorySeparatorChar, " "] });
        Assert.Equal(Path.TrimEndingDirectorySeparator(ExtensionRegistry.DefaultDirectory), dirs[0]);
        Assert.Equal(2, dirs.Count);
    }
}
