using System.Linq;
using System.Text.Json;
using MuxSwarm.Engine.Acp;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>session/load replay rebuilds the whole thread: user + assistant text, reasoning, and
/// tool calls with their finished status, in order; the runtime's insight line is not shown.</summary>
public class AcpReplayTests
{
    private const string Session = """
    {"stateBag":{"InMemoryChatHistoryProvider":{"messages":[
      {"role":"user","contents":[{"$type":"text","text":"fix notes"},{"$type":"text","text":"\n\n[mux context: 1k/200k (1%), auto-compact at 200k"}]},
      {"role":"assistant","contents":[{"$type":"reasoning","text":"read it first"},{"$type":"functionCall","name":"Filesystem_read_text_file","arguments":{"path":"C:\\w\\notes.txt"},"callId":"c1"}]},
      {"role":"tool","contents":[{"$type":"functionResult","result":"alpha\nbeta","callId":"c1"}]},
      {"role":"assistant","contents":[{"$type":"functionCall","name":"execute_command_async","arguments":{"command":"dir"},"callId":"c2"},{"$type":"functionCall","name":"wait_job_progress","arguments":{"job_id":"job_1"},"callId":"c3"}]},
      {"role":"tool","contents":[{"$type":"functionResult","result":[{"$type":"text","text":"Error: boom"}],"callId":"c2"}]},
      {"role":"assistant","contents":[{"$type":"text","text":"done"}]}
    ]}}}
    """;

    private static JsonElement J(object o) => JsonDocument.Parse(AcpProtocol.Serialize(o)).RootElement;

    [Fact]
    public void Replays_Text_Reasoning_And_ToolCalls_InOrder()
    {
        var ups = AcpServer.ReplayUpdates(JsonDocument.Parse(Session).RootElement).Select(J).ToList();
        string Kind(int i) => ups[i].GetProperty("sessionUpdate").GetString()!;
        Assert.Equal(new[] { "user_message_chunk", "agent_thought_chunk", "tool_call", "tool_call_update",
            "tool_call", "tool_call", "tool_call_update", "agent_message_chunk", "tool_call_update" },
            Enumerable.Range(0, ups.Count).Select(Kind).ToArray());

        Assert.Equal("fix notes", ups[0].GetProperty("content").GetProperty("text").GetString());   // insight line stripped
        Assert.Equal("read it first", ups[1].GetProperty("content").GetProperty("text").GetString());
        Assert.Equal("Read C:\\w\\notes.txt", ups[2].GetProperty("title").GetString());
        Assert.Equal("C:\\w\\notes.txt", ups[2].GetProperty("rawInput").GetProperty("path").GetString());
        Assert.Equal("completed", ups[3].GetProperty("status").GetString());
        Assert.Equal("alpha\nbeta", ups[3].GetProperty("content")[0].GetProperty("content").GetProperty("text").GetString());
        Assert.Equal("c2", ups[6].GetProperty("toolCallId").GetString());
        Assert.Equal("failed", ups[6].GetProperty("status").GetString());                         // error result
        Assert.Equal("done", ups[7].GetProperty("content").GetProperty("text").GetString());
        Assert.Equal("c3", ups[8].GetProperty("toolCallId").GetString());                          // never finished
        Assert.Equal("failed", ups[8].GetProperty("status").GetString());
    }

    [Fact]
    public void Unknown_Shape_Replays_Nothing()
    {
        Assert.Empty(AcpServer.ReplayUpdates(JsonDocument.Parse("{}").RootElement));
    }
}
