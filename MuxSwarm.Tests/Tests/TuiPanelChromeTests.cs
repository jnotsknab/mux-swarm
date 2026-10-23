using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>panel.header / selectedStyle / hints restyle every full-screen view without moving click regions.</summary>
[Collection("ConsoleState")]
public sealed class TuiPanelChromeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-chrome-tests-" + Guid.NewGuid().ToString("N"));
    public TuiPanelChromeTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Style(string panel)
    {
        var dir = Path.Combine(_root, "p");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": { \"panel\": " + panel + " } } }");
        Assert.Empty(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }).Problems);
    }

    private static AgentPickerView Picker() =>
        new([AgentPickerViewTests.Agent("CodeAgent", "writes code"), AgentPickerViewTests.Agent("WebAgent", "browses")], "CodeAgent");

    [Fact]
    public void HeaderStrip_FillsTheTitleRow_Centered()
    {
        Style("""{ "header": "strip", "headerAlign": "center", "headerBg": "#102030" }""");
        var row = Picker().Render(80, 20)[0];
        Assert.StartsWith("[on #102030]", row);
        Assert.Equal(80, TuiMarkup.MarkupWidth(row));
        string plain = TuiMarkup.Plain(row);
        int lead = plain.TakeWhile(c => c == ' ').Count();
        Assert.InRange(lead - (80 - plain.TrimEnd().Length), -2, 2);
    }

    [Fact]
    public void HeaderRule_SetsTheTitleInARule()
    {
        Style("""{ "header": "rule", "rule": "=" }""");
        string plain = TuiMarkup.Plain(Picker().Render(80, 20)[0]);
        Assert.StartsWith("== CHOOSE LEAD AGENT =", plain);
        Assert.Equal(80, TuiMarkup.Width(plain));
    }

    [Fact]
    public void SelectedBar_And_Fill_KeepTheClickRegion()
    {
        Style("""{ "selectedStyle": "bar", "bar": "|" }""");
        var hits = new MouseHitMap();
        var rows = Picker().Render(80, 20, hits).Select(TuiMarkup.Plain).ToList();
        int sel = rows.FindIndex(r => r.StartsWith("| CodeAgent"));
        Assert.True(sel > 0, string.Join("\n", rows));
        Assert.True(hits.TryHit(sel + 1, 5, out var hit, out _, out _));
        Assert.Equal(MouseTargetKind.PickerItem, hit.Kind);

        Style("""{ "selectedStyle": "fill", "selectedBg": "#334455" }""");
        var filled = Picker().Render(80, 20);
        string fillRow = filled.First(r => TuiMarkup.Plain(r).Contains("CodeAgent (current)"));
        Assert.StartsWith("[on #334455]", fillRow);
        Assert.Equal(80, TuiMarkup.MarkupWidth(fillRow));
    }

    [Fact]
    public void HintsStrip_FillsTheKeyRows_InEveryView()
    {
        Style("""{ "hints": "strip", "hintBg": "#202020" }""");
        foreach (var rows in new[]
        {
            Picker().Render(80, 20),
            new ThemePickerView().Render(100, 22),
            new HistoryView([("a", "b", null)], resumePicker: true).Render(80, 20),
            new ExtensionsPanelView([ExtensionsPanelTests.Item("ember")]).Render(100, 24),
        })
            Assert.StartsWith("[on #202020]", rows[^1]);
    }

    [Fact]
    public void AsciiGlyphs_ReachTheBar()
    {
        var dir = Path.Combine(_root, "a");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), """{ "tui": { "glyphs": "ascii", "regions": { "panel": { "selectedStyle": "bar" } } } }""");
        Assert.Empty(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }).Problems);
        Assert.Contains(Picker().Render(80, 20).Select(TuiMarkup.Plain), r => r.StartsWith("| CodeAgent"));
    }

    [Fact]
    public void Frame_BoxesTheView_AndClicksStillPickTheRightItem()
    {
        Style("""{ "frame": "rounded", "frameFg": "#445566" }""");
        using var scope = new AgentPickerDriverTests.Scope(frame: true);
        var agents = new[] { AgentPickerViewTests.Agent("CodeAgent", "writes code"), AgentPickerViewTests.Agent("WebAgent", "browses") };
        // Screen row of "WebAgent" inside the frame: the view's row + 1 (top border); column inside the left border.
        var inner = new AgentPickerView(agents, "CodeAgent").Render(scope.Term.Width - 1 - 2, scope.Term.Height - 2).Select(TuiMarkup.Plain).ToList();
        int row = inner.FindIndex(r => r.Contains("WebAgent")) + 1 + 1;
        ConsoleInputPump.InputEvent M(bool release) => ConsoleInputPump.InputEvent.OfMouse(0, 6, row, release);
        // A trailing Esc ends the loop if the clicks miss, so a regression fails instead of hanging.
        scope.Pump.PushFront(new[] { M(false), M(true), M(false), M(true), ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)) });
        Assert.True(scope.Driver.RunAgentPicker(new AgentPickerView(agents, "CodeAgent"), out var selected, input: scope.Pump));
        Assert.Same(agents[1], selected);
        string screen = scope.Term.Output.ToString();
        Assert.Contains("\u256d\u2500", screen);
        Assert.Contains("\u2570\u2500", screen);
    }

    [Fact]
    public void Frame_IsSkippedOnTinyScreens()
    {
        Style("""{ "frame": "double" }""");
        Assert.Null(ViewFrame.Glyphs(20, 20));
        Assert.Equal((20, 6), ViewFrame.Inner(20, 6));
        Assert.Equal((78, 18), ViewFrame.Inner(80, 20));
    }
}
