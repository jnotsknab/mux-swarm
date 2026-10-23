using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Live-band layout seam: footer position (above / below / hidden), compose gap, and the
/// recorded line indexes staying valid when competing activity is trimmed off the top.</summary>
[Collection("ConsoleState")]
public sealed class TuiLayoutSeamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-layout-tests-" + Guid.NewGuid().ToString("N"));
    public TuiLayoutSeamTests() => Directory.CreateDirectory(_root);
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
    private static string Plain(string s) => TuiMarkup.Plain(s);

    private void Style(string regions)
    {
        var dir = Path.Combine(_root, "layout");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": " + regions + " } }");
        var result = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        Assert.Empty(result.Problems);
    }

    private static (TuiDriver Driver, LineEditor Editor) AtPrompt(string draft, int height = 24)
    {
        var d = new TuiDriver(new Terminal { Height = height }, frameEngine: true);
        d.SetMouseTrackingPreset("buttons");
        var ed = (LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(d)!;
        ed.InsertText(draft);
        typeof(TuiDriver).GetField("_inInput", PrivateInstance)!.SetValue(d, true);
        d.SetModel("prov/model-x");
        return (d, ed);
    }

    private static int FooterLine(TuiDriver d) => (int)typeof(TuiDriver).GetField("_footerLine", PrivateInstance)!.GetValue(d)!;
    private static List<(int Line, int MetaRow)> ComposeVisible(TuiDriver d) =>
        (List<(int Line, int MetaRow)>)typeof(TuiDriver).GetField("_composeVisible", PrivateInstance)!.GetValue(d)!;

    [Fact]
    public void FooterBelow_PutsTheStatusRowUnderTheInput_AndClicksStillHit()
    {
        Style("""{ "footer": { "position": "below" } }""");
        var (d, ed) = AtPrompt("hello world");
        var lines = d.BuildLiveFrame(d.Width).Select(Plain).ToList();
        int input = lines.FindIndex(l => l.Contains("hello world"));
        int footer = lines.FindIndex(l => l.Contains("model-x"));
        Assert.True(input >= 0 && footer > input, string.Join("\n", lines));
        Assert.Equal(footer, FooterLine(d));
        Assert.Equal(input, ComposeVisible(d)[0].Line);
        // Click-to-caret still lands on the input row.
        d.ComposeFrameRows();
        var map = d.FrameHitMap!;
        int row = -1; HitRegion region = default;
        for (int r = 1; r <= 24 && row < 0; r++)
            if (map.TryHit(r, 6, out var hit, out _, out _) && hit.Kind == MouseTargetKind.ComposeArea) { row = r; region = hit; }
        Assert.True(row > 0);
        d.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, region.Left + 4 + 5, row, false));
        d.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, region.Left + 4 + 5, row, true));
        Assert.Equal(5, ed.Cursor);
    }

    [Fact]
    public void FooterBelow_SurvivesReverseSearch()
    {
        Style("""{ "footer": { "position": "below" } }""");
        var (d, ed) = AtPrompt("");
        ed.Remember("prior command");
        ed.BeginReverseSearch();
        var lines = d.BuildLiveFrame(d.Width).Select(Plain).ToList();
        Assert.Contains(lines, l => l.Contains("model-x"));
    }

    [Fact]
    public void FooterHidden_DropsTheRowAndItsClickTargets()
    {
        Style("""{ "footer": { "position": "hidden" } }""");
        var (d, _) = AtPrompt("hi");
        Assert.DoesNotContain(d.BuildLiveFrame(d.Width).Select(Plain), l => l.Contains("model-x"));
        Assert.Equal(-1, FooterLine(d));
    }

    [Fact]
    public void ComposeGap_AddsBlankRowsAboveThePinnedZone()
    {
        var (d0, _) = AtPrompt("hi");
        int before = d0.BuildLiveFrame(80).Count;
        Style("""{ "compose": { "gap": 2 } }""");
        var (d, _) = AtPrompt("hi");
        var lines = d.BuildLiveFrame(80);
        Assert.Equal(before + 2, lines.Count);
        Assert.Equal(["", ""], lines.Take(2));
    }

    [Fact]
    public void TrimmedActivity_ShiftsRecordedLines_SoClicksStayOnTheirRows()
    {
        // A tall stream tail plus a paste card overflows a short viewport: the top is trimmed, and the
        // recorded footer/compose line indexes must move with it (they used to point at the wrong rows).
        var (d, ed) = AtPrompt("", height: 10);
        d.BeginStream();
        d.StreamChunk(string.Join(" ", Enumerable.Repeat("word", 400)));
        ed.InsertPaste(string.Join("\n", Enumerable.Range(0, 30).Select(i => "pasted " + i)));
        var lines = d.BuildLiveFrame(80);
        var plain = lines.Select(Plain).ToList();
        Assert.Contains("model-x", plain[FooterLine(d)]);
        var visible = ComposeVisible(d);
        Assert.NotEmpty(visible);
        Assert.All(visible, v => Assert.InRange(v.Line, 0, lines.Count - 1));
        Assert.Contains("[\u2261", plain[visible[0].Line]);   // the input row holds the paste card label
    }

    [Theory]
    [InlineData("""{ "footer": { "position": "left" } }""", "'footer.position' must be one of above, below, hidden")]
    [InlineData("""{ "compose": { "gap": 5 } }""", "'compose.gap' must be a whole number from 0 to 2")]
    [InlineData("""{ "compose": { "gap": "1" } }""", "'compose.gap' must be a whole number from 0 to 2")]
    public void LayoutValues_AreValidated(string regions, string expected)
    {
        var dir = Path.Combine(_root, "bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": " + regions + " } }");
        var result = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        Assert.Contains(expected, Assert.Single(result.Problems));
    }
}
