using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

// "Did you mean" suggestions for unrecognized top-level input: layered exact/prefix/
// subsequence/bounded-OSA matching over the canonical TuiCommands catalog. Guards both
// directions - close typos recover, unrelated prose stays silent (no false /help on "hello").
public class CommandSuggestTests
{
    [Theory]
    [InlineData("/agnet", "/agent")]     // adjacent transposition
    [InlineData("agnet", "/agent")]      // slashless token, same typo
    [InlineData("/hepl", "/help")]       // transposition
    [InlineData("/swrm", "/swarm")]      // single deletion
    [InlineData("/prne", "/prune")]      // deletion
    [InlineData("/setmodl", "/setmodel")] // deletion in the middle
    public void Suggest_CloseTypo_RanksIntendedCommandFirst(string input, string expected)
    {
        var got = CommandSuggest.Suggest(input);
        Assert.True(got.Count > 0, $"no suggestion for {input}");
        Assert.Equal(expected, got[0]);
    }

    [Fact]
    public void Suggest_ExactAndPrefix_WinOverEditDistance()
    {
        Assert.Equal("/agent", CommandSuggest.Suggest("/agent")[0]);
        // Prefix: "/se" should offer the shortest /se* command first, not an edit-distance hit.
        var pre = CommandSuggest.Suggest("/se");
        Assert.True(pre.Count > 0);
        Assert.StartsWith("/se", pre[0]);
    }

    [Fact]
    public void Suggest_Gibberish_ReturnsNothing()
    {
        Assert.Empty(CommandSuggest.Suggest("/zzqxv"));
        Assert.Empty(CommandSuggest.Suggest("/xyzzynotacommand"));
    }

    [Fact]
    public void Suggest_StrictMode_DoesNotMisreadProse()
    {
        // Bare prose at the menu uses strict=true (edit budget 1): ordinary words must not
        // trigger command suggestions ("hello" is NOT an attempt to type /help).
        Assert.Empty(CommandSuggest.Suggest("hello", max: 1, strict: true));
        Assert.Empty(CommandSuggest.Suggest("what is the weather", max: 1, strict: true));
        // But a genuine near-miss still recovers under the strict budget.
        var got = CommandSuggest.Suggest("agentt", max: 1, strict: true);
        Assert.True(got.Count == 1 && got[0] == "/agent");
    }

    [Fact]
    public void Suggest_MultiWordInput_UsesFirstTokenOnly()
    {
        var got = CommandSuggest.Suggest("/agnet do something for me");
        Assert.True(got.Count > 0);
        Assert.Equal("/agent", got[0]);
    }

    [Fact]
    public void Suggest_CapsResults_AndNeverThrowsOnEdgeInput()
    {
        Assert.True(CommandSuggest.Suggest("/s", max: 3).Count <= 3);
        Assert.Empty(CommandSuggest.Suggest(""));
        Assert.Empty(CommandSuggest.Suggest("   "));
        Assert.Empty(CommandSuggest.Suggest("/"));
        Assert.Empty(CommandSuggest.Suggest(null!));
    }
}
