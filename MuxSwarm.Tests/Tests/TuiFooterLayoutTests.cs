using System.Reflection;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Footer layout: left/right groups, separator styles, background strip, rule fill, and the footer
/// sitting in the compose box's top edge - with clickable chips staying on their characters.</summary>
[Collection("ConsoleState")]
public sealed class TuiFooterLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-footer-tests-" + Guid.NewGuid().ToString("N"));
    public TuiFooterLayoutTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 80;
        public int Height { get; set; } = 24;
        public void Write(string text) { }
        public void Flush() { }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private void Load(string manifest)
    {
        var dir = Path.Combine(_root, "f");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), manifest);
        Assert.Empty(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }).Problems);
    }

    private const string Split = "\"footer\": { \"template\": \"{mode} {context} || {model} {effort}\" }";

    private static TuiDriver AtPrompt()
    {
        var d = new TuiDriver(new Terminal(), frameEngine: true);
        d.SetMouseTrackingPreset("buttons");
        ((LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(d)!).InsertText("hi");
        typeof(TuiDriver).GetField("_inInput", PrivateInstance)!.SetValue(d, true);
        d.SetModel("prov/model-x");
        d.SetEffort("high");
        d.SetFooter(45000, 200000, false, true, false);
        return d;
    }

    private static string Footer(int width, List<(int Col, int Width, string Id)>? badges = null) =>
        TuiComponents.Footer(5, 100, false, true, false, false, "high", false, 0, 0, 0, null, false, null, null, 0, "prov/model-x", width, -1, null, badges);

    [Fact]
    public void SplitTemplate_PushesTheRightGroupToTheEdge_AndChipsStayClickable()
    {
        Load("{ " + Split + " }");
        var badges = new List<(int Col, int Width, string Id)>();
        string plain = TuiMarkup.Plain(Footer(80, badges));
        Assert.Equal(80, TuiMarkup.Width(plain));
        Assert.EndsWith("model-x \u00b7 \u25d0 high ", plain);
        Assert.StartsWith("  ultra", plain);
        foreach (var (col, w, id) in badges)
            Assert.Equal(id == TuiComponents.BadgeModel ? "model-x" : "\u25d0 high", plain.Substring(col, w));
    }

    [Theory]
    [InlineData("pipe", " | ")]
    [InlineData("ascii", " | ")]
    [InlineData("powerline", " \ue0b1 ")]
    public void Styles_ChangeTheSeparator(string style, string joint)
    {
        Load("{ \"tui\": { \"regions\": { \"footer\": { \"style\": \"" + style + "\" } } } }");
        Assert.Contains("ultra" + joint, TuiMarkup.Plain(Footer(80)));
    }

    [Fact]
    public void Strip_FillsTheWholeRowWithTheFooterBackground()
    {
        Load("{ \"tui\": { \"regions\": { \"footer\": { \"strip\": true, \"bg\": \"#102030\" } } } }");
        string line = Footer(60);
        Assert.StartsWith("[on #102030]", line);
        Assert.Equal(60, TuiMarkup.MarkupWidth(line));
    }

    [Fact]
    public void RuleFill_DrawsALineBetweenTheGroups()
    {
        Load("{ " + Split + ", \"tui\": { \"regions\": { \"footer\": { \"fill\": \"rule\" } } } }");
        Assert.Contains("\u2500\u2500\u2500", TuiMarkup.Plain(Footer(80)));
    }

    [Theory]
    [InlineData("box", "\u256d\u2500 ultra")]
    [InlineData("bracket", "\u2500\u2500 ultra")]
    [InlineData("rule", "\u2500\u2500 ultra")]
    public void BorderPosition_PutsTheFooterInTheTopEdge_AndBadgeClicksHitTheChip(string shape, string start)
    {
        Load("{ " + Split + ", \"tui\": { \"regions\": { \"compose\": { \"shape\": \"" + shape + "\" }, \"footer\": { \"position\": \"border\" } } } }");
        var d = AtPrompt();
        var lines = d.BuildLiveFrame(d.Width).Select(TuiMarkup.Plain).ToList();
        int edge = lines.FindIndex(l => l.StartsWith(start));
        Assert.True(edge >= 0, string.Join("\n", lines));
        Assert.Equal(d.Width, TuiMarkup.Width(lines[edge]));
        Assert.Equal(1, lines.Count(l => l.Contains("model-x")));   // not also drawn above the box
        // The model chip's hit region covers the chip's characters in the edge row.
        var rows = d.ComposeFrameRows();
        var map = d.FrameHitMap!;
        int physRow = rows.FindIndex(r => TuiMarkup.Plain(TuiMarkup.StripAnsi(r)).Contains("model-x")) + 1;
        int col = TuiMarkup.Plain(TuiMarkup.StripAnsi(rows[physRow - 1])).IndexOf("model-x", StringComparison.Ordinal) + 1;
        Assert.True(map.TryHit(physRow, col + 2, out var hit, out _, out _), "no hit on the model chip");
        Assert.Equal(MouseTargetKind.FooterBadge, hit.Kind);
        Assert.Equal(TuiComponents.BadgeModel, hit.Tag);
    }

    [Fact]
    public void BorderPosition_Bubble_PutsTheFooterInsideTheOutline_WithClickableChips()
    {
        Load("{ " + Split + ", \"tui\": { \"regions\": { \"compose\": { \"shape\": \"bubble\" }, \"footer\": { \"position\": \"border\" } } } }");
        var d = AtPrompt();
        var lines = d.BuildLiveFrame(80).Select(TuiMarkup.Plain).ToList();
        int top = lines.FindIndex(l => l.StartsWith("\u256d"));
        int input = lines.FindIndex(l => l.Contains("\u203a hi"));
        int footer = lines.FindIndex(l => l.Contains("model-x"));
        int bottom = lines.FindIndex(l => l.StartsWith("\u2570"));
        Assert.True(top < input && input < footer && footer == bottom - 1, string.Join("\n", lines));
        Assert.StartsWith("\u2502", lines[footer]);
        Assert.EndsWith("\u2502", lines[footer]);
        Assert.Equal(80, TuiMarkup.Width(lines[footer]));
        Assert.EndsWith("high", lines[footer].TrimEnd('\u2502').TrimEnd());   // right group at the inner right edge

        // The model chip is clickable where it is drawn.
        var rows = d.ComposeFrameRows();
        int physRow = rows.FindIndex(r => TuiMarkup.Plain(TuiMarkup.StripAnsi(r)).Contains("model-x")) + 1;
        int col = TuiMarkup.Plain(TuiMarkup.StripAnsi(rows[physRow - 1])).IndexOf("model-x", StringComparison.Ordinal) + 1;
        Assert.True(d.FrameHitMap!.TryHit(physRow, col + 2, out var hit, out _, out _), "no hit on the model chip");
        Assert.Equal(MouseTargetKind.FooterBadge, hit.Kind);
        Assert.Equal(TuiComponents.BadgeModel, hit.Tag);
    }

    [Fact]
    public void BorderPosition_WithAShapeWithoutAnEdge_FallsBackToAbove()
    {
        Load("{ \"tui\": { \"regions\": { \"compose\": { \"shape\": \"smooth\" }, \"footer\": { \"position\": \"border\" } } } }");
        var lines = AtPrompt().BuildLiveFrame(80).Select(TuiMarkup.Plain).ToList();
        int footer = lines.FindIndex(l => l.Contains("model-x"));
        int input = lines.FindIndex(l => l.Contains("\u203a hi"));
        Assert.True(footer >= 0 && footer < input);
    }

    [Fact]
    public void DefaultFooter_IsUnchanged()
    {
        var before = Footer(80);
        Load("{ \"tui\": { \"regions\": { \"footer\": { \"style\": \"dot\", \"strip\": false } } } }");
        Assert.Equal(before, Footer(80));
    }
}
