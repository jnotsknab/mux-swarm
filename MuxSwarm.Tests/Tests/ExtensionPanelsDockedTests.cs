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
        typeof(TuiDriver).GetMethod("ClickDocked", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, [hit]);
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

    // Drive the real press/drag/release sinks (same path the mouse handler dispatches to).
    private static void Mouse(TuiDriver d, MouseEventKind kind, int row, int col) =>
        typeof(TuiDriver).GetMethod(kind switch { MouseEventKind.Press => "OnMousePress", MouseEventKind.Drag => "OnMouseDrag", _ => "OnMouseRelease" },
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, [new MouseEvent(kind, 0, row, col, 0)]);

    private static void Click(TuiDriver d, int row, int col) { Mouse(d, MouseEventKind.Press, row, col); Mouse(d, MouseEventKind.Release, row, col); }

    private static MouseHitMap Hits(TuiDriver d) => d.FrameHitMap!;

    [Fact]
    public void SidePane_TheScrollbarIsTheDivider_OnTheTranscriptEdge()
    {
        ExtensionPanels.Open("x", "side", "Usage", J(Tree), "side", columns: 40);
        var d = Frame(150, 24, out _);
        for (int i = 0; i < 80; i++) d.CommitLine("history " + i);   // overflow: the scrollbar shows
        var rows = d.ComposeFrameRows().Select(Plain).ToList();
        int edge = d.Width + 1;                                      // 110: between transcript (1-109) and pane (111-150)
        Assert.Equal(110, edge);
        Assert.All(rows, r => Assert.DoesNotContain('\u2502', r[..Math.Min(r.Length, 150)].Skip(110)));   // no rule drawn inside the pane any more
        // The rail regions sit on the divider, not on the screen's last column.
        Assert.True(Hits(d).TryHit(3, edge, out var rail, out _, out _));
        Assert.True(rail.Kind is MouseTargetKind.ScrollBarRail or MouseTargetKind.ScrollBarThumb, rail.Kind.ToString());
        Assert.True(Hits(d).TryHit(3, 150, out var right, out _, out _));
        Assert.Equal(MouseTargetKind.DockedPanel, right.Kind);
        // The pane starts right of the divider.
        Assert.Contains(rows, r => r.IndexOf("Usage", StringComparison.Ordinal) is >= 110 and < 115);
    }

    [Fact]
    public void FramePresent_PutsTheGutterAtTheRequestedColumn()
    {
        var out_ = new System.Text.StringBuilder();
        var term = new RecTerm(80, 3, out_);
        var fr = new FrameRenderer(term);
        fr.Present(["a", "b", "c"], ["|", "|", "|"], 50);
        Assert.Contains("\u001b[1;50H", out_.ToString());
        Assert.DoesNotContain("\u001b[1;80H", out_.ToString());
        out_.Clear();
        fr.Present(["a", "b", "c"], ["|", "|", "|"]);               // moving the gutter forces a full redraw at the last column
        Assert.Contains("\u001b[1;80H", out_.ToString());
    }

    private sealed class RecTerm(int w, int h, System.Text.StringBuilder sink) : ITuiTerminal
    {
        public int Width { get; set; } = w;
        public int Height { get; set; } = h;
        public void Write(string text) => sink.Append(text);
        public void Flush() { }
    }

    [Fact]
    public void Clicks_FocusAPanelAnywhere_SwitchTabs_AndClickAwayReturnsToThePrompt()
    {
        ExtensionPanels.Open("x", "one", "One", J(Tree), "side", columns: 40);
        ExtensionPanels.Open("x", "two", "Two", J(Tree), "side", columns: 40);
        var d = Frame(150, 24, out _);
        var rows = d.ComposeFrameRows().Select(Plain).ToList();
        // A click on blank pane space focuses the panel (no Esc / F6 needed) without pressing anything.
        Click(d, 20, 140);
        Assert.True(d.DockFocused);
        lock (_sent) Assert.Empty(_sent);
        // A click on the other tab switches the pane to it.
        int tabCol = rows[0].IndexOf("Two", StringComparison.Ordinal) + 1;
        Click(d, 1, tabCol);
        rows = d.ComposeFrameRows().Select(Plain).ToList();
        Assert.Contains("Two", rows[0]);
        Assert.Equal(("x", "two"), typeof(TuiDriver).GetField("_dockFocus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(d));
        // A click on the transcript gives the keyboard back to the prompt; the panels stay open.
        Click(d, 5, 10);
        Assert.False(d.DockFocused);
        Assert.Equal(2, ExtensionPanels.All().Count);
    }

    [Fact]
    public void DraggingTheDivider_ResizesTheSidePane_WithinLimits()
    {
        ExtensionPanels.Open("x", "side", null, J(Tree), "side", columns: 40);
        var d = Frame(150, 24, out _);
        d.ComposeFrameRows();
        int edge = d.Width + 1;
        Mouse(d, MouseEventKind.Press, 22, edge);                   // below the (absent) scrollbar track: the sash
        Mouse(d, MouseEventKind.Drag, 22, edge - 20);
        d.ComposeFrameRows();
        Assert.Equal(150 - 1 - 60, d.Width);                        // pane 60 wide
        Mouse(d, MouseEventKind.Drag, 22, 5);                       // clamped: the transcript keeps 60 columns, pane <= 45%
        d.ComposeFrameRows();
        Assert.Equal(150 - 1 - 67, d.Width);
        Mouse(d, MouseEventKind.Drag, 22, 149);                     // and the pane keeps a minimum width
        Mouse(d, MouseEventKind.Release, 22, 149);
        d.ComposeFrameRows();
        Assert.Equal(150 - 1 - 24, d.Width);
    }

    [Fact]
    public void Band_HasARuleOnTop_NoGapRows_AndDraggingTheRuleResizesIt()
    {
        const string Gappy = """{ "type": "box", "gap": 1, "children": [ { "type": "text", "text": "line A" }, { "type": "text", "text": "line B" }, { "type": "text", "text": "line C" } ] }""";
        ExtensionPanels.Open("x", "b", "Live", J(Gappy), "band", rows: 3);
        var d = Frame(100, 40, out _);
        var rows = d.ComposeFrameRows().Select(Plain).ToList();
        int tab = rows.FindIndex(r => r.Contains("Live") && r.Contains("F6 focus"));
        Assert.StartsWith("\u2500\u2500", rows[tab]);                // the tab row is a rule: a clear top edge
        Assert.EndsWith("\u2500", rows[tab].TrimEnd());
        Assert.Contains("line A", rows[tab + 1]);
        Assert.Contains("line B", rows[tab + 2]);                    // gap rows dropped in a band
        Assert.Contains("line C", rows[tab + 3]);
        // Drag the rule up two rows: the band grows by two.
        Mouse(d, MouseEventKind.Press, tab + 1, 60);
        Mouse(d, MouseEventKind.Drag, tab - 1, 60);
        Mouse(d, MouseEventKind.Release, tab - 1, 60);
        var before = rows.Count(r => Regex.IsMatch(r, "line [ABC]"));
        Assert.Equal(3, before);
        ExtensionPanels.Update("x", "b", J("""{ "type": "box", "children": [ { "type": "text", "text": "l1\nl2\nl3\nl4\nl5\nl6\nl7" } ] }"""));
        rows = d.ComposeFrameRows().Select(Plain).ToList();
        Assert.Equal(5, rows.Count(r => r.Trim() is ['l', >= '1' and <= '7']));
        // Dragging down shrinks from the rows on screen (not the panel's ask): one row down = one row less.
        tab = rows.FindIndex(r => r.Contains("Live") && r.Contains("F6 focus"));
        Mouse(d, MouseEventKind.Press, tab + 1, 60);
        Mouse(d, MouseEventKind.Drag, tab + 2, 60);
        Mouse(d, MouseEventKind.Release, tab + 2, 60);
        rows = d.ComposeFrameRows().Select(Plain).ToList();
        Assert.Equal(4, rows.Count(r => r.Trim() is ['l', >= '1' and <= '7']));
    }
}
