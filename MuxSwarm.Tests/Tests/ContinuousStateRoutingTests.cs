using System;
using System.IO;
using System.Text;
using MuxSwarm.State;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Regression guard for the v0.12.1 TUI viewport-artifact fix: ContinuousStateManager status
/// lines must flow through MuxConsole (the managed renderer / NDJSON sink), never a raw
/// Console.WriteLine that bypasses the TUI live-region diff and strands ghost rows. We assert
/// the routing in stdio mode, where the managed sink emits a parseable NDJSON envelope while a
/// raw write would emit a bare "[CONTINUOUS] ..." line with no JSON wrapper.
/// </summary>
[Collection("ConsoleState")]
public class ContinuousStateRoutingTests
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
    public void MarkStopped_RoutesThroughManagedSink_NotRawConsole()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mux_cont_routing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var state = new CurrentStateMetadata(
                goalId: "g1", goal: "demo", iteration: 3,
                lastCompletedAt: DateTime.UtcNow, nextWakeAt: DateTime.UtcNow,
                status: "running", minDelaySeconds: 0);

            var outp = CaptureStdio(() => ContinuousStateManager.MarkStopped("g1", state, dir));

            // The managed sink wraps every emit as an NDJSON object. A raw Console.WriteLine
            // (the bug) would print a bare line with no JSON envelope.
            Assert.Contains("[CONTINUOUS]", outp);
            Assert.Contains("{", outp);
            Assert.Contains("\"type\"", outp);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Load_MissingState_DoesNotWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mux_cont_routing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outp = CaptureStdio(() => ContinuousStateManager.Load("nope", dir));
            Assert.Equal(string.Empty, outp.Trim());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
