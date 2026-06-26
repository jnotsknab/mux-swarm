using System.Text.Json;
using MuxSwarm.Utils;
using MuxSwarm.Utils.Acp;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Unit coverage for the ACP (Zed Agent Client Protocol) adapter's pure protocol layer:
/// JSON-RPC envelope shapes, the initialize result, prompt content-block flattening, the
/// Mux-event -> session/update translation field shapes, tool-kind mapping, and the
/// AcpInputReader turn-boundary tick. No live model / no console I/O.
/// </summary>
public class AcpProtocolTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Reparse(object payload) =>
        JsonDocument.Parse(AcpProtocol.Serialize(payload)).RootElement;

    [Fact]
    public void Response_HasJsonRpcEnvelope()
    {
        var el = Reparse(AcpProtocol.Response(7, new Dictionary<string, object?> { ["ok"] = true }));
        Assert.Equal("2.0", el.GetProperty("jsonrpc").GetString());
        Assert.Equal(7, el.GetProperty("id").GetInt32());
        Assert.True(el.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.False(el.TryGetProperty("error", out _));
    }

    [Fact]
    public void Error_HasCodeAndMessage()
    {
        var el = Reparse(AcpProtocol.Error(3, -32601, "Method not found"));
        Assert.Equal(-32601, el.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("Method not found", el.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Notification_HasNoId()
    {
        var el = Reparse(AcpProtocol.Notification("session/update", new Dictionary<string, object?> { ["x"] = 1 }));
        Assert.Equal("session/update", el.GetProperty("method").GetString());
        Assert.False(el.TryGetProperty("id", out _));
    }

    [Fact]
    public void SessionUpdate_WrapsUpdateUnderSessionId()
    {
        var el = Reparse(AcpProtocol.SessionUpdate("sess_x", AcpProtocol.AgentMessageChunk("hi", "msg_1")));
        Assert.Equal("session/update", el.GetProperty("method").GetString());
        var prms = el.GetProperty("params");
        Assert.Equal("sess_x", prms.GetProperty("sessionId").GetString());
        var update = prms.GetProperty("update");
        Assert.Equal("agent_message_chunk", update.GetProperty("sessionUpdate").GetString());
        Assert.Equal("hi", update.GetProperty("content").GetProperty("text").GetString());
        Assert.Equal("text", update.GetProperty("content").GetProperty("type").GetString());
        Assert.Equal("msg_1", update.GetProperty("messageId").GetString());
    }

    [Fact]
    public void ThoughtChunk_UsesThoughtDiscriminator()
    {
        var el = Reparse(AcpProtocol.AgentThoughtChunk("thinking"));
        Assert.Equal("agent_thought_chunk", el.GetProperty("sessionUpdate").GetString());
        // messageId omitted (null) -> not serialized
        Assert.False(el.TryGetProperty("messageId", out _));
    }

    [Fact]
    public void InitializeResult_AdvertisesProtocolV1AndAgentInfo()
    {
        var el = Reparse(AcpProtocol.InitializeResult("1.2.3"));
        Assert.Equal(1, el.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("mux-swarm", el.GetProperty("agentInfo").GetProperty("name").GetString());
        Assert.Equal("1.2.3", el.GetProperty("agentInfo").GetProperty("version").GetString());
        Assert.False(el.GetProperty("agentCapabilities").GetProperty("loadSession").GetBoolean());
    }

    [Fact]
    public void ExtractPromptText_JoinsTextBlocks()
    {
        var prompt = Parse("""[{"type":"text","text":"line one"},{"type":"text","text":"line two"}]""");
        Assert.Equal("line one\nline two", AcpProtocol.ExtractPromptText(prompt));
    }

    [Fact]
    public void ExtractPromptText_HandlesEmbeddedAndLinkResources()
    {
        var prompt = Parse("""
        [
          {"type":"text","text":"see"},
          {"type":"resource","resource":{"uri":"file:///a.py","text":"print(1)"}},
          {"type":"resource_link","uri":"file:///b.txt","name":"b"}
        ]
        """);
        string s = AcpProtocol.ExtractPromptText(prompt);
        Assert.Contains("see", s);
        Assert.Contains("[resource: file:///a.py]", s);
        Assert.Contains("print(1)", s);
        Assert.Contains("[resource: file:///b.txt]", s);
    }

    [Fact]
    public void ExtractPromptText_NonArrayIsEmpty()
    {
        Assert.Equal(string.Empty, AcpProtocol.ExtractPromptText(Parse("""{"type":"text"}""")));
    }

    [Theory]
    [InlineData("read_text_file", "read")]
    [InlineData("Filesystem_write_file", "edit")]
    [InlineData("delete_thing", "delete")]
    [InlineData("move_file", "move")]
    [InlineData("search_files", "search")]
    [InlineData("execute_command_async", "execute")]
    [InlineData("Fetch_fetch", "fetch")]
    [InlineData("some_random_tool", "other")]
    public void ToolKind_MapsKnownPrefixes(string tool, string expected)
    {
        Assert.Equal(expected, AcpProtocol.ToolKind(tool));
    }

    [Fact]
    public void ToolCallUpdate_OmitsContentWhenNoText()
    {
        var el = Reparse(AcpProtocol.ToolCallUpdate("call_1", "completed"));
        Assert.Equal("tool_call_update", el.GetProperty("sessionUpdate").GetString());
        Assert.Equal("call_1", el.GetProperty("toolCallId").GetString());
        Assert.Equal("completed", el.GetProperty("status").GetString());
        Assert.False(el.TryGetProperty("content", out _));
    }

    [Fact]
    public void ToolCall_CarriesTitleKindStatus()
    {
        var el = Reparse(AcpProtocol.ToolCall("call_9", "read_text_file", "read", "in_progress"));
        Assert.Equal("tool_call", el.GetProperty("sessionUpdate").GetString());
        Assert.Equal("call_9", el.GetProperty("toolCallId").GetString());
        Assert.Equal("read", el.GetProperty("kind").GetString());
        Assert.Equal("in_progress", el.GetProperty("status").GetString());
    }

    [Fact]
    public void Serialize_ProducesNoEmbeddedNewline()
    {
        // The stdio framing requires one compact JSON message per line.
        string line = AcpProtocol.Serialize(AcpProtocol.SessionUpdate("s", AcpProtocol.AgentMessageChunk("a\nb")));
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void InputReader_FirstReadTickThenPromptDriven()
    {
        using var reader = new AcpInputReader();
        int ticks = 0;
        reader.ReadEntered += () => ticks++;

        reader.Push("hello");
        Assert.Equal("hello", reader.ReadLine());
        Assert.Equal(1, ticks);

        reader.CloseSession();
        Assert.Equal("/qc", reader.ReadLine());
        Assert.Equal(2, ticks);
    }
}
