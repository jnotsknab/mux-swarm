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
        Assert.Contains("ctrl+o expand", plain);
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
        Assert.Contains("ctrl+o expand", plain);
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
}
