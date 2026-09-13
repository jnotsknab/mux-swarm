using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Agent-picker command contracts; pure source/catalog checks never access live configuration.</summary>
public class AgentPickerTests
{
    [Fact]
    public void SwapCatalog_DescribesSearchableAgentSelectionRatherThanModelEditing()
    {
        var command = Assert.Single(TuiCommands.All, e => e.Cmd == "/swap");
        Assert.Equal(TuiCommands.Scope.ReplOnly, command.Scope);
        Assert.Contains("agent", command.Desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fuzzy", command.Desc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", command.Desc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Help_AdvertisesSwapSearchAndExplicitSelection()
    {
        var line = Assert.Single(Help.HelpText.Split('\n'), l => l.TrimStart().StartsWith("/swap "));
        Assert.Contains("search", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Enter", line);
        Assert.Contains("Esc", line);
    }

    [Fact]
    public void SwapHandler_OffersPickerBeforeRetainedNumberNameFallback()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "Engine", "CliCmdUtils.cs"));
        int start = source.IndexOf("public static void HandleAgentSwap()", StringComparison.Ordinal);
        int end = source.IndexOf("public static void HandleMaxP()", start, StringComparison.Ordinal);
        var method = source[start..end];
        Assert.Contains("MuxConsole.TryAgentPicker", method);
        Assert.True(method.IndexOf("TryAgentPicker", StringComparison.Ordinal) < method.IndexOf("MuxConsole.Prompt", StringComparison.Ordinal));
        Assert.Contains("SingleAgentOrchestrator.AgentDef = matched", method);
        Assert.DoesNotContain("WriteAllText", method);
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}


/// <summary>Pure roster filtering and display bounds, with no console or configuration access.</summary>
public class AgentPickerViewTests
{
    internal static Common.AgentDefinition Agent(string name, string description = "")
        => new(name, description, "unused-prompt", false, tools => tools);
    internal static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool ctrl = false)
        => new(c, key, false, false, ctrl);

    [Fact]
    public void Search_FuzzyNamesDescriptionTermsRankingAndExactIdentity()
    {
        var agents = new[] { Agent("Other", "CodeAgent alternative"), Agent("CodeAgent", "Writes code"), Agent("DataAnalysisAgent", "Tables and statistics") };
        var view = new AgentPickerView(agents, "Other");
        view.Paste("CodeAgent");
        Assert.Same(agents[1], view.Selected);
        view.Handle(Key(ConsoleKey.U, ctrl: true), 5);
        view.Paste("daa statistics");
        Assert.Equal(1, view.MatchCount);
        Assert.Same(agents[2], view.Selected);
        Assert.Equal(AgentPickerView.Action.Select, view.Handle(Key(ConsoleKey.Enter), 5));
        Assert.Equal("Other", view.CurrentName);
    }

    [Fact]
    public void ExplicitNullDescription_DoesNotBreakFuzzyFilteringOfAnyRosterMember()
    {
        var agents = new[] { Agent("NullDescription", null!), Agent("WebAgent", "Research") };
        var view = new AgentPickerView(agents, "NullDescription");
        view.Paste("wba");
        Assert.Same(agents[1], view.Selected);
    }

    [Fact]
    public void NoMatchesAndEmptyRosterNeverSelect_EditingDoesNotConsumeSearchLettersAsNavigation()
    {
        var view = new AgentPickerView(new[] { Agent("KimiAgent"), Agent("JackAgent") }, "KimiAgent");
        view.Handle(Key(ConsoleKey.J, 'j'), 5);
        Assert.Equal("j", view.Query);
        Assert.Equal("JackAgent", view.Selected!.Name);
        view.Paste("zzz");
        Assert.Null(view.Selected);
        Assert.Equal(AgentPickerView.Action.None, view.Handle(Key(ConsoleKey.Enter), 5));
        view.Handle(Key(ConsoleKey.U, ctrl: true), 5);
        Assert.Equal(2, view.MatchCount);
        var empty = new AgentPickerView(Array.Empty<Common.AgentDefinition>(), null);
        Assert.Null(empty.Selected);
        Assert.Equal(AgentPickerView.Action.None, empty.Handle(Key(ConsoleKey.Enter), 5));
    }

