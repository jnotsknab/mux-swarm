using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>The layout example in DOCS.md (## Extensions, tui) loads and renders the box with the footer in its edge.</summary>
[Collection("ConsoleState")]
public sealed class TuiLayoutDocsExampleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-layoutdoc-tests-" + Guid.NewGuid().ToString("N"));
    public TuiLayoutDocsExampleTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void DocsExample_LoadsCleanly()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MuxSwarm.csproj"))) root = Path.GetDirectoryName(root)!;
        string docs = File.ReadAllText(Path.Combine(root, "Context", "DOCS.md"));
        int at = docs.IndexOf("Example, an editor-style box", StringComparison.Ordinal);
        Assert.True(at > 0);
        int open = docs.IndexOf("```jsonc", at, StringComparison.Ordinal) + "```jsonc".Length;
        string body = docs[open..docs.IndexOf("```", open, StringComparison.Ordinal)];
        var dir = Path.Combine(_root, "example");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{" + body + "}");
        var result = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        Assert.Empty(result.Problems);
        Assert.Equal("box", RegionStyle.Choice("compose.shape"));
        Assert.Equal("border", RegionStyle.Choice("footer.position"));
    }
}
