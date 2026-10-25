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
    public void UserMessageBubble_OutlinesTheMessage_WithinTheWidth()
    {
        AddExtension("bub", """{ "tui": { "regions": { "userMessage": { "style": "bubble" } } } }""");
        Assert.Empty(LoadEnabled().Problems);
        var rows = TuiComponents.UserEcho("hello there, this wraps at a narrow width", 24).Select(Plain).ToList();
        Assert.Equal("", rows[0]);
        Assert.StartsWith("  \u256d", rows[1]);
        Assert.StartsWith("  \u2570", rows[^1]);
        Assert.All(rows.Skip(2).SkipLast(1), r => { Assert.StartsWith("  \u2502 ", r); Assert.EndsWith(" \u2502", r); });
        Assert.Single(rows.Select(r => TuiMarkup.Width(r)).Skip(1).Distinct());   // every outline row the same width
        Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= 24, r));
        Assert.Contains("hello there", string.Join(" ", rows));
    }

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

    [Fact]
    public void Cards_Footer_Input_TakeTheirRegionStyles()
    {
        AddExtension("cards", """
            { "tui": { "regions": {
                "toolCard": { "dot": "+", "ok": "Y", "error": "N", "result": ">", "rail": "|", "labelFg": "#112233" },
                "diff": { "rail": ":", "addFg": "#00FF00" },
                "delegation": { "arrow": ">", "branch": "`", "mark": "*" },
                "thinking": { "fg": "#445566" },
                "footer": { "separator": "//", "ruleShow": false },
                "input": { "prompt": ">>", "continuation": ":", "placeholder": "say it", "cursorBg": "#FF0000" } } } }
            """);
        Assert.Empty(LoadEnabled().Problems);
        Assert.StartsWith("  + Reading", Plain(TuiComponents.ToolCall("Filesystem_read_text_file", null)[0]));
        Assert.Contains("[#112233]", TuiComponents.ToolCall("Filesystem_read_text_file", null)[0]);
        Assert.StartsWith("  N shell", Plain(TuiComponents.ToolCallResultMerged("shell", null, "boom", true, false)[0]));
        Assert.Contains("  > ok", Plain(TuiComponents.ToolCallResultMerged("shell", null, "ok", false, false)[0]));
        Assert.StartsWith("  | Y read", Plain(TuiComponents.ToolResultPanel("read", "x", false, 40)[0]));
        Assert.All(TuiComponents.Diff("f", "@@ -1 +1 @@\n-a\n+b", 40).Skip(1).SkipLast(1), r => Assert.StartsWith("  :", Plain(r)));
        Assert.Contains("[#00FF00", string.Join("", TuiComponents.Diff("f", "@@ -1 +1 @@\n+b", 40)));
        Assert.Contains("> Worker", Plain(TuiComponents.DelegationSummary("Lead", "Worker", "t")));
        Assert.StartsWith("    ` t", Plain(TuiComponents.Delegation("Lead", "Worker", "t", 10)[1]));
        Assert.StartsWith("  * Worker", Plain(TuiComponents.SubAgentCollapsed("Worker", null, 0, 0, "#123456")));
        Assert.Contains("[#445566 italic]", TuiComponents.ThinkingLine("Pondering", 0));
        Assert.Null(TuiComponents.FooterRule(40));

        // A 2-cell separator moves the clickable badge columns with it.
        var badges = new List<(int Col, int Width, string Id)>();
        string line = TuiComponents.Footer(5, 100, false, false, false, false, "low", false, 0, 0, 0, null, false, null, null, 0, "m1", 120, -1, null, badges);
        string plain = Plain(line);
        Assert.Contains(" // ", plain);
        foreach (var (col, w, id) in badges)
            Assert.Equal(id == TuiComponents.BadgeModel ? "m1" : "\u25d0 low", plain.Substring(col, w));

        var meta = new List<TuiComponents.InputRowMeta>();
        var rows = TuiComponents.InputRowsWithCursor("", 0, 40, false, meta);
        Assert.Equal("  >>  say it", Plain(rows[0]));
        Assert.Equal(5, meta[0].LeadCols);
        Assert.Contains("on #FF0000", rows[0]);
        rows = TuiComponents.InputRowsWithCursor("first\nsecond", 9, 40, false, meta);
        Assert.StartsWith("  : sec", Plain(rows[1]));
        Assert.Equal([false, true], meta.Select(m => m.Caret));
    }

    [Fact]
    public void ComposeCaretRow_IsFoundFromMetadata_WhateverTheCursorColour()
    {
        // The compose scroller used to search for the literal "[black on #E0E0E0]" cursor markup.
        AddExtension("cursor", """{ "tui": { "regions": { "input": { "cursorFg": "white", "cursorBg": "#123456" } } } }""");
        LoadEnabled();
        var meta = new List<TuiComponents.InputRowMeta>();
        var rows = TuiComponents.InputRowsWithCursor(string.Join("\n", Enumerable.Range(0, 12).Select(i => "line" + i)), 3, 40, false, meta);
        Assert.Equal(12, rows.Count);
        Assert.Equal(0, meta.FindIndex(m => m.Caret));
        Assert.DoesNotContain("#E0E0E0", string.Join("", rows));
    }

    [Fact]
    public void AsciiGlyphSet_ReplacesBoxDrawing_AndRegionKeysWinOverIt()
    {
        AddExtension("plain", """{ "tui": { "glyphs": "ascii", "regions": { "notice": { "okMark": "OK" } } } }""");
        Assert.Empty(LoadEnabled().Problems);
        Assert.Equal("OK", RegionStyle.PlainGlyph("notice.okMark"));
        Assert.Equal("--------", Plain(TuiComponents.FullRule(8)));
        string all = string.Join("\n", new[]
        {
            Plain(TuiComponents.TurnHeader("Agent", 20)[1]), Plain(TuiComponents.UserEcho("hi")[1]),
            string.Join("\n", TuiComponents.Diff("f", "@@ -1 +1 @@\n-a\n+b", 30).Skip(1).Select(Plain)),   // header keeps its "·" text separator
            string.Join("\n", TuiComponents.ToolResultPanel("t", "x", false, 30).Select(Plain)),
            string.Join("\n", TuiComponents.SlashPalette("/", [("/a", "x"), ("/b", "y")], 0).Select(Plain)),
            string.Join("\n", TuiComponents.InputRowsWithCursor("one\ntwo", 0, 30).Select(Plain)),
        });
        Assert.All(all.Replace("\u2212", "-"), c => Assert.True(c < 128, $"non-ASCII '{c}' (U+{(int)c:X4})"));

        AddExtension("zz-bad", """{ "tui": { "glyphs": "nerd" } }""");
        Assert.Contains(LoadEnabled().Problems, p => p.Contains("glyphs 'nerd' is not one of unicode, ascii"));
    }

    [Fact]
    public void Palette_Modal_PanelTabs_AndSplash_TakeTheirStyles()
    {
        AddExtension("lists", """
            { "tui": { "regions": {
                "palette": { "selected": ">", "titleMark": "#", "down": "v" },
                "modal": { "title": "?", "selected": ">", "checked": "x", "unchecked": "-" },
                "panel": { "rule": "=", "focusBg": "#00FF00" },
                "splash": { "art": ["  MY TOOL", "  v1"], "artFg": "#ABCDEF" } } } }
            """);
        Assert.Empty(LoadEnabled().Problems);
        var rows = TuiComponents.SkillsPreview("", Enumerable.Range(0, 14).Select(i => ($"s{i:00}", "d")).ToList(), 60, 0).Select(Plain).ToList();
        Assert.StartsWith("  # skills", rows[0]);
        Assert.StartsWith("  > s00", rows[1]);
        Assert.StartsWith("    v ", rows[^1]);
        var modal = new PromptModalView();
        modal.Open(PromptModalView.Kind.MultiSelect, "Pick", ["a", "b"], null, false);
        modal.ToggleChecked();
        var m = modal.Render(40, 20).Select(Plain).ToList();
        Assert.StartsWith("  ? input requested", m[0]);
        Assert.Contains("  > x a", m);
        Assert.Contains("    - b", m);
        Assert.Equal("==========", Plain(TuiComponents.FullRule(10)));
        var splash = Engine.MuxConsole.BuildFrameSplashLines("1.0", "", "", "hi", 120);
        Assert.Contains(splash, l => Plain(l).Contains("MY TOOL"));
        Assert.DoesNotContain(splash, l => Plain(l).Contains("\u2588\u2588\u2588"));
        Assert.Contains(splash, l => l.Contains("[#ABCDEF]MY[/]"));
    }

    [Theory]
    [InlineData("""{ "splash": { "art": "one line" } }""", "'splash.art' must be a list of 1-12 strings")]
    [InlineData("""{ "splash": { "art": [] } }""", "'splash.art' must be a list of 1-12 strings")]
    [InlineData("""{ "footer": { "separator": "\u2502\u2502\u2502\u2502" } }""", "'footer.separator' must be 1-3 terminal cells wide (got 4)")]
    [InlineData("""{ "input": { "placeholder": 3 } }""", "'input.placeholder' must be a string")]
    public void T3Values_AreValidated(string regions, string expected)
    {
        AddExtension("bad", "{ \"tui\": { \"regions\": " + regions + " } }");
        Assert.Contains(expected, Assert.Single(LoadEnabled().Problems));
    }

    private static TuiManifest User(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<TuiManifest>(json, MuxSwarm.Setup.Setup.CfgSerialOpts)!;

    [Fact]
    public void ConsoleTui_WinsOverExtensions_AndWorksWithExtensionsOff()
    {
        AddExtension("ext", """{ "tui": { "regions": { "notice": { "okMark": "+", "warnMark": "?" } } } }""");
        var user = User("""{ "regions": { "notice": { "okMark": "*" }, "toast": { "mark": "#" } } }""");
        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }, user);
        Assert.Equal("*", RegionStyle.PlainGlyph("notice.okMark"));   // user beats the extension
        Assert.Equal("?", RegionStyle.PlainGlyph("notice.warnMark"));  // extension keys the user did not set stay
        Assert.Equal("#", RegionStyle.PlainGlyph("toast.mark"));

        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = false }, user);
        Assert.Equal("*", RegionStyle.PlainGlyph("notice.okMark"));
        Assert.Equal("!", RegionStyle.PlainGlyph("notice.warnMark"));

        var bad = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = false }, User("""{ "regions": { "notice": { "okMark": 1 } } }"""));
        Assert.Contains("console.tui: tui 'notice.okMark'", Assert.Single(bad.Problems));
        Assert.Equal("\u2713", RegionStyle.PlainGlyph("notice.okMark"));
    }

    [Fact]
    public void ConsoleConfig_RoundTripsTheTuiBlock()
    {
        var cfg = System.Text.Json.JsonSerializer.Deserialize<ConsoleConfig>(
            """{ "theme": "nord", "tui": { "glyphs": "ascii", "regions": { "footer": { "separator": "|" } } } }""", MuxSwarm.Setup.Setup.CfgSerialOpts)!;
        Assert.Equal("ascii", cfg.Tui!.Glyphs);
        string json = System.Text.Json.JsonSerializer.Serialize(cfg, MuxSwarm.Setup.Setup.CfgSerialOpts);
        Assert.Contains("\"separator\": \"|\"", json);
        Assert.DoesNotContain("\"tui\"", System.Text.Json.JsonSerializer.Serialize(new ConsoleConfig(), MuxSwarm.Setup.Setup.CfgSerialOpts));
    }

    // The ember demo from DOCS.md (## Extensions, tui): every region restyled, loads without problems.
    private const string Ember = """{"name": "ember", "version": "0.1.0", "description": "Warm theme that restyles every TUI region (demo)", "themes": [{"name": "ember", "base": "gruvbox", "accent": "#FF8C42", "banner": "#FF8C42", "agent": "#F4C27A"}], "tui": {"regions": {"notice": {"okMark": "\u2714", "warnMark": "\u25b2", "errorMark": "\u2716", "info": "muted"}, "header": {"mark": "\u25c6", "accent": "accent"}, "userMessage": {"bar": "\u2503", "barFg": "#FF8C42"}, "agentHeader": {"mark": "\u25c6", "rule": "\u2504", "fg": "agent", "ruleFg": "border"}, "answer": {"lead": "\u2022", "leadFg": "accent"}, "reasoning": {"fg": "#8A7F72", "italic": true}, "toast": {"mark": "\u25c6", "markFg": "accent"}, "divider": {"glyph": "\u2503", "activeFg": "#FF8C42"}, "scrollbar": {"thumb": "\u2503", "track": "\u2502", "thumbFg": "#FF8C42"}, "toolCard": {"dot": "\u25c6", "ok": "\u2714", "error": "\u2716", "result": "\u2570", "rail": "\u2503", "labelFg": "#F4C27A", "railFg": "#5C4A3A"}, "diff": {"rail": "\u2503", "corner": "\u250f", "end": "\u2517", "line": "\u2501"}, "delegation": {"arrow": "\u21aa", "mark": "\u25c6", "done": "\u2714"}, "thinking": {"fg": "#FFB86B"}, "footer": {"separator": "\u2502", "separatorFg": "#5C4A3A", "rule": "\u2501", "ruleFg": "#5C4A3A"}, "input": {"prompt": "\u25b8", "promptFg": "#FF8C42", "continuation": "\u2503", "placeholder": "what are we building?", "cursorBg": "#FF8C42"}, "palette": {"selected": "\u25b8", "titleMark": "\u25c6", "selectedFg": "#FF8C42"}, "modal": {"title": "\u25c6", "selected": "\u25b8", "checked": "\u25c6", "unchecked": "\u25c7"}, "panel": {"rule": "\u2501", "ruleFg": "#5C4A3A", "focusBg": "#FF8C42"}, "splash": {"art": ["  \u2584\u2584\u2584 \u2584   \u2584 \u2584\u2584\u2584  \u2584\u2584\u2584 \u2584\u2584\u2584", "  \u2588\u2584\u2584 \u2588\u2580\u2584\u2580\u2588 \u2588\u2584\u2584\u2588 \u2588\u2584\u2584 \u2588\u2584\u2584\u2580", "  \u2588\u2584\u2584 \u2588   \u2588 \u2588\u2584\u2584\u2588 \u2588\u2584\u2584 \u2588  \u2588", "  ember  -  a warm Mux"], "artFg": "#FF8C42"}}}}""";

    [Fact]
    public void EmberDemo_StylesEveryRegion_WithoutProblems()
    {
        AddExtension("ember", Ember);
        var result = LoadEnabled();
        Assert.Empty(result.Problems);
        var regions = Assert.Single(result.Extensions).TuiRegions;
        // Every region of the colour/glyph round (later regions: compose layout, command-output block).
        Assert.Equal(RegionStyle.Regions.Where(r => r is not ("compose" or "block")).Order(), regions.Order());
        Assert.Contains("[#FF8C42]", TuiComponents.InputRowsWithCursor("", 0, 40)[0]);
    }
}
