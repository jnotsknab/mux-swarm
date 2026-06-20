using MuxSwarm.Utils.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Headless coverage for the v0.11.0 TUI components and driver (Workstream G Option B,
/// phases 2-3). Components are pure markup builders; the driver's live-frame composition
/// and streaming line-splitting are exercised through an in-memory fake terminal so the
/// pinned-footer / commit-to-scrollback behavior is verified without a real console.
/// </summary>
public class TuiDriverTests
{
    private sealed class FakeTerminal : ITuiTerminal
    {
        private readonly System.Text.StringBuilder _sb = new();
        public int Width { get; set; } = 60;
        public int Height { get; set; } = 24;
        public void Write(string s) => _sb.Append(s);
        public void Flush() { }
        public string Output => _sb.ToString();
        public void Clear() => _sb.Clear();
    }

    // --- components ----------------------------------------------------------

    [Fact]
    public void Footer_WithThreshold_RendersMeterAndPercent()
    {
        var f = TuiComponents.Footer(50_000, 200_000, plan: true, ultra: false, psub: false);
        Assert.Contains("tui", f);
        Assert.Contains("plan", f);
        Assert.Contains("25%", f);            // 50k/200k
        Assert.Contains("50,000/200,000", f);
    }

    [Fact]
    public void Footer_NoThreshold_FallsBackToTokenCount()
    {
        var f = TuiComponents.Footer(1234, 0, false, false, false);
        Assert.Contains("1,234 tokens", f);
    }

    [Fact]
    public void Footer_BadgesReflectModes()
    {
        var f = TuiComponents.Footer(0, 0, plan: true, ultra: true, psub: true);
        Assert.Contains("plan", f);
        Assert.Contains("ultra", f);
        Assert.Contains("psub", f);
    }

    [Fact]
    public void ToolResultCompact_ShowsFirstLineAndMoreHint()
    {
        var r = TuiComponents.ToolResultCompact("line one\nline two\nline three");
        Assert.Single(r);
        Assert.Contains("line one", r[0]);
        Assert.Contains("(+2 lines)", r[0]);
    }

    [Fact]
    public void ToolResultCompact_EmptyText_ProducesNothing()
        => Assert.Empty(TuiComponents.ToolResultCompact("   \n  \n"));

    [Fact]
    public void Diff_TintsAddRemoveHunkHeaders()
    {
        var d = TuiComponents.Diff("file.cs", "@@ -1 +1 @@\n-old\n+new\n ctx", 60);
        string joined = string.Join("\n", d);
        Assert.Contains(TuiComponents.DiffAdd, joined); // +new
        Assert.Contains(TuiComponents.DiffDel, joined); // -old
        Assert.Contains("diff", joined);
    }

    [Fact]
    public void Delegation_RendersFromToAndTask()
    {
        var d = TuiComponents.Delegation("CompanionAgent", "CodeAgent", "scan the repo", 80);
        string j = string.Join("\n", d);
        Assert.Contains("CompanionAgent", j);
        Assert.Contains("CodeAgent", j);
        Assert.Contains("scan the repo", j);
    }

    [Fact]
    public void SlashPalette_FiltersByToken()
    {
        var entries = new (string, string)[] { ("/plan", "x"), ("/tui", "y"), ("/help", "z") };
        var rows = TuiComponents.SlashPalette("/pl", entries);
        Assert.Single(rows);
        Assert.Contains("/plan", rows[0]);
    }

    [Fact]
    public void SlashPalette_NoMatch_ShowsPlaceholder()
    {
        var entries = new (string, string)[] { ("/plan", "x") };
        var rows = TuiComponents.SlashPalette("/zzz", entries);
        Assert.Single(rows);
        Assert.Contains("no commands match", rows[0]);
    }

    [Fact]
    public void InputRow_EmptyShowsPlaceholder_NonEmptyShowsBuffer()
    {
        Assert.Contains("type a message", TuiComponents.InputRow(""));
        Assert.Contains("hello", TuiComponents.InputRow("hello"));
    }

    // --- driver: live frame composition -------------------------------------

    [Fact]
    public void Driver_SetFooter_PaintsFooterIntoLiveRegion()
    {
        var term = new FakeTerminal();
        var d = new TuiDriver(term);
        d.SetFooter(10_000, 200_000, plan: false, ultra: true, psub: false);
        Assert.Contains("ultra", term.Output);
        Assert.Contains("5%", term.Output);
    }

    [Fact]
    public void Driver_Commit_WritesLineThenRepaintsFooter()
    {
        var term = new FakeTerminal();
        var d = new TuiDriver(term);
        d.SetFooter(0, 0, false, false, false);
        term.Clear();
        d.CommitLine("  permanent transcript line");
        Assert.Contains("permanent transcript line", term.Output);
        Assert.Contains("tui", term.Output); // footer repainted below the committed line
    }

    [Fact]
    public void Driver_Streaming_CommitsCompleteLines_KeepsPartialTailLive()
    {
        var term = new FakeTerminal();
        var d = new TuiDriver(term);
        d.SetFooter(0, 0, false, false, false);
        d.BeginStream();
        term.Clear();

        d.StreamChunk("first line\nsecond par");  // one complete line + partial tail
        // The complete line is committed to scrollback...
        Assert.Contains("first line", term.Output);
        // ...and the partial tail is shown in the live frame.
        Assert.Contains("second par", term.Output);

        term.Clear();
        d.StreamChunk("tial\n");                   // completes the second line
        Assert.Contains("second partial", term.Output);

        d.EndStream();
    }

    [Fact]
    public void Driver_BuildLiveFrame_IncludesFooterAndRule()
    {
        var term = new FakeTerminal { Width = 50 };
        var d = new TuiDriver(term);
        d.SetFooter(100, 1000, plan: true, ultra: false, psub: false);
        var frame = d.BuildLiveFrame(50);
        string j = string.Join("\n", frame);
        Assert.Contains("plan", j);
        Assert.Contains("10%", j);
        // a thin rule line of box-drawing chars precedes the footer
        Assert.Contains("\u2500", j);
    }

    [Fact]
    public void Driver_Suspend_ClearsRegionAndShowsCursor()
    {
        var term = new FakeTerminal();
        var d = new TuiDriver(term);
        d.SetFooter(0, 0, false, false, false);
        term.Clear();
        d.Suspend();
        Assert.Contains(Ansi.ShowCursor, term.Output);
    }

    [Fact]
    public void Driver_Shutdown_IsIdempotentAndClears()
    {
        var term = new FakeTerminal();
        var d = new TuiDriver(term);
        d.SetFooter(0, 0, false, false, false);
        d.Shutdown();
        d.Shutdown(); // no throw, no double work
        Assert.Contains(Ansi.ShowCursor, term.Output);
    }
}
