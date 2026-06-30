using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// M4 (g12.30) Mailbox: inter-agent P2P file inboxes. Covers send/broadcast, peek vs drain,
/// persistence reload, the cooperative shutdown flag, and history (the Agent View m-log source).
/// </summary>
public class MailboxTests
{
    private static string TempRoot()
    {
        var p = Path.Combine(Path.GetTempPath(), "muxmail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static readonly List<string> Team = new() { "Lead", "Alice", "Bob" };

    [Fact]
    public void Send_DeliversToOneInbox_ReadDrainsOnce()
    {
        var root = TempRoot();
        var mb = Mailbox.Open("t", root);
        Assert.Equal(1, mb.Send("Lead", "Alice", MsgType.Info, "hello", Team));

        var peek = mb.ReadInbox("Alice", drain: false);
        Assert.Single(peek);
        Assert.Equal("hello", peek[0].Body);
        Assert.Equal("Lead", peek[0].From);

        // Drain returns it once; a second read sees nothing new.
        Assert.Single(mb.ReadInbox("Alice", drain: true));
        Assert.Empty(mb.ReadInbox("Alice", drain: true));
        // Bob never got it.
        Assert.Empty(mb.ReadInbox("Bob", drain: false));
    }

    [Fact]
    public void Broadcast_DeliversToAllMembersExceptSender()
    {
        var root = TempRoot();
        var mb = Mailbox.Open("t", root);
        int n = mb.Send("Lead", "all", MsgType.Info, "standup", Team);
        Assert.Equal(2, n);                       // Alice + Bob, not Lead
        Assert.Single(mb.ReadInbox("Alice", drain: false));
        Assert.Single(mb.ReadInbox("Bob", drain: false));
        Assert.Empty(mb.ReadInbox("Lead", drain: false));
    }

    [Fact]
    public void Persistence_ReloadKeepsMessagesAndUnreadState()
    {
        var root = TempRoot();
        var mb1 = Mailbox.Open("t", root);
        mb1.Send("Lead", "Alice", MsgType.Question, "status?", Team);

        // A fresh Mailbox over the same root restores the message as still-unread.
        var mb2 = Mailbox.Open("t", root);
        var fresh = mb2.ReadInbox("Alice", drain: true);
        Assert.Single(fresh);
        Assert.Equal(MsgType.Question, fresh[0].Type);

        // After draining + reloading, it is no longer "new".
        var mb3 = Mailbox.Open("t", root);
        Assert.Empty(mb3.ReadInbox("Alice", drain: false));
        // ...but history still shows it.
        Assert.Single(mb3.History("Alice"));
    }

    [Fact]
    public void Shutdown_SetsCooperativeFlag_AndSurvivesReload()
    {
        var root = TempRoot();
        var mb = Mailbox.Open("t", root);
        Assert.False(mb.IsShutdownRequested("Bob"));
        mb.Send("Lead", "Bob", MsgType.Shutdown, "wrap up", Team);
        Assert.True(mb.IsShutdownRequested("Bob"));
        Assert.False(mb.IsShutdownRequested("Alice"));

        // An undelivered (unread) shutdown re-arms the flag on reload.
        var mb2 = Mailbox.Open("t", root);
        Assert.True(mb2.IsShutdownRequested("Bob"));
    }

    [Fact]
    public void History_IsOldestFirst_AndIncludesReadMessages()
    {
        var root = TempRoot();
        var mb = Mailbox.Open("t", root);
        mb.Send("Lead", "Alice", MsgType.Info, "one", Team);
        mb.Send("Bob", "Alice", MsgType.Answer, "two", Team);
        mb.ReadInbox("Alice", drain: true);     // mark both read

        var hist = mb.History("Alice");
        Assert.Equal(2, hist.Count);
        Assert.Equal("one", hist[0].Body);
        Assert.Equal("two", hist[1].Body);
        Assert.True(hist.All(m => m.Read));
    }

    [Fact]
    public void UnreadCount_TracksAcrossInboxes()
    {
        var root = TempRoot();
        var mb = Mailbox.Open("t", root);
        mb.Send("Lead", "all", MsgType.Info, "x", Team);   // Alice + Bob
        Assert.Equal(2, mb.UnreadCount());
        mb.ReadInbox("Alice", drain: true);
        Assert.Equal(1, mb.UnreadCount());
    }
}
