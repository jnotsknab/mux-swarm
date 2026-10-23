using System.Text.Json;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Requests an extension process sends to Mux (ExtensionHostApi). ConsoleState collection: the
// injection seam, ActiveMode and the TUI driver are process-global. Process tests need python.
[Collection("ConsoleState")]
public sealed class ExtensionHostApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-hostapi-" + Guid.NewGuid().ToString("N"));
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";
    private readonly Func<string, string, string?> _inject = ExtensionHostApi.Inject;
    private readonly string _mode = ServeMode.ActiveMode;
    private readonly List<(string Ext, string Line)> _sent = [];

    public ExtensionHostApiTests()
    {
        Directory.CreateDirectory(_dir);
        ExtensionHostApi.Inject = (ext, line) => { lock (_sent) _sent.Add((ext, line)); return null; };
    }

    public void Dispose()
    {
        ExtensionHostApi.Inject = _inject;
        ServeMode.ActiveMode = _mode;
        MuxConsole.ClearInjected();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void PromptSubmit_QueuesInSession_RefusesAtMenu_AndRefusesCommandsAndShellEscapes()
    {
        ServeMode.ActiveMode = "interactive";
        Assert.Contains("no session", ExtensionHostApi.Handle("x", "prompt/submit", P("""{"text":"hi"}""")).Error);

        ServeMode.ActiveMode = "agent";
        var ok = ExtensionHostApi.Handle("x", "prompt/submit", P("""{"text":"  summarize the diff  "}"""));
        Assert.Null(ok.Error);
        Assert.Equal(("x", "summarize the diff"), Assert.Single(_sent));

        Assert.Contains("command/run", ExtensionHostApi.Handle("x", "prompt/submit", P("""{"text":"/exit"}""")).Error);
        Assert.Contains("shell escape", ExtensionHostApi.Handle("x", "prompt/submit", P("""{"text":"!rm -rf ."}""")).Error);
        Assert.Equal(-32602, ExtensionHostApi.Handle("x", "prompt/submit", P("""{"text":7}""")).Code);
        Assert.Single(_sent);
    }

    [Fact]
    public void CommandRun_AllowsListedBuiltIns_RefusesTheRest()
    {
        Assert.Null(ExtensionHostApi.Handle("x", "command/run", P("""{"line":"/compact keep the plan"}""")).Error);
        foreach (var refused in new[] { "/extensions trust x", "/exit", "/sandbox host", "/share", "/set a b", "/qc", "hello", "/compact\n/exit" })
            Assert.NotNull(ExtensionHostApi.Handle("x", "command/run", JsonSerializer.SerializeToElement(new { line = refused })).Error);
        Assert.Equal(("x", "/compact keep the plan"), Assert.Single(_sent));
    }

    [Fact]
    public void AllowedBuiltIns_AreRealCommands_AndNeverLifecycleOrTrust()
    {
        var known = TuiCommands.All.Select(e => e.Cmd).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(ExtensionHostApi.AllowedBuiltIns, c => Assert.Contains(c, known));
        foreach (var never in new[] { "/extensions", "/exit", "/qc", "/qm", "/sandbox", "/dockerexec", "/share", "/set", "/setup", "/login", "/provider", "/update", "/refresh", "/agent", "/wipe" })
            Assert.DoesNotContain(never, ExtensionHostApi.AllowedBuiltIns);
    }

    // The brief tells the agent to use only what DOCS.md documents, so the docs must list every method and built-in.
    [Fact]
    public void Docs_ListEveryHostMethod_AndEveryAllowedBuiltIn()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Context", "DOCS.md"))) dir = Path.GetDirectoryName(dir);
        var docs = File.ReadAllText(Path.Combine(dir!, "Context", "DOCS.md"));
        int start = docs.IndexOf("### Extension host API", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var section = docs[start..docs.IndexOf("\n### ", start + 4, StringComparison.Ordinal)];
        Assert.All(ExtensionHostApi.Methods, m => Assert.Contains($"`{m}`", section));
        Assert.All(ExtensionHostApi.AllowedBuiltIns, c => Assert.Contains($"`{c}`", section));
    }

    [Fact]
    public void SessionInfo_And_UnknownMethods()
    {
        ServeMode.ActiveMode = "swarm";
        var info = JsonSerializer.SerializeToElement(ExtensionHostApi.Handle("x", "session/info", default).Result);
        Assert.Equal("swarm", info.GetProperty("mode").GetString());
        Assert.True(info.GetProperty("inSession").GetBoolean());
        Assert.True(info.GetProperty("context").TryGetProperty("percent", out _));
        Assert.Equal(-32601, ExtensionHostApi.Handle("x", "ui/nope", default).Code);
        Assert.Equal(-32602, ExtensionHostApi.Handle("x", "ui/toast", P("{}")).Code);
    }

    [Fact]
    public async Task Process_Requests_AreAnswered_WithTheSameId()
    {
        // Asks Mux three things on initialize, then answers our "done?" request with what it got back.
        File.WriteAllText(Path.Combine(_dir, "main.py"), """
            import sys, json
            def send(o): print(json.dumps(o), flush=True)
            got = {}
            for line in sys.stdin:
                msg = json.loads(line)
                if msg.get("method") == "initialize":
                    send({"jsonrpc": "2.0", "id": "a", "method": "session/info"})
                    send({"jsonrpc": "2.0", "id": 2, "method": "prompt/submit", "params": {"text": "from ext"}})
                    send({"jsonrpc": "2.0", "id": 3, "method": "nope"})
                elif "method" not in msg:
                    got[str(msg["id"])] = msg
                elif msg.get("method") == "done":
                    send({"jsonrpc": "2.0", "id": msg["id"], "result": got})
            """);
        ServeMode.ActiveMode = "agent";
        using var ext = ExtensionProcess.Start("asker", _dir, new ExtensionProcessSpec { Command = Python, Args = ["-u", "main.py"] }, []);
        JsonElement got = default;
        for (int i = 0; i < 100; i++)
        {
            got = (await ext.RequestAsync("done", null, TimeSpan.FromSeconds(20))).GetProperty("result");
            if (got.EnumerateObject().Count() == 3) break;
            await Task.Delay(100);
        }
        Assert.Equal("agent", got.GetProperty("a").GetProperty("result").GetProperty("mode").GetString());
        Assert.True(got.GetProperty("2").GetProperty("result").GetProperty("queued").GetBoolean());
        Assert.Equal(-32601, got.GetProperty("3").GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(("asker", "from ext"), Assert.Single(_sent));
    }

    private sealed class Terminal : ITuiTerminal
    {
        private readonly System.Text.StringBuilder _out = new();
        public int Width => 80;
        public int Height => 24;
        public void Write(string text) { lock (_out) _out.Append(text); }
        public void Flush() { }
        internal string Plain() { lock (_out) return System.Text.RegularExpressions.Regex.Replace(_out.ToString(), "\u001b\\[[0-9;?]*[A-Za-z]", ""); }
    }

    // The TUI prompt submits a queued line only while the draft is empty, and says who sent it.
    [Fact]
    public async Task Tui_SubmitsInjectedLine_OnlyWhenTheDraftIsEmpty()
    {
        var term = new Terminal();
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var driver = new TuiDriver(term, frameEngine: false);
        var queue = typeof(MuxConsole).GetField("_injected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as System.Collections.Concurrent.ConcurrentQueue<(string, string)>;
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false)));
            await Task.Delay(300);
            queue!.Enqueue(("notes", "summarize"));
            await Task.Delay(400);
            Assert.False(read.IsCompleted);   // the user's draft "h" blocks it
            pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false)));
            Assert.Equal("summarize", await read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("sent by extension notes", term.Plain());
        }
        finally
        {
            pump.Dispose();
            try { await read.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            driver.Shutdown();
        }
    }

    [Fact]
    public void StatusSet_SetsAndClearsAFooterChip()
    {
        try
        {
            var set = JsonSerializer.SerializeToElement(ExtensionHostApi.Handle("meter", "status/set", P("""{"key":"build","text":"green"}""")).Result);
            Assert.True(set.GetProperty("changed").GetBoolean());
            Assert.Contains(("meter.build", "green"), ExtensionFooter.Snapshot());
            var again = JsonSerializer.SerializeToElement(ExtensionHostApi.Handle("meter", "status/set", P("""{"key":"build","text":"green"}""")).Result);
            Assert.False(again.GetProperty("changed").GetBoolean());
            ExtensionHostApi.Handle("meter", "status/set", P("""{"key":"build"}"""));
            Assert.DoesNotContain(ExtensionFooter.Snapshot(), c => c.Id == "meter.build");
            Assert.Equal(-32602, ExtensionHostApi.Handle("meter", "status/set", P("""{"key":"Bad Key"}""")).Code);
        }
        finally { ExtensionFooter.Clear("meter"); }
    }
}
