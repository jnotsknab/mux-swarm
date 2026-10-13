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
}
