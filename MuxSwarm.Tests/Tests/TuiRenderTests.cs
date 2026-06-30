using System.Linq;
using System.Text;
using MuxSwarm.Utils;
using MuxSwarm.Utils.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Coverage for the v0.11.0 "tui" render layer (G2-G9, Model A: inline chrome). These
/// assert the gating contract: the IsTui-gated public entrypoints are safe no-ops in
/// stdio/serve mode (the NDJSON contract must never see TUI chrome), and emit nothing on
/// the stdio stream. Visual fidelity of the panels is validated by the operator on a real
/// terminal; here we guard the boundaries that can be checked headlessly.
/// </summary>
[Collection("ConsoleState")]
public class TuiRenderTests
{
    private static string CaptureStdio(Action body)
    {
        var prior = Console.Out;
        var sb = new StringBuilder();
        try
        {
            MuxConsole.StdioMode = true;
            Console.SetOut(new StringWriter(sb));
            body();
        }
        finally
        {
            Console.SetOut(prior);
            MuxConsole.StdioMode = false;
        }
        return sb.ToString();
    }

    [Fact]
    public void TuiEntrypoints_InStdioMode_EmitNothing()
    {
        // IsTui is false whenever StdioMode is set, so every IsTui-gated TUI helper must
        // be a no-op and must not write to the NDJSON stream.
        var outp = CaptureStdio(() =>
        {
            MuxConsole.RenderTuiSessionHeader("CodeAgent", "gpt-5", "openai");
            MuxConsole.RenderTuiStatusBar(1000, 80000, plan: true, ultra: true, parallelSub: true);
            MuxConsole.RenderTuiSlashPalette();
            MuxConsole.RenderTuiSlashPalette("/comp");
            MuxConsole.RenderTuiDiff("file.cs", "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new");
        });
        Assert.Equal(string.Empty, outp);
    }

    [Fact]
    public void IsTui_IsFalse_InStdioMode()
    {
        try
        {
            MuxConsole.StdioMode = true;
            MuxConsole.SetTuiRenderMode();
            Assert.False(MuxConsole.IsTui);
        }
        finally
        {
            MuxConsole.StdioMode = false;
            MuxConsole.SetClassicRenderMode();
        }
    }

    [Fact]
    public void StatusBar_StdioByteIdentical_RegardlessOfModeFlags()
    {
        // Even with all mode flags on, the status bar must not perturb the stdio stream.
        string Run(bool plan, bool ultra, bool psub) => CaptureStdio(() =>
        {
            MuxConsole.WriteStream("x");
            MuxConsole.RenderTuiStatusBar(500, 80000, plan, ultra, psub);
            MuxConsole.WriteStream("y");
        });

        var a = Run(false, false, false);
        var b = Run(true, true, true);
        Assert.Equal(a, b);
        Assert.DoesNotContain("plan", a);
        Assert.DoesNotContain("ultra", a);
    }

    [Fact]
    public void SubAgentCollapsed_ShowsAgentLinesToolsAndExpandHint()
    {
        var line = TuiComponents.SubAgentCollapsed("CodeAgent", "success", 12, 3, "#82C49B");
        var plain = TuiMarkup.Plain(line);
        Assert.Contains("CodeAgent", plain);
        Assert.Contains("12 lines", plain);
        Assert.Contains("3 tools", plain);
        Assert.Contains("ctrl+e expand", plain);
        Assert.Contains("\u2713", plain);                        // success check glyph
    }

    [Fact]
    public void SubAgentCollapsed_FailureGlyph_AndSingularUnits()
    {
        var fail = TuiMarkup.Plain(TuiComponents.SubAgentCollapsed("WebAgent", "failure", 1, 1, "#7FB3D5"));
        Assert.Contains("\u2717", fail);                         // failure cross
        Assert.Contains("1 line", fail);
        Assert.Contains("1 tool", fail);
        Assert.DoesNotContain("1 lines", fail);
        Assert.DoesNotContain("1 tools", fail);
    }

    [Fact]
    public void SubAgentCollapsed_NoToolsOrLines_OmitsThoseBits()
    {
        var plain = TuiMarkup.Plain(TuiComponents.SubAgentCollapsed("X", null, 0, 0, "#C9A26B"));
        Assert.DoesNotContain("line", plain);
        Assert.DoesNotContain("tool", plain);
        Assert.Contains("ctrl+e expand", plain);
    }

    [Fact]
    public void BeginSubAgentCapture_NullInStdioMode_NeverSuppressesStreamEvents()
    {
        // The web app demultiplexes per-agent stream frames, so capture must NOT engage in
        // stdio/serve mode even when collapse is enabled - sub-agents must still stream.
        bool prior = MuxConsole.CollapseSubAgents;
        try
        {
            MuxConsole.CollapseSubAgents = true;
            string outp = CaptureStdio(() =>
            {
                using var scope = MuxConsole.BeginSubAgentCapture("CodeAgent");
                Assert.Null(scope);                              // no capture in stdio mode
                MuxConsole.WriteStream("hello", agentName: "CodeAgent");
            });
            Assert.Contains("hello", outp);                      // stream frame still emitted
        }
        finally { MuxConsole.CollapseSubAgents = prior; }
    }

