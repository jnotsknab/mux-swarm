using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Long command output opens a scrollable, searchable full-screen panel in the panel.* chrome.</summary>
[Collection("ConsoleState")]
public class TextPanelViewTests
{
    private static List<string> Lines(int n) => Enumerable.Range(1, n).Select(i => $"[white]row {i}[/]").ToList();
    private static ConsoleKeyInfo K(ConsoleKey k, char c = '\0') => new(c, k, false, false, false);

    [Fact]
    public void Render_ShowsTitle_Rows_Position_AndHint()
    {
        var view = new TextPanelView("Mux-Swarm Status", Lines(40));
        var rows = view.Render(80, 20).Select(TuiMarkup.Plain).ToList();
        Assert.Equal(20, rows.Count);
        Assert.Contains("Mux-Swarm Status", rows[0]);
        Assert.Contains(rows, r => r.Contains("row 1"));
        Assert.DoesNotContain(rows, r => r.Contains("row 40"));
        Assert.Contains("15/40 rows", rows[^2]);
        Assert.Contains("Esc/q close", rows[^1]);
        Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= 80, r));
    }

    [Fact]
    public void Keys_Scroll_Search_AndClose()
    {
        var view = new TextPanelView("t", Lines(40));
        view.Render(80, 20);
        int page = TextPanelView.PageSize(20);
        Assert.True(view.Handle(K(ConsoleKey.End), page));
        Assert.Contains(view.Render(80, 20).Select(TuiMarkup.Plain), r => r.Contains("row 40"));
        Assert.True(view.Handle(K(ConsoleKey.G, 'g'), page));
        Assert.True(view.Handle(K(ConsoleKey.Oem2, '/'), page));
        foreach (char c in "row 33") view.Handle(K(ConsoleKey.NoName, c), page);
        view.Handle(K(ConsoleKey.Enter, '\r'), page);
        var rows = view.Render(80, 20).Select(TuiMarkup.Plain).ToList();
        Assert.Contains(rows, r => r.Contains("row 33"));
        Assert.Contains(rows, r => r.Contains("match 1/1"));
        Assert.False(view.Handle(K(ConsoleKey.Q, 'q'), page));
        Assert.False(new TextPanelView("t", Lines(3)).Handle(K(ConsoleKey.Escape), page));
    }

    [Fact]
    public void Chrome_FollowsPanelStyles()
    {
        var root = Path.Combine(Path.GetTempPath(), "mux-textpanel-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "x");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName),
                """{ "tui": { "regions": { "panel": { "header": "rule" }, "block": { "rail": "#" } } } }""");
            Assert.Empty(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [root] }).Problems);
            var top = TuiMarkup.Plain(new TextPanelView("Help", Lines(30)).Render(60, 20)[0]);
            Assert.Contains("Help", top);
            Assert.Contains("\u2500", top);   // rule header style
            Assert.Equal("#", RegionStyle.PlainGlyph("block.rail"));
        }
        finally
        {
            ExtensionRegistry.Load(null);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void LongOutput_NeedsADriver_ElseFallsBackInline() =>
        Assert.False(MuxConsole.TryLongPanel("t", Lines(100)));   // no live driver in tests: callers print inline
}
