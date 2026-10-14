using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// The /extensions panel through the real TuiDriver + shared pump, in both renderers: keys and mouse
// (click, double click, action-bar buttons, wheel) return the same actions, and ownership is restored.
// Uses the agent picker's harness (test terminal, driver, unstarted pump).
[Collection("ConsoleState")]
public sealed class ExtensionsPanelDriverTests
{
    private static ExtensionsPanelView View() => new([ExtensionsPanelTests.Item("demo", process: true), ExtensionsPanelTests.Item("glow")]);

    private static ConsoleInputPump.InputEvent Key(ConsoleKey key, char c = '\0') => ConsoleInputPump.InputEvent.OfKey(ExtensionsPanelTests.Key(key, c));

    private static IEnumerable<ConsoleInputPump.InputEvent> Click(int row, int col) =>
        [ConsoleInputPump.InputEvent.OfMouse(0, col, row, false), ConsoleInputPump.InputEvent.OfMouse(0, col, row, true)];

    // Where a target sits in the panel as the driver paints it (width = terminal width - 1).
    private static (int Row, int Col) Find(ExtensionsPanelView view, Func<HitRegion, bool> match)
    {
        var hits = new MouseHitMap();
        view.Render(89, 20, hits);
        for (int r = 1; r <= 20; r++)
            for (int c = 1; c <= 89; c++)
                if (hits.TryHit(r, c, out var h, out _, out _) && match(h)) return (r, c);
        throw new InvalidOperationException("target not found");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Keys_ReturnTheirAction_AndRestoreOwnership(bool frame)
    {
        using var scope = new AgentPickerDriverTests.Scope(frame);
        var view = View();
        scope.Pump.PushFront([Key(ConsoleKey.DownArrow), Key(ConsoleKey.R, 'r'), Key(ConsoleKey.X, 'x')]);
        Assert.True(MuxConsole.TryExtensionsPanel(view, out var action));
        Assert.Equal(ExtensionsPanelView.Action.Reload, action);
        Assert.Equal("glow", view.Selected!.Name);
        Assert.True(scope.Pump.TryTake(out var next, 0));
        Assert.Equal('x', next.Key.KeyChar);   // following input is not swallowed
        scope.AssertRestored();
        Assert.Contains("EXTENSIONS", scope.Term.Output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoubleClickRow_SelectsAndToggles(bool frame)
    {
        using var scope = new AgentPickerDriverTests.Scope(frame);
        var view = View();
        var (row, col) = Find(view, h => h.Kind == MouseTargetKind.PickerItem && h.Payload == 1);
        scope.Pump.PushFront([.. Click(row, col), .. Click(row, col), Key(ConsoleKey.Escape)]);   // Esc: a broken click fails, not hangs
        Assert.True(scope.Driver.RunExtensionsPanel(view, out var action, input: scope.Pump));
        Assert.Equal(ExtensionsPanelView.Action.Toggle, action);
        Assert.Equal("glow", view.Selected!.Name);
        scope.AssertRestored();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClickActionButton_ReturnsItsAction(bool frame)
    {
        using var scope = new AgentPickerDriverTests.Scope(frame);
        var view = View();
        var (row, col) = Find(view, h => h.Kind == MouseTargetKind.PromptModalOption && ExtensionsPanelView.ClickButton(h.Payload) == ExtensionsPanelView.Action.Reload);
        scope.Pump.PushFront([.. Click(row, col), Key(ConsoleKey.Escape)]);
        Assert.True(scope.Driver.RunExtensionsPanel(view, out var action, input: scope.Pump));
        Assert.Equal(ExtensionsPanelView.Action.Reload, action);
        scope.AssertRestored();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WheelOverList_MovesSelection_AndEscCloses(bool frame)
    {
        using var scope = new AgentPickerDriverTests.Scope(frame);
        var view = View();
        scope.Pump.PushFront([ConsoleInputPump.InputEvent.OfWheel(-1), Key(ConsoleKey.Escape)]);
        Assert.True(scope.Driver.RunExtensionsPanel(view, out var action, input: scope.Pump));
        Assert.Equal(ExtensionsPanelView.Action.Close, action);
        Assert.Equal("glow", view.Selected!.Name);
        scope.AssertRestored();
    }

    [Fact]
    public void BusyModalOrScriptedInput_FallsBackWithoutStealingInput()
    {
        using var scope = new AgentPickerDriverTests.Scope(true);
        scope.Pump.Enqueue(Key(ConsoleKey.Enter));
        ConsoleInputPump.ModalActive = true;
        Assert.False(scope.Driver.RunExtensionsPanel(View(), out _, input: scope.Pump));
        ConsoleInputPump.ModalActive = false;
        MuxConsole.InputOverride = new StringReader("x\n");
        Assert.False(MuxConsole.TryExtensionsPanel(View(), out _));
        Assert.True(scope.Pump.TryTake(out _, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InPlaceActions_ApplyWithoutLeavingThePanel(bool frame)
    {
        using var scope = new AgentPickerDriverTests.Scope(frame);
        var view = View();
        var applied = new List<ExtensionsPanelView.Action>();
        int enters = 0;
        scope.Pump.PushFront([Key(ConsoleKey.Spacebar, ' '), Key(ConsoleKey.R, 'r'), Key(ConsoleKey.T, 't'), Key(ConsoleKey.T, 't'), Key(ConsoleKey.Escape)]);
        var before = scope.Term.Output.Length;
        Assert.True(scope.Driver.RunExtensionsPanel(view, out var action, input: scope.Pump, apply: a => { applied.Add(a); enters++; }));
        Assert.Equal(ExtensionsPanelView.Action.Close, action);
        Assert.Equal([ExtensionsPanelView.Action.Toggle, ExtensionsPanelView.Action.Reload, ExtensionsPanelView.Action.Trust], applied);
        if (!frame)   // inline engine: one alt-screen session, never left and re-entered between actions
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(scope.Term.Output.ToString()[before..], System.Text.RegularExpressions.Regex.Escape(Ansi.LeaveAltScreen)));
        scope.AssertRestored();
    }
}
