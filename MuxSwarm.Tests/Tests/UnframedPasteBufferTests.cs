using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Ambiguous transport bursts are staged before Enter dispatch; idle completion cannot send a draft.</summary>
public class UnframedPasteBufferTests
{
    private static ConsoleInputPump.InputEvent Key(char c) => ConsoleInputPump.InputEvent.OfKey(
        new ConsoleKeyInfo(c, c == '\r' ? ConsoleKey.Enter : ConsoleKey.A, false, false, false));

    [Fact]
    public void BurstWithTrailingNewline_StagesLiteralPaste_NotSubmit()
    {
        var buffer = new UnframedPasteBuffer();
        var events = new List<ConsoleInputPump.InputEvent>();
        foreach (char c in "first\rsecond\r") events.AddRange(buffer.Feed(Key(c), 0));
        events.AddRange(buffer.Flush(100));
        var paste = Assert.Single(events);
        Assert.Equal(ConsoleInputPump.EventKind.InferredPaste, paste.Kind);
        Assert.Equal("first\nsecond\n", paste.PasteText);
    }

    [Fact]
    public void SlowTypingAndStandaloneEnter_RemainKeys()
    {
        var buffer = new UnframedPasteBuffer();
        var events = new List<ConsoleInputPump.InputEvent>();
        long now = 0;
        foreach (char c in "ok\r")
        {
            events.AddRange(buffer.Feed(Key(c), now));
            events.AddRange(buffer.Flush(now + 100)); now += 200;
        }
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(ConsoleInputPump.EventKind.Key, e.Kind));
        Assert.Equal(ConsoleKey.Enter, events[^1].Key.Key);
    }

    [Fact]
    public void ExplicitPasteAndModifiedKey_BypassInferenceWithoutReordering()
    {
        var buffer = new UnframedPasteBuffer();
        Assert.Empty(buffer.Feed(Key('a'), 0));
        var paste = ConsoleInputPump.InputEvent.OfPaste("known\ntext");
        var emitted = buffer.Feed(paste, 1).ToList();
        Assert.Equal(2, emitted.Count);
        Assert.Equal('a', emitted[0].Key.KeyChar);
        Assert.Equal(paste, emitted[1]);
        var ctrl = ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\x03', ConsoleKey.C, false, false, true));
        Assert.Equal(ctrl, Assert.Single(buffer.Feed(ctrl, 2)));
    }
}
