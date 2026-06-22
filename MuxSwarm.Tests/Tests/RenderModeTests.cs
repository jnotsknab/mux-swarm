using System.Text;
using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Regression coverage for the v0.11.0 render-mode plumbing (G1/G10). The new
/// interactive TUI render layer must be completely unreachable in stdio/serve mode:
/// whenever <see cref="MuxConsole.StdioMode"/> is set, <see cref="MuxConsole.RenderMode"/>
/// must report <see cref="RenderMode.Stdio"/> and the emitted NDJSON must stay
/// byte-identical regardless of the interactive preference. This protects the serve/web
/// contract from the interactive default flip.
/// </summary>
[Collection("ConsoleState")]
public class RenderModeTests
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
    public void StdioMode_AlwaysReportsStdioRenderMode_RegardlessOfPreference()
    {
        try
        {
            MuxConsole.StdioMode = true;
            // Even if an interactive preference was resolved to TUI, stdio wins.
            MuxConsole.ResolveRenderMode("tui");
            Assert.Equal(RenderMode.Stdio, MuxConsole.RenderMode);
            Assert.False(MuxConsole.IsTui);

            MuxConsole.SetTuiRenderMode();
            Assert.Equal(RenderMode.Stdio, MuxConsole.RenderMode);
            Assert.False(MuxConsole.IsTui);
        }
        finally
        {
            MuxConsole.StdioMode = false;
            MuxConsole.SetClassicRenderMode();
        }
    }

    [Fact]
    public void ResolveRenderMode_Classic_ForcesClassic()
    {
        try
        {
            MuxConsole.StdioMode = false;
            MuxConsole.ResolveRenderMode("classic");
            Assert.Equal(RenderMode.Classic, MuxConsole.RenderMode);
            Assert.False(MuxConsole.IsTui);
        }
        finally
        {
            MuxConsole.SetClassicRenderMode();
        }
    }

    [Fact]
    public void ResolveRenderMode_NullOrUnknown_DefaultsToAutoCapabilityAware()
    {
        try
        {
            MuxConsole.StdioMode = false;
            // The test host has redirected stdout, so the capability-aware "auto"
            // default must resolve to Classic (never a broken TUI).
            MuxConsole.ResolveRenderMode(null);
            Assert.Equal(RenderMode.Classic, MuxConsole.RenderMode);

            MuxConsole.ResolveRenderMode("something-unrecognized");
            Assert.Equal(RenderMode.Classic, MuxConsole.RenderMode);
        }
        finally
        {
            MuxConsole.SetClassicRenderMode();
        }
    }

    [Fact]
    public void IsTuiCapableTerminal_RedirectedOutput_IsNotCapable()
    {
        // The xUnit host redirects stdout, which must disqualify the live TUI so
        // automation/pipes never get a full-screen renderer.
        Assert.False(MuxConsole.IsTuiCapableTerminal());
    }

    [Fact]
    public void StdioOutput_ByteIdentical_AcrossRenderModePreferences()
    {
        // The NDJSON contract must not change when the interactive render preference
        // changes. Capture the same calls under classic vs tui preference and assert equality.
        string Run(string pref) => CaptureStdio(() =>
        {
            MuxConsole.ResolveRenderMode(pref); // no-op for output: stdio short-circuits first
            MuxConsole.WriteStream("hello");
            MuxConsole.BeginStreaming();
            MuxConsole.WriteStream("world");
            MuxConsole.EndStreaming();
            MuxConsole.WriteInfo("info-line");
            MuxConsole.WriteSuccess("ok-line");
        });

        var classic = Run("classic");
        var tui = Run("tui");

        Assert.Equal(classic, tui);
        // Sanity: it is real NDJSON, and no render-mode noise leaked in.
        Assert.Contains("\"type\":\"stream\"", classic);
        Assert.DoesNotContain("renderMode", classic);
    }
}
