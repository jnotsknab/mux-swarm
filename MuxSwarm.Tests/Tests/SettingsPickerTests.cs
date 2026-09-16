using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Settings picker discoverability and compatibility contracts.</summary>
public class SettingsPickerContractTests
{
    [Fact]
    public void HelpAndCatalogAdvertiseNativeSearchAndExplicitApply()
    {
        Assert.Contains("F4", Help.HelpText);
        var entry = Assert.Single(TuiCommands.All, x => x.Cmd == "/set");
        Assert.Contains("search", entry.Desc, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TuiCommands.Scope.ReplOnly, entry.Scope);
    }
    [Theory]
    [InlineData("/set", true)]
    [InlineData("/set collapse", true)]
    [InlineData("/set collapse 10", false)]
    public void ExistingInlineCommandFormRemainsSeparate(string command, bool interactive)
        => Assert.Equal(interactive, TuiConfigCommands.NeedsInteractive(command));

    [Fact]
    public void InteractiveSetUsesDedicatedViewBeforeLegacyFallback()
    {
        string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "..", "Engine", "Tui", "TuiConfigCommands.cs"));
        string text = File.ReadAllText(file);
        Assert.Contains("MuxConsole.TrySettingsPicker", text);
    }
    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}


/// <summary>Pure settings selection, staged text and visible behavior.</summary>
public class SettingsPickerViewTests
{
    internal static TuiConfigCommands.SettingSnapshot Item(string name, string current = "10", string hint = "<int>", bool secret = false, params string[] aliases)
        => new(name, aliases, secret ? "(masked)" : current, hint, "Setting " + name, secret);
    internal static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool ctrl = false) => new(c, key, false, false, ctrl);

    [Fact]
    public void SearchAliasesAndEditStageOnly_F4IsOnlyApplyIntent()
    {
        var view = new SettingsPickerView(new[] { Item("other"), Item("collapseToolLines", aliases: new[] { "collapse" }) });
        view.Paste("ctl"); Assert.Equal("collapseToolLines", view.Selected!.Name);
        Assert.Equal(SettingsPickerView.Action.None, view.Handle(Key(ConsoleKey.Enter), 5));
        Assert.True(view.Editing); Assert.Null(view.Selection);
        view.Paste("25"); Assert.Equal("25", view.Draft);
        Assert.Equal(SettingsPickerView.Action.Apply, view.Handle(Key(ConsoleKey.F4), 5));
        Assert.Equal(new TuiConfigCommands.SettingSelection("collapseToolLines", "25"), view.Selection);
        Assert.Equal(SettingsPickerView.Action.Cancel, view.Handle(Key(ConsoleKey.Escape), 5));
    }
    [Fact]
    public void BooleanAndEnumChoiceEditorsUseExistingHintAndKeepDraftLocal()
    {
        var view = new SettingsPickerView(new[] { Item("flag", "true", "on|off") }, "flag");
        view.Handle(Key(ConsoleKey.DownArrow), 5);
        Assert.Equal("off", view.Draft);
        Assert.Equal(SettingsPickerView.Action.None, view.Handle(Key(ConsoleKey.Enter), 5));
        Assert.False(view.Editing); Assert.Null(view.Selection);
        view.Handle(Key(ConsoleKey.Enter), 5); Assert.Equal("true", view.Draft); // canceled draft does not change snapshot
    }
    [Fact]
    public void SecretDraftIsVisibleWhileTyping_StoredValueStaysMasked_PasteIsNotAnAction()
    {
        // v0.14.1 contract (Jonathan): the LIVE DRAFT is never masked - users must see what they
        // type. Only the STORED current value renders as (masked), and pasted text must never be
        // interpreted as commands/actions.
        var view = new SettingsPickerView(new[] { Item("serve.auth.token", secret: true) }, "serve.auth.token");
        view.Paste("unique-secret\r\n/set renderEngine inline");
        Assert.NotNull(view.Selection);
        string output = string.Join("\n", view.Render(100, 20));
        Assert.Contains("unique-secret", output);       // typed draft visible
        Assert.Contains("(masked)", output);            // stored Current stays masked
        // The embedded newline + "/set renderEngine inline" lands INSIDE the draft as flattened
        // inert text (visible below) - proving paste is never interpreted as a command/action.
        Assert.Contains("renderEngine", output);
        Assert.Equal(SettingsPickerView.Action.Cancel, view.Handle(Key(ConsoleKey.Q, ctrl: true), 5));
    }
    [Theory]
    [InlineData(40,14)] [InlineData(80,24)] [InlineData(160,40)] [InlineData(5,2)]
    public void RenderKeepsSelectionAndRowsBounded(int width, int height)
    {
        var items = Enumerable.Range(0,80).Select(i => Item($"setting{i:00}")).ToArray();
        var view = new SettingsPickerView(items);
        view.Handle(Key(ConsoleKey.End), 10);
        var rows = view.Render(width,height);
        Assert.Equal(height,rows.Count);
        Assert.All(rows,r => Assert.InRange(TuiMarkup.MarkupWidth(r),0,width));
        if(width >= SettingsPickerView.MinWidth && height >= SettingsPickerView.MinHeight)
            Assert.Contains(rows,r => TuiMarkup.Plain(r).Contains("› setting79"));
    }
    [Fact]
    public void SearchAndDraftCaretFollowMidTextEditsAndUnicodeGraphemes()
    {
        var view = new SettingsPickerView(new[] { Item("abcd") });
        view.Paste("abcd"); view.Handle(Key(ConsoleKey.LeftArrow),5);view.Handle(Key(ConsoleKey.LeftArrow),5);
        Assert.Contains(view.Render(60,18),r=>TuiMarkup.Plain(r).Contains("ab▏cd"));
        view.Handle(Key(ConsoleKey.X,'x'),5);Assert.Equal("abxcd",view.Query);
        var edit = new SettingsPickerView(new[] {Item("text","e\u0301🙂","<text>")},"text");
        edit.Handle(Key(ConsoleKey.Backspace),5);Assert.Equal("e\u0301",edit.Draft);
        edit.Handle(Key(ConsoleKey.Backspace),5);Assert.Equal("",edit.Draft);
        edit.Paste(new string('x',2000)+"終");
        Assert.Contains(edit.Render(60,18),r=>TuiMarkup.Plain(r).Contains("終▏"));
        Assert.Equal(2001,edit.Draft.Length);
    }

    [Fact]
    public void EmptyFilterAndOversizedPasteCannotApply()
    {
        var view = new SettingsPickerView(new[] { Item("one") });
        view.Paste("missing"); Assert.Equal(0,view.MatchCount);
        Assert.Equal(SettingsPickerView.Action.None,view.Handle(Key(ConsoleKey.F4),5));
        view.Handle(Key(ConsoleKey.U,ctrl:true),5); view.Handle(Key(ConsoleKey.Enter),5);
        view.Paste(new string('x',65537)); Assert.Equal("10",view.Draft);
        Assert.Null(view.Selection); Assert.Contains("too long",view.Message);
    }
}

