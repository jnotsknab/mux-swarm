using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Lsp;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Post-edit diagnostics: errors for files a tool changed are appended to that tool's result, whether
// the file tool named the path or the change came from the watcher (shell/REPL); changes landing
// between calls go to the next result; pending is never reported as clean; off = untouched result.
[Collection("ConsoleState")]
public sealed class LspWriteDiagnosticsTests : IDisposable
{
    private readonly LspTestRig _rig = new();

    public LspWriteDiagnosticsTests() => LspWriteDiagnostics.ShellSettle = TimeSpan.Zero;

    public void Dispose()
    {
        LspWriteDiagnostics.Reset();
        LspWriteDiagnostics.ShellSettle = TimeSpan.FromMilliseconds(150);
        _rig.Dispose();
    }

    private static async Task<string> Invoke(AIFunction fn, params (string Key, object? Value)[] args)
    {
        var ctx = new FunctionInvocationContext { Function = fn, Arguments = new AIFunctionArguments(args.ToDictionary(a => a.Key, a => a.Value)) };
        var result = await LspWriteDiagnostics.InvokeAsync(ctx, CancellationToken.None, null);
        return result switch { string s => s, IEnumerable<AIContent> parts => string.Join("\n", parts.OfType<TextContent>().Select(t => t.Text)), _ => result?.ToString() ?? "" };
    }

    private static AIFunction WriteTool(string name = "Filesystem_write_file") =>
        AIFunctionFactory.Create((string path, string content) => { File.WriteAllText(path, content); return "Wrote " + path; }, name);

    [Fact]
    public async Task FileTool_AppendsErrorsOfTheFileItWrote()
    {
        _rig.Use();
        var f = Path.Combine(_rig.Dir, "proj", "src", "a.fake");
        var text = await Invoke(WriteTool(), ("path", f), ("content", "ok\nhas BUG\n"));
        Assert.StartsWith("Wrote ", text);
        Assert.Contains("[LSP] errors in", text);
        Assert.Contains("a.fake:2:5 error: found a BUG (fake)", text);
        Assert.DoesNotContain("warning", text);   // errors only

        text = await Invoke(WriteTool(), ("path", f), ("content", "ok\nfixed\n"));
        Assert.Contains("[LSP] no errors in", text);
    }

    [Fact]
    public async Task ChangesSeenDuringTheCall_FromShellOrRepl_AreReportedOnThatResult()
    {
        _rig.Use();
        var f = _rig.Src("b.fake", "x");
        var shellish = AIFunctionFactory.Create(() =>
        {
            File.WriteAllText(f, "BUG one\n");
            LspWriteDiagnostics.OnFileChanged(f, "modified");   // what the watcher reports
            return "exit 0";
        }, "repl_shell_exec");
        var text = await Invoke(shellish);
        Assert.StartsWith("exit 0", text);
        Assert.Contains("b.fake:1:1 error: found a BUG", text);
    }

    [Fact]
    public async Task ChangesBetweenCalls_GoToTheNextResult()
    {
        _rig.Use();
        var f = _rig.Src("c.fake", "BUG\n");
        LspWriteDiagnostics.OnFileChanged(f, "modified");   // a background job wrote it
        var text = await Invoke(AIFunctionFactory.Create(() => "status: done", "check_job_status"));
        Assert.Contains("c.fake:1:1 error", text);
        Assert.Equal("again", await Invoke(AIFunctionFactory.Create(() => "again", "check_job_status")));   // reported once
    }

    [Fact]
    public async Task LateWatcherEchoOfAReportedWrite_IsNotRepeatedOnTheNextResult()
    {
        // The file tool reports its own write; the watcher sees the same write and its event can arrive after that
        // call ended. Repeating it on the next result (e.g. an async shell call still "running") read as "that
        // command left no errors" before the command had written anything.
        _rig.Use();
        var f = Path.Combine(_rig.Dir, "proj", "src", "g.fake");
        Assert.Contains("[LSP] no errors in", await Invoke(WriteTool(), ("path", f), ("content", "fine\n")));
        LspWriteDiagnostics.OnFileChanged(f, "modified");   // the late echo of that write
        var running = AIFunctionFactory.Create(() => "Job ID: job_1\nStatus: running", "execute_command_async");
        Assert.Equal("Job ID: job_1\nStatus: running", await Invoke(running));

        File.WriteAllText(f, "BUG later\n");   // a real change afterwards is still reported
        LspWriteDiagnostics.OnFileChanged(f, "modified");
        Assert.Contains("g.fake:1:1 error", await Invoke(AIFunctionFactory.Create(() => "done", "check_job_status")));
    }

    [Fact]
    public async Task ReadsAndUncoveredFiles_LeaveTheResultAlone()
    {
        _rig.Use();
        var f = _rig.Src("d.fake", "BUG");
        var read = AIFunctionFactory.Create((string path) => "content", "Filesystem_read_text_file");
        Assert.Equal("content", await Invoke(read, ("path", f)));
        var txt = Path.Combine(_rig.Dir, "notes.txt");
        Assert.Equal("Wrote " + txt, await Invoke(WriteTool(), ("path", txt), ("content", "BUG")));
    }

    [Fact]
    public async Task SlowServer_SaysPending_NeverNoErrors()
    {
        _rig.SetEnv("FAKE_LSP_NO_DIAG", "1");
        _rig.Use(waitMs: 300);
        var text = await Invoke(WriteTool(), ("path", Path.Combine(_rig.Dir, "proj", "e.fake")), ("content", "BUG"));
        Assert.Contains("diagnostics pending", text);
        Assert.DoesNotContain("no errors", text);
    }

    [Fact]
    public async Task Off_ResultIsUntouched()
    {
        var cfg = _rig.Use();
        cfg.DiagnosticsOnWrite = false;
        var f = Path.Combine(_rig.Dir, "proj", "f.fake");
        Assert.Equal("Wrote " + f, await Invoke(WriteTool(), ("path", f), ("content", "BUG")));
        LspWriteDiagnostics.OnFileChanged(f, "modified");
        Assert.Equal("x", await Invoke(AIFunctionFactory.Create(() => "x", "check_job_status")));
    }

    [Fact]
    public void LspTurnedOnAfterStartup_EnsureFileTracker_StartsTheWatchersOnce()
    {
        // lsp enabled after startup (config edit + refresh): post-edit diagnostics need the file watchers,
        // which used to start only at launch.
        var savedConfig = App.Config; var savedTracker = FileChangeTracker.Shared;
        try
        {
            FileChangeTracker.Shared = null;
            App.Config = new AppConfig { Serve = { FileEvents = false }, Filesystem = { SandboxPath = _rig.Dir, AllowedPaths = [] } };
            App.EnsureFileTracker();
            Assert.Null(FileChangeTracker.Shared);   // lsp off, no file events: nothing needs watchers

            App.Config.Lsp = _rig.Use();
            App.EnsureFileTracker();
            var tracker = FileChangeTracker.Shared;
            Assert.NotNull(tracker);
            Assert.Contains(FileChangeTracker.NormalizeRoots([_rig.Dir])[0], tracker!.Roots);
            App.EnsureFileTracker();
            Assert.Same(tracker, FileChangeTracker.Shared);   // idempotent: one set of watchers
        }
        finally
        {
            FileChangeTracker.Shared?.Stop();
            FileChangeTracker.Shared = savedTracker; App.Config = savedConfig;
        }
    }
}
