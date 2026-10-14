using System.Text.Json;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Extension slash commands (command/run) and footer chips (footer/set). ConsoleState collection:
// Load() and the footer chip store are process-global. The process tests need python on PATH.
[Collection("ConsoleState")]
public sealed class ExtensionCommandsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-extcmd-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";

    // Serves /stats: streams two command/output lines, pushes a footer chip, replies with output +
    // prompt. /fail replies with an error. /slow never replies (cancel test).
    private const string Script = """
        import sys, json
        def send(o): print(json.dumps(o), flush=True)
        for line in sys.stdin:
            msg = json.loads(line)
            if msg.get("method") != "command/run": continue
            p = msg["params"]
            if p["name"] == "stats":
                send({"jsonrpc": "2.0", "method": "command/output", "params": {"text": "working " + p["args"]}})
                send({"jsonrpc": "2.0", "method": "footer/set", "params": {"id": "branch", "text": "main\u0007 *"}})
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {"output": "3 files", "prompt": "Summarize " + p["args"]}})
            elif p["name"] == "fail":
                send({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": 1, "message": "nope"}})
        """;

    public ExtensionCommandsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ExtensionHost.StopAll();
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void AddExtension(string folder, string manifestJson, string? script = null)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), manifestJson);
        if (script is not null) File.WriteAllText(Path.Combine(dir, "main.py"), script);
    }

    private string Proc => $$"""{ "command": "{{Python}}", "args": ["-u", "main.py"] }""";

    [Fact]
    public void Scan_ValidatesCommands_NeverShadowsBuiltIns_FirstNameWins()
    {
        AddExtension("a", $$"""{ "process": {{Proc}}, "commands": [ { "name": "stats", "description": "Repo  stats" }, { "name": "help" }, { "name": "bg" }, { "name": "Bad Name" }, { "name": "slow", "timeoutSeconds": 99999 } ] }""");
        AddExtension("b", $$"""{ "process": {{Proc}}, "commands": [ { "name": "stats" } ] }""");
        AddExtension("c", """{ "commands": [ { "name": "orphan" } ] }""");

        var result = ExtensionRegistry.Scan([_root]);

        var a = result.Extensions.Single(e => e.Name == "a");
        Assert.Equal(["/stats", "/slow"], a.Commands.Select(c => c.Cmd));
        Assert.Equal("Repo stats", a.Commands[0].Description);
        Assert.Equal(ExtensionRegistry.MaxCommandTimeoutSeconds, a.Commands[1].TimeoutSeconds);
        Assert.Empty(result.Extensions.Single(e => e.Name == "b").Commands);
        Assert.Contains(result.Problems, p => p.Contains("'/help' is a built-in"));
        Assert.Contains(result.Problems, p => p.Contains("'/bg' is a built-in"));
        Assert.Contains(result.Problems, p => p.Contains("invalid command name"));
        Assert.Contains(new ExtensionRegistry.Conflict("command /stats", "a", "b"), result.Conflicts);
        Assert.Contains(result.Problems, p => p.Contains("'/orphan' needs a process"));
    }

    [Fact]
    public void Load_PublishesCommands_ToPaletteAndMatch_AndClearsWhenOff()
    {
        AddExtension("a", $$"""{ "process": {{Proc}}, "commands": [ { "name": "stats", "description": "Repo stats" } ] }""");
        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });

        Assert.Equal("stats", ExtensionCommands.Match("  /STATS  now")!.Name);
        Assert.Null(ExtensionCommands.Match("/statsx"));
        Assert.Contains(TuiCommands.WithExtensions(TuiCommands.SessionUnified), e => e.Cmd == "/stats" && e.Desc == "Repo stats [a]");

        ExtensionRegistry.Load(null);
        Assert.Null(ExtensionCommands.Match("/stats"));
        Assert.Same(TuiCommands.SessionUnified, TuiCommands.WithExtensions(TuiCommands.SessionUnified));
    }

    [Fact]
    public async Task Run_StreamsOutput_PushesFooterChip_AndReturnsPrompt()
    {
        AddExtension("git", $$"""{ "process": {{Proc}}, "commands": [ { "name": "stats" }, { "name": "fail" } ] }""", Script);
        var loaded = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        Assert.Empty(ExtensionHost.Restart(loaded.Extensions, new HashSet<string> { "git" }));

        var outcome = await ExtensionCommands.RunAsync("/stats src", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(outcome.Handled);
        Assert.Equal("Summarize src", outcome.Prompt);
        // The chip arrives before the reply on the same stdout stream: cleaned to one line of text.
        Assert.Equal(("git.branch", "main *"), Assert.Single(ExtensionFooter.Snapshot()));
        Assert.Contains(TuiComponents.ExtensionChips(), s => s.Id == "git.branch" && s.Markup.Contains("main *"));

        var failed = await ExtensionCommands.RunAsync("/fail", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((true, (string?)null), (failed.Handled, failed.Prompt));

        ExtensionHost.StopAll();
        Assert.Empty(ExtensionFooter.Snapshot());   // chips go away with their process
        Assert.False((await ExtensionCommands.RunAsync("/nope", CancellationToken.None)).Handled);
    }

    [Fact]
    public async Task Run_IsCancellable_AndNotRunningIsReported()
    {
        AddExtension("git", $$"""{ "process": {{Proc}}, "commands": [ { "name": "slow" } ] }""", Script);
        var loaded = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });

        // Not started (not trusted): handled with a warning, no prompt.
        Assert.Equal((true, (string?)null), ((await ExtensionCommands.RunAsync("/slow", CancellationToken.None)).Handled, null));

        ExtensionHost.Restart(loaded.Extensions, new HashSet<string> { "git" });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var outcome = await ExtensionCommands.RunAsync("/slow", cts.Token).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(outcome.Handled);
        Assert.Null(outcome.Prompt);
    }

    [Fact]
    public void ShareGuests_CannotRunExtensionCommands()
    {
        AddExtension("a", $$"""{ "process": {{Proc}}, "commands": [ { "name": "stats" } ] }""");
        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        Assert.NotNull(ExtensionCommands.Match("/stats"));
        Assert.False(MuxSwarm.Engine.Share.RemotePolicy.IsAllowed("/stats x"));
    }

    [Fact]
    public void Interpret_ErrorsTimeoutsAndEmptyPrompts()
    {
        var cmd = new ExtensionRegistry.ExtensionCommand("x", "c", "", 5);
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
        Assert.Null(ExtensionCommands.Interpret(cmd, default, cancelled: false, running: true).Prompt);
        Assert.Null(ExtensionCommands.Interpret(cmd, J("""{"id":1,"error":{"code":1,"message":"m"}}"""), false, true).Prompt);
        Assert.Null(ExtensionCommands.Interpret(cmd, J("""{"id":1,"result":{"prompt":"   "}}"""), false, true).Prompt);
        Assert.Equal("go", ExtensionCommands.Interpret(cmd, J("""{"id":1,"result":{"prompt":"go"}}"""), false, true).Prompt);
    }

    [Fact]
    public void FooterChips_TemplateIds_AndValidation()
    {
        try
        {
            Assert.True(ExtensionFooter.Set("git", "branch", "main"));
            Assert.False(ExtensionFooter.Set("git", "branch", "main"));          // unchanged = no repaint
            Assert.False(ExtensionFooter.Set("git", "Bad Id", "x"));
            Assert.True(ExtensionFooter.Set("ci", "status", new string('x', 80)));
            Assert.Equal(ExtensionFooter.MaxChipChars, ExtensionFooter.Snapshot()[1].Text.Length);
            Assert.True(ExtensionFooter.Set("git", "branch", ""));               // empty removes
            Assert.Equal(["ci.status"], ExtensionFooter.Snapshot().Select(c => c.Id));

            var problems = new List<string>();
            Assert.Equal(["model", "ci.status"], ExtensionRegistry.ParseFooterTemplate("{model} {ci.status} {ci.}", problems, "x"));
            Assert.Single(problems);
            var segs = TuiComponents.ApplyFooterTemplate([.. TuiComponents.ExtensionChips(), new("model", "M")], ["ci.status", "model"]);
            Assert.Equal(["ci.status", "model"], segs.Select(s => s.Id));
        }
        finally { ExtensionFooter.Clear("git"); ExtensionFooter.Clear("ci"); }
    }
}
