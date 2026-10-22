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

    /// <summary>Every region T2 styles: tool cards, diff, delegation, thinking, footer + rules, compose box.</summary>
    internal static string RenderCards()
    {
        var priorTheme = Theme.Active;
        var sb = new StringBuilder();
        void Add(IEnumerable<string> lines) { foreach (var l in lines) sb.Append(TuiMarkup.ToAnsi(l)).Append('\n'); }
        try
        {
            Theme.Set(Theme.Default);
            Add(TuiComponents.ToolCall("Filesystem_read_text_file", "{\"path\":\"a.txt\"}"));
            Add(TuiComponents.ToolCall("Filesystem_read_text_file", null, 2));
            Add(TuiComponents.ToolCallResultMerged("shell", "ls", "Command: ls\nfile1\nfile2", false, true));
            Add(TuiComponents.ToolCallResultMerged("shell", "ls", "STDERR: boom", true, false, 1));
            Add(TuiComponents.ToolCallResultMerged("shell", null, "ok", false, false, 3));
            Add(TuiComponents.ToolResultCompact("Job ID: 1\nCommand: dir\nmore"));
            Add(TuiComponents.ToolResultPanel("read", "line one\n# Head\n**bold**", false, 60));
            Add(TuiComponents.ToolResultPanel("read", "failed hard", true, 60, markdown: true));
            Add(TuiComponents.BoundedLivePanel("Worker", "a\nb\nc\nd", "#7FB3D5", 60, 2, anchorTail: true, markdown: true));
            Add(TuiComponents.BoundedLivePanel("Result", "a\nb\nc\nd", "#7FB3D5", 60, 2, anchorTail: false));
            Add(TuiComponents.Diff("file.cs", "--- a\n+++ b\n@@ -1,2 +1,2 @@\n-old\n+new\n same", 70));
            Add([TuiComponents.DelegationSummary("Lead", "Worker", "do the thing")]);
            Add(TuiComponents.Delegation("Lead", "Worker", "do the thing", 40));
            Add(TuiComponents.TaskComplete("Task", "all good"));
            foreach (var st in new[] { "success", "failed", "partial", null })
                Add([TuiComponents.SubAgentCollapsed("Worker", st, 3, 2, "#7FB3D5")]);
            Add(TuiComponents.SubAgentActivity([("Worker", "building", "#7FB3D5")], 1));
            Add([TuiComponents.ThinkingLine("Pondering", 0), TuiComponents.ThinkingLine("\u2836 Thinking", 0)]);
            Add([TuiComponents.Footer(1200, 200_000, plan: true, ultra: false, psub: true, effort: "high", modeCycleHint: true,
                cached: 300, sysTokens: 50, toolTokens: 70, toolCalls: 4, model: "provider/claude-x", width: 160)]);
            Add([TuiComponents.Footer(1200, 200_000, false, false, false, width: 40)]);
            var badges = new List<(int Col, int Width, string Id)>();
            TuiComponents.Footer(5, 100, false, false, false, false, "low", false, 0, 0, 0, null, false, null, null, 0, "m1", 120, -1, null, badges);
            sb.Append(string.Join(",", badges)).Append('\n');
            var driver = new TuiDriver(new Terminal(), frameEngine: true);
            Add(driver.BuildLiveFrame(79).Where(l => l.Contains('\u2500')));
            Add(TuiComponents.InputRowsWithCursor("", 0, 50));
            Add(TuiComponents.InputRowsWithCursor("hello\nsecond line that wraps around the edge", 8, 30, highlight: true));
            Add(TuiComponents.InputRowsWithCursor("tail", 4, 40));
            Add([TuiComponents.InputRow(""), TuiComponents.InputRow("typed")]);
        }
        finally { Theme.Set(priorTheme); }
        return sb.ToString();
    }

    [Fact]
    public void NoExtensionStyles_CardsFooterAndInputUnchanged()
    {
        string output = RenderCards();
        Assert.Contains("diff", output);
        Assert.Equal("6C50EBF1B6D3DEAF", Hash(output));
    }
}
