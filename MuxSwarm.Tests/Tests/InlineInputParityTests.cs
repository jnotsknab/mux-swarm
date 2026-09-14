using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Both renderer activations use the same raw capture, paste classification and submit contract.</summary>
[Collection("ConsoleState")]
public class InlineInputParityTests
{
    private sealed class Terminal : ITuiTerminal
    {
        internal readonly StringBuilder Output = new();
        internal Action? OnFlush;
        public int Width => 70;
        public int Height => 24;
        public void Write(string text) => Output.Append(text);
        public void Flush() => OnFlush?.Invoke();
    }

    /// <summary>Renderer choice never disables the shared pump; inline must not enable terminal mouse capture.</summary>
    [Theory]
    [InlineData(false, "wheel", false)]
    [InlineData(false, "off", false)]
    [InlineData(true, "wheel", true)]
    [InlineData(true, "off", false)]
    public void Activation_StartsSharedInputWithoutChangingInlinePresentation(bool frame, string mouse, bool expectedTracking)
    {
        var terminal = new Terminal();
        var driver = new TuiDriver(terminal, frameEngine: frame);
        using var supplied = ConsoleInputPump.CreateUnstartedForTest();
        int calls = 0;
        try
        {
            var actual = driver.InitializeInput(true, mouse, (tracking, bracketed, nativeMouse) =>
            {
                calls++;
                Assert.Equal(expectedTracking, tracking);
                Assert.True(bracketed);
                Assert.Equal(frame, nativeMouse); // Preserve existing frame native policy even with preset off.
                return supplied;
            });
            Assert.Same(supplied, actual);
            Assert.Equal(1, calls);
            driver.SetMouseTrackingPreset(frame ? "off" : mouse);
            driver.SetFooter(100, 10000, false, false, false);
            if (!frame)
            {
                Assert.DoesNotContain(Ansi.EnterAltScreen, terminal.Output.ToString());
                Assert.DoesNotContain(Ansi.MouseTrackingOn, terminal.Output.ToString());
            }
        }
        finally { driver.Shutdown(); }
    }

    /// <summary>Inline VT capture leaves native selection enabled; frame's mouse policy remains unchanged.</summary>
    [Theory]
    [InlineData(false, 0x1F7u, 0x3E0u)]
    [InlineData(false, 0x1B7u, 0x3A0u)]
    [InlineData(true, 0x1F7u, 0x3B0u)]
    public void NativeMode_PreservesInlineQuickEditAndFrameCapture(bool mouse, uint original, uint expected)
    {
        Assert.Equal(expected, Win32ConsoleInput.InputMode(original, mouse, virtualTerminal: true));
    }

    /// <summary>Real producer parsing/classification, not preclassified paste injection, protects both renderers.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RawCapture_LargePasteStaysDraftUntilExplicitSubmit(bool frame, bool framed, bool multiline)
    {
        var terminal = new Terminal();
        var driver = new TuiDriver(terminal, frameEngine: frame);
        using var supplied = ConsoleInputPump.CreateUnstartedForTest(mouseTracking: frame);
        var pump = driver.InitializeInput(true, "wheel", (_, _, _) => supplied);
        Assert.NotNull(pump); // Exercises InitializeInput; EnableDockedFooter wiring is verified separately in review.
        driver.SetMouseTrackingPreset("off"); // Tests must never change a real console's mouse mode.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.OnFlush = () =>
        {
            string view = string.Join("\n", driver.BuildLiveFrame(driver.Width).Select(TuiMarkup.Plain));
            if (view.Contains("[≡ ")) staged.TrySetResult();
            ready.TrySetResult();
        };
        string text = multiline ? string.Join("\r", Enumerable.Repeat("row\t漢字", 25)) + "\rEND_SENTINEL"
            : new string('x', 1500) + "END_SENTINEL";
        void Feed(string raw)
        {
            foreach (char c in raw) pump.FeedRawKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        }
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Feed(framed ? "\x1b[200~" + text + "\x1b[201~" : text);
            if (!framed) { await Task.Delay(80); pump.FlushAssemblerTimeout(); }
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(read.IsCompleted);
            Feed("\r");
            await Task.Delay(80); pump.FlushAssemblerTimeout();
            if (!framed)
            {
                await Task.Delay(40);
                Assert.False(read.IsCompleted); // Trailing unframed newline cannot dispatch a command.
                Feed("\x1bOS"); // SS3 F4 is the explicit compatibility-send action.
            }
            string? submitted = await read.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(text.Replace("\r", "\n") + (framed ? "" : "\n"), submitted);
            Assert.DoesNotContain("END_SENTINEL", terminal.Output.ToString()); // Compact display/echo only.
            if (!frame) Assert.DoesNotContain(Ansi.EnterAltScreen, terminal.Output.ToString());
        }
        finally
        {
            terminal.OnFlush = null;
            pump.Dispose();
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            driver.Shutdown();
        }
    }

    /// <summary>Slow raw typing and a standalone Enter retain ordinary submission in both renderers.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawTyping_StandaloneEnterSubmitsNormally(bool frame)
    {
        var terminal = new Terminal();
        var driver = new TuiDriver(terminal, frameEngine: frame);
        using var supplied = ConsoleInputPump.CreateUnstartedForTest(mouseTracking: frame);
        var pump = driver.InitializeInput(true, "off", (_, _, _) => supplied);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.OnFlush = () => ready.TrySetResult();
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (char c in "ok\r")
            {
                pump.FeedRawKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
                await Task.Delay(80);
                pump.FlushAssemblerTimeout();
            }
            Assert.Equal("ok", await read.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.DoesNotContain("Unframed paste staged", terminal.Output.ToString());
        }
        finally
        {
            terminal.OnFlush = null;
            pump.Dispose();
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            driver.Shutdown();
        }
    }
}
