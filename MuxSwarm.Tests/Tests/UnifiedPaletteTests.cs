using System.Linq;
using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Unified in-session palette (g12.23): the session palette now surfaces ALL commands - session-
/// native first, then REPL/mode-launch commands tagged "(ends session)" - so they autocomplete
/// everywhere. Selecting a REPL command in a live session still routes through the existing
/// slash-anywhere warn-then-checkpoint path.
/// </summary>
public class UnifiedPaletteTests
{
    [Fact]
    public void SessionUnified_IncludesSessionNativeCommands()
    {
        var cmds = TuiCommands.SessionUnified.Select(e => e.Cmd).ToList();
        Assert.Contains("/compact", cmds);
        Assert.Contains("/kanban", cmds);
        Assert.Contains("/background", cmds);
        Assert.Contains("/daemon", cmds);
    }

    [Fact]
    public void SessionUnified_IncludesModeLaunchCommands_WithEndsSessionHint()
    {
        var entry = TuiCommands.SessionUnified.First(e => e.Cmd == "/agent");
        Assert.Contains("ends session", entry.Desc);
        var teams = TuiCommands.SessionUnified.First(e => e.Cmd == "/teams");
        Assert.Contains("ends session", teams.Desc);
    }

    [Fact]
    public void SessionUnified_NoDuplicateCommands()
    {
        var cmds = TuiCommands.SessionUnified.Select(e => e.Cmd).ToList();
        Assert.Equal(cmds.Count, cmds.Distinct().Count());
    }

    [Fact]
    public void SessionUnified_SessionNativeNotTaggedEndsSession()
    {
        var entry = TuiCommands.SessionUnified.First(e => e.Cmd == "/kanban");
        Assert.DoesNotContain("ends session", entry.Desc);
    }

    // --- g12.27: top-level (menu) unified palette + slash-anywhere symmetry ---

    [Fact]
    public void ReplUnified_IncludesModeLaunchCommands()
    {
        var cmds = TuiCommands.ReplUnified.Select(e => e.Cmd).ToList();
        Assert.Contains("/agent", cmds);
        Assert.Contains("/swarm", cmds);
        Assert.Contains("/pswarm", cmds);
        Assert.Contains("/teams", cmds);
    }

    [Fact]
    public void ReplUnified_SurfacesSessionNativeCommands_WithNeedsSessionHint()
    {
        var entry = TuiCommands.ReplUnified.First(e => e.Cmd == "/compact");
        Assert.Contains("needs a session", entry.Desc);
        var bg = TuiCommands.ReplUnified.First(e => e.Cmd == "/background");
        Assert.Contains("needs a session", bg.Desc);
    }

    [Fact]
    public void ReplUnified_ModeLaunchNotTaggedNeedsSession()
    {
        var entry = TuiCommands.ReplUnified.First(e => e.Cmd == "/agent");
        Assert.DoesNotContain("needs a session", entry.Desc);
    }

    [Fact]
    public void ReplUnified_NoDuplicateCommands()
    {
        var cmds = TuiCommands.ReplUnified.Select(e => e.Cmd).ToList();
        Assert.Equal(cmds.Count, cmds.Distinct().Count());
    }

    [Fact]
    public void IsSessionNative_DetectsSessionOnlyCommands_ForMenuWarn()
    {
        // The App menu uses IsSessionNative to warn instead of "unknown command".
        Assert.True(TuiCommands.IsSessionNative("/compact"));
        Assert.True(TuiCommands.IsSessionNative("/background"));
        Assert.False(TuiCommands.IsSessionNative("/agent"));
        Assert.False(TuiCommands.IsSessionNative("/notacmd"));
    }
}
