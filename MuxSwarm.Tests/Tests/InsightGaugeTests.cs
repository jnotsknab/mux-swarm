using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Insight;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// /insight basic|full: the context line is stamped IN PLACE onto the newest message of each model call, so the
/// agent session commits exactly what the model was sent. That invariant is what keeps the provider prompt cache
/// intact (measured: a line sent but not saved, or moved each call, drops cached share from ~85% to ~34-47%).
/// </summary>
public class InsightGaugeTests
{
    private static ChatMessage ToolResult(string id, string text) => new(ChatRole.Tool, [new FunctionResultContent(id, text)]);

    [Fact]
    public void Off_LeavesMessagesUntouched()
    {
        var user = new ChatMessage(ChatRole.User, "hi");
        InsightClient.Stamp([user], InsightLevel.Off);
        Assert.Single(user.Contents);
    }

    [Fact]
    public void ToolResult_IsStampedOnce_AndTheLineIsLast()
    {
        var msgs = new List<ChatMessage> { new(ChatRole.User, "go"), ToolResult("c0", "content of f0") };
        InsightClient.Stamp(msgs, InsightLevel.Basic);
        InsightClient.Stamp(msgs, InsightLevel.Basic);   // FIC replays the same list: never stamp twice
        string result = ((FunctionResultContent)msgs[1].Contents[0]).Result!.ToString()!;
        Assert.StartsWith("content of f0\n\n" + InsightClient.Marker, result);
        Assert.Equal(1, CountOf(result, InsightClient.Marker));
        Assert.EndsWith("]", result);
    }

    /// <summary>
    /// MCP tools (e.g. Brave) return content parts, not a string. Live bug 2026-10-12: the stamp did
    /// Result.ToString() and replaced the search results with "Microsoft.Extensions.AI.AIContent[]".
    /// </summary>
    [Fact]
    public void ContentPartArrayResult_KeepsItsContent_AndGetsTheLineAsAnExtraPart()
    {
        AIContent[] parts = [new TextContent("result one"), new TextContent("result two")];
        var msgs = new List<ChatMessage> { new(ChatRole.User, "go"), new(ChatRole.Tool, [new FunctionResultContent("c0", parts)]) };
        InsightClient.Stamp(msgs, InsightLevel.Basic);
        InsightClient.Stamp(msgs, InsightLevel.Basic);
        var stamped = Assert.IsAssignableFrom<IList<AIContent>>(((FunctionResultContent)msgs[1].Contents[0]).Result);
        var texts = stamped.OfType<TextContent>().Select(t => t.Text).ToList();
        Assert.Equal("result one", texts[0]);
        Assert.Equal("result two", texts[1]);
        Assert.StartsWith(InsightClient.Marker, texts[2].TrimStart());
        Assert.Equal(3, texts.Count);                                   // stamped once
        Assert.DoesNotContain(texts, t => t.Contains("AIContent[]"));
    }

    [Fact]
    public void ContentPartListResult_IsStampedInPlace()
    {
        var parts = new List<AIContent> { new TextContent("r") };
        var msgs = new List<ChatMessage> { new(ChatRole.Tool, [new FunctionResultContent("c0", parts)]) };
        InsightClient.Stamp(msgs, InsightLevel.Basic);
        Assert.Equal(2, parts.Count);
        Assert.Contains(InsightClient.Marker, ((TextContent)parts[1]).Text);
    }

