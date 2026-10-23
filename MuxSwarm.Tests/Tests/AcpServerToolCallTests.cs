using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Acp;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Drives AcpServer's dispatch + event translation in-process (no model) and asserts the ACP
/// wire for tool calls: readable title + rawInput + locations at call time, callId pairing,
/// real old/new diffs for edits, failed status for error results, and no card left open at
/// the end of a turn.
/// </summary>
[Collection("ConsoleState")]
public class AcpServerToolCallTests
{
    private sealed class Harness : IDisposable
    {
        private readonly TextWriter _prevOut = Console.Out;
        private readonly StringWriter _out = new();
        private readonly SemaphoreSlim _turnDone = new(0);
        private readonly AcpServer _server;
        private readonly MethodInfo _dispatch;
        private readonly MethodInfo _onEvent;
        private AcpInputReader? _reader;
        private readonly ManualResetEventSlim _promptRead = new(false);

        public Harness()
        {
            Console.SetOut(TextWriter.Synchronized(_out));
            _server = new AcpServer("test", async (reader, _) =>
            {
                _reader = reader;
                while (true)
                {
                    string? line = await Task.Run(reader.ReadLine);
                    if (line is null || line == "/qc") break;
                    _promptRead.Set();
                    await _turnDone.WaitAsync();
                }
            });
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            _dispatch = typeof(AcpServer).GetMethod("Dispatch", F)!;
            _onEvent = typeof(AcpServer).GetMethod("OnMuxEvent", F)!;
        }

        public void Send(string json) => _dispatch.Invoke(_server, new object[] { json });

        public void Event(string type, params (string, object?)[] fields) =>
            _onEvent.Invoke(_server, new object[] { type, fields.ToDictionary(f => f.Item1, f => f.Item2) });

        public void StartTurn()
        {
            Send("""{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/tmp","mcpServers":[]}}""");
            Send("""{"jsonrpc":"2.0","id":2,"method":"session/prompt","params":{"sessionId":"x","prompt":[{"type":"text","text":"go"}]}}""");
            Assert.True(_promptRead.Wait(5000), "prompt never reached the session loop");
        }

        /// <summary>Finish the turn and wait for the session/prompt response (id 2).</summary>
        public void EndTurn()
        {
            _turnDone.Release();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !Messages().Any(m => m.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == 2 && m.TryGetProperty("result", out _)))
                Thread.Sleep(10);
        }

