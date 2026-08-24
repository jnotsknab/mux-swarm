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
    public async Task Hide_IsRemoved_FallsThroughAsGoal()
    {
        // /hide was removed: hiding moved into the backslash Agent View (the 'h' key). The old
        // command is no longer registered, so it is NOT a meta command anymore and falls through
        // (the caller then reports it as an unknown slash command).
        var r = await MetaCommandDispatch.TryHandleAsync("/hide", null, null, CancellationToken.None);
        Assert.Equal(MetaCommandDispatch.Result.NotHandled, r);
    }

    [Fact]
    public async Task Unhide_WithNoArg_IsHandled_NotAGoal()
    {
        var r = await MetaCommandDispatch.TryHandleAsync("/unhide", null, null, CancellationToken.None);
        Assert.Equal(MetaCommandDispatch.Result.Handled, r);
    }

    [Fact]
    public void Hide_IsNotRegistered_Unhide_IsRegistered()
    {
        Assert.DoesNotContain(MuxSwarm.Utils.Tui.TuiCommands.All, e => e.Cmd == "/hide");
        Assert.Contains(MuxSwarm.Utils.Tui.TuiCommands.All, e => e.Cmd == "/unhide");
    }
}
