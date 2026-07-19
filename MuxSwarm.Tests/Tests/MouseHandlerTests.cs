using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers MouseHandler classification: raw SGR reports -> semantic MouseEvent, and Dispatch's wheel
/// coalescing return contract. Uses a headless fake terminal (mode writes are captured, not asserted).
/// </summary>
public class MouseHandlerTests
{
    private sealed class FakeTerm : ITuiTerminal
    {
        private readonly System.Text.StringBuilder _sb = new();
        public int Width { get; set; } = 80;
        public int Height { get; set; } = 24;
        public void Write(string s) => _sb.Append(s);
        public void Flush() { }
        public string Output => _sb.ToString();
    }

    private static MouseHandler New() => new(new FakeTerm());

    [Fact]
    public void SetEnabled_IsIdempotent_AndWritesModes()
    {
        var t = new FakeTerm();
        var h = new MouseHandler(t);
        Assert.False(h.Enabled);
        h.SetEnabled(true);
        Assert.True(h.Enabled);
        Assert.Contains(Ansi.MouseTrackingOn, t.Output);
        h.SetEnabled(true);                 // no-op second time
        h.SetEnabled(false);
        Assert.False(h.Enabled);
        Assert.Contains(Ansi.MouseTrackingOff, t.Output);
    }

    [Theory]
    [InlineData(64, +1)]   // wheel up
    [InlineData(65, -1)]   // wheel down
    public void Classify_Wheel_ProducesWheelEvent(int button, int dir)
    {
        var h = New();
        var ev = h.Classify(button, col: 10, row: 5, release: false);
        Assert.NotNull(ev);
        Assert.Equal(MouseEventKind.Wheel, ev!.Value.Kind);
        Assert.Equal(dir, ev.Value.WheelDir);
        Assert.Equal(5, ev.Value.Row);
        Assert.Equal(10, ev.Value.Col);
    }

    [Fact]
    public void Dispatch_Wheel_ReturnsDirection_ForCoalescing()
    {
        var h = New();
        Assert.Equal(+1, h.Dispatch(h.Classify(64, 1, 1, false)!.Value));
        Assert.Equal(-1, h.Dispatch(h.Classify(65, 1, 1, false)!.Value));
    }

    [Fact]
    public void Classify_PressThenRelease_TracksHeldButton()
    {
        var h = New();
        var press = h.Classify(0, col: 3, row: 4, release: false);
        Assert.Equal(MouseEventKind.Press, press!.Value.Kind);
        var rel = h.Classify(0, col: 3, row: 4, release: true);
        Assert.Equal(MouseEventKind.Release, rel!.Value.Kind);
    }

    [Fact]
    public void Classify_MotionWithButtonHeld_IsDrag_ElseMove()
    {
        var h = New();
        // Motion with no prior press -> Move (motion flag 0x20).
        var move = h.Classify(0x20 | 3, col: 1, row: 1, release: false);
        Assert.Equal(MouseEventKind.Move, move!.Value.Kind);

        // Press, then motion -> Drag.
        h.Classify(0, col: 1, row: 1, release: false);          // press latches held button
        var drag = h.Classify(0x20 | 0, col: 2, row: 1, release: false);
        Assert.Equal(MouseEventKind.Drag, drag!.Value.Kind);
    }

    [Fact]
    public void Dispatch_NonWheel_WithButtonsEnabled_InvokesSink()
    {
        var h = New();
        h.ButtonsEnabled = true;
        MouseEvent? seen = null;
        h.OnPress = e => seen = e;
        int r = h.Dispatch(h.Classify(0, 7, 8, false)!.Value);
        Assert.Equal(0, r);
        Assert.NotNull(seen);
        Assert.Equal(MouseEventKind.Press, seen!.Value.Kind);
    }

    [Fact]
    public void Dispatch_NonWheel_WhenButtonsDisabled_SwallowsSink()
    {
        // wheel preset (ButtonsEnabled false): press/release/drag are parsed but NOT dispatched.
        var h = New();                       // ButtonsEnabled defaults to false
        Assert.False(h.ButtonsEnabled);
        bool pressed = false, released = false, dragged = false;
        h.OnPress = _ => pressed = true;
        h.OnRelease = _ => released = true;
        h.OnDrag = _ => dragged = true;
        h.Dispatch(h.Classify(0, 1, 1, false)!.Value);            // press
        h.Dispatch(h.Classify(0x20 | 0, 2, 1, false)!.Value);     // drag (button held)
        h.Dispatch(h.Classify(0, 2, 1, true)!.Value);             // release
        Assert.False(pressed);
        Assert.False(released);
        Assert.False(dragged);
    }

    [Fact]
    public void Dispatch_Wheel_WorksRegardlessOfButtonsEnabled()
    {
        var h = New();                       // wheel preset: buttons disabled
        Assert.Equal(+1, h.Dispatch(h.Classify(64, 1, 1, false)!.Value));
        h.ButtonsEnabled = true;
        Assert.Equal(+1, h.Dispatch(h.Classify(64, 1, 1, false)!.Value));
    }

    // ---- TuiDriver.SetMouseTrackingPreset: normalization + frame/inline gating ----

    [Theory]
    [InlineData("off", "off")]
    [InlineData("buttons", "buttons")]
    [InlineData("wheel", "wheel")]
    [InlineData("garbage", "wheel")]   // unknown normalizes to wheel
    [InlineData("", "wheel")]
    public void Driver_Preset_NormalizesUnknownValues(string input, string expected)
    {
        var d = new TuiDriver(new FakeTerm(), frameEngine: true);
        d.SetMouseTrackingPreset(input);
        Assert.Equal(expected, d.MousePreset);
    }

    [Fact]
    public void Driver_PresetOff_NeverEnablesReporting_InFrameMode()
    {
        var t = new FakeTerm();
        var d = new TuiDriver(t, frameEngine: true);
        d.SetMouseTrackingPreset("off");
        Assert.DoesNotContain(Ansi.MouseTrackingOn, t.Output);
    }

    [Fact]
    public void Driver_PresetWheel_EnablesReporting_InFrameMode()
    {
        var t = new FakeTerm();
        var d = new TuiDriver(t, frameEngine: true);
        d.SetMouseTrackingPreset("wheel");
        Assert.Contains(Ansi.MouseTrackingOn, t.Output);
    }

    [Fact]
    public void Driver_InlineMode_NeverEnablesReporting_RegardlessOfPreset()
    {
        var t = new FakeTerm();
        var d = new TuiDriver(t, frameEngine: false);   // classic line engine
        d.SetMouseTrackingPreset("wheel");
        d.SetMouseTrackingPreset("buttons");
        Assert.DoesNotContain(Ansi.MouseTrackingOn, t.Output);
    }
}