    [Theory]
    [InlineData(20, 10)]
    [InlineData(35, 14)]
    [InlineData(180, 30)]
    [InlineData(5, 2)]
    public void Render_IsExactlyBoundedAndLastSelectionVisible(int width, int height)
    {
        var agents = Enumerable.Range(0, 40).Select(i => Agent($"Agent{i:00}", "Long [description] 👩‍💻 漢字 " + new string('x', 300))).ToArray();
        var view = new AgentPickerView(agents, "Agent20");
        Assert.Same(agents[20], view.Selected);
        view.Handle(Key(ConsoleKey.End), 5);
        var rows = view.Render(width, height);
        Assert.Equal(height, rows.Count);
        Assert.All(rows, row => Assert.InRange(TuiMarkup.MarkupWidth(row), 0, width));
        if (width >= AgentPickerView.MinWidth && height >= AgentPickerView.MinHeight)
            Assert.Contains(rows, row => TuiMarkup.Plain(row).Contains("› Agent39"));
        else Assert.Contains("Esc", rows[0]);
    }

    [Fact]
    public void QueryAndMetadata_AreInertBoundedAndBackspaceRemovesGrapheme()
    {
        var agent = Agent("Code[Agent]\n\u001b", "Description\n[red]literal[/]\u001b[2J");
        var view = new AgentPickerView(new[] { agent }, agent.Name);
        view.Paste("e\u0301");
        view.Handle(Key(ConsoleKey.Backspace), 5);
        Assert.Equal("", view.Query);
        view.Paste(new string('x', 255) + "🙂");
        Assert.Equal(255, view.Query.Length);
        view.Handle(Key(ConsoleKey.U, ctrl: true), 5);
        var rendered = string.Join("", view.Render(100, 14));
        Assert.DoesNotContain('\u001b', rendered);
        Assert.DoesNotContain('\n', rendered);
        Assert.Contains("[[Agent]]", rendered);
        Assert.All(view.Render(100, 14), row => Assert.NotNull(TuiMarkup.ToAnsi(row)));
    }
}

