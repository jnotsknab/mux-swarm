using Microsoft.Extensions.AI;
using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

// /split lead-context sharing: the delimited block builder renders the lead's window with role
// tags and compacted tool traffic, excludes system messages, caps tail-biased (newest survives),
// and WrapTask is identity when there is nothing to share. Sharing is opt-in per delegation -
// the armed flag alone changes nothing.
public class LeadContextShareTests
{
    private static List<ChatMessage> History(params ChatMessage[] msgs) => msgs.ToList();

    [Fact]
    public void BuildBlock_RendersRolesAndDelimiters_ExcludesSystem()
    {
        var block = LeadContextShare.BuildBlock(History(
            new ChatMessage(ChatRole.System, "persona text MUST NOT leak"),
            new ChatMessage(ChatRole.User, "investigate the flaky test"),
            new ChatMessage(ChatRole.Assistant, "found the race in FooTests")));

        Assert.StartsWith(LeadContextShare.BlockHeader, block);
        Assert.EndsWith(LeadContextShare.BlockFooter, block);
        Assert.Contains("## USER", block);
        Assert.Contains("## LEAD", block);
        Assert.Contains("investigate the flaky test", block);
        Assert.DoesNotContain("persona text MUST NOT leak", block);
    }

    [Fact]
    public void BuildBlock_CompactsToolCallsAndTruncatesHugeResults()
    {
        var call = new FunctionCallContent("call1", "Filesystem_read_text_file",
            new Dictionary<string, object?> { ["path"] = "x.cs" });
        var result = new FunctionResultContent("call1", new string('r', 9000));
        var block = LeadContextShare.BuildBlock(History(
            new ChatMessage(ChatRole.Assistant, [call]),
            new ChatMessage(ChatRole.Tool, [result])));

        Assert.Contains("[tool call: Filesystem_read_text_file]", block);
        Assert.Contains("[...result truncated in shared context...]", block);
        Assert.True(block.Length < 6000, $"tool result must be capped, got {block.Length}");
    }

    [Fact]
    public void BuildBlock_TailBiasedCap_KeepsNewestAndMarksOmission()
    {
        var msgs = Enumerable.Range(1, 50)
            .Select(i => new ChatMessage(ChatRole.User, $"msg-{i} " + new string('x', 400)))
            .ToArray();
        var block = LeadContextShare.BuildBlock(History(msgs), maxChars: 3000);

        Assert.Contains("msg-50", block);                       // newest survives
        Assert.DoesNotContain("msg-1 ", block);                  // oldest dropped
        Assert.Contains(LeadContextShare.OmissionMarker, block);
        Assert.True(block.Length < 4200, $"cap overshoot: {block.Length}");
    }

    [Fact]
    public void BuildBlock_EmptyOrNull_YieldsEmpty_AndWrapTaskIsIdentity()
    {
        Assert.Equal("", LeadContextShare.BuildBlock(null));
        Assert.Equal("", LeadContextShare.BuildBlock(History()));
        Assert.Equal("do the thing", LeadContextShare.WrapTask("do the thing", ""));
    }

    [Fact]
    public void WrapTask_PrependsBlockAndKeepsTaskLast()
    {
        var block = LeadContextShare.BuildBlock(History(new ChatMessage(ChatRole.User, "ctx")));
        string wrapped = LeadContextShare.WrapTask("summarize findings", block);
        Assert.StartsWith(LeadContextShare.BlockHeader, wrapped);
        Assert.Contains("YOUR TASK:", wrapped);
        Assert.EndsWith("summarize findings", wrapped);
        Assert.True(wrapped.IndexOf("ctx", StringComparison.Ordinal)
                  < wrapped.IndexOf("summarize findings", StringComparison.Ordinal));
    }

    [Fact]
    public void SplitFlag_DefaultOff_AndToggleable()
    {
        Assert.False(App.SplitContextShare);   // off by default: schemas/behavior unchanged
        try
        {
            App.SplitContextShare = true;
            Assert.True(App.SplitContextShare);
        }
        finally { App.SplitContextShare = false; }
    }
}
