using System.Diagnostics;
using System.Reflection;
using System.Text;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Proxy;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Headless /login output: the OAuth URL must reach the terminal as one unindented line (so a selection
/// copies it whole), optionally as an OSC 8 link, and be copyable with the c key via OSC 52.
/// </summary>
[Collection("ConsoleState")]
public class HeadlessLoginConsoleTests
{
    private const string Url =
        "https://claude.ai/oauth/authorize?client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e&code=true"
        + "&code_challenge=fcH0EUnk9_P1hgnsuxF8X-HXXmpaxWSfgBEAuFNW2ho&code_challenge_method=S256"
        + "&redirect_uri=http%3A%2F%2Flocalhost%3A54545%2Fcallback&response_type=code"
        + "&scope=user%3Aprofile+user%3Ainference+user%3Asessions%3Aclaude_code+user%3Amcp_servers"
        + "+user%3Afile_upload&state=aa5e260aa6a4b9cabe8a0c8fd6c13b4d";

    private static (HeadlessLoginConsole Console, StringBuilder Out) Raw(bool links = false)
    {
        var sb = new StringBuilder();
        return (new HeadlessLoginConsole(raw: true, links, localClipboard: false, write: s => sb.Append(s)), sb);
    }

    private static ConsoleKeyInfo Key(char c, ConsoleKey k, bool ctrl = false) => new(c, k, false, false, ctrl);

    [Fact]
    public void Url_IsPrintedAloneOnOneUnindentedLine()
    {
        var (c, sb) = Raw();
        c.Line("Visit the following URL to continue authentication:", err: false);
        c.Line(Url, err: false);
        c.Line("Waiting for Claude authentication callback...", err: false);

        var lines = sb.ToString().Split("\r\n");
        Assert.Contains(Url, lines);                                   // whole URL, exactly, as one line
        Assert.DoesNotContain(lines, l => l != Url && l.Contains("oauth/authorize"));
        Assert.Equal(Url, c.Url);
    }

    [Fact]
    public void Url_WithLinksSupported_IsAnOsc8HyperlinkAroundTheSameText()
    {
        var (c, sb) = Raw(links: true);
        c.Line(Url, err: false);
        Assert.Contains($"\u001b]8;;{Url}\u001b\\{Url}\u001b]8;;\u001b\\", sb.ToString());
    }

    [Fact]
    public void ChildEscapeSequences_AreStripped()
    {
        var (c, sb) = Raw();
        c.Line("\u001b[31mred\u001b[0m \u001b]52;c;ZXZpbA==\u0007done", err: false);
        Assert.Equal("red done\r\n", sb.ToString());
    }

    [Fact]
    public void CopyKey_SendsTheUrlThroughOsc52()
    {
        var (c, sb) = Raw();
        c.Line(Url, err: false);
        sb.Clear();
        Assert.False(c.HandleKey(Key('c', ConsoleKey.C)));
        Assert.Contains(TuiClipboard.Osc52(Url), sb.ToString());
    }

    [Fact]
    public void CopyKey_BeforeAnyUrl_DoesNothing()
    {
        var (c, sb) = Raw();
        Assert.False(c.HandleKey(Key('c', ConsoleKey.C)));
        Assert.Equal("", sb.ToString());
    }

    [Fact]
    public void EscAndCtrlC_Cancel_OtherKeysDoNot()
    {
        var (c, _) = Raw();
        Assert.True(c.HandleKey(Key('\u001b', ConsoleKey.Escape)));
        Assert.True(c.HandleKey(Key('\u0003', ConsoleKey.C, ctrl: true)));
        Assert.False(c.HandleKey(Key('x', ConsoleKey.X)));
        Assert.False(c.HandleKey(Key('\r', ConsoleKey.Enter)));
    }

    [Fact]
    public void StdioMode_RelaysLinesAsEvents_Unchanged()
    {
        var prevOut = Console.Out;
        bool prevStdio = MuxConsole.StdioMode;
        var capture = new StringWriter();
        try
        {
            MuxConsole.StdioMode = true;
            Console.SetOut(capture);
            using var c = HeadlessLoginConsole.Begin();
            c.Line(Url, err: false);
        }
        finally
        {
            Console.SetOut(prevOut);
            MuxConsole.StdioMode = prevStdio;
        }
        string json = capture.ToString();
        Assert.Contains("\"type\":\"info\"", json);
        Assert.Contains("client_id=9d1c250a", json);
        Assert.DoesNotContain("\u001b]52", json);
    }

    private sealed class Terminal : ITuiTerminal
    {
        public int Width => 120;
        public int Height => 40;
        public StringBuilder Output { get; } = new();
        public void Write(string s) => Output.Append(s);
        public void Flush() { }
    }

    [Fact]
    public void Begin_SuspendsTheFrameTui_AndDisposeResumesIt()
    {
        var names = new[] { "_driver", "_tuiActive", "_interactiveRenderMode" };
        var fields = names.Select(n => typeof(MuxConsole).GetField(n, BindingFlags.NonPublic | BindingFlags.Static)!).ToList();
        var saved = fields.ToDictionary(f => f, f => f.GetValue(null));
        bool prevStdio = MuxConsole.StdioMode;
        var prevOut = Console.Out;
        var driver = new TuiDriver(new Terminal(), frameEngine: true);
        try
        {
            fields[0].SetValue(null, driver);
            fields[1].SetValue(null, true);
            fields[2].SetValue(null, RenderMode.Tui);
            MuxConsole.StdioMode = false;
            Console.SetOut(new StringWriter());

            var c = HeadlessLoginConsole.Begin();
            Assert.True(driver.Suspended);   // the URL is written on the main screen, not into the frame
            c.Dispose();
            Assert.False(driver.Suspended);
        }
        finally
        {
            foreach (var kv in saved) kv.Key.SetValue(null, kv.Value);
            MuxConsole.StdioMode = prevStdio;
            Console.SetOut(prevOut);
        }
    }

    private static Process LongRunning()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 30 127.0.0.1")
            : new ProcessStartInfo("sleep", "30");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task WaitAsync_OnCancellation_StopsTheChildAndThrows()
    {
        var (c, _) = Raw();
        using var proc = LongRunning();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.WaitAsync(proc, cts.Token));
        Assert.True(proc.WaitForExit(5000));
    }

    [Fact]
    public async Task WaitAsync_ReturnsTrueWhenTheChildExits()
    {
        var (c, _) = Raw();
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", "/c exit 0")
            : new ProcessStartInfo("true");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        using var proc = Process.Start(psi)!;
        Assert.True(await c.WaitAsync(proc, CancellationToken.None));
    }
}
