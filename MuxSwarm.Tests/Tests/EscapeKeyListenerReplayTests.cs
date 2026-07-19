using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Input-plane hardening (post mouse-scrollback): the mid-turn EscapeKeyListener must never LOSE a
/// key it read but does not act on - it enqueues them for the prompt input loop to replay on entry.
/// Covers the shared replay channel + the wheel/mouse-report hook wiring contract.
/// </summary>
public class EscapeKeyListenerReplayTests
{
    [Fact]
    public void ReplayKey_ThenDrain_ReplaysInFifoOrder()
    {
        // Push several keys as the listener would for un-acted typed chars mid-turn.
        EscapeKeyListener.ReplayKey(new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false));
        EscapeKeyListener.ReplayKey(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false));
        EscapeKeyListener.ReplayKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        var sink = new Queue<ConsoleKeyInfo>();
        EscapeKeyListener.DrainReplayTo(sink);

        Assert.Equal(3, sink.Count);
        Assert.Equal('h', sink.Dequeue().KeyChar);
        Assert.Equal('i', sink.Dequeue().KeyChar);
        Assert.Equal('\r', sink.Dequeue().KeyChar);
        // Queue is now empty; a second drain yields nothing.
        EscapeKeyListener.DrainReplayTo(sink);
        Assert.Empty(sink);
    }

    [Fact]
    public void DrainReplayTo_EmptyQueue_IsNoOp()
    {
        var sink = new Queue<ConsoleKeyInfo>();
        EscapeKeyListener.DrainReplayTo(sink);
        Assert.Empty(sink);
    }

    [Fact]
    public void MouseHooks_AreSettable_AndNullable()
    {
        // The TUI wires these at driver activation and clears them on teardown; both must be
        // nullable statics that accept a Func/Action and can be cleared without throwing.
        var savedActive = EscapeKeyListener.IsMouseReportingActive;
        var savedScroll = EscapeKeyListener.OnWheelScroll;
        try
        {
            EscapeKeyListener.IsMouseReportingActive = () => true;
            int? seen = null;
            EscapeKeyListener.OnWheelScroll = rows => seen = rows;
            Assert.True(EscapeKeyListener.IsMouseReportingActive!());
            EscapeKeyListener.OnWheelScroll!(+1);
            Assert.Equal(+1, seen);

            EscapeKeyListener.IsMouseReportingActive = null;
            EscapeKeyListener.OnWheelScroll = null;
            Assert.Null(EscapeKeyListener.IsMouseReportingActive);
            Assert.Null(EscapeKeyListener.OnWheelScroll);
        }
        finally
        {
            EscapeKeyListener.IsMouseReportingActive = savedActive;
            EscapeKeyListener.OnWheelScroll = savedScroll;
        }
    }
}
