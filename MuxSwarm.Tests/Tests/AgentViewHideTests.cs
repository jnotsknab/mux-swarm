using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Actual Agent View -> capture registry -> renderer with raw keys and isolated, inert capture fixtures.</summary>
[Collection("ConsoleState")]
public class AgentViewHideTests
{
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static string Plain(string ansi) => Regex.Replace(ansi, "\u001b\\[[0-9;?]*[A-Za-z]", "");
    private sealed class Terminal : ITuiTerminal
    {
        public int Width => 110;
        public int Height => 35;
        public StringBuilder Output { get; } = new();
        public Action<string>? OnWrite;
        public void Write(string text) { Output.Append(text); OnWrite?.Invoke(text); }
        public void Flush() { }
    }
    private sealed class Scope : IDisposable
    {
        private readonly Dictionary<FieldInfo, object?> _saved;
        private readonly IList _captures;
        private readonly object[] _previousCaptures;
        private readonly bool _stdio = MuxConsole.StdioMode;
        private readonly Func<bool>? _composing = ConsoleInputPump.IsComposing;
        private readonly TextReader _input = MuxConsole.InputOverride;
        private readonly bool _prompt = ConsoleInputPump.PromptActive, _modal = ConsoleInputPump.ModalActive;
        public Terminal Term { get; } = new();
        public TuiDriver Driver { get; }
        public ConsoleInputPump Pump { get; } = ConsoleInputPump.CreateUnstartedForTest();
        public Scope(bool frame)
        {
            _saved = new[] { "_driver", "_tuiActive", "_interactiveRenderMode" }
                .Select(n => typeof(MuxConsole).GetField(n, Static)!)
                .Append(typeof(ConsoleInputPump).GetField("_current", Static)!)
                .ToDictionary(f => f, f => f.GetValue(null));
            _captures = (IList)typeof(MuxConsole).GetField("_activeCaptures", Static)!.GetValue(null)!;
            _previousCaptures = _captures.Cast<object>().ToArray();
            _captures.Clear();
            Driver = new TuiDriver(Term, frameEngine: frame);
            foreach (var f in _saved.Keys)
                f.SetValue(null, f.Name switch { "_driver" => Driver, "_tuiActive" => true, "_interactiveRenderMode" => RenderMode.Tui, _ => Pump });
            MuxConsole.StdioMode = false;
            MuxConsole.InputOverride = Console.In;
            ConsoleInputPump.IsComposing = () => false;
            ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
            Add("Worker", "Worker", "FIRSTBODY");
            Add("Worker", "Worker 2", "SECONDBODY");
            Driver.SetSubAgentActivity(new[] { ("Worker", "working", TuiComponents.Accent), ("Worker 2", "working", TuiComponents.Accent) }, 0);
            Term.Output.Clear();
        }
        private void Add(string name, string lane, string body)
        {
            var type = typeof(MuxConsole).GetNestedType("SubAgentCapture", BindingFlags.NonPublic)!;
            object capture = Activator.CreateInstance(type, nonPublic: true)!;
            type.GetField("Agent")!.SetValue(capture, name);
            type.GetField("Lane")!.SetValue(capture, lane);
            ((StringBuilder)type.GetField("Buffer")!.GetValue(capture)!).Append(body);
            _captures.Add(capture);
        }
        public int CaptureCount => _captures.Count;
        public void Raw(char c, int virtualKey = 0, uint modifiers = 0)
        {
            // Mirrors native ConPTY record observation: printable characters have VK=0.
            Assert.True(Win32ConsoleInput.TryTranslateKey(true, (ushort)virtualKey, c, modifiers, out var key));
            Pump.FeedRawKey(key);
        }
        public void Close() => Pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)));
        public void Enter() => Pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)));
        public bool Open() => MuxConsole.TuiEnterAgentView();
        public void Dispose()
        {
            _captures.Clear(); foreach (var cap in _previousCaptures) _captures.Add(cap);
            foreach (var pair in _saved) pair.Key.SetValue(null, pair.Value);
            MuxConsole.StdioMode = _stdio;
            MuxConsole.InputOverride = _input;
            ConsoleInputPump.IsComposing = _composing;
            ConsoleInputPump.PromptActive = _prompt; ConsoleInputPump.ModalActive = _modal;
            Pump.Dispose();
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, (int)ConsoleKey.NoName)]
    [InlineData(true, (int)ConsoleKey.NoName)]
    [InlineData(false, (int)ConsoleKey.H)]
    [InlineData(true, (int)ConsoleKey.H)]
    public void H_HidesSelectedLaneViaRealRegistryAndPreservesWorker(bool frame, int virtualKey)
    {
        using var scope = new Scope(frame);
        // Select exact duplicate-name lane by arrow (already transport-decoded).
        scope.Pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false)));
        scope.Raw('h', virtualKey); scope.Close();
        Assert.True(scope.Open());
        Assert.Contains("Worker 2", MuxConsole.HiddenSubAgentLanes());
        Assert.DoesNotContain("Worker 2", MuxConsole.VisibleSubAgentLanes());
        Assert.Contains("Worker", MuxConsole.VisibleSubAgentLanes());
        Assert.Equal(2, scope.CaptureCount);
        Assert.Contains("[hidden]", Plain(scope.Term.Output.ToString()));
        var rows = scope.Driver.BuildLiveFrame(100).Select(TuiMarkup.Plain).ToArray();
        Assert.DoesNotContain(rows, r => r.Contains("Worker 2"));
        Assert.Contains(rows, r => r.Contains("Worker"));
    }

    [Fact]
    public void HiddenMarkerIsLiteralVisibleTextAfterRenderingNotAnUnknownStyleTag()
    {
        var view = new AgentView(); var now = DateTime.UtcNow;
        view.SetRows(new[] { ("Worker", "working", TuiComponents.Accent) }, now);
        view.MarkHidden("Worker", true);
        var rows = view.RenderDashboard(100, now, 0).Select(TuiMarkup.ToAnsi).Select(Plain);
        Assert.Contains(rows, r => r.Contains("Worker") && r.Contains("[hidden]"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharacterNavigationHideAndQClose_WorkWithoutConsumingNextQueuedKey(bool frame)
    {
        using var scope = new Scope(frame);
        scope.Raw('j'); scope.Raw('k'); scope.Raw('j'); scope.Raw('H'); scope.Raw('q');
        scope.Raw('x'); scope.Close(); // bounded fallback; x must survive q's successful close.
        Assert.True(scope.Open());
        Assert.Contains("Worker 2", MuxConsole.HiddenSubAgentLanes());
        Assert.True(scope.Pump.TryTake(out var next, 0)); Assert.Equal('x', next.Key.KeyChar);
        Assert.Equal(2, scope.CaptureCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HideCollapsesExpandedPanel_EnterUnhidesExactLaneAndPreservesCapture(bool frame)
    {
        using var scope = new Scope(frame);
        Assert.True(scope.Driver.ToggleSubAgentExpanded("Worker 2", "SECONDBODY"));
        scope.Raw('j'); scope.Raw('h'); scope.Close();
        Assert.True(scope.Open());
        Assert.False(scope.Driver.IsSubAgentExpanded("Worker 2"));
        Assert.Contains("Worker 2", MuxConsole.HiddenSubAgentLanes());
        scope.Enter();
        Assert.True(scope.Open()); // selection survives by lane name
        Assert.DoesNotContain("Worker 2", MuxConsole.HiddenSubAgentLanes());
        Assert.True(scope.Driver.IsSubAgentExpanded("Worker 2"));
        Assert.Equal("Worker 2", scope.Driver.ForegroundAgent);
        Assert.Equal(2, scope.CaptureCount);
        Assert.Equal("Worker 2", MuxConsole.HideSubAgentLane("Worker 2"));
        Assert.Equal("Worker 2", MuxConsole.UnhideSubAgentLane("Worker 2"));
        Assert.Contains("Worker 2", MuxConsole.VisibleSubAgentLanes());
    }

    [Theory]
    [InlineData(4u)] // native LEFT_CTRL_PRESSED
    [InlineData(2u)] // native LEFT_ALT_PRESSED
    public void ModifiedHDoesNotHideAndPasteDoesNotInvokeCommands(uint modifiers)
    {
        using var scope = new Scope(true);
        scope.Raw('h', (int)ConsoleKey.H, modifiers);
        scope.Pump.Enqueue(ConsoleInputPump.InputEvent.OfPaste("hhHjq"));
        scope.Pump.Enqueue(ConsoleInputPump.InputEvent.OfInferredPaste("h"));
        scope.Close();
        Assert.True(scope.Open());
        Assert.Empty(MuxConsole.HiddenSubAgentLanes());
        Assert.Equal(2, scope.CaptureCount);
    }

    [Fact]
    public void MissingLaneDoesNotRenderAFalseHiddenConfirmation()
    {
        using var scope = new Scope(true);
        scope.Raw('h'); scope.Close();
        Assert.True(scope.Driver.EnterAgentView(new[] { ("unknown", "working", TuiComponents.Accent) }, _ => "body", _ => null));
        Assert.DoesNotContain("[hidden]", Plain(scope.Term.Output.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdlePromptBackslashUsesSameHidePathWithoutSubmittingItAsText(bool frame)
    {
        using var scope = new Scope(frame);
        scope.Driver.AgentViewOpener = scope.Open;
        scope.Raw('\\'); scope.Raw('h'); scope.Close();
        scope.Raw('o'); scope.Raw('k'); scope.Enter();
        Assert.Equal("ok", scope.Driver.ReadLineCore(scope.Pump));
        Assert.Contains("Worker", MuxConsole.HiddenSubAgentLanes());
        Assert.Equal(2, scope.CaptureCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MidTurnBackslashUsesSameHidePathWithoutCancellingLeadOrWorker(bool frame)
    {
        using var scope = new Scope(frame);
        using var lead = new CancellationTokenSource();
        using var child = new CancellationTokenSource();
        var ctsMap = (Dictionary<string, CancellationTokenSource>)typeof(MuxConsole).GetField("_laneCts", Static)!.GetValue(null)!;
        var previous = ctsMap.TryGetValue("Worker", out var old) ? old : null;
        ctsMap["Worker"] = child;
        using var finished = new ManualResetEventSlim();
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            scope.Raw('\\'); scope.Raw('h'); scope.Close();
            using var listener = EscapeKeyListener.Start(lead, outer.Token, null, null,
                () => { try { scope.Open(); } finally { finished.Set(); } });
            Assert.True(finished.Wait(TimeSpan.FromSeconds(3)), "Agent View did not complete from listener");
            Assert.Contains("Worker", MuxConsole.HiddenSubAgentLanes());
            Assert.False(lead.IsCancellationRequested); Assert.False(child.IsCancellationRequested);
            Assert.Equal(2, scope.CaptureCount);
        }
        finally
        {
            outer.Cancel();
            if (previous is null) ctsMap.Remove("Worker"); else ctsMap["Worker"] = previous;
        }
    }

    [Fact]
    public void DisposedPumpClosesTheDashboardInsteadOfWaitingForever()
    {
        using var scope = new Scope(true);
        bool hookFired = false;
        scope.Term.OnWrite = text =>
        {
            if (!Plain(text).Contains("▸ agents")) return;
            hookFired = true;
            scope.Pump.Dispose();
        };
        // If the hook or disposal exit regresses, a front-lane Escape still bounds this test.
        using var fallback = new System.Threading.Timer(_ => scope.Pump.PushFront(new[]
        {
            ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false))
        }), null, 2000, Timeout.Infinite);
        Assert.True(scope.Open());
        Assert.True(hookFired);
        Assert.False((bool)typeof(TuiDriver).GetField("_agentViewActive", Instance)!.GetValue(scope.Driver)!);
    }

    [Theory]
    [InlineData('h', (int)ConsoleKey.H)]
    [InlineData('j', (int)ConsoleKey.J)]
    [InlineData('k', (int)ConsoleKey.K)]
    [InlineData('m', (int)ConsoleKey.M)]
    [InlineData('q', (int)ConsoleKey.Q)]
    public void PrintableShortcutMatcherRejectsCtrlAndAltChords(char character, int key)
    {
        Assert.False(AgentView.IsShortcut(new ConsoleKeyInfo(character, (ConsoleKey)key, false, false, true), character, (ConsoleKey)key));
        Assert.False(AgentView.IsShortcut(new ConsoleKeyInfo(character, (ConsoleKey)key, false, true, false), character, (ConsoleKey)key));
        Assert.True(AgentView.IsShortcut(new ConsoleKeyInfo(char.ToUpperInvariant(character), ConsoleKey.NoName, true, false, false), character, (ConsoleKey)key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawCtrlQStillClosesDashboardAndPreservesFollowingInput(bool frame)
    {
        using var scope = new Scope(frame);
        scope.Raw('\u0011'); scope.Raw('x'); scope.Close();
        Assert.True(scope.Open());
        Assert.True(scope.Pump.TryTake(out var next, 0)); Assert.Equal('x', next.Key.KeyChar);
        Assert.Empty(MuxConsole.HiddenSubAgentLanes()); Assert.Equal(2, scope.CaptureCount);
    }

}
