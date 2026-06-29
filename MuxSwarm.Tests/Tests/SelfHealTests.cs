using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

public class SelfHealTests
{
    [Fact]
    public void ParseProposals_ParsesValidPipeLines()
    {
        var text = string.Join("\n",
            "BRAIN|EOL discipline|Detect per-file EOL before editing .cs files",
            "MEMORY|build hash|publish g12.75 SHA256 abc123",
            "garbage line with no pipes",
            "WRONG|only two");
        var result = SelfHeal.ParseProposals(text);

        Assert.Equal(2, result.Count);
        Assert.Equal("BRAIN", result[0].Type);
        Assert.Equal("EOL discipline", result[0].Key);
        Assert.Contains("Detect per-file", result[0].Content);
        Assert.Equal("MEMORY", result[1].Type);
    }

    [Fact]
    public void ParseProposals_KeepsPipesInContent()
    {
        var result = SelfHeal.ParseProposals("BRAIN|key|a | b | c");
        Assert.Single(result);
        Assert.Equal("a | b | c", result[0].Content);
    }

    [Fact]
    public void ParseProposals_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(SelfHeal.ParseProposals(""));
        Assert.Empty(SelfHeal.ParseProposals("   \n  \n"));
    }

    [Fact]
    public void Proposal_Label_IsHumanReadable()
    {
        var p = new SelfHeal.Proposal("BRAIN", "k", "v");
        Assert.Equal("[BRAIN] k: v", p.Label);
    }
}
