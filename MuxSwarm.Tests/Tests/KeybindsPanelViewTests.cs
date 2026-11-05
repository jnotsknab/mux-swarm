using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>The /keybinds panel: capture, remove, reset, unbind, conflicts, minimal saved overrides. Pure (the
/// view gets its own map; Write targets a temp file), so it never touches <see cref="KeyBinds.Current"/>.</summary>
public class KeybindsPanelViewTests
{
    private static ConsoleKeyInfo K(ConsoleKey k, char c = '\0', bool ctrl = false, bool alt = false, bool shift = false) => new(c, k, shift, alt, ctrl);
    private static KeybindsPanelView Fresh() => new(KeyBinds.Parse("{}", []));
    private static void Select(KeybindsPanelView v, string action)
    {
        v.Handle(K(ConsoleKey.Home), 5);
        while (v.Selected.Action != action) v.Handle(K(ConsoleKey.DownArrow), 5);
    }

    [Fact]
    public void Enter_CapturesNextChord_RawKeyNotItsBinding()
    {
        var v = Fresh();
        Select(v, KeyAction.Newline);
        v.Handle(K(ConsoleKey.Enter, '\r'), 5);
        Assert.True(v.Capturing);
        // Esc while capturing cancels the capture, not the panel.
        Assert.Equal(KeybindsPanelView.Action.None, v.Handle(K(ConsoleKey.Escape, '\x1b'), 5));
        Assert.False(v.Capturing);
        v.Handle(K(ConsoleKey.A, 'a'), 5);
        v.Handle(K(ConsoleKey.F9), 5);
        Assert.Contains(new KeyChord(ConsoleKey.F9, 0), v.Chords(KeyAction.Newline));
        Assert.True(v.Dirty);
        Assert.Equal(["Ctrl+J", "Ctrl+Enter", "Shift+Enter", "Alt+Enter", "F9"], v.Overrides()[KeyAction.Newline]);
    }

    [Fact]
    public void Delete_Reset_Unbind_AndMinimalOverrides()
    {
        var v = Fresh();
        Select(v, KeyAction.TaskBoard);
        v.Handle(K(ConsoleKey.U, 'u'), 5);
        Assert.Empty(v.Chords(KeyAction.TaskBoard));
        Assert.Empty(v.Overrides()[KeyAction.TaskBoard]);
        v.Handle(K(ConsoleKey.R, 'r'), 5);
        Assert.False(v.Overrides().ContainsKey(KeyAction.TaskBoard));   // back to default = not written
        Select(v, KeyAction.Paste);
        v.Handle(K(ConsoleKey.Delete), 5);                              // removes the last chord (Alt+V)
        Assert.Equal(["Ctrl+V"], v.Overrides()[KeyAction.Paste]);
        Assert.Single(v.Overrides());
    }

    [Fact]
    public void Conflict_InSameContext_IsReported()
    {
        var v = Fresh();
        Select(v, KeyAction.TaskBoard);
        v.Handle(K(ConsoleKey.A, 'a'), 5);
        v.Handle(K(ConsoleKey.R, '\x12', ctrl: true), 5);                // Ctrl+R = prompt.search
        Assert.Contains(KeyAction.Search, v.Message);
        Assert.Contains(KeyAction.Search, v.Conflicts(KeyAction.TaskBoard, new KeyChord(ConsoleKey.R, ConsoleModifiers.Control)));
        Assert.DoesNotContain(KeyAction.TurnTaskBoard, v.Conflicts(KeyAction.TaskBoard, new KeyChord(ConsoleKey.T, ConsoleModifiers.Control)));
    }

    [Fact]
    public void F4_Applies_Esc_Cancels_AndRenderFits()
    {
        var v = Fresh();
        var rows = v.Render(100, 20);
        Assert.Equal(20, rows.Count);
        Assert.Contains(rows, r => r.Contains("prompt.newline"));
        Assert.Equal(KeybindsPanelView.Action.Apply, v.Handle(K(ConsoleKey.F4), 5));
        Assert.Equal(KeybindsPanelView.Action.Cancel, v.Handle(K(ConsoleKey.Escape, '\x1b'), 5));
    }

    [Fact]
    public void Write_KeepsDollarKeys_ReplacesActions_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mux-kb-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "$schema": "x", "prompt.view": "F8" }""");
            Assert.Empty(KeyBinds.Write(path, new Dictionary<string, string[]> { [KeyAction.Newline] = ["F9"] }));
            string text = File.ReadAllText(path);
            Assert.Contains("\"$schema\"", text);
            Assert.DoesNotContain("prompt.view", text);                  // the panel's set is complete
            var warnings = new List<string>();
            var map = KeyBinds.Read(path, warnings);
            Assert.Empty(warnings);
            Assert.True(KeyBinds.Matches(map, KeyAction.Newline, K(ConsoleKey.F9)));
            Assert.False(KeyBinds.Matches(map, KeyAction.Newline, K(ConsoleKey.J, '\n', ctrl: true)));
        }
        finally { File.Delete(path); }
    }
}
