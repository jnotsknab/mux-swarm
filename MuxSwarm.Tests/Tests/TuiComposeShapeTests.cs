using System.Reflection;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>compose.shape: each shape frames the input without breaking width, caret or click-to-caret.</summary>
[Collection("ConsoleState")]
public sealed class TuiComposeShapeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-shape-tests-" + Guid.NewGuid().ToString("N"));
    public TuiComposeShapeTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 60;
        public int Height { get; set; } = 24;
        public void Write(string text) { }
        public void Flush() { }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private void Style(string compose)
    {
        var dir = Path.Combine(_root, "shape");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": { \"compose\": " + compose + " } } }");
        Assert.Empty(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }).Problems);
    }

    private static (TuiDriver D, LineEditor Ed) AtPrompt(string draft)
    {
        var d = new TuiDriver(new Terminal(), frameEngine: true);
        d.SetMouseTrackingPreset("buttons");
        var ed = (LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(d)!;
        ed.InsertText(draft);
        typeof(TuiDriver).GetField("_inInput", PrivateInstance)!.SetValue(d, true);
        return (d, ed);
    }

    private static List<string> Pinned(TuiDriver d) => d.BuildLiveFrame(d.Width).Select(TuiMarkup.Plain).ToList();

    [Theory]
    [InlineData("rule", "\u2500", null)]
    [InlineData("bracket", "\u2500", "\u2500")]
    [InlineData("box", "\u256d", "\u2570")]
    [InlineData("bubble", null, null)]
    [InlineData("rail", null, null)]
    [InlineData("bare", null, null)]
    public void EachShape_FramesTheInput_WithinTheWidth(string shape, string? top, string? bottom)
    {
        Style($$"""{ "shape": "{{shape}}" }""");
        var (d, _) = AtPrompt("hello world");
        var lines = Pinned(d);
        int input = lines.FindIndex(l => l.Contains("hello world"));
        Assert.True(input >= 0, string.Join("\n", lines));
        if (top is not null) Assert.StartsWith(top, lines[input - 1].TrimStart());
        if (bottom is not null) Assert.StartsWith(bottom, lines[input + 1].TrimStart());
        Assert.All(lines, l => Assert.True(TuiMarkup.Width(l) <= d.Width, $"overflow: '{l}'"));
        // The stock input rule is not drawn on top of the shape's own chrome.
        Assert.Equal(1, lines.Count(l => l.Contains("hello world")));
    }

    [Theory]
    [InlineData("box")]
    [InlineData("bubble")]
    [InlineData("rail")]
    [InlineData("bracket")]
    public void ClickToCaret_AccountsForTheShapesLeftChrome(string shape)
    {
        Style($$"""{ "shape": "{{shape}}", "padX": 2 }""");
        var (d, ed) = AtPrompt("hello world");
        d.ComposeFrameRows();
        var map = d.FrameHitMap!;
        int row = -1; HitRegion region = default;
        for (int r = 1; r <= 24 && row < 0; r++)
            if (map.TryHit(r, 8, out var hit, out _, out _) && hit.Kind == MouseTargetKind.ComposeArea) { row = r; region = hit; }
        Assert.True(row > 0);
        int left = ComposeShape.SideCols(shape).Left - ComposeShape.DroppedLead(shape);
        int col = region.Left + left + 4 + 5;   // shape chrome (replacing the 2-col lead) + "  › " lead + buffer offset 5
        d.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, col, row, false));
        d.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, col, row, true));
        Assert.Equal(5, ed.Cursor);
    }

    [Fact]
    public void Box_WrapsLongInput_InsideTheBorder_AndKeepsTheCaretVisible()
    {
        Style("""{ "shape": "box", "border": "double", "padY": 1 }""");
        var (d, _) = AtPrompt(new string('x', 150));
        var lines = Pinned(d);
        int top = lines.FindIndex(l => l.StartsWith("\u2554"));
        int bottom = lines.FindIndex(l => l.StartsWith("\u255a"));
        Assert.True(top >= 0 && bottom > top + 3, string.Join("\n", lines));
        for (int i = top + 1; i < bottom; i++)
        {
            Assert.StartsWith("\u2551", lines[i]);
            Assert.EndsWith("\u2551", lines[i]);
            Assert.Equal(d.Width, TuiMarkup.Width(lines[i]));
        }
        Assert.Equal(new string(' ', d.Width - 2), lines[top + 1][1..^1]);   // padY blank row
    }

    [Fact]
    public void Classic_IsTheDefault_AndUnchanged()
    {
        var (d0, _) = AtPrompt("hello");
        var before = d0.BuildLiveFrame(60);
        Style("""{ "shape": "classic" }""");
        var (d, _) = AtPrompt("hello");
        Assert.Equal(before, d.BuildLiveFrame(60));
    }

    [Theory]
    [InlineData("""{ "shape": "pill" }""", "'compose.shape' must be one of classic, rule, bracket, box, bubble, rail, bare")]
    [InlineData("""{ "border": "dotted" }""", "'compose.border' must be one of rounded, square, double, thick, ascii")]
    [InlineData("""{ "padX": 4 }""", "'compose.padX' must be a whole number from 0 to 3")]
    public void ShapeValues_AreValidated(string compose, string expected)
    {
        var dir = Path.Combine(_root, "bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": { \"compose\": " + compose + " } } }");
        Assert.Contains(expected, Assert.Single(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }).Problems));
    }
}
