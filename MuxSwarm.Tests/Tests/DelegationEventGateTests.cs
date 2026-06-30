using System;
using System.IO;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Regression for the delegation_compacted TUI leak: EmitDelegationCompacted must write the raw
// NDJSON event ONLY in stdio/serve (or ACP) transport, never to the interactive console where it
// would print "{"type":"delegation_compacted",...}" into the rendered transcript.
[Collection("ConsoleState")]
public class DelegationEventGateTests
{
    [Fact]
    public void EmitDelegationCompacted_NonStdio_WritesNothing()
    {
        var prevStdio = MuxConsole.StdioMode;
        var prevOut = Console.Out;
        try
        {
            MuxConsole.StdioMode = false;      // interactive console
            MuxConsole.AcpActive = false;
            MuxConsole.AcpSink = null;
            var sw = new StringWriter();
            Console.SetOut(sw);

            MuxConsole.EmitDelegationCompacted("CodeAgent", 1823, "summary", null);

            Assert.DoesNotContain("delegation_compacted", sw.ToString());
            Assert.Equal(string.Empty, sw.ToString().Trim());
        }
        finally
        {
            Console.SetOut(prevOut);
            MuxConsole.StdioMode = prevStdio;
        }
    }

    [Fact]
    public void EmitDelegationCompacted_StdioMode_EmitsEvent()
    {
        var prevStdio = MuxConsole.StdioMode;
        var prevOut = Console.Out;
        try
        {
            MuxConsole.StdioMode = true;       // serve/stdio transport
            MuxConsole.AcpActive = false;
            MuxConsole.AcpSink = null;
            var sw = new StringWriter();
            Console.SetOut(sw);

            MuxConsole.EmitDelegationCompacted("CodeAgent", 1823, "summary", null);

            var outp = sw.ToString();
            Assert.Contains("delegation_compacted", outp);
            Assert.Contains("\"posture\"", outp);
        }
        finally
        {
            Console.SetOut(prevOut);
            MuxConsole.StdioMode = prevStdio;
        }
    }
}
