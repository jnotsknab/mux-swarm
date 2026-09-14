using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

// /help now shares the /shortcuts visual language: section headers as group titles, command
// entries as a two-tone key column + muted description, continuations muted. The Help.HelpText
// catalog string remains the single source of truth - this is presentation only.
public class HelpRenderTests
{
    [Fact]
    public void HelpLine_SectionHeader_GetsGroupTitleTint()
    {
        string got = MuxConsole.HelpLineMarkup("Slash Commands");
        Assert.Contains("Slash Commands", got);
        Assert.DoesNotContain("PadRight", got);
        // Header tint = the Step color used for /shortcuts group titles.
        Assert.StartsWith("  [", got);
    }

    [Fact]
    public void HelpLine_CommandEntry_SplitsKeyAndDescriptionColumns()
    {
        string got = MuxConsole.HelpLineMarkup("  /agent          Open the main agentic interface");
        int keyIdx = got.IndexOf("/agent", StringComparison.Ordinal);
        int descIdx = got.IndexOf("Open the main agentic interface", StringComparison.Ordinal);
        Assert.True(keyIdx > 0 && descIdx > keyIdx, got);
        // Two-tone: at least two distinct markup opens (key tint + muted description).
        Assert.True(got.Count(c => c == '[') >= 2, got);
    }

    [Fact]
    public void HelpLine_ContinuationLine_IsMutedNotColumnized()
    {
        string got = MuxConsole.HelpLineMarkup("                  Work directly or delegate; ultra leads can match swarm modes.");
        Assert.Contains("Work directly or delegate", got);
        Assert.DoesNotContain("[/][", got.Replace("[/]  [", "SPLIT"));   // one tint run, not key+desc pair
    }

    [Fact]
    public void HelpLine_BlankAndFlags_AreSafe()
    {
        Assert.Equal("", MuxConsole.HelpLineMarkup(""));
        Assert.Equal("", MuxConsole.HelpLineMarkup("   "));
        string flag = MuxConsole.HelpLineMarkup("  --goal <text|file>         Explicit goal");
        Assert.Contains("--goal", flag);
        Assert.Contains("Explicit goal", flag);
    }

    [Fact]
    public void HelpText_EveryLine_RendersWithoutThrowing()
    {
        foreach (var raw in Help.HelpText.Replace("\r\n", "\n").Split('\n'))
        {
            string markup = MuxConsole.HelpLineMarkup(raw.TrimEnd());
            // Must parse as valid Spectre markup (Esc() protects payload brackets).
            _ = new Spectre.Console.Markup(markup.Length == 0 ? " " : markup);
        }
    }
}
