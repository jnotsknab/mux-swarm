using System.IO;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// FallbackReader must deliver a MULTI-LINE seed whole on the first ReadLine() (newlines flattened),
/// not truncate to the first physical line - the bug that cut off the /createhook helper brief.
/// </summary>
public class FallbackReaderTests
{
    [Fact]
    public void ReadLine_MultiLineSeed_DeliveredWholeNotTruncated()
    {
        var seed = "line one of the brief\nline two with detail\nline three end";
        var fr = new FallbackReader(seed, new StringReader(""));
        var got = fr.ReadLine();
        Assert.NotNull(got);
        Assert.Contains("line one", got);
        Assert.Contains("line two", got);
        Assert.Contains("line three", got);
        Assert.DoesNotContain("\n", got);   // flattened to one logical line
    }

    [Fact]
    public void ReadLine_AfterSeed_FallsThroughToFallback()
    {
        var fr = new FallbackReader("seed", new StringReader("real input\n"));
        Assert.Equal("seed", fr.ReadLine());
        Assert.Equal("real input", fr.ReadLine());
    }
}
