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
                  | ExtensionScaffold.Parts.Command | ExtensionScaffold.Parts.Tool | ExtensionScaffold.Parts.Intercept;
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
        Assert.Equal(["/my-ext"], ext.Commands.Select(c => c.Cmd));
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
    public void Scaffold_WritesTheFullReferenceGuide_FromDocs()
    {
        var docs = Path.Combine(_root, "DOCS.md");
        File.WriteAllText(docs, "# Ref\n\n## Before\nx\n\n## Extensions\n\nEvery key: `thinking`, `footer/set`, `command/run`.\n\n## After\ny\n");
        var dir = ExtensionScaffold.Scaffold(Path.Combine(_root, "ext"), "guide-test", null, ExtensionScaffold.Parts.Command, out _, docs)!;
        var guide = File.ReadAllText(Path.Combine(dir, ExtensionScaffold.GuideFileName));
        Assert.StartsWith("# guide-test: Mux extension guide", guide);
        Assert.Contains("## Extensions", guide);
        Assert.Contains("`thinking`, `footer/set`, `command/run`", guide);
        Assert.DoesNotContain("## After", guide);
        Assert.DoesNotContain("## Before", guide);
        Assert.Contains("\"method\":\"command/run\"", guide);   // pipe-test example survives interpolation

        var missing = ExtensionScaffold.Guide("x", Path.Combine(_root, "nope.md"));
        Assert.Contains("See the '## Extensions' section of", missing);
    }

    [Fact]
    public void Brief_NamesExactPathsAndTheGuide()
    {
        var dir = Path.Combine(_root, "Extensions", "moon");
        var brief = MuxSwarm.Engine.CliCmdUtils.ExtensionBrief("moon", dir, "show the moon phase", process: true);
        Assert.Contains(Path.Combine(dir, "extension.json"), brief);
        Assert.Contains(Path.Combine(dir, "main.py"), brief);
        Assert.Contains(Path.Combine(dir, ExtensionScaffold.GuideFileName), brief);
        Assert.Contains("show the moon phase", brief);
        Assert.Contains("say so instead of guessing", brief);
        Assert.DoesNotContain("main.py", MuxSwarm.Engine.CliCmdUtils.ExtensionBrief("t", dir, "a theme", process: false));
    }

    [Fact]
    public void ScaffoldedProcess_SpeaksTheProtocol()
    {
        var dir = ExtensionScaffold.Scaffold(_root, "proto", null, ExtensionScaffold.Parts.Command | ExtensionScaffold.Parts.Tool, out _)!;
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
        }) p.StandardInput.WriteLine(line);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(10000));
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("\"footer/set\"", output);
        Assert.Contains("\"id\": 1, \"result\": {\"output\": \"hello from proto", output);
        Assert.Contains("\"content\": \"hello J\"", output);
        Assert.Contains("\"id\": 3, \"result\": {}", output);
    }
}