/// <summary>Actual modal ownership and handler behavior using an unstarted input pump and sandbox fixtures.</summary>
[Collection("ConsoleState")]
public class AgentPickerDriverTests
{
    private const System.Reflection.BindingFlags StaticPrivate = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
    private const System.Reflection.BindingFlags InstancePrivate = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 90;
        public int Height { get; set; } = 20;
        public System.Text.StringBuilder Output { get; } = new();
        public System.Action<string>? OnWrite { get; set; }
        public void Write(string text) { Output.Append(text); OnWrite?.Invoke(text); }
        public void Flush() { }
    }
    private sealed class Scope : IDisposable
    {
        private readonly Dictionary<System.Reflection.FieldInfo, object?> _saved;
        private readonly bool _stdio = MuxConsole.StdioMode, _prompt = ConsoleInputPump.PromptActive, _modal = ConsoleInputPump.ModalActive;
        private readonly TextReader _input = MuxConsole.InputOverride;
        public Terminal Term { get; } = new();
        public TuiDriver Driver { get; }
        public ConsoleInputPump Pump { get; } = ConsoleInputPump.CreateUnstartedForTest();
        public Scope(bool frame)
        {
            _saved = new[] { "_driver", "_tuiActive", "_interactiveRenderMode" }
                .Select(n => typeof(MuxConsole).GetField(n, StaticPrivate)!)
                .Append(typeof(ConsoleInputPump).GetField("_current", StaticPrivate)!)
                .ToDictionary(f => f, f => f.GetValue(null));
            Driver = new TuiDriver(Term, frameEngine: frame);
            foreach (var field in _saved.Keys)
                field.SetValue(null, field.Name switch { "_driver" => Driver, "_tuiActive" => true, "_interactiveRenderMode" => RenderMode.Tui, _ => Pump });
            MuxConsole.StdioMode = false;
            MuxConsole.InputOverride = Console.In;
            ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
            Driver.CommitLine("original transcript");
        }
        public void Dispose()
        {
            foreach (var pair in _saved) pair.Key.SetValue(null, pair.Value);
            MuxConsole.StdioMode = _stdio; MuxConsole.InputOverride = _input;
            ConsoleInputPump.PromptActive = _prompt; ConsoleInputPump.ModalActive = _modal;
            Pump.Dispose();
        }
        public void AssertRestored()
        {
            Assert.False(ConsoleInputPump.PromptActive); Assert.False(ConsoleInputPump.ModalActive);
            Assert.False((bool)typeof(TuiDriver).GetField("_navActive", InstancePrivate)!.GetValue(Driver)!);
            Assert.Contains(Driver.ComposeFrameRows(), row => row.Contains("original transcript"));
        }
    }
    private static ConsoleInputPump.InputEvent Key(ConsoleKey key, char c = '\0', bool ctrl = false)
        => ConsoleInputPump.InputEvent.OfKey(AgentPickerViewTests.Key(key, c, ctrl));
    private static Common.AgentDefinition[] Agents() => new[] { AgentPickerViewTests.Agent("CodeAgent", "Writes code"), AgentPickerViewTests.Agent("WebAgent", "Research") };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchAndEnter_ReturnExactDefinitionOnceAndPreserveFollowingInput(bool frame)
    {
        using var scope = new Scope(frame);
        var agents = Agents();
        scope.Pump.PushFront(new[] { ConsoleInputPump.InputEvent.OfPaste("wba"), Key(ConsoleKey.Enter), Key(ConsoleKey.X, 'x') });
        Assert.True(MuxConsole.TryAgentPicker(agents, "CodeAgent", out var selected));
        Assert.Same(agents[1], selected);
        Assert.True(scope.Pump.TryTake(out var next, 0));
        Assert.Equal('x', next.Key.KeyChar);
        scope.AssertRestored();
        if (!frame) Assert.Contains(Ansi.LeaveAltScreen, scope.Term.Output.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EscapeOrCtrlQ_AfterInferredPasteCancelsWithoutFallback(bool frame, bool ctrlQ)
    {
        using var scope = new Scope(frame);
        scope.Pump.PushFront(new[] { ConsoleInputPump.InputEvent.OfInferredPaste("WebAgent\r\n"), ctrlQ ? Key(ConsoleKey.Q, ctrl: true) : Key(ConsoleKey.Escape) });
        Assert.True(MuxConsole.TryAgentPicker(Agents(), "CodeAgent", out var selected));
        Assert.Null(selected);
        scope.AssertRestored();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResizeTinyThenRestore_BlocksBlindEnterAndKeepsChoiceVisible(bool frame)
    {
        using var scope = new Scope(frame);
        var agents = Agents(); int paints = 0;
        scope.Term.OnWrite = output =>
        {
            if (!output.Contains("SWAP AGENT")) return;
            if (++paints == 1) { scope.Term.Height = 3; scope.Pump.Enqueue(Key(ConsoleKey.Enter)); }
        };
        // Tiny repaint restores size only after an ignored Enter; enqueue cancel from its output.
        scope.Term.OnWrite += output =>
        {
            if (!output.Contains("Resize to")) return;
            scope.Term.Height = 20;
            scope.Pump.Enqueue(Key(ConsoleKey.Escape));
        };
        Assert.True(scope.Driver.RunAgentPicker(new AgentPickerView(agents, "CodeAgent"), out var selected, input: scope.Pump));
        Assert.Null(selected);
        scope.AssertRestored();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrowingFromTinyWarning_RequiresAVisibleChoiceBeforeAcceptingEnter(bool frame)
    {
        using var scope = new Scope(frame);
        scope.Term.Height = 3;
        bool recovered = false;
        scope.Term.OnWrite = output =>
        {
            if (recovered || !output.Contains("Resize to")) return;
            recovered = true;
            scope.Term.Height = 20;
            scope.Pump.PushFront(new[] { Key(ConsoleKey.Enter), Key(ConsoleKey.Escape) });
        };
        Assert.True(scope.Driver.RunAgentPicker(new AgentPickerView(Agents(), "CodeAgent"), out var selected, input: scope.Pump));
        Assert.Null(selected);
        Assert.Contains("SWAP AGENT", scope.Term.Output.ToString());
        scope.AssertRestored();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDisposalAndPaintFailureRestoreOwnership(bool frame)
    {
        using var scope = new Scope(frame);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.True(scope.Driver.RunAgentPicker(new AgentPickerView(Agents(), null), out var selected, cts.Token, scope.Pump));
        Assert.Null(selected); scope.AssertRestored();
        scope.Term.OnWrite = text => { if (text.Contains("SWAP AGENT")) throw new IOException("synthetic terminal failure"); };
        Assert.Throws<IOException>(() => scope.Driver.RunAgentPicker(new AgentPickerView(Agents(), null), out _, input: scope.Pump));
        scope.Term.OnWrite = null; scope.AssertRestored();
        scope.Term.OnWrite = text => { if (text.Contains("SWAP AGENT")) scope.Pump.Dispose(); };
        Assert.True(scope.Driver.RunAgentPicker(new AgentPickerView(Agents(), null), out selected, input: scope.Pump));
        Assert.Null(selected); scope.Term.OnWrite = null; scope.AssertRestored();
    }

    [Fact]
    public void BusyModalAndScriptedInput_DoNotStealPumpEvents()
    {
        using var scope = new Scope(true);
        scope.Pump.Enqueue(Key(ConsoleKey.Enter));
        ConsoleInputPump.ModalActive = true;
        Assert.False(scope.Driver.RunAgentPicker(new AgentPickerView(Agents(), null), out _, input: scope.Pump));
        Assert.True(ConsoleInputPump.ModalActive);
        ConsoleInputPump.ModalActive = false;
        MuxConsole.InputOverride = new StringReader("2\n");
        Assert.False(MuxConsole.TryAgentPicker(Agents(), null, out _));
        Assert.True(scope.Pump.TryTake(out var next, 0));
        Assert.Equal(ConsoleKey.Enter, next.Key.Key);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RealSwapHandler_ChangesOnlyOverrideOnEnterAndNeverWritesConfig(bool frame, bool cancel)
    {
        using var scope = new Scope(frame);
        using var fixture = new ConfigFixture();
        var original = AgentPickerViewTests.Agent("Main", "Existing override");
        SingleAgentOrchestrator.AgentDef = original;
        scope.Pump.PushFront(new[] { ConsoleInputPump.InputEvent.OfPaste("wba"), Key(cancel ? ConsoleKey.Escape : ConsoleKey.Enter) });
        CliCmdUtils.HandleAgentSwap();
        if (cancel) Assert.Same(original, SingleAgentOrchestrator.AgentDef);
        else
        {
            Assert.Equal("WebAgent", SingleAgentOrchestrator.AgentDef!.Name);
            Assert.Equal("Research", SingleAgentOrchestrator.AgentDef.Description);
            Assert.True(SingleAgentOrchestrator.AgentDef.CanDelegate);
        }
        fixture.AssertUnchanged(); scope.AssertRestored();
    }

    [Theory]
    [InlineData("2", "WebAgent")]
    [InlineData("webagent", "WebAgent")]
    [InlineData("wba", "Original")]
    public void StdioFallback_RetainsNumberAndExactNameOnly(string input, string expected)
    {
        using var scope = new Scope(true);
        using var fixture = new ConfigFixture();
        SingleAgentOrchestrator.AgentDef = AgentPickerViewTests.Agent("Original");
        MuxConsole.StdioMode = true;
        MuxConsole.InputOverride = new StringReader(input + "\n");
        var output = Console.Out;
        using var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            CliCmdUtils.HandleAgentSwap();
            Assert.Equal(expected, SingleAgentOrchestrator.AgentDef!.Name);
            Assert.Contains("input_request", capture.ToString());
            Assert.DoesNotContain("SWAP AGENT", scope.Term.Output.ToString());
            fixture.AssertUnchanged();
        }
        finally { Console.SetOut(output); }
    }

    private sealed class ConfigFixture : IDisposable
    {
        private readonly System.Reflection.FieldInfo _path = typeof(PlatformContext).GetField("_swarmPathOverride", StaticPrivate)!;
        private readonly object? _previousPath;
        private readonly string _swarmPath = MultiAgentOrchestrator.SwarmConfPath;
        private readonly Common.AgentDefinition? _agent = SingleAgentOrchestrator.AgentDef;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-agent-picker-" + Guid.NewGuid().ToString("N"));
        private readonly string _file;
        private readonly byte[] _bytes;
        public ConfigFixture()
        {
            _previousPath = _path.GetValue(null);
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "Swarm.json");
            var config = new SwarmConfig
            {
                SingleAgent = new AgentConfig { Name = "Main", Description = "Default" },
                Agents = new List<AgentConfig> { new() { Name = "WebAgent", Description = "Research", CanDelegate = true } }
            };
            _bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(config));
            File.WriteAllBytes(_file, _bytes);
            _path.SetValue(null, _file);
        }
        public void AssertUnchanged() => Assert.Equal(_bytes, File.ReadAllBytes(_file));
        public void Dispose()
        {
            _path.SetValue(null, _previousPath);
            SingleAgentOrchestrator.AgentDef = _agent;
            Directory.Delete(_dir, recursive: true);
        }
    }

}