    [Fact]
    public void SubAgentActivity_OneLinePerAgent_WithSpinnerAndStatus()
    {
        var agents = new (string, string, string)[]
        {
            ("CodeAgent", "calling: read_file", "#82C49B"),
            ("WebAgent",  "working",            "#7FB3D5"),
        };
        var rows = TuiComponents.SubAgentActivity(agents, frame: 3);
        Assert.Equal(2, rows.Count);
        Assert.Contains("CodeAgent", TuiMarkup.Plain(rows[0]));
        Assert.Contains("calling: read_file", TuiMarkup.Plain(rows[0]));
        Assert.Contains("WebAgent", TuiMarkup.Plain(rows[1]));
        Assert.Contains("working", TuiMarkup.Plain(rows[1]));
    }

    [Fact]
    public void SubAgentActivity_Empty_ReturnsNoLines()
    {
        Assert.Empty(TuiComponents.SubAgentActivity(System.Array.Empty<(string, string, string)>(), 0));
    }

    [Fact]
    public void SubAgentActivity_DefaultsBlankStatusToWorking()
    {
        var rows = TuiComponents.SubAgentActivity(new (string, string, string)[] { ("X", "", "#C9A26B") }, 0);
        Assert.Single(rows);
        Assert.Contains("working", TuiMarkup.Plain(rows[0]));
    }

    [Fact]
    public void SubAgentCollapsed_UsesCtrlEExpandHint()
    {
        var plain = TuiMarkup.Plain(TuiComponents.SubAgentCollapsed("CodeAgent", "success", 5, 2, "#82C49B"));
        Assert.Contains("ctrl+e expand", plain);
        Assert.DoesNotContain("ctrl+o", plain);
    }

    [Fact]
    public void SubAgentActivity_UsesPulsingDotHead_NotMainAgentBraille()
    {
        // g12.07: the live lane head is the PULSING dot (motion = working), not the main agent's
        // Braille thinking dots - so concurrent sub-agent activity reads as its own lane. Use the
        // frame whose pulse cell is the distinctive big circle to avoid the row's middot separator.
        int bigFrame = System.Array.IndexOf(TuiComponents.PulseFrames, "\u25CF");
        var rows = TuiComponents.SubAgentActivity(
            new (string, string, string)[] { ("CodeAgent", "working", "#82C49B") }, frame: bigFrame);
        var plain = TuiMarkup.Plain(rows[0]);
        Assert.Contains("\u25CF", plain);                          // pulsing head dot present
        foreach (var braille in TuiComponents.ThinkFrames)
            Assert.DoesNotContain(braille, plain);                  // no main-agent Braille frame
    }

    [Fact]
    public void SubAgentActivity_AdvertisesLiveCtrlEAffordance()
    {
        // The expand affordance is shown WHILE the sub-agent runs (not only on the finished
        // collapsed line), so the user knows buffered output can be expanded inline mid-stream.
        var rows = TuiComponents.SubAgentActivity(
            new (string, string, string)[] { ("CodeAgent", "working", "#82C49B") }, frame: 0);
        Assert.Contains("ctrl+e", TuiMarkup.Plain(rows[0]));
    }

