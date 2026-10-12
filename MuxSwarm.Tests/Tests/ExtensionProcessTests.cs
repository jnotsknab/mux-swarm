using System.Text.Json;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// RpcCorrelator (shared with ACP) and a real extension process over NDJSON JSON-RPC.
// The process tests need `python` on PATH, like NativeReplShellToolsTests.
public sealed class RpcCorrelatorTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public async Task Response_CompletesItsRequest_ById()
    {
        var rpc = new RpcCorrelator(firstId: 7);
        var (a, ta) = rpc.Begin(TimeSpan.FromSeconds(5));
        var (b, tb) = rpc.Begin(TimeSpan.FromSeconds(5));
        Assert.Equal(7, a);
        Assert.Equal(8, b);

        Assert.True(rpc.TryComplete(Json($$"""{"jsonrpc":"2.0","id":{{b}},"result":"B"}""")));
        Assert.Equal("B", (await tb).GetProperty("result").GetString());
        Assert.False(ta.IsCompleted);
        Assert.Equal(1, rpc.PendingCount);
    }

    [Fact]
    public void Requests_UnknownIds_AndNonObjects_AreNotResponses()
    {
        var rpc = new RpcCorrelator();
        Assert.False(rpc.TryComplete(Json("""{"jsonrpc":"2.0","id":99,"result":1}""")));
        Assert.False(rpc.TryComplete(Json("""{"jsonrpc":"2.0","id":1,"method":"x"}""")));
        Assert.False(rpc.TryComplete(Json("[1]")));
    }

    [Fact]
    public async Task Timeout_And_CancelAll_CompleteWithDefault()
    {
        var rpc = new RpcCorrelator();
        var (_, timedOut) = rpc.Begin(TimeSpan.FromMilliseconds(50));
        Assert.Equal(JsonValueKind.Undefined, (await timedOut.WaitAsync(TimeSpan.FromSeconds(5))).ValueKind);

        var (_, pending) = rpc.Begin(TimeSpan.FromMinutes(5));
        rpc.CancelAll();
        Assert.Equal(JsonValueKind.Undefined, (await pending).ValueKind);
        Assert.Equal(0, rpc.PendingCount);
    }
}

public sealed class ExtensionProcessTests : IDisposable
{
    // Echo extension: records notifications, answers "ping" requests, prints junk to stderr.
    private const string Script = """
        import sys, json
        sys.stderr.write("hello from stderr\n"); sys.stderr.flush()
        seen = []
        for line in sys.stdin:
            msg = json.loads(line)
            if "id" in msg:
                result = {"method": msg["method"], "params": msg.get("params"), "seen": seen}
                print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": result}), flush=True)
            else:
                seen.append(msg["method"] + ":" + str((msg.get("params") or {}).get("event", "")))
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-extproc-" + Guid.NewGuid().ToString("N"));

    // Linux images often ship only python3 (the REPL tests rely on it too).
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";

    public ExtensionProcessTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "echo.py"), Script);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ExtensionProcess StartEcho(params string[] events) =>
        ExtensionProcess.Start("echo", _dir, new ExtensionProcessSpec { Command = Python, Args = ["-u", "echo.py"] }, events);

    [Fact]
    public async Task Request_RoundTrips_AfterInitializeAndEvents()
    {
        using var ext = StartEcho("tool_call");
        Assert.True(ext.Wants("TOOL_CALL"));
        Assert.False(ext.Wants("turn_end"));
        ext.PostEvent(new HookEvent { Event = "tool_call", Tool = "Shell_execute" });

        var reply = await ext.RequestAsync("ping", new { n = 1 }, TimeSpan.FromSeconds(20));

        var result = reply.GetProperty("result");
        Assert.Equal("ping", result.GetProperty("method").GetString());
        Assert.Equal(1, result.GetProperty("params").GetProperty("n").GetInt32());
        var seen = result.GetProperty("seen").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["initialize:", "event:tool_call"], seen);
    }

    [Fact]
    public async Task Stderr_IsCaptured_NotForwarded()
    {
        using var ext = StartEcho();
        await ext.RequestAsync("ping", null, TimeSpan.FromSeconds(20));
        Assert.Contains("hello from stderr", ext.StderrTail);
    }

    [Fact]
    public async Task ProcessExit_FailsPendingAndLaterRequests_Fast()
    {
        var spec = new ExtensionProcessSpec { Command = Python, Args = ["-c", "import sys; sys.stdin.readline()"] };
        using var ext = ExtensionProcess.Start("quits", _dir, spec, []);
        var reply = await ext.RequestAsync("ping", null, TimeSpan.FromSeconds(20)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(JsonValueKind.Undefined, reply.ValueKind);
        for (int i = 0; i < 50 && ext.IsRunning; i++) await Task.Delay(100);
        Assert.False(ext.IsRunning);
        Assert.Equal(JsonValueKind.Undefined, (await ext.RequestAsync("ping", null, TimeSpan.FromSeconds(20))).ValueKind);
    }

    [Fact]
    public void Host_StartsOnlyTrusted_AndStopAllClears()
    {
        var ext = new ExtensionRegistry.Extension("echo", null, null, _dir, [])
        {
            Process = new ExtensionProcessSpec { Command = Python, Args = ["-u", "echo.py"] },
            Events = ["*"],
        };
        try
        {
            var problems = ExtensionHost.Restart([ext], new HashSet<string>());
            Assert.Empty(ExtensionHost.Running);
            Assert.Contains(problems, p => p.Contains("extensions.trusted"));

            problems = ExtensionHost.Restart([ext], new HashSet<string> { "echo" });
            Assert.Empty(problems);
            Assert.True(Assert.Single(ExtensionHost.Running).Wants("anything"));
        }
        finally { ExtensionHost.StopAll(); }
        Assert.Empty(ExtensionHost.Running);
    }
}
