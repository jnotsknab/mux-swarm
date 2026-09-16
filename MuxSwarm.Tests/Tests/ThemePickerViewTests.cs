using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

// Theme picker view: fuzzy filter over the preset roster, live preview rendered in the
// HIGHLIGHTED theme's palette (not the active one), apply/cancel actions, bounded rendering.
public class ThemePickerViewTests
{
    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.Oem1, false, false, false);
    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    [Fact]
    public void RosterMatchesThemePresets_AndStartsOnActiveTheme()
    {
        var v = new ThemePickerView(activeName: "dracula");
        Assert.Equal(Theme.Presets.Count, v.MatchCount);
        Assert.Equal("dracula", v.Selected!.Name);
    }

    [Fact]
    public void Filter_NarrowsByName_AndCtrlUClears()
    {
        var v = new ThemePickerView();
        foreach (char c in "catppuccin") v.Handle(Key(c), 5);
        Assert.Equal(3, v.MatchCount);   // mocha, macchiato, latte
        v.Handle(new ConsoleKeyInfo('u', ConsoleKey.U, false, false, true), 5);
        Assert.Equal(Theme.Presets.Count, v.MatchCount);
    }

    [Fact]
    public void EnterAppliesSelected_EscCancels()
    {
        var v = new ThemePickerView();
        v.Handle(K(ConsoleKey.DownArrow), 5);
        Assert.Equal(ThemePickerView.Action.Apply, v.Handle(K(ConsoleKey.Enter), 5));
        Assert.Equal(ThemePickerView.Action.Cancel, v.Handle(K(ConsoleKey.Escape), 5));
    }

    [Fact]
    public void PreviewLines_RenderInTargetThemePalette_NotActive()
    {
        // Preview for tokyo-night must carry ITS accent hex even while another theme is active.
        var tokyo = Theme.Find("tokyo-night")!;
        var lines = ThemePickerView.PreviewLines(tokyo, 60);
        string joined = string.Join("\n", lines);
        Assert.Contains("#BB9AF7", joined);   // tokyo-night accent
        Assert.Contains(tokyo.Name, joined);
        Assert.Contains("#F7768E", joined);   // its error red
    }

    [Fact]
    public void Render_BoundedAndAlignedAtVariousSizes()
    {
        var v = new ThemePickerView();
        foreach (var (w, h) in new[] { (52, 12), (100, 30), (160, 45), (30, 6) })
        {
            var rows = v.Render(w, h);
            Assert.Equal(h, rows.Count);
            Assert.All(rows, r => Assert.InRange(TuiMarkup.MarkupWidth(r), 0, w));
        }
    }

    [Fact]
    public void NewThemes_AreRegistered_WithCanonicalSignatureAccents()
    {
        // Spot-check canonical hexes against upstream palettes (see THEME_PALETTES_*_20260916.md).
        Assert.Equal("#CBA6F7", Theme.Find("catppuccin-mocha")!.Accent);   // Mauve
        Assert.Equal("#88C0D0", Theme.Find("nord")!.Accent);               // nord8
        Assert.Equal("#EBBCBA", Theme.Find("rose-pine")!.Accent);          // rose
        Assert.Equal("#FFA066", Theme.Find("kanagawa")!.Accent);           // surimiOrange
        Assert.Equal("#A7C080", Theme.Find("everforest")!.Accent);         // green
        Assert.Equal("#F92672", Theme.Find("monokai")!.Accent);            // pink
        Assert.Equal("#FF7EDB", Theme.Find("synthwave")!.Accent);          // neon pink
        Assert.Equal("#FFCC66", Theme.Find("ayu-mirage")!.Accent);
        Assert.Equal("#61AFEF", Theme.Find("one-dark")!.Accent);
        Assert.True(Theme.Presets.Count >= 21);
        // Every preset resolves by its own name (round-trip through Find).
        Assert.All(Theme.Presets, t => Assert.Same(t, Theme.Find(t.Name)));
    }
}
