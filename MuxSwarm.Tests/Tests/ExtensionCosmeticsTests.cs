using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Cosmetic extension overrides: spinner, quips, toolLabels, splash. ConsoleState collection because
// Load() publishes into process-global ThinkingIndicator/ToolActionLabel/SplashMessages state.
[Collection("ConsoleState")]
public sealed class ExtensionCosmeticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-extcos-tests-" + Guid.NewGuid().ToString("N"));

    public ExtensionCosmeticsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void AddExtension(string folder, string manifestJson)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), manifestJson);
    }

    private ExtensionRegistry.Result LoadEnabled() => ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });

    [Fact]
    public void Load_PublishesEveryOverride_AndDisabledRestoresBuiltIns()
    {
        string builtInLabel = ToolActionLabel.Describe("Filesystem_read_text_file");
        AddExtension("fun", """
            { "spinner": ["-", "\\", "|", "/"], "quips": ["Plotting"],
              "toolLabels": { "read": "Peeking at" }, "splash": ["Hello from fun"] }
            """);

        var result = LoadEnabled();

        Assert.Empty(result.Problems);
        Assert.Equal(["spinner", "quips", "splash", "toolLabels"], Assert.Single(result.Extensions).Overrides);
        Assert.Equal(["-", "\\", "|", "/"], ThinkingIndicator.ExtensionFrames);
        Assert.Equal(["Plotting"], ThinkingIndicator.Rotation());
        Assert.Equal("Peeking at text file", ToolActionLabel.Describe("Filesystem_read_text_file"));
        Assert.Equal("Searching files", ToolActionLabel.Describe("search_files"));   // built-ins still apply
        Assert.Equal(("", "Hello from fun"), SplashMessages.Pick());

        ExtensionRegistry.Load(null);
        Assert.Null(ThinkingIndicator.ExtensionFrames);
        Assert.Contains("Thinking", ThinkingIndicator.Rotation());
        Assert.Equal(builtInLabel, ToolActionLabel.Describe("Filesystem_read_text_file"));
        Assert.NotEqual("Hello from fun", SplashMessages.Pick().Text);
    }

    [Fact]
    public void Lists_LaterNameWins_AndToolLabelsMergePerKey()
    {
        AddExtension("aaa", """{ "quips": ["A"], "toolLabels": { "read": "A-read", "list": "A-list" } }""");
        AddExtension("bbb", """{ "quips": ["B"], "toolLabels": { "read": "B-read" } }""");

        var result = ExtensionRegistry.Scan([_root]);

        Assert.Equal(["B"], result.Cosmetics.Quips!);
        Assert.Contains(result.Problems, p => p.Contains("quips replaces"));
        Assert.Equal("B-read", result.Cosmetics.ToolLabels!["read"]);
        Assert.Equal("A-list", result.Cosmetics.ToolLabels!["LIST"]);
    }

    [Theory]
    [InlineData("""{ "spinner": [] }""", "spinner needs 1-32")]
    [InlineData("""{ "spinner": ["ab"] }""", "not one terminal cell")]
    [InlineData("""{ "spinner": ["\u4E2D"] }""", "not one terminal cell")]
    [InlineData("""{ "quips": ["ok", " "] }""", "empty entry")]
    [InlineData("""{ "quips": ["bad\u001b[31m"] }""", "control characters")]
    [InlineData("""{ "splash": ["xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"] }""", "longer than 80")]
    [InlineData("""{ "toolLabels": { "read file": "x" } }""", "single word")]
    [InlineData("""{ "toolLabels": { "read": "" } }""", "1-40 characters")]
    public void Scan_RejectsInvalidOverrides_WithAProblem(string manifest, string expected)
    {
        AddExtension("pack", manifest);
        var result = ExtensionRegistry.Scan([_root]);
        Assert.Empty(Assert.Single(result.Extensions).Overrides);
        Assert.Equal(new ExtensionRegistry.Cosmetics(null, null, null, null), result.Cosmetics);
        Assert.Contains(result.Problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MarkupInLabelsAndQuips_IsRenderedLiterally()
    {
        AddExtension("evil", """{ "toolLabels": { "read": "[red]Boom" }, "spinner": ["["] }""");
        LoadEnabled();

        string call = Assert.Single(TuiComponents.ToolCall("Filesystem_read_text_file", null));
        Assert.Contains("[[red]]Boom", call);
        Assert.Equal("[red]Boom text file", TuiMarkup.Plain(call).Trim()[2..]);

        string line = TuiComponents.ThinkingLine("Plotting [bold]now", 0);   // no leading frame: fallback uses the extension spinner
        Assert.Equal("[ Plotting [bold]now", TuiMarkup.Plain(line).Trim());
    }
}
