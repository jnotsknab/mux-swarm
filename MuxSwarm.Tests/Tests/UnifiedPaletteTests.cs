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
        Assert.Contains("/detach", cmds);
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
}
