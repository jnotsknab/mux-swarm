using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>console.inputDuringTurn: "hide" (default) keeps today's turn layout; "steer" keeps the docked input box up
/// during a turn and turns what is typed there into a mid-turn steer.</summary>
[Collection("ConsoleState")]
public class InputDuringTurnTests
{
    private sealed class Term : ITuiTerminal
    {
        public int Width => 100;
        public int Height => 30;
        public void Write(string text) { }
        public void Flush() { }
    }

    private static string Plain(IEnumerable<string> rows) =>
        System.Text.RegularExpressions.Regex.Replace(string.Join("\n", rows), "\u001b\\[[0-9;?]*[A-Za-z]", "");

    private static ConsoleKeyInfo K(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);

    [Fact]
    public void Default_IsHide_AndTheTurnFrameHasNoInputBox()
    {
        Assert.Equal("hide", new ConsoleConfig().InputDuringTurn);
        Assert.Equal("hide", System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"console\":{}}")!.Console.InputDuringTurn);
        var driver = new TuiDriver(new Term(), frameEngine: true);
        Assert.DoesNotContain("steer the agent", Plain(driver.ComposeFrameRows()));
        Assert.DoesNotContain("type a message", Plain(driver.ComposeFrameRows()));
    }

    [Fact]
    public void SteerMode_ShowsTheBox_TypingThenEnterYieldsTheSteer()
    {
        var driver = new TuiDriver(new Term(), frameEngine: true);
        driver.SetTurnInput(true);
        Assert.Contains("steer the agent", Plain(driver.ComposeFrameRows()));
        foreach (var c in "use tabs") Assert.Null(driver.TurnInputKey(K(c)));
        Assert.Contains("use tabs", Plain(driver.ComposeFrameRows()));
        Assert.Equal("use tabs", driver.TurnInputKey(Enter));
        Assert.Contains("steer the agent", Plain(driver.ComposeFrameRows()));        // cleared, box still up
        Assert.Null(driver.TurnInputKey(Enter));                                       // empty Enter sends nothing
        driver.TurnInputPaste("pasted note");
        Assert.Equal("pasted note", driver.TurnInputKey(Enter));
        driver.SetTurnInput(false);
        Assert.DoesNotContain("steer the agent", Plain(driver.ComposeFrameRows()));   // turn over: box gone
        Assert.Null(driver.TurnInputKey(K('x')));                                      // and inert
    }

    [Fact]
    public void SteerBox_AfterAPromptSubmit_ShowsNoStalePasteNotice()
    {
        // ReadLine's exit runs the paste cleanup on every submit; with nothing pending it must not leave
        // "Paste cancelled; draft retained." for the mid-turn box to render.
        var driver = new TuiDriver(new Term(), frameEngine: true);
        typeof(TuiDriver).GetMethod("CancelPendingPaste", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(driver, new object[] { true });
        driver.SetTurnInput(true);
        Assert.DoesNotContain("Paste cancelled", Plain(driver.ComposeFrameRows()));
        driver.TurnInputKey(K('a'));
        Assert.DoesNotContain("Paste cancelled", Plain(driver.ComposeFrameRows()));
    }

    [Fact]
    public void UnsentText_IsKeptForTheNextPrompt()
    {
        var driver = new TuiDriver(new Term(), frameEngine: true);
        driver.SetTurnInput(true);
        foreach (var c in "half typed") driver.TurnInputKey(K(c));
        driver.SetTurnInput(false);
        var draft = typeof(TuiDriver).GetField("_turnDraft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Equal("half typed", draft.GetValue(driver));
    }

    [Fact]
    public void SteerInputDuringTurn_FollowsTheConfigKey()
    {
        var names = new[] { "_driver", "_tuiActive", "_interactiveRenderMode" };
        var fields = names.Select(n => typeof(MuxConsole).GetField(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!).ToList();
        var saved = fields.ToDictionary(f => f, f => f.GetValue(null));
        bool stdio = MuxConsole.StdioMode, frame = MuxConsole.FrameEngineEnabled;
        var cfg = App.Config;
        try
        {
            fields[0].SetValue(null, new TuiDriver(new Term(), frameEngine: true));
            fields[1].SetValue(null, true);
            fields[2].SetValue(null, RenderMode.Tui);
            MuxConsole.StdioMode = false;
            MuxConsole.FrameEngineEnabled = true;
            App.Config = new AppConfig();
            Assert.False(MuxConsole.SteerInputDuringTurn);
            App.Config = new AppConfig { Console = new ConsoleConfig { InputDuringTurn = "steer" } };
            Assert.True(MuxConsole.SteerInputDuringTurn);
            MuxConsole.FrameEngineEnabled = false;                                     // inline renderer: unaffected
            Assert.False(MuxConsole.SteerInputDuringTurn);
        }
        finally
        {
            foreach (var (f, v) in saved) f.SetValue(null, v);
            MuxConsole.StdioMode = stdio;
            MuxConsole.FrameEngineEnabled = frame;
            App.Config = cfg;
        }
    }

    [Fact]
    public void Listener_SteerMode_RoutesTypingToTheBox_EscStillCancels_HideModeReplaysAsBefore()
    {
        const System.Reflection.BindingFlags Static = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var fields = new[] { typeof(ConsoleInputPump).GetField("_current", Static)!, typeof(MuxConsole).GetField("_driver", Static)!,
            typeof(MuxConsole).GetField("_tuiActive", Static)!, typeof(MuxConsole).GetField("_interactiveRenderMode", Static)! };
        var saved = fields.ToDictionary(f => f, f => f.GetValue(null));
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive, stdio = MuxConsole.StdioMode;
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var driver = new TuiDriver(new Term(), frameEngine: true);
            foreach (var f in fields) f.SetValue(null, f.Name switch { "_current" => pump, "_driver" => driver, "_tuiActive" => true, _ => RenderMode.Tui });
            ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false; MuxConsole.StdioMode = false;
            EscapeKeyListener.DrainReplayEventsTo(new List<ConsoleInputPump.InputEvent>());

            // steer: "q" and "\\" are typing, Enter yields the steer, nothing is replayed into the next prompt.
            var steers = new System.Collections.Concurrent.ConcurrentQueue<string>();
            driver.SetTurnInput(true);
            using (var turn = new CancellationTokenSource())
            using (EscapeKeyListener.Start(turn, stop.Token, null, null, () => { }, null,
                ev => { if (MuxConsole.TuiTurnInput(ev) is { } s) steers.Enqueue(s); }))
            {
                foreach (var c in "q\\x") pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(K(c)));
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(Enter));
                SpinWait.SpinUntil(() => !steers.IsEmpty, TimeSpan.FromSeconds(2));
                Assert.Equal(new[] { "q\\x" }, steers.ToArray());
                Assert.False(turn.IsCancellationRequested);                       // bare q did not cancel
                var replay = new List<ConsoleInputPump.InputEvent>();
                EscapeKeyListener.DrainReplayEventsTo(replay);
                Assert.Empty(replay);
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)));
                Assert.True(turn.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)), "Esc did not cancel");
            }
            driver.SetTurnInput(false);

            // hide (no turn-input callback): typed keys are replayed into the next prompt, as before.
            using (var turn = new CancellationTokenSource())
            using (EscapeKeyListener.Start(turn, stop.Token, null, null, null, null))
            {
                pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(K('z')));
                var replay = new List<ConsoleInputPump.InputEvent>();
                SpinWait.SpinUntil(() => { EscapeKeyListener.DrainReplayEventsTo(replay); return replay.Count > 0; }, TimeSpan.FromSeconds(2));
                Assert.Equal('z', Assert.Single(replay).Key.KeyChar);
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var pair in saved) pair.Key.SetValue(null, pair.Value);
            ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; MuxConsole.StdioMode = stdio;
        }
    }
}
