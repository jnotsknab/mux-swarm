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
}
