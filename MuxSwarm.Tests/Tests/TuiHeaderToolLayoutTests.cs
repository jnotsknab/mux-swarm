using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Header template/frame and tool-line styles (bullet, gutter, box).</summary>
[Collection("ConsoleState")]
public sealed class TuiHeaderToolLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-headtool-tests-" + Guid.NewGuid().ToString("N"));
    public TuiHeaderToolLayoutTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Style(string regions)
    {
        var dir = Path.Combine(_root, "s");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), "{ \"tui\": { \"regions\": " + regions + " } }");
        Assert.Empty(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] }).Problems);
    }

    [Fact]
    public void HeaderTemplate_FillsPlaceholders_AndEscapesValues()
    {
        Style("""{ "header": { "template": ">> {agent} on {model} [{provider}] {tools}" } }""");
        var rows = TuiComponents.SessionHeader("Code[Agent]", "opus", "cliproxy", 3);
        Assert.Equal(2, rows.Count);
        Assert.Equal("  \u2503 >> Code[Agent] on opus [cliproxy] 3 tools", TuiMarkup.Plain(rows[1]));
    }

    [Fact]
    public void HeaderFrameBox_DrawsARoundedCardAroundTheTemplate()
    {
        Style("""{ "header": { "template": "{agent} \u00b7 {model}", "frame": "box" } }""");
        var rows = TuiComponents.SessionHeader("A", "m", "p").Select(TuiMarkup.Plain).ToList();
        Assert.Equal(["", "  \u256d\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u256e", "  \u2502 A \u00b7 m \u2502", "  \u2570\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u256f"], rows);
    }

    [Theory]
    [InlineData("gutter", "  \u250a \u25cf Reading text file")]
    [InlineData("box", "  \u2595 \u25cf Reading text file \u258f")]
    [InlineData("bullet", "  \u25cf Reading text file")]
    public void ToolStyles_FrameTheCallLine(string style, string expected)
    {
        Style("{ \"toolCard\": { \"style\": \"" + style + "\" } }");
        Assert.Equal(expected, TuiMarkup.Plain(TuiComponents.ToolCall("Filesystem_read_text_file", null)[0]));
    }

    [Fact]
    public void ToolStyles_ApplyToMergedResultLines()
    {
        Style("""{ "toolCard": { "style": "gutter", "gutter": "|" } }""");
        Assert.StartsWith("  | \u25cf shell", TuiMarkup.Plain(TuiComponents.ToolCallResultMerged("shell", null, "ok", false, false)[0]));
    }
}