    [Fact]
    public void SubAgentLivePanel_BoundsToMaxRows_ElidesFromTop()
    {
        var body = string.Join("\n", System.Linq.Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var rows = TuiComponents.SubAgentLivePanel("CodeAgent", body, width: 80, maxRows: 5);
        var plain = rows.Select(TuiMarkup.Plain).ToList();
        // header + (elision marker) + 5 body + footer border = 8 rows max.
        Assert.True(rows.Count <= 8, $"expected <=8 rows, got {rows.Count}");
        Assert.Contains(plain, l => l.Contains("earlier line"));   // top elided
        Assert.Contains(plain, l => l.Contains("line 50"));         // newest tail kept
        Assert.DoesNotContain(plain, l => l.Contains("line 1 "));   // oldest dropped
        Assert.Contains(plain, l => l.Contains("ctrl+e collapse")); // reversible affordance
    }

    [Fact]
    public void SubAgentLivePanel_ShortBody_NoElisionMarker()
    {
        var rows = TuiComponents.SubAgentLivePanel("CodeAgent", "just one line", width: 80, maxRows: 10);
        var plain = rows.Select(TuiMarkup.Plain).ToList();
        Assert.DoesNotContain(plain, l => l.Contains("earlier line"));
        Assert.Contains(plain, l => l.Contains("just one line"));
    }

    // --- g12.28: TaskBoard strip scroll windowing -----------------------------

    private static System.Collections.Generic.List<(string, string, string?, string, int)> BoardRows(int n)
    {
        var l = new System.Collections.Generic.List<(string, string, string?, string, int)>();
        for (int i = 0; i < n; i++) l.Add(($"t{i}", "Todo", null, $"task subject {i}", 0));
        return l;
    }

    [Fact]
    public void TaskBoardStrip_WindowsRows_AtOffsetZero_ShowsFirstWindowAndMore()
    {
        var rows = BoardRows(12);
        var outp = TuiComponents.TaskBoardStrip(12, 0, 0, 0, 0, rows, maxRows: 5, offset: 0);
        var plain = string.Join("\n", outp.Select(TuiMarkup.Plain));
        Assert.Contains("task subject 0", plain);
        Assert.Contains("task subject 4", plain);
        Assert.DoesNotContain("task subject 5", plain);   // outside the window
        Assert.Contains("+7 more", plain);                // 12 - 5 below
        Assert.DoesNotContain("above", plain);            // at top, nothing above
    }

    [Fact]
    public void TaskBoardStrip_WindowsRows_AtOffset_ShowsAboveAndShiftedWindow()
    {
        var rows = BoardRows(12);
        var outp = TuiComponents.TaskBoardStrip(12, 0, 0, 0, 0, rows, maxRows: 5, offset: 3);
        var plain = string.Join("\n", outp.Select(TuiMarkup.Plain));
        Assert.Contains("3 above", plain);
        Assert.Contains("task subject 3", plain);
        Assert.Contains("task subject 7", plain);
        Assert.DoesNotContain("task subject 2", plain);
        Assert.DoesNotContain("task subject 8", plain);
        Assert.Contains("+4 more", plain);                // 12 - 3 - 5
    }

    [Fact]
    public void TaskBoardStrip_OffsetClampedToEnd_NoNegativeMore()
    {
        var rows = BoardRows(7);
        // Over-large offset is clamped to rows-window (=2); last window is rows 2..6, no "more".
        var outp = TuiComponents.TaskBoardStrip(7, 0, 0, 0, 0, rows, maxRows: 5, offset: 99);
        var plain = string.Join("\n", outp.Select(TuiMarkup.Plain));
        Assert.Contains("task subject 6", plain);
        Assert.DoesNotContain("more", plain);
        Assert.Contains("2 above", plain);
    }

    [Fact]
    public void TaskBoardStrip_ShortList_NoScrollAffordances()
    {
        var rows = BoardRows(3);
        var outp = TuiComponents.TaskBoardStrip(3, 0, 0, 0, 0, rows, maxRows: 5, offset: 0);
        var plain = string.Join("\n", outp.Select(TuiMarkup.Plain));
        Assert.DoesNotContain("more", plain);
        Assert.DoesNotContain("above", plain);
    }

    // --- g12.28: daemon output collapse gate ----------------------------------

    [Fact]
    public void BeginDaemonCapture_NullInStdioMode_IndependentOfSubAgentToggle()
    {
        // Daemon collapse is gated on its OWN flag, and (like sub-agent capture) must NOT engage
        // in stdio/serve mode - so daemon goals still stream for the web app.
        bool priorD = MuxConsole.CollapseDaemonOutput;
        bool priorS = MuxConsole.CollapseSubAgents;
        try
        {
            MuxConsole.CollapseDaemonOutput = true;
            MuxConsole.CollapseSubAgents = false;            // independent of /sav
            string outp = CaptureStdio(() =>
            {
                using var scope = MuxConsole.BeginDaemonCapture("daemon:rt1");
                Assert.Null(scope);                          // no capture in stdio mode
                MuxConsole.WriteStream("daemon work", agentName: "MuxAgent");
            });
            Assert.Contains("daemon work", outp);            // stream frame still emitted
        }
        finally { MuxConsole.CollapseDaemonOutput = priorD; MuxConsole.CollapseSubAgents = priorS; }
    }
}


/// <summary>Coverage for TuiMarkup.WrapMarkup (v0.11.1 NAV prose wrapping): wraps a markup
/// line to a width while preserving the visible text and keeping each slice within width.</summary>
public class TuiWrapMarkupTests
{
    [Fact]
    public void WrapMarkup_LongLine_WrapsAndPreservesText()
    {
        var src = "[#C8C8C8]" + string.Join(" ", Enumerable.Repeat("word", 40)) + "[/]";
        var rows = TuiMarkup.WrapMarkup(src, 20);
        Assert.True(rows.Count > 1);
        foreach (var r in rows)
            Assert.True(TuiMarkup.MarkupWidth(r) <= 20, $"row too wide: {TuiMarkup.MarkupWidth(r)}");
        var joined = string.Concat(rows.Select(r => TuiMarkup.Plain(r))).Replace(" ", "");
        Assert.Equal(string.Concat(Enumerable.Repeat("word", 40)), joined);
    }

    [Fact]
    public void WrapMarkup_ShortLine_SingleRowKeepsStyle()
    {
        var rows = TuiMarkup.WrapMarkup("[#64B4DC]hello[/]", 80);
        Assert.Single(rows);
        Assert.Equal("hello", TuiMarkup.Plain(rows[0]));
    }

    [Fact]
    public void WrapMarkup_HardBreaksOverlongWord()
    {
        var rows = TuiMarkup.WrapMarkup(new string('x', 50), 10);
        Assert.True(rows.Count >= 5);
        foreach (var r in rows)
            Assert.True(TuiMarkup.MarkupWidth(r) <= 10);
        Assert.Equal(50, rows.Sum(r => TuiMarkup.Plain(r).Length));
    }
}
