using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>A caret at the end of a compose row that is already full goes on its own row, so no input row is ever
/// wider than the compose width (a row one cell too wide wrapped and broke shaped boxes until the next key).</summary>
[Collection("ConsoleState")]
public sealed class CaretFullRowTests
{
    [Theory]
    [InlineData(30)] [InlineData(78)]
    public void CaretAtEndOfAFullRow_RollsToANewRow_InsteadOfOverflowing(int width)
    {
        int cap = width - 4;   // "  › " lead
        foreach (string text in new[] { new string('a', cap), new string('a', cap) + new string('b', cap - 4), "x\n" + new string('a', cap) })
        {
            var rows = TuiComponents.InputRowsWithCursor(text, text.Length, width);
            Assert.All(rows, r => Assert.True(TuiMarkup.MarkupWidth(r) <= width, $"overflow {TuiMarkup.MarkupWidth(r)} > {width}: '{TuiMarkup.Plain(r)}'"));
        }
        var full = TuiComponents.InputRowsWithCursor(new string('a', cap), cap, width);
        Assert.Equal(2, full.Count);                         // full text row + caret-only continuation row
        Assert.Single(TuiComponents.InputRowsWithCursor(new string('a', cap - 1), cap - 1, width));
    }

    [Theory]
    [InlineData("box")] [InlineData("bubble")] [InlineData("smooth")] [InlineData("rail")] [InlineData("bracket")] [InlineData("rule")] [InlineData("bare")]
    public void ShapedCompose_FullRowWithTrailingCaret_KeepsEveryRowInsideTheWidth(string shape)
    {
        var root = Path.Combine(Path.GetTempPath(), "mux-caret-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "s"); Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName),
                "{ \"tui\": { \"regions\": { \"compose\": { \"shape\": \"" + shape + "\", \"border\": \"rounded\", \"padX\": 1 } } } }");
            Assert.Empty(ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [root] }).Problems);
            const int width = 80;
            int inner = ComposeShape.InnerWidth(shape, width);
            string text = new string('a', inner - 5) + "t";   // fills the first input row exactly ("  › " lead = 4 cols)
            var frame = ComposeShape.Wrap(shape, TuiComponents.InputRowsWithCursor(text, text.Length, inner), width);
            Assert.All(frame.Lines, l => Assert.True(TuiMarkup.MarkupWidth(l) <= width, $"overflow {TuiMarkup.MarkupWidth(l)}: '{TuiMarkup.Plain(l)}'"));
            Assert.All(frame.Lines, l => Assert.Single(LiveRegion.WrapMarkupLine(l, width)));
            if (shape is "box" or "bubble") Assert.All(frame.InputLines, i => Assert.EndsWith("\u2502", TuiMarkup.Plain(frame.Lines[i])));
        }
        finally { ExtensionRegistry.Load(null); try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
