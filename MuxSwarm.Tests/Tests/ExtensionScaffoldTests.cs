using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// /extensions new: the scaffold writes an extension that loads without problems.
public sealed class ExtensionScaffoldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-extscaffold-" + Guid.NewGuid().ToString("N"));

    public ExtensionScaffoldTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Scaffold_WritesAnExtensionThatLoadsClean()
    {
        var all = ExtensionScaffold.Parts.Theme | ExtensionScaffold.Parts.Cosmetics | ExtensionScaffold.Parts.FooterChip
                  | ExtensionScaffold.Parts.Command | ExtensionScaffold.Parts.Tool | ExtensionScaffold.Parts.Intercept
                  | ExtensionScaffold.Parts.Panel;
        var dir = ExtensionScaffold.Scaffold(_root, "My-Ext", "Does things", all, out var error);
        Assert.NotNull(dir);
        Assert.Equal("", error);
        Assert.True(File.Exists(Path.Combine(dir!, "main.py")));

        var result = ExtensionRegistry.Scan([_root]);
        Assert.Empty(result.Problems);
        var ext = Assert.Single(result.Extensions);
        Assert.Equal("my-ext", ext.Name);
        Assert.Equal("Does things", ext.Description);
        Assert.Equal(["my-ext"], ext.Themes);
        Assert.Equal(["/my-ext", "/my-ext-panel"], ext.Commands.Select(c => c.Cmd));
        Assert.True(ext.Commands[1].Immediate);
        Assert.Equal("hello", Assert.Single(ext.Tools).Name);
        Assert.Single(ext.Intercepts);
        Assert.NotNull(ext.Process);

        Assert.Null(ExtensionScaffold.Scaffold(_root, "my-ext", null, all, out error));
        Assert.Contains("already exists", error);
        Assert.Null(ExtensionScaffold.Scaffold(_root, "bad name", null, all, out error));
        Assert.Contains("not a valid name", error);
    }

    [Fact]
    public void Scaffold_ThemeOnly_HasNoProcess_AndBuiltInNamesGetAPrefixedCommand()
    {
        Assert.NotNull(ExtensionScaffold.Scaffold(_root, "pretty", null, ExtensionScaffold.Parts.Theme, out _));
        Assert.False(File.Exists(Path.Combine(_root, "pretty", "main.py")));
        Assert.Null(Assert.Single(ExtensionRegistry.Scan([_root]).Extensions).Process);

        Assert.NotNull(ExtensionScaffold.Scaffold(_root, "help", null, ExtensionScaffold.Parts.Command, out _));
        var help = ExtensionRegistry.Scan([_root]).Extensions.Single(e => e.Name == "help");
        Assert.Equal(["/ext-help"], help.Commands.Select(c => c.Cmd));
    }

    [Fact]
    public void Brief_NamesExactPathsAndTheDocsSections()
    {
        var dir = Path.Combine(_root, "Extensions", "moon");
        var brief = MuxSwarm.Engine.CliCmdUtils.ExtensionBrief("moon", dir, "show the moon phase", process: true);
        Assert.Contains(Path.Combine(dir, "extension.json"), brief);
        Assert.Contains(Path.Combine(dir, "main.py"), brief);
        Assert.Contains(Path.Combine(MuxSwarm.Engine.PlatformContext.ContextDirectory, "DOCS.md"), brief);
        Assert.Contains("'## Extensions'", brief);
        Assert.Contains("'## Event Hooks'", brief);
        Assert.False(File.Exists(Path.Combine(ExtensionScaffold.Scaffold(_root, "noguide", null, ExtensionScaffold.Parts.Theme, out _)!, "EXTENSION_GUIDE.md")));
        Assert.Contains("show the moon phase", brief);
        Assert.Contains("say so instead of guessing", brief);
        Assert.DoesNotContain("main.py", MuxSwarm.Engine.CliCmdUtils.ExtensionBrief("t", dir, "a theme", process: false));
    }

    [Fact]
    public void ScaffoldedProcess_SpeaksTheProtocol()
    {
        var dir = ExtensionScaffold.Scaffold(_root, "proto", null, ExtensionScaffold.Parts.Command | ExtensionScaffold.Parts.Tool | ExtensionScaffold.Parts.Panel, out _)!;
        string py = OperatingSystem.IsWindows() ? "python" : "python3";
        var psi = new System.Diagnostics.ProcessStartInfo(py) { ArgumentList = { "-u", "main.py" }, WorkingDirectory = dir,
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8 };
        using var p = System.Diagnostics.Process.Start(psi)!;
        foreach (var line in new[]
        {
            """{"jsonrpc":"2.0","method":"initialize","params":{"name":"proto","dir":"."}}""",
            """{"jsonrpc":"2.0","id":1,"method":"command/run","params":{"name":"proto","args":"\u263d"}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tool/call","params":{"name":"hello","arguments":{"who":"J"}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tool/after","params":{"tool":"t","result":"r"}}""",
            """{"jsonrpc":"2.0","id":4,"method":"command/run","params":{"name":"proto-panel","args":""}}""",
            """{"jsonrpc":"2.0","method":"ui/event","params":{"id":"main","key":"click","type":"press"}}""",
            """{"jsonrpc":"2.0","id":"upd","result":{}}""",
        }) p.StandardInput.WriteLine(line);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(10000));
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("\"footer/set\"", output);
        Assert.Contains("\"id\": 1, \"result\": {\"output\": \"hello from proto", output);
        Assert.Contains("\"content\": \"hello J\"", output);
        Assert.Contains("\"id\": 3, \"result\": {}", output);
        // The panel command replies with a tree that validates, and a click pushes ui/update.
        var open = output.Split('\n').Select(l => l.Trim()).First(l => l.Contains("\"id\": 4"));
        var view = System.Text.Json.JsonDocument.Parse(open).RootElement.GetProperty("result").GetProperty("open").GetProperty("view");
        Assert.Null(ExtensionPanels.Parse(view, out _));
        Assert.Contains("\"method\": \"ui/update\"", output);
        Assert.Contains("1 clicks", output);
        Assert.DoesNotContain("unknown method", output);   // the reply to our own request was skipped
    }

    [Fact]
    public void AtTheMenu_TheBriefOpensAnAgentSession()
    {
        try
        {
            MuxSwarm.Engine.CliCmdUtils.QueueAgentSession("build it");
            Assert.Equal("/agent", MuxSwarm.Engine.SingleAgentOrchestrator.PendingReplCommand);
            Assert.Equal("build it", MuxSwarm.Engine.SingleAgentOrchestrator.PendingOpeningGoal);
        }
        finally
        {
            MuxSwarm.Engine.SingleAgentOrchestrator.PendingReplCommand = null;
            MuxSwarm.Engine.SingleAgentOrchestrator.PendingOpeningGoal = null;
        }
        // The session consumes it before reading the first input (once), so the brief is turn one.
        var src = File.ReadAllText(Path.Combine(SourceRoot(), "Engine", "SingleAgentOrchestrator.cs"));
        int take = src.IndexOf("Interlocked.Exchange(ref PendingOpeningGoal, null)", StringComparison.Ordinal);
        Assert.True(take > 0 && take < src.IndexOf("string firstInput = MuxConsole.ReadInput(cancellationToken)", StringComparison.Ordinal));
    }

    // Every event name the code emits, and every built-in theme name, must be documented in DOCS.md,
    // since the brief tells the agent to use only what DOCS.md documents.
    [Fact]
    public void Docs_ListEveryEmittedEventAndBuiltInTheme()
    {
        var root = SourceRoot();
        var docs = File.ReadAllText(Path.Combine(root, "Context", "DOCS.md"));
        int start = docs.IndexOf("\n## Event Hooks", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = docs.IndexOf("\n## ", start + 4, StringComparison.Ordinal);
        var hooks = docs[start..(end < 0 ? docs.Length : end)];
        var emitted = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("MuxSwarm.Tests") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f =>
            {
                var src = File.ReadAllText(f);
                return System.Text.RegularExpressions.Regex.Matches(src, "new HookEvent\\s*\\{[^}]*?Event\\s*=\\s*\"([a-z_]+)\"")
                    .Concat(System.Text.RegularExpressions.Regex.Matches(src, "(?:HookWorker\\.|\\b)Emit\\(\"([a-z_]+)\""))
                    .Select(m => m.Groups[1].Value);
            })
            .ToHashSet();
        Assert.True(emitted.Count >= 15, $"found only {emitted.Count} events; is the scan broken?");
        foreach (var e in emitted) Assert.Contains($"| `{e}` |", hooks);
        Assert.Contains("delegation_end", emitted);   // via HookWorker.DelegationEnd's literal
        // /createhook offers exactly the emitted events, so a new event is always pickable.
        Assert.Equal(emitted.OrderBy(e => e), MuxSwarm.Engine.Tui.TuiConfigCommands.HookEvents.OrderBy(e => e));

        int ext = docs.IndexOf("\n## Extensions", StringComparison.Ordinal);
        var extensions = docs[ext..docs.IndexOf("\n## ", ext + 4, StringComparison.Ordinal)];
        foreach (var t in MuxSwarm.Engine.Theme.Presets) Assert.Contains(t.Name, extensions);
        foreach (var id in MuxSwarm.Engine.Tui.TuiComponents.FooterSegmentIds) Assert.Contains(id, extensions);
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
