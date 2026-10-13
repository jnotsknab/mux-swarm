using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Markdown style from extensions (table border, bullet, rule) and the web theme mapping behind
// GET /api/themes. ConsoleState collection: Load() publishes process-global theme + markdown state.
[Collection("ConsoleState")]
public sealed class ExtensionWebMarkdownTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-extmd-tests-" + Guid.NewGuid().ToString("N"));

    public ExtensionWebMarkdownTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void AddExtension(string folder, string manifestJson)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), manifestJson);
    }

    private static readonly string[] Table = ["| a | b |", "|---|---|", "| 1 | 2 |"];

    [Fact]
    public void BuiltIn_IsHeavyBorders_BulletAndRule()
    {
        ExtensionRegistry.Load(null);
        var rows = string.Join("\n", TuiTable.Render(Table, 80));
        Assert.Contains("\u250f", rows);
        Assert.Contains("\u2503", rows);
        Assert.Contains("\u2022", TuiMarkdown.ToMarkup("- item"));
        Assert.Contains(new string('\u2500', 24), TuiMarkdown.ToMarkup("---"));
    }

    [Fact]
    public void Extension_SetsBorderBulletAndRule_LaterNameWins()
    {
        AddExtension("a", """{ "markdown": { "tableBorder": "ascii" } }""");
        AddExtension("b", """{ "markdown": { "tableBorder": "rounded", "bullet": "\u25b8", "rule": "=" } }""");
        var result = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });

        Assert.Contains(result.Problems, p => p.Contains("replaces the one from an earlier extension"));
        Assert.True(result.Extensions.Single(e => e.Name == "b").Markdown);
        var rows = string.Join("\n", TuiTable.Render(Table, 80));
        Assert.Contains("\u256d", rows);                 // rounded corner
        Assert.Contains("\u2502", rows);                 // light vertical
        Assert.DoesNotContain("\u2503", rows);
        Assert.Contains("\u25b8", TuiMarkdown.ToMarkup("- item"));
        Assert.Contains(new string('=', 24), TuiMarkdown.ToMarkup("***"));

        ExtensionRegistry.Load(null);
        Assert.Same(MarkdownStyle.Default, MarkdownStyle.Active);
    }

    [Fact]
    public void AsciiBorders_RenderWithoutBoxDrawing()
    {
        MarkdownStyle.Set(MarkdownStyle.Default with { Border = "ascii" });
        try
        {
            var plain = string.Join("\n", TuiTable.Render(Table, 80).Select(TuiMarkup.Plain));
            Assert.Contains("+---+---+", plain);
            Assert.Contains("| a | b |", plain);
            Assert.DoesNotContain(plain, c => c is >= '\u2500' and <= '\u257f');
        }
        finally { MarkdownStyle.Set(null); }
    }

    [Fact]
    public void InvalidKeys_AreReported_AndKeepTheBuiltIn()
    {
        var problems = new List<string>();
        Assert.Null(ExtensionRegistry.BuildMarkdown(new MarkdownManifest { TableBorder = "double", Bullet = "->", Rule = " " }, problems, "x"));
        Assert.Equal(3, problems.Count);
        Assert.Null(ExtensionRegistry.BuildMarkdown(new MarkdownManifest { Bullet = "\u4e2d" }, problems, "x"));   // two cells wide
        var ok = ExtensionRegistry.BuildMarkdown(new MarkdownManifest { TableBorder = "LIGHT" }, [], "x");
        Assert.Equal(MarkdownStyle.Default with { Border = "light" }, ok);
    }

    [Fact]
    public void WebThemes_MapRoles_ToCssVariables_WithFallbacks()
    {
        var t = Theme.Default with { Name = "dusk", Accent = "#FF8800", InputBg = "#F0F0F0", Success = "green", Border = "#102030" };
        var item = Assert.Single(WebThemes.Build([t]));

        Assert.Equal("ext-dusk", item.Id);
        Assert.Equal("dusk", item.Name);
        Assert.Equal("#ff8800", item.Vars["--accent"]);
        Assert.Equal("255, 136, 0", item.Vars["--accent-rgb"]);
        Assert.Equal("#f0f0f0", item.Vars["--bg"]);
        Assert.True(item.IsLight);
        Assert.Equal("#10b981", item.Vars["--green"]);                     // named color -> web default
        Assert.Equal("rgba(16, 32, 48, 0.5)", item.Vars["--border"]);
        Assert.Equal(18, item.Vars.Count);
    }

    [Fact]
    public void ApiThemes_ListsOnlyExtensionThemes()
    {
        AddExtension("dusk", """{ "themes": [ { "name": "dusk", "base": "nord", "accent": "#FF8800" } ] }""");
        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        var items = WebThemes.Build(Theme.All.Except(Theme.Presets));
        Assert.Equal(["ext-dusk"], items.Select(i => i.Id));
    }
}
