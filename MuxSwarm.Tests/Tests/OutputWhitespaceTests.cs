using MuxSwarm.Engine.NativeTools;

namespace MuxSwarm.Tests.Tests;

// Whitespace normalization for native Shell/REPL results: strips end-of-line padding (the
// PowerShell Format-Table class) without touching semantic leading indentation, CRLF identity,
// or an unterminated trailing segment. Deterministic + idempotent = prompt-cache safe.
public class OutputWhitespaceTests
{
    [Fact]
    public void TrimLineTails_StripsTrailingPadding_KeepsIndentation()
    {
        string input = "Name       Length   \r\n----       ------   \r\n    nested.txt    12\r\n";
        string got = OutputWhitespace.TrimLineTails(input);
        Assert.Equal("Name       Length\r\n----       ------\r\n    nested.txt    12\r\n", got);
    }

    [Fact]
    public void TrimLineTails_PreservesCrLfAndLfIdentity()
    {
        Assert.Equal("a\r\nb\r\n", OutputWhitespace.TrimLineTails("a  \r\nb\t\r\n"));
        Assert.Equal("a\nb\n", OutputWhitespace.TrimLineTails("a  \nb \n"));
    }

    [Fact]
    public void TrimLineTails_NeverTouchesUnterminatedTail()
    {
        // A streaming delta can split mid-line; its trailing spaces may be real content.
        Assert.Equal("done  ", OutputWhitespace.TrimLineTails("done  "));
        Assert.Equal("a\nk:  ", OutputWhitespace.TrimLineTails("a  \nk:  "));
    }

    [Fact]
    public void TrimLineTails_IdempotentAndEdgeSafe()
    {
        string once = OutputWhitespace.TrimLineTails("x  \r\n  y\t \n");
        Assert.Equal(once, OutputWhitespace.TrimLineTails(once));
        Assert.Equal("", OutputWhitespace.TrimLineTails(""));
        Assert.Equal("\r\n", OutputWhitespace.TrimLineTails("   \r\n"));
        Assert.Equal("\n\n", OutputWhitespace.TrimLineTails(" \n\t\n"));
    }

    [Fact]
    public void TrimLineTails_FormatTableSample_MeaningfulSavings()
    {
        // Synthetic Format-Table shape: 40 rows padded to 120 columns like a real console grid.
        var rows = Enumerable.Range(1, 40)
            .Select(i => ("row-" + i + "  value").PadRight(120) + "\r\n");
        string sample = string.Concat(rows);
        string trimmed = OutputWhitespace.TrimLineTails(sample);
        Assert.True(trimmed.Length < sample.Length * 0.25,
            $"expected >75% reduction on padded grids, got {sample.Length} -> {trimmed.Length}");
    }
}
