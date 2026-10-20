using System.Collections.Concurrent;
using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>file_changed: real file-system changes during tool calls, whatever wrote them.</summary>
public class FileChangeTrackerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mux-fct-").FullName;
    private readonly ConcurrentQueue<(string Path, string Change, string? Tool)> _events = new();
    private readonly FileChangeTracker _t;

    public FileChangeTrackerTests()
    {
        _t = new FileChangeTracker((p, c, tool) => _events.Enqueue((p, c, tool)));
        _t.Start(new[] { _root });
    }

    public void Dispose()
    {
        _t.Stop();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private bool WaitFor(Func<(string Path, string Change, string? Tool), bool> match, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (_events.Any(match)) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    [Fact]
    public void WriteDuringAToolCall_IsReported_WithTheTool()
    {
        var f = Path.Combine(_root, "a.md");
        using (_t.BeginToolCall("repl_shell_exec"))
            File.WriteAllText(f, "hello");   // any writer: this stands in for the REPL / a shell child
        Assert.True(WaitFor(e => e.Path == f && e.Tool == "repl_shell_exec" && e.Change is "created" or "modified"));
    }

    [Fact]
    public void OverlappingScopes_KeepReporting_UntilTheLastOneEnds()
    {
        // A background shell/REPL job holds its own scope after the tool call that started it returns.
        var job = _t.BeginToolCall("execute_command_async");
        using (_t.BeginToolCall("execute_command_async")) { }   // the tool call itself ends at once
        Thread.Sleep(FileChangeTracker.Tail + TimeSpan.FromMilliseconds(200));
        var f = Path.Combine(_root, "late.txt");
        File.WriteAllText(f, "written by the still-running job");
        Assert.True(WaitFor(e => e.Path == f));
        job.Dispose();
    }

    [Fact]
    public void MuxOwnState_IsNotReported_EvenInsideAWatchedRoot()
    {
        // Telemetry traces, REPL worker files, the browser profile and the graph file are written by
        // Mux itself during tool calls; they used to steal Follow mode on every call.
        var tele = Directory.CreateDirectory(Path.Combine(_root, "Telemetry")).FullName;
        var kg = Path.Combine(_root, "memory.jsonl");
        _t.Exclude(FileChangeTracker.OwnStateDirs(_root, null, kg, null));
        var own = Path.Combine(tele, "traces-20261020.jsonl");
        var mine = Path.Combine(_root, "notes.md");
        using (_t.BeginToolCall("repl_shell_exec"))
        {
            File.WriteAllText(own, "{}");
            File.WriteAllText(kg, "{}");
            File.WriteAllText(mine, "# notes");
        }
        Assert.True(WaitFor(e => e.Path == mine));
        Thread.Sleep(300);
        Assert.DoesNotContain(_events, e => e.Path == own || e.Path == kg);
        Assert.False(_t.IsExcluded(Path.Combine(_root, "memory.jsonl.bak")));   // exact file, not a prefix
    }

    [Fact]
    public void WriteOutsideAnyToolCall_IsNotReported()
    {
        File.WriteAllText(Path.Combine(_root, "user-edit.md"), "mine");
        Thread.Sleep(800);
        Assert.Empty(_events);
    }

    [Fact]
    public void IgnoredDirs_AndTempFiles_AreNotReported()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "x"));
        using (_t.BeginToolCall("execute_command_async"))
        {
            File.WriteAllText(Path.Combine(_root, "node_modules", "x", "i.js"), "1");
            File.WriteAllText(Path.Combine(_root, "scratch.tmp"), "1");
            File.WriteAllText(Path.Combine(_root, "kept.txt"), "1");
        }
        Assert.True(WaitFor(e => e.Path.EndsWith("kept.txt")));
        Assert.DoesNotContain(_events, e => e.Path.Contains("node_modules") || e.Path.EndsWith(".tmp"));
    }

    [Fact]
    public void Roots_AreDeduplicated_AndNestedOrMissingRootsDropped()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        var roots = FileChangeTracker.NormalizeRoots(new[] { _root, nested, _root + Path.DirectorySeparatorChar, Path.Combine(_root, "missing"), null, "" });
        Assert.Equal(new[] { Path.TrimEndingDirectorySeparator(_root) }, roots);
    }

    [Theory]
    [InlineData("C:/w/.git/index", true)]
    [InlineData("/w/obj/x.dll", true)]
    [InlineData("/w/src/~$doc.docx", true)]
    [InlineData("/w/src/notes.md.mux-tmp-1961a22b", true)]
    [InlineData("/w/src/app.cs", false)]
    [InlineData("/w/build.md", false)]   // a FILE named like an ignored dir is still reported
    [InlineData("C:/Temp/mux-herdr-hook.log", true)]   // append-only logs (hooks, services) are never followed
    [InlineData("/w/app.lock", true)]
    [InlineData("/w/report.md", false)]
    public void IsIgnored(string path, bool expected) => Assert.Equal(expected, FileChangeTracker.IsIgnored(path));

    [Fact]
    public void ConfigDefaultsOn_AndCanBeDisabled()
    {
        Assert.True(new ServeConfig().FileEvents);
        var cfg = System.Text.Json.JsonSerializer.Deserialize<ServeConfig>("{\"fileEvents\": false}");
        Assert.False(cfg!.FileEvents);
    }

    [Fact]
    public void WebUiFollow_UsesFileChangedEvents()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MuxSwarm.csproj"))) root = Path.GetDirectoryName(root)!;
        var html = File.ReadAllText(Path.Combine(root, "Runtime", "mux-web-app", "index.html"));
        Assert.Contains("ev.type !== 'file_changed'", html);
        Assert.Contains("window.MuxFileChips.lookup(path, true)", html);   // bypass a cached pre-creation miss
        Assert.DoesNotContain("var WRITE_TOOL =", html);
    }
}