/// <summary>Settings bridge uses sandbox-only configs and restores global pointers/flags.</summary>
[Collection("ConsoleState")]
public class SettingsPickerDriverTests
{
    private const System.Reflection.BindingFlags Static = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
    private const System.Reflection.BindingFlags Instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 100;
        public int Height { get; set; } = 24;
        public System.Text.StringBuilder Output { get; } = new();
        public Action<string>? OnWrite;
        public void Write(string text) { Output.Append(text); OnWrite?.Invoke(text); }
        public void Flush() { }
    }
    private sealed class Scope : IDisposable
    {
        private readonly Dictionary<System.Reflection.FieldInfo, object?> _saved;
        private readonly AppConfig _config = App.Config;
        private readonly SwarmConfig? _swarm = App.SwarmConfig;
        private readonly bool _stdio = MuxConsole.StdioMode, _prompt = ConsoleInputPump.PromptActive, _modal = ConsoleInputPump.ModalActive;
        private readonly TextReader _input = MuxConsole.InputOverride;
        private readonly int _collapse = MuxConsole.CollapseToolLines;
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),"mux-settings-"+Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(DirectoryPath,"Config.json");
        public string SwarmPath => Path.Combine(DirectoryPath,"Swarm.json");
        private readonly System.Threading.Timer _fallback;
        public Terminal Term { get; } = new();
        public TuiDriver Driver { get; }
        public ConsoleInputPump Pump { get; } = ConsoleInputPump.CreateUnstartedForTest();
        public Scope(bool frame)
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            _saved = new[]{"_driver","_tuiActive","_interactiveRenderMode"}.Select(n=>typeof(MuxConsole).GetField(n,Static)!)
                .Concat(new[]{typeof(ConsoleInputPump).GetField("_current",Static)!,typeof(PlatformContext).GetField("_configPathOverride",Static)!,typeof(PlatformContext).GetField("_swarmPathOverride",Static)!})
                .ToDictionary(f=>f,f=>f.GetValue(null));
            Driver = new TuiDriver(Term,frameEngine:frame);
            foreach(var f in _saved.Keys) f.SetValue(null,f.Name switch
            { "_driver"=>Driver,"_tuiActive"=>true,"_interactiveRenderMode"=>RenderMode.Tui,"_current"=>Pump,"_configPathOverride"=>ConfigPath,_=>SwarmPath });
            App.Config = new AppConfig(); App.SwarmConfig = new SwarmConfig { Agents = new() {new AgentConfig{Name="Test",Model="m1"}} };
            MuxConsole.StdioMode=false; MuxConsole.InputOverride=Console.In;
            ConsoleInputPump.PromptActive=false;ConsoleInputPump.ModalActive=false;
            File.WriteAllText(ConfigPath,System.Text.Json.JsonSerializer.Serialize(App.Config));
            File.WriteAllText(SwarmPath,System.Text.Json.JsonSerializer.Serialize(App.SwarmConfig));
            Driver.CommitLine("existing transcript"); Term.Output.Clear();
            _fallback = new System.Threading.Timer(_ => Pump.PushFront(new[] { Key(ConsoleKey.Escape) }), null, 3000, 1000);
        }
        public void Queue(params ConsoleInputPump.InputEvent[] events)=>Pump.PushFront(events);
        public void AssertRestored()
        {
            Assert.False(ConsoleInputPump.PromptActive);Assert.False(ConsoleInputPump.ModalActive);
            Assert.False((bool)typeof(TuiDriver).GetField("_navActive",Instance)!.GetValue(Driver)!);
        }
        public void Dispose()
        {
            _fallback.Dispose();
            foreach(var pair in _saved)pair.Key.SetValue(null,pair.Value);
            App.Config=_config;App.SwarmConfig=_swarm;MuxConsole.StdioMode=_stdio;MuxConsole.InputOverride=_input;
            ConsoleInputPump.PromptActive=_prompt;ConsoleInputPump.ModalActive=_modal;
            MuxConsole.CollapseToolLines=_collapse;
            Pump.Dispose();System.IO.Directory.Delete(DirectoryPath,true);
        }
    }
    private static ConsoleInputPump.InputEvent Key(ConsoleKey k,char c='\0',bool ctrl=false)
        =>ConsoleInputPump.InputEvent.OfKey(SettingsPickerViewTests.Key(k,c,ctrl));
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NativeCancelDoesNotSaveOrMaterializeNullConfig(bool frame)
    {
        using var scope=new Scope(frame);
        App.Config.Ultra=null!;
        string before=System.Text.Json.JsonSerializer.Serialize(App.Config);
        byte[] disk=File.ReadAllBytes(scope.ConfigPath);
        scope.Queue(Key(ConsoleKey.Escape));
        var result=TuiConfigCommands.RunInteractive("/set");
        Assert.True(result.Cancelled);Assert.False(result.Ok);
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(App.Config));
        Assert.Equal(disk,File.ReadAllBytes(scope.ConfigPath));scope.AssertRestored();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ApplyUsesRealCuratedValidatorAndLiveSetterOnlyAfterModalExit(bool frame)
    {
        using var scope=new Scope(frame);
        scope.Queue(ConsoleInputPump.InputEvent.OfPaste("27"),Key(ConsoleKey.F4),Key(ConsoleKey.X,'x'));
        var result=TuiConfigCommands.RunInteractive("/set collapse");
        Assert.True(result.Ok,result.Message);Assert.False(result.Cancelled);
        Assert.Equal(27,App.Config.Console.CollapseToolLines);Assert.Equal(27,MuxConsole.CollapseToolLines);
        Assert.Equal(27,(int)typeof(TuiDriver).GetField("_collapseToolLines",Instance)!.GetValue(scope.Driver)!);
        using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(scope.ConfigPath));
        Assert.Equal(27,json.RootElement.GetProperty("console").GetProperty("collapseToolLines").GetInt32());
        Assert.True(scope.Pump.TryTake(out var next,0));Assert.Equal('x',next.Key.KeyChar);scope.AssertRestored();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void InvalidValueKeepsEditorUntilCorrected(bool frame)
    {
        using var scope=new Scope(frame);
        scope.Queue(ConsoleInputPump.InputEvent.OfPaste("-1"),Key(ConsoleKey.F4),Key(ConsoleKey.U,ctrl:true),ConsoleInputPump.InputEvent.OfPaste("19"),Key(ConsoleKey.F4));
        var result=TuiConfigCommands.RunInteractive("/set collapse");
        Assert.True(result.Ok,result.Message + scope.Term.Output.ToString());Assert.Equal(19,App.Config.Console.CollapseToolLines);
        Assert.Contains("Could not apply/save",scope.Term.Output.ToString());scope.AssertRestored();
    }
    [Fact]
    public void SnapshotsMaskSecretsAndApplyRedactsSuccess()
    {
        using var scope=new Scope(true);
        App.Config.Serve.Auth.Token="original-secret";
        var snapshots=TuiConfigCommands.GetSettingSnapshots();
        var token=Assert.Single(snapshots,s=>s.Name=="serve.auth.token");
        Assert.True(token.Sensitive);Assert.DoesNotContain("original-secret",token.Current);
        var baseline=TuiConfigCommands.CapturePickerBaseline();
        var result=TuiConfigCommands.ApplyPickerSelection(new("serve.auth.token","new-secret"),baseline);
        Assert.True(result.Ok,result.Message);Assert.DoesNotContain("new-secret",result.Message);
        Assert.Equal("new-secret",App.Config.Serve.Auth.Token);
    }
    [Fact]
    public void ReflectedSwarmPersistsAndStaleBaselineRejectsWithoutOverwriting()
    {
        using var scope=new Scope(true);
        var baseline=TuiConfigCommands.CapturePickerBaseline();
        var result=TuiConfigCommands.ApplyPickerSelection(new("swarm.agents.Test.model","m2"),baseline);
        Assert.True(result.Ok,result.Message);Assert.Contains("m2",File.ReadAllText(scope.SwarmPath));
        Assert.False(TuiConfigCommands.ApplyPickerSelection(new("swarm.agents.Test.model","m3"),baseline).Ok);
        Assert.Equal("m2",App.SwarmConfig!.Agents[0].Model);
    }
    [Fact]
    public void StdioRetainsLegacyValuePromptAndInlineCommandWorks()
    {
        using var scope=new Scope(true);MuxConsole.StdioMode=true;MuxConsole.InputOverride=new StringReader("18\n");
        var output=Console.Out;using var capture=new StringWriter();
        try
        {
            Console.SetOut(capture);
            var result=TuiConfigCommands.RunInteractive("/set collapse");
            Assert.True(result.Ok);Assert.Equal(18,App.Config.Console.CollapseToolLines);
            Assert.Contains("input_request",capture.ToString());Assert.DoesNotContain("SETTINGS",scope.Term.Output.ToString());
            Assert.True(TuiConfigCommands.Handle("/set collapse 21").Ok);Assert.Equal(21,App.Config.Console.CollapseToolLines);
        }
        finally {Console.SetOut(output);}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void TinyResizeAndRecoveryNeverAppliesUnseenDraft(bool frame)
    {
        using var scope=new Scope(frame);
        scope.Term.Height=3;
        bool restored=false;
        scope.Term.OnWrite = text =>
        {
            if(restored || !text.Contains("Resize to"))return;
            restored=true;scope.Term.Height=24;
            scope.Queue(Key(ConsoleKey.F4),Key(ConsoleKey.Escape));
        };
        var view=new SettingsPickerView(new[]{SettingsPickerViewTests.Item("value")},"value");view.Paste("22");
        Assert.True(scope.Driver.RunSettingsPicker(view,out var selection,input:scope.Pump));
        Assert.Null(selection);Assert.True(restored);scope.AssertRestored();
        if(!frame)Assert.Contains(Ansi.LeaveAltScreen,scope.Term.Output.ToString());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CancelDisposalAndOutputFailureRestoreModalOwnership(bool frame)
    {
        using var scope=new Scope(frame);
        var view=new SettingsPickerView(new[]{SettingsPickerViewTests.Item("value")});
        Assert.True(scope.Driver.RunSettingsPicker(view,out _,new CancellationToken(true),scope.Pump));scope.AssertRestored();
        scope.Term.OnWrite=text=>{if(text.Contains("SETTINGS"))throw new IOException("test output error");};
        Assert.Throws<IOException>(()=>scope.Driver.RunSettingsPicker(view,out _,input:scope.Pump));
        scope.Term.OnWrite=null;scope.AssertRestored();
        scope.Pump.Dispose();Assert.False(scope.Driver.RunSettingsPicker(view,out _,input:scope.Pump));scope.AssertRestored();
    }
    [Fact]
    public void BusyViewDoesNotConsumeInputOrStartSetter()
    {
        using var scope=new Scope(true);ConsoleInputPump.ModalActive=true;
        scope.Queue(Key(ConsoleKey.Enter));
        Assert.False(scope.Driver.RunSettingsPicker(new SettingsPickerView(Array.Empty<TuiConfigCommands.SettingSnapshot>()),out _,input:scope.Pump));
        Assert.True(ConsoleInputPump.ModalActive);Assert.True(scope.Pump.TryTake(out var ev,0));Assert.Equal(ConsoleKey.Enter,ev.Key.Key);
    }
    [Fact]
    public void ExternalConfigWriteRejectsApplyAndPreservesExternalBytes()
    {
        using var scope=new Scope(true);var baseline=TuiConfigCommands.CapturePickerBaseline();
        File.WriteAllText(scope.ConfigPath,"{\"external\":true}");
        var result=TuiConfigCommands.ApplyPickerSelection(new("collapse","25"),baseline);
        Assert.False(result.Ok);Assert.Contains("changed",result.Message);
        Assert.Equal("{\"external\":true}",File.ReadAllText(scope.ConfigPath));
        Assert.NotEqual(25,App.Config.Console.CollapseToolLines);
    }
    [Fact]
    public void FailedSaveIsReportedAndDoesNotPretendLegacySetterRolledBack()
    {
        using var scope=new Scope(true);
        File.Delete(scope.ConfigPath);System.IO.Directory.CreateDirectory(scope.ConfigPath);
        var baseline=TuiConfigCommands.CapturePickerBaseline();
        var result=TuiConfigCommands.ApplyPickerSelection(new("collapse","31"),baseline);
        Assert.False(result.Ok);Assert.Contains("live effects may already have run",result.Message);
        Assert.Equal(31,App.Config.Console.CollapseToolLines); // established backend semantics, not claimed atomic
    }
    [Fact]
    public void ReadOnlyReflectionDoesNotMaterializeAndDefaultWalkStillDoes()
    {
        var config=new AppConfig {Ultra=null!};
        Assert.Contains(ConfigReflector.Walk(config,"",materializeNulls:false),x=>x.Path=="ultra.thinkingBudget");
        Assert.Null(config.Ultra);
        Assert.Contains(ConfigReflector.Walk(config,""),x=>x.Path=="ultra.thinkingBudget");
        Assert.NotNull(config.Ultra);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void EmptyDraftEnterAndEscapeNeverWritesAndNextInputSurvives(bool frame)
    {
        using var scope=new Scope(frame);var before=File.ReadAllBytes(scope.ConfigPath);
        scope.Queue(Key(ConsoleKey.U,ctrl:true),Key(ConsoleKey.F4),Key(ConsoleKey.Escape),Key(ConsoleKey.X,'x'));
        var result=TuiConfigCommands.RunInteractive("/set collapse");
        Assert.True(result.Cancelled);Assert.Equal(before,File.ReadAllBytes(scope.ConfigPath));
        Assert.True(scope.Pump.TryTake(out var next,0));Assert.Equal('x',next.Key.KeyChar);scope.AssertRestored();
    }

    [Fact]
    public void InvalidReflectedValueDoesNotMaterializeNullsOrPreventRetry()
    {
        using var scope=new Scope(true);App.Config.Ultra=null!;
        var baseline=TuiConfigCommands.CapturePickerBaseline();
        var invalid=TuiConfigCommands.ApplyPickerSelection(new("console.scrollSpeedRows","invalid"),baseline);
        Assert.False(invalid.Ok);Assert.Null(App.Config.Ultra);
        var corrected=TuiConfigCommands.ApplyPickerSelection(new("console.scrollSpeedRows","12"),baseline);
        Assert.True(corrected.Ok,corrected.Message);Assert.Equal(12,App.Config.Console.ScrollSpeedRows);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void RawUnframedMultilinePasteStaysDraftWhilePickerOwnsInput(bool frame)
    {
        using var scope=new Scope(frame);var view=new SettingsPickerView(new[]{SettingsPickerViewTests.Item("value","old","<text>")},"value");
        var previous=ConsoleInputPump.IsComposing;
        ConsoleInputPump.IsComposing=()=>false;
        bool fed=false;
        scope.Term.OnWrite=text=>
        {
            if(fed || !text.Contains("SETTINGS"))return;
            fed=true;
            foreach(char c in "abc\rdef")scope.Pump.FeedRawKey(new ConsoleKeyInfo(c,0,false,false,false));
            scope.Pump.FeedRawKey(new ConsoleKeyInfo('\0',ConsoleKey.F4,false,false,false));
        };
        try
        {
            Assert.True(scope.Driver.RunSettingsPicker(view,out var selection,input:scope.Pump));
            Assert.NotNull(selection);Assert.Equal("abc def",selection.Value);
            Assert.False(ConsoleInputPump.IsComposing!());scope.AssertRestored();
        }
        finally {ConsoleInputPump.IsComposing=previous;}
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ValidationFailureThenCancelLeavesNullsAndFilesIntact(bool frame)
    {
        using var scope=new Scope(frame);App.Config.Ultra=null!;
        string before=System.Text.Json.JsonSerializer.Serialize(App.Config);var file=File.ReadAllBytes(scope.ConfigPath);
        scope.Queue(ConsoleInputPump.InputEvent.OfPaste("bad"),Key(ConsoleKey.F4),Key(ConsoleKey.Escape));
        var result=TuiConfigCommands.RunInteractive("/set console.scrollSpeedRows");
        Assert.True(result.Cancelled);Assert.Null(App.Config.Ultra);
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(App.Config));Assert.Equal(file,File.ReadAllBytes(scope.ConfigPath));
    }
    [Fact]
    public void NativeCatalogKeepsCuratedAliasesAndReflectedNamesDistinct()
    {
        using var scope=new Scope(true);var items=TuiConfigCommands.GetSettingSnapshots();
        var curated=Assert.Single(items,x=>x.Name=="collapseToolLines");
        Assert.Contains("collapse",curated.Aliases);Assert.Equal("<int>=0",curated.ValueHint);
        Assert.Contains(items,x=>x.Name=="console.collapseToolLines"&&x.ValueHint=="<int>");
        var baseline=TuiConfigCommands.CapturePickerBaseline();
        Assert.False(TuiConfigCommands.ApplyPickerSelection(new("collapseToolLines","-1"),baseline).Ok);
        Assert.True(TuiConfigCommands.ApplyPickerSelection(new("console.collapseToolLines","-1"),baseline).Ok);
        Assert.Equal(-1,App.Config.Console.CollapseToolLines); // preserve existing reflected semantics, don't normalize silently
    }
    [Fact]
    public void ReflectedPrevalidationRetainsEnumNullableAndNumericConversionRules()
    {
        var swarm=new SwarmConfig { Agents=new(){new AgentConfig{Name="A",Model="m1"}}};
        var model=ConfigReflector.Walk(swarm,"swarm",false).Single(x=>x.Path=="swarm.agents.A.model");
        Assert.True(model.Validate!("null").ok);Assert.Equal("m1",swarm.Agents[0].Model);
        Assert.True(model.Set("null").ok);Assert.Null(swarm.Agents[0].Model);
        var cfg=new AppConfig();var count=ConfigReflector.Walk(cfg,"",false).Single(x=>x.Path=="console.scrollSpeedRows");
        int original=cfg.Console.ScrollSpeedRows;Assert.False(count.Validate!("bad").ok);Assert.Equal(original,cfg.Console.ScrollSpeedRows);
        Assert.True(count.Validate("17").ok);Assert.Equal(original,cfg.Console.ScrollSpeedRows);
    }

    [Fact]
    public void ReturnedSwarmSaveFailureIsNotMislabeledAsAValidationError()
    {
        using var scope=new Scope(true);
        File.Delete(scope.SwarmPath);System.IO.Directory.CreateDirectory(scope.SwarmPath);
        var baseline=TuiConfigCommands.CapturePickerBaseline();
        var result=TuiConfigCommands.ApplyPickerSelection(new("swarm.maxOrchestratorIterations","15"),baseline);
        Assert.False(result.Ok);
        Assert.Contains("Could not apply/save",result.Message);
        Assert.DoesNotContain("Value rejected",result.Message);
        Assert.True(System.IO.Directory.Exists(scope.SwarmPath));
    }

}
