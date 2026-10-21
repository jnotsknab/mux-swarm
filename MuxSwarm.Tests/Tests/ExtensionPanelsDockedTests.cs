using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Docked extension panels (placement side / band / auto) in the TUI frame and inline renderers.
// ConsoleState: the panel registry and its seams are process-global.
[Collection("ConsoleState")]
public sealed class ExtensionPanelsDockedTests : IDisposable
{
    private readonly Func<string?> _surface = ExtensionPanels.Surface;
    private readonly Action<string, string, object> _notify = ExtensionPanels.Notify;
    private readonly List<string> _sent = [];

    public ExtensionPanelsDockedTests()
    {
        ExtensionPanels.Surface = () => null;
        ExtensionPanels.Notify = (ext, m, p) => { lock (_sent) _sent.Add(JsonSerializer.Serialize(p)); };
    }

    public void Dispose()
    {
        foreach (var p in ExtensionPanels.All()) ExtensionPanels.Close(p.Extension, p.Id, byUser: false);
        ExtensionPanels.Surface = _surface;
        ExtensionPanels.Notify = _notify;
    }

    private sealed class Term(int w, int h) : ITuiTerminal
    {
        public int Width { get; set; } = w;
        public int Height { get; set; } = h;
        public void Write(string text) { }
        public void Flush() { }
    }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
    private static string Plain(string ansi) => Regex.Replace(ansi, "\u001b\\[[0-9;?]*[A-Za-z]", "");
    private const string Tree = """
        { "type": "box", "children": [ { "type": "text", "text": "tokens 42" },
          { "type": "button", "key": "go", "label": "Go", "hotkey": "g" }, { "type": "input", "key": "q", "placeholder": "ask" } ] }
        """;

    private static TuiDriver Frame(int w, int h, out Term t)
    {
        t = new Term(w, h);
        var d = new TuiDriver(t, frameEngine: true);
        d.CommitLine("transcript line one");
        return d;
    }

    private static ConsoleKeyInfo K(ConsoleKey k, char c = '\0', bool shift = false) => new(c, k, shift, false, false);

    [Fact]
    public void Placement_IsValidated_AndAutoDependsOnWidth()
    {
        Assert.Contains("placement must be", ExtensionPanels.Open("x", "a", null, J(Tree), "dock"));
        Assert.Null(ExtensionPanels.Open("x", "a", null, J(Tree), "auto"));
        var p = ExtensionPanels.Get("x", "a")!;
        Assert.Equal("side", Frame(160, 30, out _).DockedPlacement(p));
        Assert.Equal("band", Frame(120, 30, out _).DockedPlacement(p));
        Assert.Equal("band", new TuiDriver(new Term(200, 30), frameEngine: false).DockedPlacement(p));   // inline: no side pane
        Assert.Equal("screen", Frame(160, 30, out _).DockedPlacement(p with { Placement = "screen" }));
        Assert.Equal("band", Frame(80, 30, out _).DockedPlacement(p with { Placement = "side" }));        // too narrow to split
    }

    [Fact]
    public void NoPanel_FrameIsUnchanged()
    {
        var before = Frame(150, 24, out _).ComposeFrameRows();
        ExtensionPanels.Open("x", "s", null, J(Tree), "screen");   // a screen panel never docks
        var after = Frame(150, 24, out _).ComposeFrameRows();
        Assert.Equal(before, after);
    }

    [Fact]
    public void SidePane_NarrowsTheTranscript_AndDrawsOnTheRight()
    {
        Assert.Null(ExtensionPanels.Open("x", "side", "Usage", J(Tree), "side", columns: 40));
        var d = Frame(150, 24, out _);
        var rows = d.ComposeFrameRows().Select(Plain).ToList();
        Assert.Equal(24, rows.Count);
        Assert.Equal(150 - 1 - 40, d.Width);   // transcript + live band lay out beside the pane
        Assert.All(rows, r => Assert.True(MuxSwarm.Engine.Tui.TuiMarkup.Width(r) <= 149, r));
        Assert.Contains(rows, r => r.Contains("Usage") && r.IndexOf("Usage", StringComparison.Ordinal) > 100);
        Assert.Contains(rows, r => r.Contains("tokens 42"));
        Assert.Contains(rows, r => r.Contains("transcript line one") && r.IndexOf("transcript", StringComparison.Ordinal) < 100);
        // Side width is capped at 45% and always leaves 60 transcript columns.
        ExtensionPanels.Update("x", "side", J(Tree));
        var p = ExtensionPanels.Get("x", "side")!;
        ExtensionPanels.Close("x", "side", false);
        ExtensionPanels.Open("x", "wide", null, J(Tree), "side", columns: 200);
        var d2 = Frame(150, 24, out _); d2.ComposeFrameRows();
        Assert.Equal(150 - 1 - 67, d2.Width);
    }

