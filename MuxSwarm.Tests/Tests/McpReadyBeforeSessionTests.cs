using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Interactive sessions open before MCP is ready, but the agent (and its tool list) is only built after MCP
/// finishes, so the first model call always carries every tool. Entries that do not defer still await MCP up front.</summary>
[Collection("ConsoleState")]
public class McpReadyBeforeSessionTests
{
    private static string AppSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.cs"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "App.cs"));
    }

    [Theory]
    [InlineData("case \"/agent\":", true)]
    [InlineData("case \"/stateless\":", true)]
    [InlineData("case var rc when rc == \"/resume\"", true)]
    [InlineData("runSession: async", true)]
    [InlineData("case \"/onboard\":", false)]
    public void SessionEntry_DefersOrAwaitsMcp(string caseLabel, bool defers)
    {
        string src = AppSource();
        int start = src.IndexOf(caseLabel, StringComparison.Ordinal);
        Assert.True(start >= 0, caseLabel);
        int tools = src.IndexOf("mcpTools: McpTools", start, StringComparison.Ordinal);
        Assert.True(tools > start, "no McpTools hand-off after " + caseLabel);
        int end = src.IndexOf(");", tools, StringComparison.Ordinal);
        if (defers) Assert.Contains("deferMcp: true", src[tools..end]);
        else Assert.Contains("await EnsureMcpReadyAsync();", src[start..tools]);
    }

    private sealed class QueueReader : TextReader
    {
        public readonly BlockingCollection<string> Lines = new();
        public readonly TaskCompletionSource FirstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override string? ReadLine() { FirstRead.TrySetResult(); return Lines.Take(); }
    }

    private sealed class RecordingClient : IChatClient
    {
        public readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Called.TrySetResult();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task DeferredSession_ReadsInputBeforeMcp_ButCallsTheModelOnlyAfterIt()
    {
        var initField = typeof(App).GetField("McpInitTask", BindingFlags.NonPublic | BindingFlags.Static)!;
        var gateField = typeof(App).GetField("_mcpReadyGate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var savedInit = initField.GetValue(null); var savedGate = gateField.GetValue(null);
        var savedTools = App.McpTools; var savedDef = SingleAgentOrchestrator.AgentDef;
        var savedIn = MuxConsole.InputOverride; var savedStdio = MuxConsole.StdioMode; var savedOut = Console.Out;
        var mcp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new QueueReader(); var client = new RecordingClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task? session = null;
        try
        {
            initField.SetValue(null, mcp.Task); gateField.SetValue(null, 0);
            App.McpTools = new List<ModelContextProtocol.Client.McpClientTool>();
            SingleAgentOrchestrator.AgentDef = new Common.AgentDefinition("Lead", "test", "unused", false, t => t);
            MuxConsole.InputOverride = reader; MuxConsole.StdioMode = true; Console.SetOut(new StringWriter());

            // Task.Run: the session reads input synchronously before its first await.
            session = Task.Run(() => SingleAgentOrchestrator.ChatAgentAsync(client, cts.Token, maxIterations: 1,
                mcpTools: App.McpTools, systemPromptOverride: "test", persistSession: false, deferMcp: true));

            await reader.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the session opened while MCP is pending
            reader.Lines.Add("hello");
            await Task.Delay(300);
            Assert.False(client.Called.Task.IsCompleted, "model was called before MCP finished connecting");

            mcp.SetResult(true);
            await client.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
            reader.Lines.Add("/qc");
            await session.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            cts.Cancel(); reader.Lines.Add("/qc");
            if (session is not null) { try { await session.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            initField.SetValue(null, savedInit); gateField.SetValue(null, savedGate);
            App.McpTools = savedTools; SingleAgentOrchestrator.AgentDef = savedDef;
            MuxConsole.InputOverride = savedIn; MuxConsole.StdioMode = savedStdio; Console.SetOut(savedOut);
        }
    }
}
