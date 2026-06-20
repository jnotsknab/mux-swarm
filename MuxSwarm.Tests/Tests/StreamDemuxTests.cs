using System.Text;
using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Regression coverage for parallel stream de-multiplexing (v0.11.0). The stdio
/// NDJSON contract must stay byte-identical for single-agent streams (no agent field)
/// while parallel callers attach an agent label so the web app can separate concurrent
/// sub-agent output.
/// </summary>
public class StreamDemuxTests
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
    public void WriteStream_NoAgent_OmitsAgentField()
    {
        var outp = CaptureStdio(() => MuxConsole.WriteStream("hello"));
        Assert.Contains("\"type\":\"stream\"", outp);
        Assert.Contains("\"text\":\"hello\"", outp);
        // Byte-identical contract: single-agent frames must NOT carry an agent key.
        Assert.DoesNotContain("\"agent\"", outp);
    }

    [Fact]
    public void WriteStream_WithAgent_IncludesAgentField()
    {
        var outp = CaptureStdio(() => MuxConsole.WriteStream("hi", agentName: "CodeAgent"));
        Assert.Contains("\"type\":\"stream\"", outp);
        Assert.Contains("\"agent\":\"CodeAgent\"", outp);
    }

    [Fact]
    public void EndStreaming_NoAgent_OmitsAgentField()
    {
        var outp = CaptureStdio(() =>
        {
            MuxConsole.BeginStreaming();
            MuxConsole.WriteStream("x");
            MuxConsole.EndStreaming();
        });
        Assert.Contains("\"type\":\"stream_end\"", outp);
        // stream_end with no agent stays byte-identical (no agent key).
        var endLine = Array.Find(outp.Split('\n'), l => l.Contains("stream_end"));
        Assert.NotNull(endLine);
        Assert.DoesNotContain("\"agent\"", endLine!);
    }

    [Fact]
    public void EndStreaming_WithAgent_IncludesAgentField()
    {
        var outp = CaptureStdio(() =>
        {
            MuxConsole.BeginStreaming("DataAnalysisAgent");
            MuxConsole.WriteStream("y", agentName: "DataAnalysisAgent");
            MuxConsole.EndStreaming("DataAnalysisAgent");
        });
        var endLine = Array.Find(outp.Split('\n'), l => l.Contains("stream_end"));
        Assert.NotNull(endLine);
        Assert.Contains("\"agent\":\"DataAnalysisAgent\"", endLine!);
    }

    [Fact]
    public void ParallelStreams_DistinctAgents_AreSeparable()
    {
        var outp = CaptureStdio(() =>
        {
            MuxConsole.WriteStream("from-a", agentName: "AgentA");
            MuxConsole.WriteStream("from-b", agentName: "AgentB");
        });
        Assert.Contains("\"agent\":\"AgentA\"", outp);
        Assert.Contains("\"agent\":\"AgentB\"", outp);
    }
}
