using System.IO;
using System.Text.Json;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Background delegations must be routable OUT of the main viewport in the web app. The mechanism is
/// MuxConsole.BeginServeOrigin("subagent", ...) wrapping the detached run (DetachedRunner), so every
/// NDJSON frame the background sub-agent emits carries origin=subagent + its agent name -- the signal
/// the frontend uses to send those frames to the sub-agent card instead of interleaving them inline.
/// These lock that tagging (the daemon uses the identical pattern for its lane).
/// </summary>
[Collection("ConsoleState")]
public class SubAgentOriginTaggingTests
{
    private static (string origin, string lane, string agent, string type)? FirstFrame(string stdout, string type)
    {
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] != '{') continue;
            using var doc = JsonDocument.Parse(t);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var ty) || ty.GetString() != type) continue;
            return (
                root.TryGetProperty("origin", out var o) ? o.GetString() ?? "" : "",
                root.TryGetProperty("lane", out var l) ? l.GetString() ?? "" : "",
                root.TryGetProperty("agent", out var a) ? a.GetString() ?? "" : "",
                type);
        }
        return null;
    }

    [Fact]
    public void BackgroundScope_TagsFramesWithSubagentOrigin()
    {
        var prevStdio = MuxConsole.StdioMode;
        var prevOut = Console.Out;
        var sw = new StringWriter();
        try
        {
            MuxConsole.StdioMode = true;
            Console.SetOut(sw);

            using (MuxConsole.BeginServeOrigin("subagent", "sub:CompanionAgent"))
            {
                MuxConsole.WriteToolResult("CompanionAgent", "read_file", "hello world");
            }
        }
        finally
        {
            Console.SetOut(prevOut);
            MuxConsole.StdioMode = prevStdio;
        }

        var frame = FirstFrame(sw.ToString(), "tool_result");
        Assert.NotNull(frame);
        Assert.Equal("subagent", frame!.Value.origin);
        Assert.Equal("sub:CompanionAgent", frame.Value.lane);
        Assert.Equal("CompanionAgent", frame.Value.agent);
    }

    [Fact]
    public void OutsideScope_NoOriginTag()
    {
        var prevStdio = MuxConsole.StdioMode;
        var prevOut = Console.Out;
        var sw = new StringWriter();
        try
        {
            MuxConsole.StdioMode = true;
            Console.SetOut(sw);
            MuxConsole.WriteToolResult("Lead", "read_file", "hi");
        }
        finally
        {
            Console.SetOut(prevOut);
            MuxConsole.StdioMode = prevStdio;
        }

        var frame = FirstFrame(sw.ToString(), "tool_result");
        Assert.NotNull(frame);
        Assert.Equal("", frame!.Value.origin);   // no origin tag outside a scope
        Assert.Equal("Lead", frame.Value.agent);
    }
}
