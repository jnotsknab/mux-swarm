using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;
using Spectre.Console;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// The stock TUI look must not change when no extension styles a region. The reference hash was
/// recorded on the code before per-region styles existed (1b0b4ba) from the same script.
/// </summary>
[Collection("ConsoleState")]
public class TuiStyleDefaultLookTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 80;
        public int Height { get; set; } = 40;
        public void Write(string text) { }
        public void Flush() { }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Every region T1 styles, rendered to ANSI (transcript rows, toast, scrollbar, classic notices).</summary>
    internal static string RenderReference()
    {
        var names = new[] { "_driver", "_tuiActive", "_interactiveRenderMode" };
        var fields = names.Select(n => typeof(MuxConsole).GetField(n, BindingFlags.NonPublic | BindingFlags.Static)!).ToList();
        var saved = fields.ToDictionary(f => f, f => f.GetValue(null));
        bool stdio = MuxConsole.StdioMode;
        var priorTheme = Theme.Active;
        var priorConsole = AnsiConsole.Console;
        var sb = new StringBuilder();
        try
        {
            Theme.Set(Theme.Default);
            var driver = new TuiDriver(new Terminal(), frameEngine: true);
            fields[0].SetValue(null, driver);
            fields[1].SetValue(null, true);
            fields[2].SetValue(null, RenderMode.Tui);
            MuxConsole.StdioMode = false;

            MuxConsole.RenderTuiSessionHeader("CodeAgent", "gpt-5", "openai", 3);
            var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", PrivateInstance)!.GetValue(driver)!;
            editor.InsertText("hello there");
            typeof(TuiDriver).GetMethod("EchoDraft", PrivateInstance)!.Invoke(driver, new object[] { "hello there" });
            MuxConsole.RenderTuiTurnHeader("CodeAgent");
            driver.BeginStream();
            driver.StreamChunk("solo tail");
            foreach (var line in driver.BuildLiveFrame(79).Where(l => l.Contains("solo tail"))) sb.Append(TuiMarkup.ToAnsi(line)).Append('\n');
            driver.EndStream();
            driver.BeginStream();
            driver.StreamChunk("weighing options\n", reasoning: true);
            driver.StreamChunk("First answer line\nsecond line\npartial tail");
            foreach (var line in driver.BuildLiveFrame(79).Where(l => l.Contains("partial tail"))) sb.Append(TuiMarkup.ToAnsi(line)).Append('\n');
            driver.BeginStream();
            driver.StreamChunk("thinking tail", reasoning: true);
            foreach (var line in driver.BuildLiveFrame(79).Where(l => l.Contains("thinking tail"))) sb.Append(TuiMarkup.ToAnsi(line)).Append('\n');
            driver.EndStream();
            MuxConsole.WriteSuccess("saved");
            MuxConsole.WriteWarning("careful");
            MuxConsole.WriteError("failed");
            MuxConsole.WriteInfo("fyi");

            var entries = ((IEnumerable)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!).Cast<object>();
            var render = typeof(TuiDriver).GetMethod("RenderEntryRows", PrivateInstance)!;
            foreach (var e in entries)
                foreach (var row in (List<string>)render.Invoke(driver, new[] { e, (object)79 })!) sb.Append(row).Append('\n');

            driver.ShowToast("copied");
            foreach (var line in driver.BuildLiveFrame(79).Where(l => l.Contains("copied"))) sb.Append(TuiMarkup.ToAnsi(line)).Append('\n');
            var bar = FrameScrollBar.Create(0, 100, 10);
            for (int r = 0; r < 10; r++) sb.Append(bar.Cell(r));
            sb.Append('\n');

            // Classic console: notices and reasoning through Spectre.
            fields[1].SetValue(null, false);
            fields[2].SetValue(null, RenderMode.Classic);
            var sw = new StringWriter();
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Yes, ColorSystem = ColorSystemSupport.TrueColor, Out = new AnsiConsoleOutput(sw),
            });
            MuxConsole.WriteSuccess("saved");
            MuxConsole.WriteWarning("careful");
            MuxConsole.WriteError("failed");
            MuxConsole.WriteInfo("fyi");
            MuxConsole.WriteStream("reasoned", muted: true);
            sb.Append(sw.ToString());
        }
        finally
        {
            foreach (var (f, v) in saved) f.SetValue(null, v);
            MuxConsole.StdioMode = stdio;
            Theme.Set(priorTheme);
            AnsiConsole.Console = priorConsole;
        }
        return sb.ToString();
    }

    // Spectre ends classic lines with the platform newline; normalize so the hash is the same on every OS.
    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n"))))[..16];

    [Fact]
    public void NoExtensionStyles_RendersTheStockLookByteForByte()
    {
        string output = RenderReference();
        Assert.Contains("session", output);
        Assert.Contains("weighing options", output);
        Assert.Equal("C9538AB6650E3114", Hash(output));
    }
}
