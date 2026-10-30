using MuxSwarm.Engine;
using MuxSwarm.Engine.Telemetry;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// What a daemon bridge process writes must not print into the TUI (it clobbered the transcript): it goes to the
// runtime log shown by /logs, at the level the line carries. Only a new pairing code is shown, once.
// Console state (StdioMode + Console.Out) and the trace store are process-global, hence ConsoleState.
[Collection("ConsoleState")]
public sealed class BridgeOutputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-bridge-out-" + Guid.NewGuid().ToString("N"));
    private readonly TextWriter _out = Console.Out;
    private readonly bool _stdio = MuxConsole.StdioMode;
    private readonly StringWriter _screen = new();

    public BridgeOutputTests()
    {
        Directory.CreateDirectory(_dir);
        TraceStore.OverrideDirectoryForTests(_dir);
        MuxConsole.StdioMode = true;          // every console notice becomes one NDJSON line on stdout
        Console.SetOut(_screen);
    }

    public void Dispose()
    {
        Console.SetOut(_out);
        MuxConsole.StdioMode = _stdio;
        TraceStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private IReadOnlyList<TraceStore.Rec> Logs(string id) =>
        TraceStore.ListLogs(100).Where(r => (r.Text ?? "").Contains($"[Bridge:{id}]")).ToList();

    [Fact]
    public void BridgeOutput_GoesToTheRuntimeLog_NotTheScreen()
    {
        string id = "tg-" + Guid.NewGuid().ToString("N")[..6];
        DaemonRunner.HandleBridgeLine(id, "2026-10-28 01:01:50,850 [INFO] connected to Mux", isStderr: true);
        DaemonRunner.HandleBridgeLine(id, "2026-10-28 01:01:51,000 [WARNING] Mux connection error: refused", isStderr: true);
        DaemonRunner.HandleBridgeLine(id, "Traceback (most recent call last):", isStderr: true);
        DaemonRunner.HandleBridgeLine(id, "ConnectionRefusedError: [WinError 10061] refused", isStderr: true);
        DaemonRunner.HandleBridgeLine(id, "plain stdout line", isStderr: false);
        DaemonRunner.HandleBridgeLine(id, "   ", isStderr: true);

        Assert.Equal("", _screen.ToString());                      // nothing reached the transcript
        var levels = Logs(id).OrderBy(r => r.TsUtc).Select(r => (r.Level, (r.Text ?? "").Split("] ", 2)[1])).ToList();
        Assert.Equal(5, levels.Count);
        Assert.Contains(("info", "2026-10-28 01:01:50,850 [INFO] connected to Mux"), levels);
        Assert.Contains(("warn", "2026-10-28 01:01:51,000 [WARNING] Mux connection error: refused"), levels);
        Assert.Contains(("error", "Traceback (most recent call last):"), levels);
        Assert.Contains(("error", "ConnectionRefusedError: [WinError 10061] refused"), levels);
        Assert.Contains(("info", "plain stdout line"), levels);
    }

    [Fact]
    public void PairingCode_IsShownOnce_PerNewCode()
    {
        string id = "sig-" + Guid.NewGuid().ToString("N")[..6];
        DaemonRunner.HandleBridgeLine(id, "2026-10-28 01:08:33,533 [INFO] Pairing code: 0c2355  (send '/pair 0C2355')", isStderr: true);
        DaemonRunner.HandleBridgeLine(id, "2026-10-28 01:09:00,000 [INFO] Pairing code: 0C2355  (send '/pair 0C2355')", isStderr: true);   // restart, same code
        var shown = _screen.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(shown);
        Assert.Contains($"[{id}] pairing code: 0C2355 - send /pair 0C2355", shown[0]);

        DaemonRunner.HandleBridgeLine(id, "[INFO] user paired. New pairing code: ABC123", isStderr: true);
        Assert.Single(_screen.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));   // "New pairing code" is not "Pairing code:"
        DaemonRunner.HandleBridgeLine(id, "[INFO] Pairing code: ABC123  (send '/pair ABC123')", isStderr: true);
        Assert.Equal(2, _screen.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(4, Logs(id).Count);                          // every line still logged
    }

    [Theory]
    [InlineData("2026-10-28 01:00:00,000 [ERROR] boom", true, "error")]
    [InlineData("a b [CRITICAL] down", false, "error")]
    [InlineData("[DEBUG] chatter", true, "info")]
    [InlineData("[warn] hmm", false, "warn")]
    [InlineData("KeyError: 'x'", false, "error")]
    [InlineData("something on stderr", true, "warn")]
    [InlineData("something on stdout", false, "info")]
    public void LineLevels(string line, bool stderr, string expected) =>
        Assert.Equal(expected, DaemonRunner.BridgeLineLevel(line, stderr));
}
