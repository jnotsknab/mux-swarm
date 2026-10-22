using System.Text.RegularExpressions;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// tui.regions styles: validation, merge/conflicts, rendering, theme-role resolution and clipboard chrome.
[Collection("ConsoleState")]
public sealed class ExtensionTuiStyleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-exttui-tests-" + Guid.NewGuid().ToString("N"));

    public ExtensionTuiStyleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        Theme.Set(Theme.Default);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void AddExtension(string folder, string manifestJson)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), manifestJson);
    }

    private ExtensionRegistry.Result LoadEnabled() => ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });

    private static string Plain(string markup) => TuiMarkup.Plain(markup);

    [Fact]
    public void Regions_RestyleTheirBuilders_AndUnloadRestoresBuiltIns()
    {
        AddExtension("neon", """
            { "tui": { "regions": {
                "userMessage": { "bar": "|", "barFg": "#FF00FF" },
                "agentHeader": { "mark": ">>", "rule": "=" },
                "answer": { "lead": "*" },
                "reasoning": { "fg": "accent", "italic": false },
                "header": { "show": false },
                "toast": { "mark": "#" } } } }
            """);
        var result = LoadEnabled();
        Assert.Empty(result.Problems);
        Assert.Equal(["userMessage", "agentHeader", "answer", "reasoning", "header", "toast"], Assert.Single(result.Extensions).TuiRegions);

        Assert.StartsWith("[#FF00FF]|[/] ", TuiComponents.UserEcho("hi")[1]);
        Assert.StartsWith("  >> Agent =", Plain(TuiComponents.TurnHeader("Agent", 30)[1]));
        Assert.Equal("  * ", Plain(TuiComponents.AnswerLead));
        Assert.Equal(Theme.Default.Accent, TuiComponents.ReasoningStyle);
        Assert.Empty(TuiComponents.SessionHeader("a", "m", "p"));
        Assert.StartsWith("  # copied", Plain(TuiComponents.Toast("copied", 40)));

        ExtensionRegistry.Load(null);
        Assert.StartsWith("\u258e ", Plain(TuiComponents.UserEcho("hi")[1]));
        Assert.Equal("grey italic", TuiComponents.ReasoningStyle);
        Assert.Equal(2, TuiComponents.SessionHeader("a", "m", "p").Count);
    }

    [Fact]
    public void RoleColors_FollowTheActiveTheme_LiteralsDoNot()
    {
        AddExtension("roles", """{ "tui": { "regions": { "notice": { "ok": "accent", "error": "#123456" } } } }""");
        LoadEnabled();
        Assert.Equal(Theme.Default.Accent, RegionStyle.Color("notice.ok"));
        Theme.Set(Theme.Presets.First(t => t.Name == "nord"));
        Assert.Equal(Theme.Active.Accent, RegionStyle.Color("notice.ok"));
        Assert.NotEqual(Theme.Default.Accent, Theme.Active.Accent);
        Assert.Equal("#123456", RegionStyle.Color("notice.error"));
    }

    [Theory]
    [InlineData("""{ "bogus": { "fg": "accent" } }""", "unknown region 'bogus'")]
    [InlineData("""{ "notice": { "glow": "accent" } }""", "'notice.glow' is not a property of region 'notice'")]
    [InlineData("""{ "notice": { "ok": "orange1" } }""", "'notice.ok' must be a theme role")]
    [InlineData("""{ "notice": { "ok": 5 } }""", "'notice.ok' must be a theme role")]
    [InlineData("""{ "userMessage": { "bar": "||" } }""", "'userMessage.bar' must be 1 terminal cell wide (got 2)")]
    [InlineData("""{ "notice": { "okMark": "\u6f22\u5b57" } }""", "'notice.okMark' must be 1-2 terminal cells wide (got 4)")]
    [InlineData("""{ "header": { "show": "no" } }""", "'header.show' must be true or false")]
    public void BadKey_SkipsTheWholeTuiBlock_NamingTheKey(string regions, string expected)
    {
        AddExtension("bad", "{ \"themes\": [{ \"name\": \"badtheme\" }], \"tui\": { \"regions\": " + regions
            + ", \"toast\": { \"mark\": \"#\" } } }");
        var result = LoadEnabled();
        var problem = Assert.Single(result.Problems);
        Assert.Contains(expected, problem);
        Assert.EndsWith("tui block skipped.", problem);
        Assert.Empty(Assert.Single(result.Extensions).TuiRegions);
        Assert.Equal(["badtheme"], result.Extensions[0].Themes);   // the rest of the extension still loads
        Assert.Equal("\u25c6", RegionStyle.PlainGlyph("toast.mark"));   // valid keys of a bad block do not apply
    }

    [Fact]
    public void LaterNameWinsPerProperty_AndRecordsOneConflictPerRegion()
    {
        AddExtension("alpha", """{ "tui": { "regions": { "notice": { "okMark": "+", "warnMark": "?" }, "toast": { "mark": "#" } } } }""");
        AddExtension("beta", """{ "tui": { "regions": { "notice": { "okMark": "*", "error": "#FF0000" } } } }""");
        var result = LoadEnabled();
        Assert.Equal("*", RegionStyle.PlainGlyph("notice.okMark"));
        Assert.Equal("?", RegionStyle.PlainGlyph("notice.warnMark"));
        Assert.Equal("#", RegionStyle.PlainGlyph("toast.mark"));
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(new ExtensionRegistry.Conflict("tui region 'notice'", "beta", "alpha"), conflict);
        Assert.Contains("tui styles: notice", ExtensionCatalog.Build(result, new ExtensionsConfig(), result.Problems, [], out _).First(i => i.Name == "beta").Contributes);
    }

    [Theory]
    [InlineData("purple")]
    [InlineData("orange1")]
    public void ThemeColor_TheTuiCannotDraw_IsRejected(string color)
    {
        // Spectre parses these names but the TUI markup resolver does not, so they used to validate and then render unstyled.
        Assert.Null(ExtensionRegistry.BuildTheme(new() { ["name"] = "x", ["accent"] = color }, out var error));
        Assert.Contains($"'{color}' is not a valid color", error);
        Assert.NotNull(ExtensionRegistry.BuildTheme(new() { ["name"] = "x", ["accent"] = "grey" }, out _));
        Assert.NotNull(ExtensionRegistry.BuildTheme(new() { ["name"] = "x", ["accent"] = "#A1B2C3" }, out _));
    }

    [Fact]
    public void CustomLeadGlyphs_AreStrippedFromCopiedText()
    {
        AddExtension("glyphs", """{ "tui": { "regions": { "userMessage": { "bar": "\u2551" }, "notice": { "okMark": "\u2605" } } } }""");
        LoadEnabled();
        Assert.Equal("hello", TuiComponents.ClipboardText(Plain(TuiComponents.UserEcho("hello")[1])));
        Assert.Equal("done", TuiComponents.ClipboardText("  \u2605 done"));
        ExtensionRegistry.Load(null);
        Assert.Equal("\u2605 done", TuiComponents.ClipboardText("  \u2605 done"));
    }
}