    [Fact]
    public void Band_SitsAboveTheFooter_InFrameAndInline()
    {
        ExtensionPanels.Open("x", "b", "Live", J(Tree), "band", rows: 3);
        foreach (var frame in new[] { true, false })
        {
            var d = new TuiDriver(new Term(100, 30), frameEngine: frame);
            var live = d.BuildLiveFrame(98).Select(MuxSwarm.Engine.Tui.TuiMarkup.Plain).ToList();
            int tabs = live.FindIndex(l => l.Contains("Live") && l.Contains("F6 focus"));
            Assert.True(tabs >= 0, string.Join("\n", live));
            Assert.Contains("tokens 42", live[tabs + 1]);
            d.ComposeFrameRows();
            Assert.Equal(frame ? 99 : 100, d.Width);   // the band never narrows anything
        }
    }

    [Fact]
    public void F6_FocusesTheDock_KeysDriveIt_EscReturnsToThePrompt()
    {
        ExtensionPanels.Open("x", "side", null, J(Tree), "side");
        var d = Frame(160, 24, out _);
        d.ComposeFrameRows();
        Assert.False(d.DockFocused);
        Assert.False(d.HandleDockKey(K(ConsoleKey.G, 'g')));   // not focused: keys belong to the prompt
        Assert.True(d.CycleDockFocus(back: false));
        Assert.True(d.DockFocused);
        Assert.True(d.HandleDockKey(K(ConsoleKey.G, 'g')));    // the hotkey presses Go
        Assert.True(d.HandleDockKey(K(ConsoleKey.Escape)));    // Esc hands focus back, the panel stays
        Assert.False(d.DockFocused);
        Assert.Single(ExtensionPanels.All());
        lock (_sent) Assert.Contains(_sent, s => s.Contains("\"key\":\"go\"") && s.Contains("\"type\":\"press\""));
        // F6 past the last docked panel returns to the prompt; with none open F6 is not consumed.
        d.CycleDockFocus(false); d.CycleDockFocus(false);
        Assert.False(d.DockFocused);
        ExtensionPanels.Close("x", "side", false);
        d.ComposeFrameRows();
        Assert.False(d.CycleDockFocus(false));
    }

    [Fact]
    public void EnteringADockedPanel_StartsOnAButton_SoHotkeysWorkAfterTyping()
    {
        ExtensionPanels.Open("x", "side", null, J("""{ "type": "box", "children": [ { "type": "input", "key": "q" }, { "type": "button", "key": "go", "label": "Go", "hotkey": "g" } ] }"""), "side");
        var d = Frame(160, 24, out _);
        d.ComposeFrameRows();
        d.CycleDockFocus(false);
        d.HandleDockKey(K(ConsoleKey.Tab, shift: true));   // to the input, type + submit
        foreach (char c in "hi") d.HandleDockKey(K(ConsoleKey.NoName, c));
        d.HandleDockKey(K(ConsoleKey.Enter, '\r'));
        d.HandleDockKey(K(ConsoleKey.Escape));
        d.CycleDockFocus(false);                          // back in: the button, not the input
        d.HandleDockKey(K(ConsoleKey.G, 'g'));
        lock (_sent)
        {
            Assert.Contains(_sent, s => s.Contains("\"type\":\"submit\"") && s.Contains("\"value\":\"hi\""));
            Assert.Contains(_sent, s => s.Contains("\"key\":\"go\"") && s.Contains("\"type\":\"press\""));
        }
    }

    [Fact]
    public void SideClicks_HitTheDockedControls()
    {
        ExtensionPanels.Open("x", "side", null, J(Tree), "side", columns: 40);
        var d = Frame(150, 24, out _);
        var rows = d.ComposeFrameRows().Select(Plain).ToList();
        var hits = (MouseHitMap)typeof(TuiDriver).GetField("_frameHits", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(d)!;
        int row = rows.FindIndex(r => r.Contains("g: Go")) + 1;
        int col = rows[row - 1].IndexOf("g: Go", StringComparison.Ordinal) + 1;
        Assert.True(hits.TryHit(row, col, out var hit, out _, out _));
        Assert.Equal(MouseTargetKind.DockedPanel, hit.Kind);
        typeof(TuiDriver).GetMethod("ClickDocked", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, [hit, 0]);
        Assert.True(d.DockFocused);
        lock (_sent) Assert.Contains(_sent, s => s.Contains("\"type\":\"press\""));
        // The transcript backdrop stops at the pane: a click in the pane never starts a transcript selection.
        Assert.True(hits.TryHit(2, 10, out var back, out _, out _));
        Assert.Equal(MouseTargetKind.ExpandedPanel, back.Kind);
        Assert.False(hits.TryHit(2, 140, out var none, out _, out _) && none.Kind == MouseTargetKind.ExpandedPanel);
    }

    [Fact]
    public void Resize_AcrossTheAutoThreshold_MovesThePanel()
    {
        ExtensionPanels.Open("x", "a", "Auto", J(Tree), "auto");
        var d = Frame(160, 24, out var t);
        d.ComposeFrameRows();
        Assert.True(d.Width < 159);
        t.Width = 120;
        var rows = d.ComposeFrameRows().Select(Plain).ToList();
        Assert.Equal(119, d.Width);
        Assert.Contains(rows, r => r.Contains("Auto") && r.Contains("F6 focus"));
    }
}
