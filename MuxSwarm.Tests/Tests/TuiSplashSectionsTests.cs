using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>splash.layout and the section styles: the theme decides which sections the splash shows and where.</summary>
[Collection("ConsoleState")]
public sealed class TuiSplashSectionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-splashsections-tests-" + Guid.NewGuid().ToString("N"));
    public TuiSplashSectionsTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private IReadOnlyList<string> Load(string splash)
    {
        var dir = Path.Combine(_root, "s");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": { \"splash\": " + splash + " } } }");
        return ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }).Problems;
    }

    private void Style(string splash) => Assert.Empty(Load(splash));

    private static List<string> Splash(int width) =>
        MuxConsole.BuildFrameSplashLines("1.2.3", "", "Tip", "hello there", width,
            [new MuxConsole.SplashSession("2026-10-20_17-52-09", "agent", "fix the login bug"),
             new MuxConsole.SplashSession("2026-10-19_09-00-00", "swarm", "release notes"),
             new MuxConsole.SplashSession("2026-10-18_09-00-00", "agent", "third one")])
        .Select(TuiMarkup.Plain).ToList();

    private static int Row(List<string> rows, string text) => rows.FindIndex(r => r.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Layout_PlacesCellsSideBySide_AndRowsBelowEachOther()
    {
        Style("""{ "layout": ["art | title version", "commands | recent"], "title": "hi {version}" }""");
        foreach (int w in new[] { 120, 200 })
        {
            var rows = Splash(w);
            int art = Row(rows, "███╗"), title = Row(rows, "hi 1.2.3"), cmd = Row(rows, "/pswarm"), recent = Row(rows, "fix the login bug");
            Assert.True(art >= 0 && title == art, $"title beside the art at {w}");
            Assert.True(cmd > art && Math.Abs(cmd - recent) <= 4, $"second row holds commands and recent side by side at {w}");
            Assert.True(rows[cmd].IndexOf("/pswarm", StringComparison.Ordinal) < rows[recent].IndexOf("fix the login bug", StringComparison.Ordinal) || cmd != recent);
            Assert.DoesNotContain(rows, r => r.Contains("Check Out The Repo"));   // only the listed sections
            Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= w - 1, $"overflow at {w}: '{r}'"));
        }
    }

    [Fact]
    public void Layout_StacksCells_WhenTheyCannotFitSideBySide()
    {
        Style("""{ "layout": ["art | art"] }""");
        var rows = Splash(80);
        const string muxBottom = "╚═╝     ╚═╝ ╚═════╝";   // a row only the MUX half of the art has
        Assert.Equal(2, rows.Count(r => r.Contains(muxBottom)));
        Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= 79, r));
        Assert.DoesNotContain(rows, r => r.Split(muxBottom).Length > 2);   // never two on one row
    }

    [Fact]
    public void Width_SetsThePanelWidth_AndCenterCentersIt()
    {
        Style("""{ "layout": ["title", "message"], "width": 50, "align": "center" }""");
        var rows = Splash(200).Where(r => r.Length > 0).ToList();
        Assert.Equal(99, rows.Max(r => TuiMarkup.Width(r.TrimStart())));   // 50% of the 199 usable columns
        int pad = rows[0].TakeWhile(c => c == ' ').Count();
        Assert.InRange(pad, 49, 51);
    }

    [Fact]
    public void BorderTitle_SitsInTheTopEdge()
    {
        Style("""{ "layout": ["title"], "borderTitle": "mux {version}" }""");
        var top = Splash(120).First();
        Assert.StartsWith("\u256d\u2500 mux 1.2.3 \u2500", top);
        Assert.EndsWith("\u256e", top);
        Assert.Equal(119, TuiMarkup.Width(top));
    }

    [Fact]
    public void Headings_AndBullet_StyleEverySection()
    {
        Style("""{ "layout": ["tips | recent"], "headings": "rule", "bullet": "\u2022", "recentCount": 2 }""");
        var rows = Splash(120);
        Assert.Contains(rows, r => r.Contains("\u2500\u2500 Quick Tips \u2500"));
        Assert.Contains(rows, r => r.Contains("\u2022 /status to view current config"));
        Assert.Contains(rows, r => r.Contains("\u2022 2026-10-19_09-00-00"));
        Assert.DoesNotContain(rows, r => r.Contains("third one"));   // recentCount 2
    }

    [Fact]
    public void Text_Section_ShowsTheThemeLines()
    {
        Style("""{ "layout": ["art text"], "frame": "none", "text": ["READY.", "cwd {version}"] }""");
        var rows = Splash(120);
        Assert.True(Row(rows, "READY.") > Row(rows, "███╗"));
        Assert.Contains(rows, r => r.Contains("cwd 1.2.3"));
        Assert.DoesNotContain(rows, r => r.Contains('\u256d'));
    }

    [Fact]
    public void ContentAlignCenter_CentersEachSection()
    {
        Style("""{ "layout": ["title"], "title": "abc", "contentAlign": "center", "frame": "none", "padY": 0 }""");
        var row = Splash(120).First(r => r.Contains("abc"));
        int at = row.IndexOf("abc", StringComparison.Ordinal);
        Assert.InRange(at, 55, 62);
    }

    [Theory]
    [InlineData("""{ "layout": ["art | nope"] }""", "unknown section 'nope'")]
    [InlineData("""{ "layout": "art" }""", "'splash.layout' must be a list of 1-8 row strings")]
    [InlineData("""{ "layout": ["a|b|c|d"] }""", "rows must hold 1-3 cells")]
    [InlineData("""{ "headings": "fancy" }""", "'splash.headings' must be one of")]
    [InlineData("""{ "width": 101 }""", "'splash.width' must be a whole number from 0 to 100")]
    [InlineData("""{ "bullet": "\u2022\u2022" }""", "'splash.bullet' must be 0-1 terminal cell")]
    public void Values_AreValidated(string splash, string expected) =>
        Assert.Contains(expected, Assert.Single(Load(splash)));
}
