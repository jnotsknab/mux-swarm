using System.Linq;
using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// /daemon is session-AGNOSTIC background control; it must be usable at BOTH the top-level menu and
/// inside a live session. These lock the Scope.Both classification so /daemon is never gated behind
/// "launch a session first" (the bug this fixed) nor mislabeled "(ends session)" in-session.
/// </summary>
public class DaemonScopeTests
{
    [Theory]
    [InlineData("/daemon")]
    [InlineData("/daemon on")]
    [InlineData("/daemon off")]
    [InlineData("/daemon jobs")]
    public void Daemon_IsBoth_MenuAndSession(string cmd)
    {
        // Scope.Both surfaces as true for BOTH classifiers, so the menu switch runs it AND the
        // in-session meta-loop treats it as native (no "needs a session" / "ends session" warning).
        Assert.True(TuiCommands.IsReplOnly(cmd), $"{cmd} should be runnable at the menu");
        Assert.True(TuiCommands.IsSessionNative(cmd), $"{cmd} should be native in-session");
    }

    [Fact]
    public void Daemon_AppearsInBothBasePalettes()
    {
        Assert.Contains(TuiCommands.Repl, e => e.Cmd == "/daemon");
        Assert.Contains(TuiCommands.Session, e => e.Cmd == "/daemon");
    }

    [Fact]
    public void Daemon_NotDuplicatedWithHint_InUnifiedPalettes()
    {
        // Because /daemon is in the base set of both, the unified builders (which dedup) must not
        // re-add it with a "(ends session)" / "(needs a session)" suffix.
        var replDaemon = TuiCommands.ReplUnified.Where(e => e.Cmd == "/daemon").ToList();
        var sessDaemon = TuiCommands.SessionUnified.Where(e => e.Cmd == "/daemon").ToList();
        Assert.Single(replDaemon);
        Assert.Single(sessDaemon);
        Assert.DoesNotContain("needs a session", replDaemon[0].Desc);
        Assert.DoesNotContain("ends session", sessDaemon[0].Desc);
    }

    [Fact]
    public void ModeLaunch_StillReplOnly_NotSessionNative()
    {
        // Sanity: the Both change didn't leak into ordinary REPL-only mode-launch commands.
        Assert.True(TuiCommands.IsReplOnly("/agent"));
        Assert.False(TuiCommands.IsSessionNative("/agent"));
    }
}
