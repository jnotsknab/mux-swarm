using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Ctrl+L / /clear: the visible transcript empties in both engines; history stays in NAV.</summary>
[Collection("ConsoleState")]
public class TuiClearScreenTests
{
    private sealed class Term : ITuiTerminal
    {
        public int Width { get; set; } = 80;
        public int Height { get; set; } = 20;
        public System.Text.StringBuilder Out { get; } = new();
        public void Write(string text) => Out.Append(text);
        public void Flush() { }
    }

    private static string Plain(string s) => Regex.Replace(s, "\u001b\\[[0-9;?]*[A-Za-z]", "");

    private static TuiDriver Filled(bool frame, out Term t)
    {
        t = new Term();
        var d = new TuiDriver(t, frameEngine: frame);
        for (int i = 0; i < 40; i++) d.CommitLine($"old line {i}");
        return d;
    }

    [Fact]
    public void Frame_ClearScreen_EmptiesTheViewport_NewOutputStillShows()
    {
        var d = Filled(frame: true, out _);
        Assert.Contains(d.ComposeFrameRows(), r => Plain(r).Contains("old line 39"));
        d.ClearScreen();
        Assert.DoesNotContain(d.ComposeFrameRows(), r => Plain(r).Contains("old line"));
        d.CommitLine("after clear");
        var rows = d.ComposeFrameRows();
        Assert.Contains(rows, r => Plain(r).Contains("after clear"));
        Assert.DoesNotContain(rows, r => Plain(r).Contains("old line"));
    }

    [Fact]
    public void Frame_ClearScreen_ScrollCannotReachClearedRows_ButHistoryIsKept()
    {
        var d = Filled(frame: true, out _);
        d.ClearScreen();
        d.CommitLine("after clear");
        d.FrameScrollBy(1000);
        Assert.DoesNotContain(d.ComposeFrameRows(), r => Plain(r).Contains("old line"));
        var transcript = (IList)typeof(TuiDriver).GetField("_transcript", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(d)!;
        Assert.Equal(41, transcript.Count);   // NAV (Ctrl+G) still browses the full history
    }

    [Fact]
    public void Inline_ClearScreen_ErasesAndRepaintsOnlyTheLiveBand()
    {
        var d = Filled(frame: false, out var t);
        t.Out.Clear();
        d.ClearScreen();
        string o = Plain(t.Out.ToString());
        Assert.Contains(Ansi.ClearScreen, t.Out.ToString());
        Assert.DoesNotContain("old line", o);
        t.Out.Clear();
        d.PollResize(); d.ForceRedraw();   // a later reflow must not bring cleared rows back
        Assert.DoesNotContain("old line", Plain(t.Out.ToString()));
    }

    [Fact]
    public void ForceRedraw_StillRepaintsTheTranscript()
    {
        var d = Filled(frame: true, out _);
        d.ForceRedraw();
        Assert.Contains(d.ComposeFrameRows(), r => Plain(r).Contains("old line 39"));
    }

    [Fact]
    public void Clear_IsAvailableInsideSessions()
    {
        Assert.True(TuiCommands.IsSessionNative("/clear"));
        Assert.True(TuiCommands.IsReplOnly("/clear"));   // still handled at the main menu
    }
}