    [Fact]
    public void JsonStringResult_FromAIFunctionFactory_GetsTheLine()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement("content of f0");
        var msgs = new List<ChatMessage> { new(ChatRole.Tool, [new FunctionResultContent("c0", json)]) };
        InsightClient.Stamp(msgs, InsightLevel.Basic);
        Assert.StartsWith("content of f0\n\n" + InsightClient.Marker, ((FunctionResultContent)msgs[0].Contents[0]).Result as string);
    }

    [Fact]
    public void StructuredJsonResult_IsLeftUntouched()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(new { hits = new[] { "a", "b" } });
        var msgs = new List<ChatMessage> { new(ChatRole.Tool, [new FunctionResultContent("c0", json)]) };
        InsightClient.Stamp(msgs, InsightLevel.Basic);
        var result = ((FunctionResultContent)msgs[0].Contents[0]).Result;
        Assert.IsType<System.Text.Json.JsonElement>(result);
        Assert.Equal(json.GetRawText(), ((System.Text.Json.JsonElement)result!).GetRawText());
    }

    [Fact]
    public void ToolShare_CountsContentPartText_NotTypeNames()
    {
        AIContent[] parts = [new TextContent(new string('t', 300))];
        var msgs = new List<ChatMessage> { new(ChatRole.User, new string('u', 100)), new(ChatRole.Tool, [new FunctionResultContent("c", parts)]) };
        Assert.Equal(75, ContextGauge.ToolShare(msgs), 1);
    }

    [Fact]
    public void UserPrompt_GetsTheLineAsAnExtraTextPart()
    {
        var user = new ChatMessage(ChatRole.User, "do the thing");
        InsightClient.Stamp([user], InsightLevel.Basic);
        Assert.Equal(2, user.Contents.Count);
        Assert.Equal("do the thing", ((TextContent)user.Contents[0]).Text);
        Assert.Contains(InsightClient.Marker, ((TextContent)user.Contents[1]).Text);
    }

    [Fact]
    public void Line_Basic_And_Full()
    {
        string basic = ContextGauge.Line(InsightLevel.Basic, 46_700, 200_000, null)!;
        Assert.Equal("[mux context: 46.7k/200k (23%), auto-compact at 200k]", basic);

        var msgs = new List<ChatMessage> { new(ChatRole.User, new string('u', 100)), ToolResult("c", new string('t', 300)) };
        string full = ContextGauge.Line(InsightLevel.Full, 46_700, 200_000, msgs)!;
        Assert.StartsWith("[mux context: 46.7k/200k (23%), auto-compact at 200k | prev turn +", full);
        Assert.Contains("| tool output 75% |", full);
        Assert.Contains("| compactions ", full);

        Assert.Equal("[mux context: 950, auto-compact off]", ContextGauge.Line(InsightLevel.Basic, 950, 0, null));
        Assert.Null(ContextGauge.Line(InsightLevel.Off, 1, 1, null));
    }

    [Fact]
    public void PrevTurn_CountsTheFirstTurn_AndShrinkAsZero()
    {
        // Live dogfood: turn 1 grew 0 -> 32.8k but the next prompt showed "prev turn +0".
        ContextGauge.PromptStarted(0);
        ContextGauge.PromptStarted(32_860);
        Assert.Contains("| prev turn +32.9k |", ContextGauge.Line(InsightLevel.Full, 32_860, 200_000, []));
        ContextGauge.PromptStarted(20_000);   // compaction/drop shrank the context
        Assert.Contains("| prev turn +0 |", ContextGauge.Line(InsightLevel.Full, 20_000, 200_000, []));
    }

    [Theory]
    [InlineData("off", 0)]
    [InlineData("BASIC", 1)]
    [InlineData(" full ", 2)]
    [InlineData("max", 3)]
    public void Levels_Parse(string raw, int expected)
    {
        Assert.True(InsightLevels.TryParse(raw, out var level));
        Assert.Equal((InsightLevel)expected, level);
    }

    [Fact]
    public void Levels_RejectUnknown_And_ConfigDefaultsOff()
    {
        Assert.False(InsightLevels.TryParse("verbose", out _));
        var cfg = JsonSerializer.Deserialize<AppConfig>("{}")!;
        Assert.Equal("off", cfg.Insight.Level);
        var round = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { Insight = { Level = "full" } }))!;
        Assert.Equal("full", round.Insight.Level);
    }

    [Fact]
    public void Palette_OffersInsightInBothScopes()
    {
        var entries = TuiCommands.All.Where(e => e.Cmd.StartsWith("/insight", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, entries.Count);
        Assert.All(entries, e => Assert.Equal(TuiCommands.Scope.Both, e.Scope));
        Assert.True(TuiCommands.TakesArgument("/insight"));
    }

    [Fact]
    public void PromptNote_NamesTheMarker()
    {
        Assert.Contains(InsightClient.Marker, InsightPrompt.Note(InsightLevel.Full));
    }

    /// <summary>
    /// The cache invariant, end to end through the real agent framework: every stamped line the model saw is
    /// committed to the session verbatim, and each message carries exactly one line.
    /// </summary>
    [Fact]
    public async Task Session_CommitsExactlyWhatTheModelWasSent()
    {
        var model = new RecordingModel();
        var tool = AIFunctionFactory.Create((string path) => "content of " + path, "read");
        var agent = model.AsBuilder().UseFunctionInvocation()
            .Use(inner => new InsightClient(inner, () => InsightLevel.Basic)).Build()
            .AsAIAgent(new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [tool] } });
        var session = await agent.CreateSessionAsync();
        await foreach (var _ in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "read two")], session)) { }

        Assert.True(session.TryGetInMemoryChatHistory(out var history));
        var saved = history!.Select(Text).ToList();
        Assert.Equal(3, model.LastSeen.Count(t => t.Contains(InsightClient.Marker)));   // prompt + 2 tool results
        foreach (string sent in model.LastSeen) Assert.Contains(sent, saved);            // saved == sent
        Assert.All(saved, t => Assert.True(CountOf(t, InsightClient.Marker) <= 1));
    }

    private static string Text(ChatMessage m) => string.Concat(m.Contents.Select(c =>
        c is FunctionResultContent r ? r.Result?.ToString() : c is TextContent t ? t.Text : ""));

    private static int CountOf(string s, string part) => (s.Length - s.Replace(part, "").Length) / part.Length;

    private sealed class RecordingModel : IChatClient
    {
        public List<string> LastSeen = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var list = messages.ToList();
            LastSeen = list.Select(Text).ToList();
            int results = list.Count(x => x.Role == ChatRole.Tool);
            if (results < 2)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent($"c{results}", "read", new Dictionary<string, object?> { ["path"] = $"f{results}" })]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "DONE");
        }

        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }
}
