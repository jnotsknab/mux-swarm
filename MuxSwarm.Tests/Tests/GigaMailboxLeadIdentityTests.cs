using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// M-D regression: the giga-team lead-identity gap. The giga lead is presented to members under the
/// alias "Giga", but the team's resolved lead identity (what member send_message routes to) is the
/// fallback single-agent name (e.g. "MuxAgent"). A member reply addressed to the resolved lead must
/// be drainable by the lead. These tests lock the mailbox-level invariant the fix relies on: a
/// message sent to the resolved lead name lands in that exact inbox, and draining the resolved name
/// (as the patched giga read_inbox now does) returns it.
/// </summary>
public class GigaMailboxLeadIdentityTests
{
    private static string TempRoot()
    {
        var p = Path.Combine(Path.GetTempPath(), "muxgigamail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    // The team as TeamController builds it for a giga team: lead resolves to "MuxAgent" (alias "Giga"
    // does not match a real agent), members are the spawned set.
    private const string Alias = "Giga";
    private const string ResolvedLead = "MuxAgent";
    private static readonly List<string> Audience = new() { "CodeAgent", "WebAgent", ResolvedLead };

    [Fact]
    public void MemberReplyToResolvedLead_IsDrainableByResolvedLead()
    {
        var mb = Mailbox.Open("giga:board-test", TempRoot());
        // Member answers the lead under the RESOLVED identity (member-tool normalization target).
        Assert.Equal(1, mb.Send("CodeAgent", ResolvedLead, MsgType.Answer, "done t1", Audience));

        // The patched giga read_inbox drains the resolved lead name -> the reply surfaces.
        var got = mb.ReadInbox(ResolvedLead, drain: true);
        Assert.Single(got);
        Assert.Equal("CodeAgent", got[0].From);
        Assert.Equal(MsgType.Answer, got[0].Type);
    }

    [Fact]
    public void DrainingBothAliasAndResolved_SurfacesReplyRegardlessOfAddressedName()
    {
        var mb = Mailbox.Open("giga:board-test", TempRoot());
        // One member addresses the alias, another the resolved name (both are valid lead handles).
        mb.Send("CodeAgent", ResolvedLead, MsgType.Answer, "via resolved", Audience);
        mb.Send("WebAgent", Alias, MsgType.Answer, "via alias", Audience);

        // The fix drains BOTH identities so neither reply is stranded.
        var msgs = new List<TeamMessage>(mb.ReadInbox(Alias, drain: true));
        msgs.AddRange(mb.ReadInbox(ResolvedLead, drain: true));

        Assert.Equal(2, msgs.Count);
        Assert.Contains(msgs, m => m.Body == "via resolved");
        Assert.Contains(msgs, m => m.Body == "via alias");
    }

    [Fact]
    public void ResolvedLeadInbox_DoesNotLeakToAliasOnly_BeforeFix()
    {
        // Documents the ROOT CAUSE: a reply to the resolved lead is NOT visible when draining only
        // the alias (the pre-fix giga read_inbox behavior) - which is why the fix drains both.
        var mb = Mailbox.Open("giga:board-test", TempRoot());
        mb.Send("CodeAgent", ResolvedLead, MsgType.Answer, "stranded under alias-only drain", Audience);
        Assert.Empty(mb.ReadInbox(Alias, drain: true));            // alias-only: nothing (the bug)
        Assert.Single(mb.ReadInbox(ResolvedLead, drain: true));    // resolved: there it is (the fix)
    }
}
