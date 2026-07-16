using System;
using System.Linq;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Covers the embedded splash-line pool: non-trivial size, Pick() never throws and always
/// returns non-empty text, and quote entries split cleanly into (author, quote).</summary>
public class SplashMessagesTests
{
    [Fact]
    public void Pool_IsComprehensive()
    {
        // Quotes + facts + tips + nudges + taglines. Guard against accidental shrink.
        Assert.True(SplashMessages.PoolSize >= 140, $"pool too small: {SplashMessages.PoolSize}");
    }

    [Fact]
    public void Pick_AlwaysReturnsNonEmptyText_AndNeverThrows()
    {
        for (int i = 0; i < 2000; i++)
        {
            var (label, text) = SplashMessages.Pick();
            Assert.False(string.IsNullOrWhiteSpace(text), "splash text must never be empty");
            Assert.NotNull(label); // may be empty, never null
        }
    }

    [Fact]
    public void Pick_QuotesSplitAuthorFromText()
    {
        // Over many picks we should hit at least one quote (label non-empty) whose text is the quote
        // body (starts with a quote mark) and whose label is the author.
        bool sawQuote = false;
        for (int i = 0; i < 3000 && !sawQuote; i++)
        {
            var (label, text) = SplashMessages.Pick();
            if (!string.IsNullOrEmpty(label) && text.StartsWith("\""))
            {
                sawQuote = true;
                Assert.DoesNotContain(" - ", label); // author only, no separator residue
            }
        }
        Assert.True(sawQuote, "expected to draw at least one attributed quote in 3000 picks");
    }
}
