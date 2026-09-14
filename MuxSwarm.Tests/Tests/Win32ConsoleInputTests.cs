using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Native record translation is pure; the Windows adapter must not need managed input buffering.</summary>
public class Win32ConsoleInputTests
{
    /// <summary>Modifier, lock, and ordinary release records are consumed without a blocking key read.</summary>
    [Theory]
    [InlineData(true, 0x10, 0)]
    [InlineData(true, 0x11, 0)]
    [InlineData(true, 0x12, 0)]
    [InlineData(true, 0x14, 0)]
    [InlineData(true, 0x90, 0)]
    [InlineData(true, 0x91, 0)]
    [InlineData(false, 0x41, 0)]
    [InlineData(true, 0x61, 2)]
    [InlineData(true, 0x21, 2)]
    public void NonKeys_DoNotYieldKey(bool down, int code, int modifiers)
        => Assert.False(Win32ConsoleInput.TryTranslateKey(down, (ushort)code, '\0', (uint)modifiers, out _));

    /// <summary>Ordinary modifiers, enhanced navigation and synthesized Unicode retain their meaning.</summary>
    [Fact]
    public void Translation_PreservesModifiersNavigationAndSynthesizedCharacters()
    {
        Assert.True(Win32ConsoleInput.TryTranslateKey(true, 0x41, 'A', 0x1a, out var shifted));
        Assert.Equal(ConsoleModifiers.Shift | ConsoleModifiers.Alt | ConsoleModifiers.Control, shifted.Modifiers);
        Assert.Equal('A', shifted.KeyChar);
        Assert.True(Win32ConsoleInput.TryTranslateKey(true, 0x21, '\0', 0x102, out var page));
        Assert.Equal(ConsoleKey.PageUp, page.Key);
        Assert.True(Win32ConsoleInput.TryTranslateKey(false, 0x12, '漢', 0, out var ime));
        Assert.Equal('漢', ime.KeyChar);
        foreach (char unit in "😀")
        {
            Assert.True(Win32ConsoleInput.TryTranslateKey(true, 0, unit, 0, out var surrogate));
            Assert.Equal(unit, surrogate.KeyChar);
        }
        Assert.True(Win32ConsoleInput.TryTranslateKey(true, 0x1b, '\x1b', 0, out var escape));
        Assert.Equal(ConsoleKey.Escape, escape.Key);
    }
}
