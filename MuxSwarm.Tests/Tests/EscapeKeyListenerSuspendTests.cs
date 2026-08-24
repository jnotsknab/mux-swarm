using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// v0.12.4: the background Esc listener must yield EXCLUSIVE stdin ownership while an interactive
/// prompt reads keys, otherwise it steals/drops the user's keystrokes (laggy / not-picked-up input).
/// SuspendInput() is reference-counted + acknowledged; Pause/Resume compose with it.
/// </summary>
public class EscapeKeyListenerSuspendTests
{
    [Fact]
    public void SuspendInput_Scope_SuspendsThenRestores()
    {
        Assert.False(EscapeKeyListener.IsInputSuspended);
        using (EscapeKeyListener.SuspendInput())
            Assert.True(EscapeKeyListener.IsInputSuspended);
        Assert.False(EscapeKeyListener.IsInputSuspended);
    }

    [Fact]
    public void SuspendInput_Nested_RefCounted()
    {
        using (EscapeKeyListener.SuspendInput())
        {
            Assert.True(EscapeKeyListener.IsInputSuspended);
            using (EscapeKeyListener.SuspendInput())
                Assert.True(EscapeKeyListener.IsInputSuspended);
            // inner disposed -> still suspended by the outer scope
            Assert.True(EscapeKeyListener.IsInputSuspended);
        }
        Assert.False(EscapeKeyListener.IsInputSuspended);
    }

    [Fact]
    public void SuspendInput_DoubleDispose_DoesNotUnderflow()
    {
        var s = EscapeKeyListener.SuspendInput();
        Assert.True(EscapeKeyListener.IsInputSuspended);
        s.Dispose();
        s.Dispose(); // must be idempotent, must not push the count negative
        Assert.False(EscapeKeyListener.IsInputSuspended);
        // A fresh suspension still works after the double-dispose.
        using (EscapeKeyListener.SuspendInput())
            Assert.True(EscapeKeyListener.IsInputSuspended);
        Assert.False(EscapeKeyListener.IsInputSuspended);
    }

    [Fact]
    public void PauseResume_ComposeWithSuspendScope()
    {
        // ask_user uses Pause/Resume; it must compose with a SuspendInput scope without underflow.
        EscapeKeyListener.Pause();
        Assert.True(EscapeKeyListener.IsInputSuspended);
        using (EscapeKeyListener.SuspendInput())
            Assert.True(EscapeKeyListener.IsInputSuspended);
        Assert.True(EscapeKeyListener.IsInputSuspended); // still paused
        EscapeKeyListener.Resume();
        Assert.False(EscapeKeyListener.IsInputSuspended);
        EscapeKeyListener.Resume(); // extra resume must not underflow
        Assert.False(EscapeKeyListener.IsInputSuspended);
    }
}
