using System.Text;
using MuxSwarm.Utils;

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
}
