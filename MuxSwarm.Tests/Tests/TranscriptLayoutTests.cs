using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Submitted-input and header layout regressions; no live input, provider or config access.</summary>
[Collection("ConsoleState")]
public class TranscriptLayoutTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 180;
        public int Height { get; set; } = 50;
        public StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Dictionary<FieldInfo, object?> _saved;
        private readonly bool _stdio = MuxConsole.StdioMode;
        public Terminal Terminal { get; } = new();
        public TuiDriver Driver { get; }
        public Scope(bool frame)
        {
            var names = new[] { "_driver", "_tuiActive", "_interactiveRenderMode" };
            _saved = names.Select(n => typeof(MuxConsole).GetField(n, BindingFlags.NonPublic | BindingFlags.Static)!)
                .ToDictionary(f => f, f => f.GetValue(null));
            Driver = new TuiDriver(Terminal, frameEngine: frame);
            foreach (var field in _saved.Keys)
            {
                if (field.Name == "_driver") field.SetValue(null, Driver);
                if (field.Name == "_tuiActive") field.SetValue(null, true);
                if (field.Name == "_interactiveRenderMode") field.SetValue(null, RenderMode.Tui);
            }
            MuxConsole.StdioMode = false;
        }
        public void Dispose()
        {
            foreach (var item in _saved) item.Key.SetValue(null, item.Value);
            MuxConsole.StdioMode = _stdio;
        }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string Plain(string ansi) => Regex.Replace(ansi, "\u001b\\[[0-9;?]*[A-Za-z]", "");
    private static List<string> Transcript(TuiDriver driver, bool frame)
    {
        if (frame)
        {
            var entries = ((IEnumerable)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!).Cast<object>();
            var render = typeof(TuiDriver).GetMethod("RenderEntryRows", PrivateInstance)!;
            return entries.SelectMany(e => (List<string>)render.Invoke(driver, new[] { e, (object)driver.Width })!)
                .Select(Plain).ToList();
        }
        return ((List<string>)typeof(TuiDriver).GetMethod("BuildReflowWindow", PrivateInstance)!.Invoke(driver, null)!)
            .Select(Plain).ToList();
    }

    private static void Echo(TuiDriver driver, string text)
    {
        ((LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(driver)!).InsertText(text);
        typeof(TuiDriver).GetMethod("EchoDraft", PrivateInstance)!.Invoke(driver, new object[] { text });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserEcho_ExplicitAndSoftWrappedRowsKeepBodyColumnAcrossResize(bool frame)
    {
        using var scope = new Scope(frame);
        string text = "USERSTART " + new string('x', 80) + "\n  code [literal]\n\nUSEREND";
        Echo(scope.Driver, text);
        foreach (int width in new[] { 180, 40, 90, 24, 180 })
        {
            scope.Terminal.Width = width;
            var rows = Transcript(scope.Driver, frame);
            Assert.Equal("", rows[0]);
            Assert.StartsWith("▎ USERSTART", rows[1]);
            Assert.All(rows.Skip(2), row => Assert.StartsWith("  ", row));
            Assert.Contains("    code [literal]", rows);
            Assert.Contains("  ", rows);
            Assert.Contains("  USEREND", rows);
            Assert.All(rows, row => Assert.InRange(TuiMarkup.Width(row), 0, width - 1));
            Assert.Equal(80, rows.Sum(r => r.Count(c => c == 'x')));
        }
        Assert.Equal(text, ((LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(scope.Driver)!).Buffer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TurnHeader_ResizeRegeneratesOneRuleOnNameRow(bool frame)
    {
        using var scope = new Scope(frame);
        MuxConsole.RenderTuiTurnHeader("CodeAgent");
        foreach (int width in new[] { 180, 60, 26, 110, 180 })
        {
            scope.Terminal.Width = width;
            var rows = Transcript(scope.Driver, frame).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            Assert.Single(rows);
            Assert.Contains("▸ CodeAgent", rows[0]);
            Assert.Contains('─', rows[0]);
            Assert.Equal(width - 1, TuiMarkup.Width(rows[0]));
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(40)]
    public void TurnHeader_LongUnicodeLabelFitsOnePhysicalRow(int width)
    {
        var markup = TuiComponents.TurnHeader("Worker 漢字 [literal] " + new string('z', 100), width);
        Assert.All(markup, row => Assert.InRange(TuiMarkup.MarkupWidth(row), 0, width));
        Assert.Equal(2, markup.SelectMany(row => LiveRegion.WrapMarkupLine(row, width)).Count());
    }

    [Fact]
    public void FittedStyledRow_DoesNotWrapAgainBySpan()
    {
        const string markup = "  [red]▸ CodeAgent[/] [grey]───────────────────────[/]";
        int width = TuiMarkup.MarkupWidth(markup);
        Assert.Equal(TuiMarkup.Plain(markup), Plain(Assert.Single(LiveRegion.WrapMarkupLine(markup, width))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserEcho_AttachmentDisplaySnapshotAndInteractiveSuppressionStayIntact(bool frame)
    {
        using var scope = new Scope(frame);
        var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(scope.Driver)!;
        editor.InsertText("lead ");
        editor.InsertPaste(new string('p', 1100));
        editor.InsertPaste("\n[Attached screenshot: C:\\sandbox\\captures\\private-name.png]\n", "private-name.png");
        string payload = editor.Buffer;
        string display = editor.Display.Text;
        typeof(TuiDriver).GetMethod("EchoDraft", PrivateInstance)!.Invoke(scope.Driver, new object[] { payload });
        editor.Reset(); // retained layout must not close over the mutable editor
        scope.Terminal.Width = 40;
        string echo = string.Join("\n", Transcript(scope.Driver, frame));
        Assert.Contains("[≡ 1]", echo);
        Assert.Contains("[▧ 2]", echo);
        Assert.DoesNotContain("private-name", echo);
        Assert.DoesNotContain(new string('p', 40), echo);
        int count = Transcript(scope.Driver, frame).Count;
        Echo(scope.Driver, "/setmodel");
        Assert.Equal(count, Transcript(scope.Driver, frame).Count);
    }

    [Fact]
    public void UserEcho_UnicodeBlankLinesAndTinyWidthsAreBounded()
    {
        const string body = "漢字 e\u0301 👩‍💻 [red] alpha\n\n  second\n";
        foreach (int width in new[] { 1, 2, 3, 4, 8, 20, 80 })
        {
            var markup = TuiComponents.UserEcho(body, width);
            Assert.All(markup, row => Assert.InRange(TuiMarkup.MarkupWidth(row), 0, width));
            Assert.All(markup, row => Assert.Single(LiveRegion.WrapMarkupLine(row, width)));
            Assert.All(markup, row => Assert.DoesNotContain('\u001b', TuiMarkup.Plain(row)));
        }
        var rows = TuiComponents.UserEcho(body, 80).Select(TuiMarkup.Plain).ToList();
        Assert.Equal("▎ 漢字 e\u0301 👩‍💻 [red] alpha", rows[1]);
        Assert.Equal(new[] { "  ", "    second", "  " }, rows.Skip(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NavOpeningAfterResize_UsesFreshLayoutsAndRestoresRenderer(bool frame)
    {
        using var scope = new Scope(frame);
        Echo(scope.Driver, "NAVSTART " + new string('x', 50) + "\nNAVEND");
        MuxConsole.RenderTuiTurnHeader("CodeAgent");
        scope.Terminal.Width = 40;
        scope.Terminal.Output.Clear();
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var current = typeof(ConsoleInputPump).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previous = current.GetValue(null);
        try
        {
            current.SetValue(null, pump);
            pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\u0011', ConsoleKey.Q, false, false, true)));
            typeof(TuiDriver).GetMethod("EnterNavMode", PrivateInstance)!.Invoke(scope.Driver, new object[] { -1 });
        }
        finally { current.SetValue(null, previous); }
        string firstPaint = Plain(scope.Terminal.Output.ToString().Split(" NAV ")[0]);
        Assert.Contains("  NAVEND", firstPaint);
        Assert.Contains("▸ CodeAgent ─", firstPaint);
        Assert.DoesNotContain(new string('─', 40), firstPaint);
        Assert.False((bool)typeof(TuiDriver).GetField("_navActive", PrivateInstance)!.GetValue(scope.Driver)!);
    }

    [Fact]
    public void PromptReplay_UsesCurrentWidthAndCannotReplayOldRuleOrMutableDraft()
    {
        using var scope = new Scope(true);
        scope.Driver.BeginPromptContext();
        Echo(scope.Driver, "START " + new string('x', 60) + "\nEND");
        MuxConsole.RenderTuiTurnHeader("CodeAgent");
        scope.Terminal.Width = 40;
        scope.Terminal.Output.Clear();
        scope.Driver.SuspendForPrompt();
        string output = Plain(scope.Terminal.Output.ToString());
        Assert.Contains("  END\r\n", output);
        Assert.Contains("▸ CodeAgent ─", output);
        Assert.DoesNotContain(new string('─', 40), output);
        scope.Driver.Resume();
        Assert.Single(Transcript(scope.Driver, true), r => r.Contains('─'));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderLayout_CapturesLaneAndDoesNotRetintUserEcho(bool frame)
    {
        using var scope = new Scope(frame);
        scope.Driver.SetLaneTint("#123456");
        scope.Driver.CommitTurnHeader("Worker");
        scope.Driver.SetLaneTint("#ABCDEF");
        Echo(scope.Driver, "user\nsecond");
        scope.Terminal.Width = 40;
        var rows = Transcript(scope.Driver, frame);
        Assert.Contains(rows, row => row.StartsWith("▎ ▸ Worker"));
        Assert.Contains("▎ user", rows);
        Assert.Contains("  second", rows);
        var entries = ((IEnumerable)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(scope.Driver)!).Cast<object>();
        var render = typeof(TuiDriver).GetMethod("RenderEntryRows", PrivateInstance)!;
        string header = string.Join("", (List<string>)render.Invoke(scope.Driver, new[] { entries.First(), (object)39 })!);
        Assert.Contains("38;2;18;52;86", header);
        Assert.DoesNotContain("38;2;171;205;239", header);
    }
}
