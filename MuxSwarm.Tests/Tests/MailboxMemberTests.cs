using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// M4 follow-up (g12.32): member-side mailbox completion. Covers the new unread helpers that drive
/// the inbox-drain + idle-wake behavior for team members (UnreadCountFor, HasActionableUnread).
/// </summary>
public class MailboxMemberTests
{
    private static string TempRoot()
    {
        var p = Path.Combine(Path.GetTempPath(), "muxmailmbr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static readonly List<string> Team = new() { "Lead", "Alice", "Bob" };

    [Fact]
    public void UnreadCountFor_IsPerAgent_AndClearsOnDrain()
    {
        var mb = Mailbox.Open("t", TempRoot());
        mb.Send("Lead", "Alice", MsgType.Info, "a", Team);
        mb.Send("Bob", "Alice", MsgType.Question, "b", Team);
        Assert.Equal(2, mb.UnreadCountFor("Alice"));
        Assert.Equal(0, mb.UnreadCountFor("Bob"));
        mb.ReadInbox("Alice", drain: true);
        Assert.Equal(0, mb.UnreadCountFor("Alice"));
    }

    [Fact]
    public void HasActionableUnread_OnlyQuestionsAndHandoffsWake()
    {
        var mb = Mailbox.Open("t", TempRoot());
        // Info + Answer are FYI: they do NOT wake an idle member.
        mb.Send("Lead", "Alice", MsgType.Info, "fyi", Team);
        mb.Send("Lead", "Alice", MsgType.Answer, "done", Team);
        Assert.False(mb.HasActionableUnread("Alice"));

        // A Question is actionable.
        mb.Send("Bob", "Alice", MsgType.Question, "status?", Team);
        Assert.True(mb.HasActionableUnread("Alice"));

        // Draining clears it.
        mb.ReadInbox("Alice", drain: true);
        Assert.False(mb.HasActionableUnread("Alice"));
    }

    [Fact]
    public void HasActionableUnread_HandoffWakes()
    {
        var mb = Mailbox.Open("t", TempRoot());
        mb.Send("Lead", "Bob", MsgType.Handoff, "take this", Team);
        Assert.True(mb.HasActionableUnread("Bob"));
        Assert.False(mb.HasActionableUnread("Alice"));
    }

    [Fact]
    public void MemberToMember_AndMemberToLead_Deliver()
    {
        var mb = Mailbox.Open("t", TempRoot());
        // Member -> member
        Assert.Equal(1, mb.Send("Alice", "Bob", MsgType.Handoff, "yours", Team));
        Assert.Single(mb.ReadInbox("Bob", drain: false));
        // Member -> lead
        Assert.Equal(1, mb.Send("Alice", "Lead", MsgType.Answer, "reply", Team));
        Assert.Single(mb.ReadInbox("Lead", drain: false));
    }
}
