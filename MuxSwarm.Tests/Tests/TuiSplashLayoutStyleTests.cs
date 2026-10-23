using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>splash.frame / columns / align / padY: the splash layout follows the style at every width.</summary>
[Collection("ConsoleState")]
public sealed class TuiSplashLayoutStyleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-splashstyle-tests-" + Guid.NewGuid().ToString("N"));
    public TuiSplashLayoutStyleTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Style(string splash)
    {
        var dir = Path.Combine(_root, "s");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": { \"splash\": " + splash + " } } }");
        Assert.Empty(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }).Problems);
    }

    private static List<string> Splash(int width) =>
        MuxConsole.BuildFrameSplashLines("1.2.3", "", "Tip", "hello", width,
            [new MuxConsole.SplashSession("2026-10-20_17-52-09", "agent", "fix the login bug")])
        .Select(TuiMarkup.Plain).ToList();

    [Theory]
    [InlineData("double", "\u2554", "\u255a", "\u2551")]
    [InlineData("ascii", "+", "+", "|")]
    [InlineData("thick", "\u250f", "\u2517", "\u2503")]
    public void Frame_ChangesTheBorderGlyphs(string frame, string top, string bottom, string side)
    {
        Style($$"""{ "frame": "{{frame}}" }""");
        foreach (int w in new[] { 120, 220 })
        {
            var rows = Splash(w).Where(r => r.Length > 0).ToList();
            Assert.StartsWith(top, rows[0]);
            Assert.StartsWith(bottom, rows[^1]);
            Assert.All(rows.Skip(1).SkipLast(1), r => Assert.StartsWith(side, r));
            Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= w - 1, $"overflow at {w}: '{r}'"));
        }
    }

    [Fact]
    public void FrameNone_DropsTheBorder_KeepsTheContent()
    {
        Style("""{ "frame": "none", "padY": 0 }""");
        var rows = Splash(120);
        Assert.DoesNotContain(rows, r => r.Contains('\u256d') || r.Contains('\u2570'));
        Assert.Contains(rows, r => r.Contains("v1.2.3"));
    }

    [Fact]
    public void LogoOnly_StaysOneColumn_AtWideWidths()
    {
        Style("""{ "columns": "logo" }""");
        var rows = Splash(220);
        Assert.DoesNotContain(rows, r => r.Contains("Getting started", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, r => r.Contains("/agent to start"));
    }

    [Fact]
    public void Columns_ChooseAndOrderTheWideColumns()
    {
        Style("""{ "columns": "recent,logo" }""");
        var body = Splash(220).First(r => r.Contains("fix the login bug"));
        Assert.True(body.IndexOf("fix the login bug", StringComparison.Ordinal) < 60, body);   // recent is now the left column
        Assert.DoesNotContain(Splash(220), r => r.Contains("Getting started", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Center_ShiftsTheWholeBlock_ByOnePad()
    {
        Style("""{ "columns": "logo", "align": "center" }""");
        var rows = Splash(200).Where(r => r.Length > 0).ToList();
        int pad = rows[0].TakeWhile(c => c == ' ').Count();
        Assert.True(pad > 10, $"pad {pad}");
        Assert.All(rows, r => Assert.True(r.TakeWhile(c => c == ' ').Count() >= pad, r));   // one shared left pad
        Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= 199));
        int right = 199 - rows.Max(r => TuiMarkup.Width(r));
        Assert.InRange(pad - right, -1, 1);   // centered
    }

    [Theory]
    [InlineData("""{ "frame": "dotted" }""", "'splash.frame' must be one of")]
    [InlineData("""{ "columns": "logo,nope" }""", "'splash.columns' must be one of")]
    [InlineData("""{ "padY": 3 }""", "'splash.padY' must be a whole number from 0 to 2")]
    public void SplashLayoutValues_AreValidated(string splash, string expected)
    {
        var dir = Path.Combine(_root, "bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": { \"splash\": " + splash + " } } }");
        Assert.Contains(expected, Assert.Single(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }).Problems));
    }
}
