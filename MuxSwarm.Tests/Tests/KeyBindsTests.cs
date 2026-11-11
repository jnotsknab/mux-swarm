using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Rebindable shortcuts: chord parsing/matching and Keybinds.json overlay rules. Pure (never mutates
/// <see cref="KeyBinds.Current"/>), so it runs in parallel with every class that feeds keys to the editor.</summary>
public class KeyBindsTests
{
    private static ConsoleKeyInfo K(ConsoleKey key, char c = '\0', bool ctrl = false, bool alt = false, bool shift = false)
        => new(c, key, shift, alt, ctrl);

    [Theory]
    [InlineData("Ctrl+J", ConsoleKey.J, ConsoleModifiers.Control)]
    [InlineData("ctrl+enter", ConsoleKey.Enter, ConsoleModifiers.Control)]
    [InlineData("Alt+Shift+Return", ConsoleKey.Enter, ConsoleModifiers.Alt | ConsoleModifiers.Shift)]
    [InlineData("Esc", ConsoleKey.Escape, (ConsoleModifiers)0)]
    [InlineData("F6", ConsoleKey.F6, (ConsoleModifiers)0)]
    [InlineData("Ctrl+5", ConsoleKey.D5, ConsoleModifiers.Control)]
    [InlineData("Shift+Tab", ConsoleKey.Tab, ConsoleModifiers.Shift)]
    [InlineData("PgUp", ConsoleKey.PageUp, (ConsoleModifiers)0)]
    public void Parse_ValidChords(string text, ConsoleKey key, ConsoleModifiers mods)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(new KeyChord(key, mods), chord);
        Assert.True(KeyChord.TryParse(chord.ToString(), out var again));
        Assert.Equal(chord, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+J")]
    [InlineData("Ctrl+Ctrl+J")]
    [InlineData("Ctrl+Banana")]
    [InlineData("42")]
    public void Parse_RejectsInvalid(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void Match_RequiresExactModifiers()
    {
        Assert.True(KeyChord.TryParse("Ctrl+J", out var ctrlJ));
        Assert.True(ctrlJ.Matches(K(ConsoleKey.J, '\n', ctrl: true)));
        Assert.False(ctrlJ.Matches(K(ConsoleKey.J, 'j')));
        Assert.False(ctrlJ.Matches(K(ConsoleKey.J, '\n', ctrl: true, alt: true)));
    }

    /// <summary>VT input delivers printable keys with no virtual key; a bare letter chord matches by character, any case.</summary>
    [Fact]
    public void Match_BareLetter_ByCharacter()
    {
        Assert.True(KeyChord.TryParse("q", out var q));
        Assert.True(q.Matches(K(ConsoleKey.NoName, 'q')));
        Assert.True(q.Matches(K(ConsoleKey.NoName, 'Q', shift: true)));
        Assert.False(q.Matches(K(ConsoleKey.NoName, 'w')));
    }

    [Fact]
    public void Defaults_CoverEveryAction_AndNewlineKeysWork()
    {
        var actions = typeof(KeyAction).GetFields().Select(f => (string)f.GetValue(null)!).ToList();
        Assert.Equal(actions.Order(), KeyBinds.Defaults.Select(b => b.Action).Order());
        var map = KeyBinds.Parse("{}", []);
        Assert.True(KeyBinds.Matches(map, KeyAction.Newline, K(ConsoleKey.J, '\n', ctrl: true)));
        Assert.True(KeyBinds.Matches(map, KeyAction.Newline, K(ConsoleKey.Enter, '\r', ctrl: true)));
        Assert.True(KeyBinds.Matches(map, KeyAction.Submit, K(ConsoleKey.Enter, '\r')));
        Assert.False(KeyBinds.Matches(map, KeyAction.Submit, K(ConsoleKey.Enter, '\r', ctrl: true)));
    }

    /// <summary>Every rebindable action is documented in /shortcuts, so a rebind is always visible there.</summary>
    [Fact]
    public void Catalog_DocumentsEveryAction()
    {
        var documented = TuiCommands.Keys.Select(k => k.Action).Where(a => a is not null).ToHashSet();
        foreach (var bind in KeyBinds.Defaults) Assert.Contains(bind.Action, documented);
    }

    [Fact]
    public void File_OverridesListedActions_KeepsTheRest()
    {
        var warnings = new List<string>();
        var map = KeyBinds.Parse("""
            // comments are allowed
            { "$schema": "x", "prompt.newline": ["Alt+N", "Ctrl+J"], "prompt.submit": "Ctrl+S", }
            """, warnings);
        Assert.Empty(warnings);
        Assert.True(KeyBinds.Matches(map, KeyAction.Newline, K(ConsoleKey.N, 'n', alt: true)));
        Assert.False(KeyBinds.Matches(map, KeyAction.Newline, K(ConsoleKey.Enter, '\r', ctrl: true)));
        Assert.True(KeyBinds.Matches(map, KeyAction.Submit, K(ConsoleKey.S, '\x13', ctrl: true)));
        Assert.False(KeyBinds.Matches(map, KeyAction.Submit, K(ConsoleKey.Enter, '\r')));
        Assert.True(KeyBinds.Matches(map, KeyAction.Search, K(ConsoleKey.R, '\x12', ctrl: true)));
        Assert.Equal("Alt+N / Ctrl+J", map[KeyAction.Newline].Label);
    }

    [Fact]
    public void File_EmptyArrayUnbinds()
    {
        var map = KeyBinds.Parse("""{ "prompt.taskBoard": [] }""", []);
        Assert.Empty(map[KeyAction.TaskBoard].Combination);
        Assert.Equal("(unbound)", map[KeyAction.TaskBoard].Label);
    }

    [Fact]
    public void File_Problems_WarnAndFallBack()
    {
        var warnings = new List<string>();
        var map = KeyBinds.Parse("""{ "prompt.nope": "Ctrl+X", "prompt.newline": "Ctrl+Banana", "prompt.view": ["Ctrl+Banana", "F9"] }""", warnings);
        Assert.Equal(4, warnings.Count);   // unknown action, 2x bad key, newline keeps defaults
        Assert.Contains(warnings, w => w.Contains("unknown action 'prompt.nope'"));
        Assert.True(KeyBinds.Matches(map, KeyAction.Newline, K(ConsoleKey.J, '\n', ctrl: true)));   // kept defaults
        Assert.True(KeyBinds.Matches(map, KeyAction.View, K(ConsoleKey.F9)));                     // valid entry kept
        Assert.False(KeyBinds.Matches(map, KeyAction.View, K(ConsoleKey.G, '\a', ctrl: true)));

        warnings.Clear();
        var broken = KeyBinds.Parse("{ not json", warnings);
        Assert.Single(warnings);
        Assert.True(KeyBinds.Matches(broken, KeyAction.Submit, K(ConsoleKey.Enter, '\r')));
    }

    [Fact]
    public void Read_MissingFile_IsDefaults_And_DefaultJsonRoundTrips()
    {
        var warnings = new List<string>();
        var missing = KeyBinds.Read(Path.Combine(Path.GetTempPath(), $"mux-keybinds-{Guid.NewGuid():N}.json"), warnings);
        Assert.Empty(warnings);
        Assert.Equal(KeyBinds.Defaults.Length, missing.Count);

        Assert.Contains("\"Ctrl+J\"", KeyBinds.DefaultJson());   // hand-editable: '+' is not \u002B-escaped
        var map = KeyBinds.Parse(KeyBinds.DefaultJson(), warnings);
        Assert.Empty(warnings);
        foreach (var bind in KeyBinds.Defaults) Assert.Equal(bind.Combination, map[bind.Action].Combination);
    }
}
