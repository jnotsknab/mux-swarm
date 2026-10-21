using System.Text.Json;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// immediate commands and the "open" reply. ConsoleState: registry, panels and injection are global.
[Collection("ConsoleState")]
public sealed class ExtensionCommandsImmediateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-extimm-" + Guid.NewGuid().ToString("N"));
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";
    private readonly Func<string?> _surface = ExtensionPanels.Surface;

    // /now replies after a short delay with a prompt; /board replies with a panel to open.
    private const string Script = """
        import sys, json, time
        def send(o): print(json.dumps(o), flush=True)
        for line in sys.stdin:
            msg = json.loads(line)
            if msg.get("method") != "command/run": continue
            p = msg["params"]
            if p["name"] == "now":
                time.sleep(0.3)
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {"prompt": "imm=" + str(p.get("immediate"))}})
            elif p["name"] == "board":
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {"open": {"id": "b", "title": "Board", "view": {"type": "text", "text": "hi"}}}})
            elif p["name"] == "bad":
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {"open": {"id": "b", "view": {"type": "nope"}}}})
        """;

    public ExtensionCommandsImmediateTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "x"));
        File.WriteAllText(Path.Combine(_root, "x", "main.py"), Script);
        File.WriteAllText(Path.Combine(_root, "x", ExtensionRegistry.ManifestFileName), $$"""
            { "process": { "command": "{{Python}}", "args": ["-u", "main.py"] },
              "commands": [ { "name": "now", "immediate": true }, { "name": "board" }, { "name": "bad" } ] }
            """);
        var loaded = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        Assert.Empty(ExtensionHost.Restart(loaded.Extensions, new HashSet<string> { "x" }));
        ExtensionPanels.Surface = () => null;
    }

    public void Dispose()
    {
        foreach (var p in ExtensionPanels.All()) ExtensionPanels.Close(p.Extension, p.Id, byUser: false);
        ExtensionPanels.Surface = _surface;
        ExtensionHost.StopAll();
        ExtensionRegistry.Load(null);
        MuxConsole.ClearInjected();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Immediate_IsOptIn_PerCommand()
    {
        Assert.True(ExtensionCommands.Match("/now")!.Immediate);
        Assert.False(ExtensionCommands.Match("/board")!.Immediate);
        Assert.False(ExtensionCommands.TryRunImmediate("/board"));   // normal commands keep waiting for the turn
        Assert.False(ExtensionCommands.TryRunImmediate("hello"));
        Assert.False(ExtensionCommands.TryRunImmediate("/compact"));
    }

    [Fact]
    public async Task Immediate_RunsInTheBackground_AndQueuesItsPrompt()
    {
        var inject = ExtensionHostApi.Inject;
        var sent = new System.Collections.Concurrent.ConcurrentQueue<(string, string)>();
        ExtensionHostApi.Inject = (ext, line) => { sent.Enqueue((ext, line)); return null; };
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(ExtensionCommands.TryRunImmediate("/now"));
            Assert.True(sw.ElapsedMilliseconds < 250);   // returned before the 300ms reply: never blocks the caller
            for (int i = 0; i < 200 && sent.IsEmpty; i++) await Task.Delay(50);
            Assert.Equal(("x", "imm=True"), Assert.Single(sent));   // the process saw immediate=true
        }
        finally { ExtensionHostApi.Inject = inject; }
    }

    [Fact]
    public async Task OpenReply_OpensAPanel_AndABadTreeIsReported()
    {
        var outcome = await ExtensionCommands.RunAsync("/board", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(outcome.Handled);
        var p = Assert.Single(ExtensionPanels.All());
        Assert.Equal(("x", "b", "Board", "hi"), (p.Extension, p.Id, p.Title, p.Root.Text));

        await ExtensionCommands.RunAsync("/bad", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("hi", ExtensionPanels.Get("x", "b")!.Root.Text);   // the last good tree stays
    }

    [Fact]
    public void Interpret_OpenWithoutAView_IsAWarningNotACrash()
    {
        var cmd = ExtensionCommands.Match("/board")!;
        var reply = JsonDocument.Parse("""{ "result": { "open": { "id": "z" } } }""").RootElement;
        Assert.True(ExtensionCommands.Interpret(cmd, reply, cancelled: false, running: true).Handled);
        Assert.Null(ExtensionPanels.Get("x", "z"));
    }
}
