using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// g12.27: the session-agnostic meta dispatch shared by the single-agent first prompt + meta-loop
/// and the /swarm + /pswarm goal loops. Verifies that plain text and unknown/mode-launch input is
/// reported as NotHandled (so the caller treats it as a goal), without needing a live console.
/// </summary>
public class MetaCommandDispatchTests
{
    [Fact]
    public async Task PlainText_IsNotHandled_TreatedAsGoal()
    {
        var r = await MetaCommandDispatch.TryHandleAsync("build the feature", null, null, CancellationToken.None);
        Assert.Equal(MetaCommandDispatch.Result.NotHandled, r);
    }

    [Fact]
    public async Task EmptyOrWhitespace_IsNotHandled()
    {
        Assert.Equal(MetaCommandDispatch.Result.NotHandled,
            await MetaCommandDispatch.TryHandleAsync("", null, null, CancellationToken.None));
        Assert.Equal(MetaCommandDispatch.Result.NotHandled,
            await MetaCommandDispatch.TryHandleAsync("   ", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task NonMetaSlash_LikePlainModeText_IsNotHandled()
    {
        // A message that merely starts with text resembling a command but is not a known REPL-only
        // or meta command must NOT be swallowed - it falls through to the agent as a goal.
        var r = await MetaCommandDispatch.TryHandleAsync("/notarealcommand do x", null, null, CancellationToken.None);
        Assert.Equal(MetaCommandDispatch.Result.NotHandled, r);
    }

    [Fact]
    public async Task Hide_WithNoArg_IsHandled_NotAGoal()
    {
        // /hide is a session-agnostic meta command: even with no live sub-agents it is Handled
        // (prints usage), never falling through to the agent as a goal.
        var r = await MetaCommandDispatch.TryHandleAsync("/hide", null, null, CancellationToken.None);
        Assert.Equal(MetaCommandDispatch.Result.Handled, r);
    }

    [Fact]
    public async Task Unhide_WithNoArg_IsHandled_NotAGoal()
    {
        var r = await MetaCommandDispatch.TryHandleAsync("/unhide", null, null, CancellationToken.None);
        Assert.Equal(MetaCommandDispatch.Result.Handled, r);
    }

    [Fact]
    public void HideUnhide_AreRegistered_AsSessionCommands()
    {
        Assert.Contains(MuxSwarm.Utils.Tui.TuiCommands.All, e => e.Cmd == "/hide");
        Assert.Contains(MuxSwarm.Utils.Tui.TuiCommands.All, e => e.Cmd == "/unhide");
    }
}
