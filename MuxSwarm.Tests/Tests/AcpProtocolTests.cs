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

    [Theory]
    [InlineData("{\"path\":\"C:\\\\a\\\\b.cs\"}", "C:\\a\\b.cs")]
    [InlineData("{\"file\":\"/home/u/x.py\"}", "/home/u/x.py")]
    [InlineData("{\"path\":\"relative/x.cs\"}", null)]
    [InlineData("not json", null)]
    [InlineData("{\"other\":1}", null)]
    public void ExtractAbsolutePath_OnlyReturnsAbsolute(string args, string? expected)
    {
        Assert.Equal(expected, AcpProtocol.ExtractAbsolutePath(args));
    }

    [Fact]
    public void Locations_NullWhenNoAbsolutePath()
    {
        Assert.Null(AcpProtocol.Locations("{\"path\":\"rel/x\"}"));
        var loc = AcpProtocol.Locations("{\"path\":\"/abs/x\"}");
        Assert.NotNull(loc);
        var el = Reparse(loc!);
        Assert.Equal("/abs/x", el[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Plan_EmitsFullSnapshotEntries()
    {
        var el = Reparse(AcpProtocol.Plan(new[] { ("step one", "high", "in_progress"), ("step two", "low", "pending") }));
        Assert.Equal("plan", el.GetProperty("sessionUpdate").GetString());
        var entries = el.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("step one", entries[0].GetProperty("content").GetString());
        Assert.Equal("high", entries[0].GetProperty("priority").GetString());
        Assert.Equal("in_progress", entries[0].GetProperty("status").GetString());
    }

    [Fact]
    public void DiffContent_KeepsNullOldTextForNewFile()
    {
        var el = Reparse(AcpProtocol.DiffContent("/abs/f.cs", null, "new body"));
        Assert.Equal("diff", el.GetProperty("type").GetString());
        Assert.Equal("/abs/f.cs", el.GetProperty("path").GetString());
        Assert.Equal("new body", el.GetProperty("newText").GetString());
        Assert.Equal(JsonValueKind.Null, el.GetProperty("oldText").ValueKind);
    }

    [Fact]
    public void ToolCallUpdateRich_OmitsEmptyContent()
    {
        var el = Reparse(AcpProtocol.ToolCallUpdateRich("call_1", "completed", null));
        Assert.False(el.TryGetProperty("content", out _));
        var el2 = Reparse(AcpProtocol.ToolCallUpdateRich("call_2", "completed", new[] { AcpProtocol.TextContent("ok") }));
        Assert.Equal(1, el2.GetProperty("content").GetArrayLength());
    }

    [Fact]
    public void ToolCallWithLocations_IncludesLocationsWhenPresent()
    {
        var loc = AcpProtocol.Locations("{\"path\":\"/abs/x\"}");
        var el = Reparse(AcpProtocol.ToolCallWithLocations("call_5", "edit_file", "edit", "in_progress", null, loc));
        Assert.Equal("/abs/x", el.GetProperty("locations")[0].GetProperty("path").GetString());
        Assert.False(el.TryGetProperty("rawInput", out _));
    }

    [Fact]
    public void InitializeResult_AdvertisesFullSuiteCapabilities()
    {
        var el = Reparse(AcpProtocol.InitializeResult("1.0", loadSession: true));
        var caps = el.GetProperty("agentCapabilities");
        Assert.True(caps.GetProperty("promptCapabilities").GetProperty("embeddedContext").GetBoolean());
        Assert.True(caps.TryGetProperty("sessionCapabilities", out var sc));
        Assert.True(sc.TryGetProperty("resume", out _));
        Assert.True(sc.TryGetProperty("close", out _));
        Assert.True(caps.GetProperty("auth").TryGetProperty("logout", out _));
    }

    [Fact]
    public void AvailableCommandsUpdate_ListsCommands()
    {
        var el = Reparse(AcpProtocol.AvailableCommandsUpdate(new[] { ("compact", "Compress"), ("qc", "Quit") }));
        Assert.Equal("available_commands_update", el.GetProperty("sessionUpdate").GetString());
        var cmds = el.GetProperty("availableCommands");
        Assert.Equal(2, cmds.GetArrayLength());
        Assert.Equal("compact", cmds[0].GetProperty("name").GetString());
        Assert.Equal("Compress", cmds[0].GetProperty("description").GetString());
    }

    [Fact]
    public void CurrentModeUpdate_CarriesModeId()
    {
        var el = Reparse(AcpProtocol.CurrentModeUpdate("ultra"));
        Assert.Equal("current_mode_update", el.GetProperty("sessionUpdate").GetString());
        Assert.Equal("ultra", el.GetProperty("currentModeId").GetString());
    }

    [Fact]
    public void RequestPermission_OffersAllowRejectOptions()
    {
        var el = Reparse(AcpProtocol.RequestPermissionParams("sess_x", "call_1", "Run tests"));
        Assert.Equal("sess_x", el.GetProperty("sessionId").GetString());
        Assert.Equal("call_1", el.GetProperty("toolCall").GetProperty("toolCallId").GetString());
        var opts = el.GetProperty("options");
        Assert.Equal(3, opts.GetArrayLength());
        Assert.Equal("allow_once", opts[0].GetProperty("kind").GetString());
        Assert.Equal("reject_once", opts[2].GetProperty("kind").GetString());
    }

    [Fact]
    public void FsParams_ShapeIsCorrect()
    {
        var r = Reparse(AcpProtocol.FsReadParams("s", "/abs/x", 10, 50));
        Assert.Equal("/abs/x", r.GetProperty("path").GetString());
        Assert.Equal(10, r.GetProperty("line").GetInt32());
        Assert.Equal(50, r.GetProperty("limit").GetInt32());
        var r2 = Reparse(AcpProtocol.FsReadParams("s", "/abs/y"));
        Assert.False(r2.TryGetProperty("line", out _));
        var w = Reparse(AcpProtocol.FsWriteParams("s", "/abs/z", "body"));
        Assert.Equal("body", w.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("""{"clientCapabilities":{"fs":{"readTextFile":true,"writeTextFile":true},"terminal":true}}""", true, true, true)]
    [InlineData("""{"clientCapabilities":{"fs":{"readTextFile":true}}}""", true, false, false)]
    [InlineData("""{"clientCapabilities":{}}""", false, false, false)]
    [InlineData("""{}""", false, false, false)]
    public void ParseClientCaps_ReadsAdvertisedFlags(string json, bool read, bool write, bool term)
    {
        var caps = AcpProtocol.ParseClientCaps(Parse(json));
        Assert.Equal(read, caps.FsRead);
        Assert.Equal(write, caps.FsWrite);
        Assert.Equal(term, caps.Terminal);
    }

    [Fact]
    public void InitializeResult_AdvertisesLoadSessionWhenRequested()
    {
        var el = Reparse(AcpProtocol.InitializeResult("1.0", loadSession: true));
        Assert.True(el.GetProperty("agentCapabilities").GetProperty("loadSession").GetBoolean());
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