        public List<JsonElement> Messages() =>
            // Console.Out is process-global; tests in other collections may write to it in parallel.
            _out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("{\"jsonrpc\""))
                .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

        public List<JsonElement> Updates() =>
            Messages().Where(m => m.TryGetProperty("method", out var me) && me.GetString() == "session/update")
                .Select(m => m.GetProperty("params").GetProperty("update")).ToList();

        public void Dispose()
        {
            _reader?.CloseSession();
            _turnDone.Release(10);
            Console.SetOut(_prevOut);
        }
    }

    [Fact]
    public void ToolCall_AnnouncedWithTitleRawInputAndLocations_AndPairedByCallId()
    {
        using var h = new Harness();
        h.StartTurn();
        string path = Path.Combine(Path.GetTempPath(), "acp-notes.txt");
        string args = JsonSerializer.Serialize(new { path });
        h.Event("tool_call", ("agent", "A"), ("tool", "Filesystem_read_text_file"), ("callId", "toolu_1"), ("args", args));
        h.Event("tool_call", ("agent", "A"), ("tool", "execute_command_async"), ("callId", "toolu_2"), ("args", """{"command":"dir"}"""));
        // Results arrive out of order: pairing must follow callId, not FIFO.
        h.Event("tool_result", ("agent", "A"), ("tool", "execute_command_async"), ("result", "Job ID: job_1"), ("callId", "toolu_2"));
        h.Event("tool_result", ("agent", "A"), ("tool", "Filesystem_read_text_file"), ("result", "alpha"), ("callId", "toolu_1"));
        h.EndTurn();

        var ups = h.Updates();
        var read = ups.Single(u => u.GetProperty("sessionUpdate").GetString() == "tool_call" && u.GetProperty("toolCallId").GetString() == "toolu_1");
        Assert.Equal($"Read {path}", read.GetProperty("title").GetString());
        Assert.Equal("read", read.GetProperty("kind").GetString());
        Assert.Equal(path, read.GetProperty("rawInput").GetProperty("path").GetString());
        Assert.Equal(path, read.GetProperty("locations")[0].GetProperty("path").GetString());
        var run = ups.Single(u => u.GetProperty("sessionUpdate").GetString() == "tool_call" && u.GetProperty("toolCallId").GetString() == "toolu_2");
        Assert.Equal("Run: dir", run.GetProperty("title").GetString());

        string Body(string id) => ups.Single(u => u.GetProperty("sessionUpdate").GetString() == "tool_call_update" && u.GetProperty("toolCallId").GetString() == id)
            .GetProperty("content")[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.Equal("alpha", Body("toolu_1"));
        Assert.Equal("Job ID: job_1", Body("toolu_2"));
    }

    [Fact]
    public void EditTool_ResultCarriesRealOldAndNewText()
    {
        string path = Path.Combine(Path.GetTempPath(), $"acp-edit-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "alpha\nbeta\n");
        try
        {
            using var h = new Harness();
            h.StartTurn();
            h.Event("tool_call", ("agent", "A"), ("tool", "Filesystem_edit_file"), ("callId", "e1"),
                ("args", JsonSerializer.Serialize(new { path, oldText = "beta", newText = "BETA" })));
            File.WriteAllText(path, "alpha\nBETA\n");   // the tool runs between call and result
            h.Event("tool_result", ("agent", "A"), ("tool", "Filesystem_edit_file"), ("result", "--- a\n+++ b\n-beta\n+BETA"), ("callId", "e1"));
            h.EndTurn();

            var call = h.Updates().Single(u => u.GetProperty("sessionUpdate").GetString() == "tool_call");
            Assert.Equal($"Edit {path}", call.GetProperty("title").GetString());
            var diff = h.Updates().Single(u => u.GetProperty("sessionUpdate").GetString() == "tool_call_update").GetProperty("content")[0];
            Assert.Equal("diff", diff.GetProperty("type").GetString());
            Assert.Equal(path, diff.GetProperty("path").GetString());
            Assert.Equal("alpha\nbeta\n", diff.GetProperty("oldText").GetString());
            Assert.Equal("alpha\nBETA\n", diff.GetProperty("newText").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ErrorResult_IsFailed_AndOpenCallsCloseBeforeTheTurnResponse()
    {
        using var h = new Harness();
        h.StartTurn();
        h.Event("tool_call", ("agent", "A"), ("tool", "Filesystem_read_text_file"), ("callId", "c1"), ("args", """{"path":"/nope"}"""));
        h.Event("tool_result", ("agent", "A"), ("tool", "Filesystem_read_text_file"), ("result", "Error: not found"), ("callId", "c1"), ("isError", true));
        h.Event("tool_call", ("agent", "A"), ("tool", "wait_job_progress"), ("callId", "c2"), ("args", """{"job_id":"job_9"}"""));
        h.EndTurn();   // c2 never got a result (e.g. cancelled)

        var msgs = h.Messages();
        int Index(Func<JsonElement, bool> p) => msgs.FindIndex(m => p(m));
        bool IsUpdate(JsonElement m, string id, string status) =>
            m.TryGetProperty("params", out var pa) && pa.TryGetProperty("update", out var u)
            && u.GetProperty("sessionUpdate").GetString() == "tool_call_update"
            && u.GetProperty("toolCallId").GetString() == id && u.GetProperty("status").GetString() == status;

        Assert.True(Index(m => IsUpdate(m, "c1", "failed")) >= 0, "error result must be failed");
        int closed = Index(m => IsUpdate(m, "c2", "failed"));
        int response = Index(m => m.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == 2 && m.TryGetProperty("result", out _));
        Assert.True(closed >= 0, "open call must be closed at the turn boundary");
        Assert.True(response > closed, "open calls close BEFORE the session/prompt response");
        var wait = h.Updates().Single(u => u.GetProperty("sessionUpdate").GetString() == "tool_call" && u.GetProperty("toolCallId").GetString() == "c2");
        Assert.Equal("execute", wait.GetProperty("kind").GetString());
        Assert.Equal("Job job_9", wait.GetProperty("title").GetString());
    }

    [Fact]
    public void ResultWithoutAnnouncedCall_IsAnnouncedFirst()
    {
        using var h = new Harness();
        h.StartTurn();
        h.Event("tool_result", ("agent", "A"), ("tool", "mystery_tool"), ("result", "ok"));
        h.EndTurn();
        var ups = h.Updates().Where(u => u.GetProperty("sessionUpdate").GetString()!.StartsWith("tool_call")).ToList();
        Assert.Equal("tool_call", ups[0].GetProperty("sessionUpdate").GetString());
        Assert.Equal("tool_call_update", ups[1].GetProperty("sessionUpdate").GetString());
        Assert.Equal(ups[0].GetProperty("toolCallId").GetString(), ups[1].GetProperty("toolCallId").GetString());
    }

    [Fact]
    public void EmitToolCallStdio_CarriesCallIdAndArgs_ResultCarriesCallId()
    {
        var prevStdio = MuxConsole.StdioMode;
        var prevOut = Console.Out;
        try
        {
            MuxConsole.StdioMode = true;
            MuxConsole.AcpActive = false;
            MuxConsole.AcpSink = null;
            var sw = new StringWriter();
            Console.SetOut(sw);

            MuxConsole.EmitToolCallStdio("A", "Filesystem_read_text_file", "toolu_9",
                new Dictionary<string, object?> { ["path"] = "/x/a.txt" });
            MuxConsole.WriteToolResult("A", "Filesystem_read_text_file", "Error: nope", callId: "toolu_9", isError: true);

            var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("{")).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            var call = lines.Single(l => l.GetProperty("type").GetString() == "tool_call");
            Assert.Equal("toolu_9", call.GetProperty("callId").GetString());
            Assert.Equal("/x/a.txt", JsonDocument.Parse(call.GetProperty("args").GetString()!).RootElement.GetProperty("path").GetString());
            var result = lines.Single(l => l.GetProperty("type").GetString() == "tool_result");
            Assert.Equal("toolu_9", result.GetProperty("callId").GetString());
            Assert.True(result.GetProperty("isError").GetBoolean());
        }
        finally
        {
            Console.SetOut(prevOut);
            MuxConsole.StdioMode = prevStdio;
        }
    }
}
