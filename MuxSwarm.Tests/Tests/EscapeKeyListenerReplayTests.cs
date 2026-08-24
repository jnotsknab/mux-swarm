using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Input-plane hardening (post mouse-scrollback): the mid-turn EscapeKeyListener must never LOSE a
/// key it read but does not act on - it enqueues them for the prompt input loop to replay on entry.
/// Covers the shared replay channel + the wheel hook wiring contract. (The mouse-report guard hook
/// was deleted with the single-input-pump migration: the pump reassembles SGR reports upstream, so
/// the listener never sees raw report bytes at all.)
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
    public void WheelHook_IsSettable_AndNullable()
    {
        // The TUI wires this at driver activation and clears it on teardown; it must be a nullable
        // static that accepts an Action and can be cleared without throwing.
        var savedScroll = EscapeKeyListener.OnWheelScroll;
        try
        {
            int? seen = null;
            EscapeKeyListener.OnWheelScroll = rows => seen = rows;
            EscapeKeyListener.OnWheelScroll!(+1);
            Assert.Equal(+1, seen);

            EscapeKeyListener.OnWheelScroll = null;
            Assert.Null(EscapeKeyListener.OnWheelScroll);
        }
        finally
        {
            EscapeKeyListener.OnWheelScroll = savedScroll;
        }
    }
}
