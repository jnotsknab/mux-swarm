using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// /extensions panel: the pure view (keys, search, mouse targets).
// Driver-level runs live in ExtensionsPanelDriverTests.
public sealed class ExtensionsPanelTests
{
    // ---- view -----------------------------------------------------------------------------

    internal static ExtensionCatalog.Item Item(string name, bool enabled = true, bool process = false, bool trusted = false,
        IReadOnlyList<ExtensionRegistry.Conflict>? conflicts = null, IReadOnlyList<string>? problems = null) =>
        new(name, "1.0", $"{name} description", Path.Combine("x", name), enabled, process, trusted, process ? "running (host)" : "",
            ["themes: t"], conflicts ?? [], problems ?? [], []);

    internal static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool ctrl = false) => new(c, key, false, false, ctrl);

    [Fact]
    public void View_KeysMapToActions_AndTrustNeedsAProcess()
    {
        var view = new ExtensionsPanelView([Item("demo", process: true), Item("glow")]);
        Assert.Equal("demo", view.Selected!.Name);
        Assert.Equal(ExtensionsPanelView.Action.Toggle, view.Handle(Key(ConsoleKey.Spacebar, ' '), 5));
        Assert.Equal(ExtensionsPanelView.Action.Toggle, view.Handle(Key(ConsoleKey.Enter), 5));
        Assert.Equal(ExtensionsPanelView.Action.Trust, view.Handle(Key(ConsoleKey.T, 't'), 5));
        Assert.Equal(ExtensionsPanelView.Action.Edit, view.Handle(Key(ConsoleKey.E, 'e'), 5));
        Assert.Equal(ExtensionsPanelView.Action.OpenFolder, view.Handle(Key(ConsoleKey.O, 'o'), 5));
        Assert.Equal(ExtensionsPanelView.Action.Reload, view.Handle(Key(ConsoleKey.R, 'r'), 5));
        view.Handle(Key(ConsoleKey.DownArrow), 5);
        Assert.Equal("glow", view.Selected!.Name);
        Assert.Equal(ExtensionsPanelView.Action.None, view.Handle(Key(ConsoleKey.T, 't'), 5));   // no process
        Assert.Equal(ExtensionsPanelView.Action.Close, view.Handle(Key(ConsoleKey.Escape), 5));
    }

    [Fact]
    public void View_SearchTypesLettersInsteadOfActions_AndEscLeavesSearchFirst()
    {
        var view = new ExtensionsPanelView([Item("demo"), Item("glow"), Item("sysmon")]);
        Assert.Equal(ExtensionsPanelView.Action.None, view.Handle(Key(ConsoleKey.Oem2, '/'), 5));
        Assert.True(view.Searching);
        foreach (var c in "gl") Assert.Equal(ExtensionsPanelView.Action.None, view.Handle(Key(ConsoleKey.NoName, c), 5));
        Assert.Equal("gl", view.Query);
        Assert.Equal(1, view.MatchCount);
        Assert.Equal("glow", view.Selected!.Name);
        Assert.Equal(ExtensionsPanelView.Action.None, view.Handle(Key(ConsoleKey.Escape), 5));
        Assert.False(view.Searching);
        Assert.Equal(1, view.MatchCount);   // filter kept
        Assert.Equal(ExtensionsPanelView.Action.Close, view.Handle(Key(ConsoleKey.Escape), 5));
    }

    [Fact]
    public void View_Render_ShowsBadgesConflictsAndRegistersMouseTargets()
    {
        var conflict = new ExtensionRegistry.Conflict("footer template", "glow", "demo");
        var view = new ExtensionsPanelView([Item("demo", conflicts: [conflict]), Item("glow", conflicts: [conflict], problems: ["bad key"]), Item("off", enabled: false)]);
        var hits = new MouseHitMap();
        var rows = view.Render(100, 24, hits).Select(TuiMarkup.Plain).ToList();

        Assert.Equal(24, rows.Count);
        Assert.Contains(rows, r => r.Contains("EXTENSIONS") && r.Contains("2/3 enabled") && r.Contains("1 conflict"));
        Assert.Contains(rows, r => r.Contains("\u25cb") && r.Contains("off"));
        Assert.Contains(rows, r => r.Contains("glow") && r.Contains("!1"));
        Assert.Contains(rows, r => r.Contains("footer template: overridden by glow"));   // wraps in the 67-cell pane
        Assert.All(rows, r => Assert.True(TuiMarkup.Width(r) <= 100, r));

        // List rows: clicking the 'glow' row selects it.
        Assert.True(hits.TryHit(5, 3, out var row, out _, out _));
        Assert.Equal(MouseTargetKind.PickerItem, row.Kind);
        view.ClickItem(row.Payload);
        Assert.Equal("glow", view.Selected!.Name);
        rows = view.Render(100, 24, hits).Select(TuiMarkup.Plain).ToList();
        Assert.Contains(rows, r => r.Contains("footer template: overrides demo"));
        Assert.Contains(rows, r => r.Contains("! bad key"));

        // Action bar: the first button is Space toggle, the last Esc close.
        int bar = rows.FindIndex(r => r.Contains("[Space toggle]")) + 1;
        Assert.True(bar > 0);
        Assert.True(hits.TryHit(bar, 3, out var first, out _, out _));
        Assert.Equal(MouseTargetKind.PromptModalOption, first.Kind);
        Assert.Equal(ExtensionsPanelView.Action.Toggle, ExtensionsPanelView.ClickButton(first.Payload));
        int closeCol = rows[bar - 1].IndexOf("[Esc close]", StringComparison.Ordinal) + 2;
        Assert.True(hits.TryHit(bar, closeCol, out var close, out _, out _));
        Assert.Equal(ExtensionsPanelView.Action.Close, ExtensionsPanelView.ClickButton(close.Payload));

        // Details pane is its own region (the wheel scrolls it).
        Assert.True(hits.TryHit(6, 60, out var pane, out _, out _));
        Assert.Equal(MouseTargetKind.ExpandedPanel, pane.Kind);
    }

    [Fact]
    public void View_Update_KeepsSelectionByName()
    {
        var view = new ExtensionsPanelView([Item("a"), Item("b"), Item("c")], select: "b");
        Assert.Equal("b", view.Selected!.Name);
        view.Update([Item("a"), Item("b", enabled: false), Item("c")], [], "b");
        Assert.Equal("b", view.Selected!.Name);
        Assert.False(view.Selected!.Enabled);
    }

    [Fact]
    public void View_TinyTerminal_ShowsResizeHint()
    {
        var rows = new ExtensionsPanelView([Item("a")]).Render(40, 8);
        Assert.Equal(8, rows.Count);
        Assert.Contains("Resize", rows[1]);
    }
}
