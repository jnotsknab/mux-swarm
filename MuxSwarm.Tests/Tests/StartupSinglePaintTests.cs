using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Startup paints once in its final layout: the first frame already has the input box, and with the frame
/// engine the primary-screen splash is held (the frame paints it) or printed at EndQuietStartup when nothing does.</summary>
[Collection("ConsoleState")]
public class StartupSinglePaintTests
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

    [Fact]
    public void FirstStartupFrame_AlreadyHasTheInputBox_AndTheSplashDoesNotMoveLater()
    {
        var driver = new TuiDriver(new Term(), frameEngine: true);
        driver.ReserveInputBox();
        driver.CommitStartup(w => MuxConsole.BuildFrameSplashLines("0.16.0", "", "Tip", "hello", w));
        var rows = driver.ComposeFrameRows();
        Assert.Contains("type a message", Plain(rows));
        int splashTop = rows.FindIndex(r => Plain([r]).Contains('\u256d'));
        Assert.True(splashTop >= 0);

        // Without the reservation the same frame lacks the input box (the layout the user saw grow a moment later).
        var bare = new TuiDriver(new Term(), frameEngine: true);
        bare.CommitStartup(w => MuxConsole.BuildFrameSplashLines("0.16.0", "", "Tip", "hello", w));
        Assert.DoesNotContain("type a message", Plain(bare.ComposeFrameRows()));
    }

    [Fact]
    public void SeededFirstInput_ReleasesTheStartupInputBox()
    {
        // /setup -> onboarding seeds its first goal through InputOverride, so no keyboard ReadLine runs before the
        // agent works. The startup reservation must not keep the empty input box (and footer) pinned meanwhile.
        var names = new[] { "_driver", "_tuiActive", "_interactiveRenderMode" };
        var fields = names.Select(n => typeof(MuxConsole).GetField(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!).ToList();
        var saved = fields.ToDictionary(f => f, f => f.GetValue(null));
        bool stdio = MuxConsole.StdioMode;
        var input = MuxConsole.InputOverride;
        try
        {
            var driver = new TuiDriver(new Term(), frameEngine: true);
            driver.ReserveInputBox();
            fields[0].SetValue(null, driver);
            fields[1].SetValue(null, true);
            fields[2].SetValue(null, RenderMode.Tui);
            MuxConsole.StdioMode = false;
            Assert.Contains("type a message", Plain(driver.ComposeFrameRows()));   // startup: box reserved

            MuxConsole.InputOverride = new FallbackReader("Begin onboarding.", input);
            Assert.Equal("Begin onboarding.", MuxConsole.ReadInput());
            Assert.DoesNotContain("type a message", Plain(driver.ComposeFrameRows()));   // agent working: no input box
        }
        finally
        {
            foreach (var (f, v) in saved) f.SetValue(null, v);
            MuxConsole.StdioMode = stdio;
            MuxConsole.InputOverride = input;
        }
    }

    [Fact]
    public void DeferredSplash_IsHeld_ThenPrintedOnceAtEndQuietStartup()
    {
        var prior = Spectre.Console.AnsiConsole.Console;
        bool priorStdio = MuxConsole.StdioMode;
        var output = new StringWriter();
        Spectre.Console.AnsiConsole.Console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = Spectre.Console.AnsiSupport.No, ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Out = new Spectre.Console.AnsiConsoleOutput(output),
        });
        try
        {
            MuxConsole.StdioMode = false;
            MuxConsole.DeferSplashToFrame = true;
            MuxConsole.WriteSplashScreen("9.9.9-test");
            Assert.DoesNotContain("9.9.9-test", output.ToString());   // held: the frame would paint it in place
            MuxConsole.EndQuietStartup();                              // nothing painted it: print it now
            Assert.Contains("9.9.9-test", output.ToString());
            int len = output.ToString().Length;
            MuxConsole.EndQuietStartup();
            Assert.Equal(len, output.ToString().Length);              // once only
            Assert.False(MuxConsole.DeferSplashToFrame);
        }
        finally
        {
            MuxConsole.DeferSplashToFrame = false;
            MuxConsole.StdioMode = priorStdio;
            Spectre.Console.AnsiConsole.Console = prior;
        }
    }
}
