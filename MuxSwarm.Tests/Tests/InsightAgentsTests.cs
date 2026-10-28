using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Insight;

namespace MuxSwarm.Tests.Tests;

/// <summary>/insight for specialists, team members and delegated sub-agents: their OWN numbers, never the lead's.</summary>
// ContextGauge's prompt baseline and drop counter are process-global: serialize with every other class that
// touches console/insight state (a parallel PromptStarted from another class reset the baseline mid-assert).
[Collection("ConsoleState")]
public class InsightAgentsTests
{
    private static ChatMessage ToolResult(string id, string text) => new(ChatRole.Tool, [new FunctionResultContent(id, text)]);

    [Fact]
    public void SubAgentLine_UsesTheRequestSize_AndAutoCompactOffWhenNothingCompacts()
    {
        var msgs = new List<ChatMessage> { new(ChatRole.User, new string('u', 2500)), ToolResult("c", new string('t', 7500)) };
        string line = ContextGauge.SubAgentLine(InsightLevel.Basic, "WebAgent-unregistered", msgs)!;
        Assert.Equal("[mux context: 4k, auto-compact off]", line);   // 10,000 chars / 2.5
    }

    [Fact]
    public void SubAgentLine_UsesTheRegisteredMemberThreshold_Live()
    {
        int threshold = 8_000;
        ContextGauge.SetMemberThreshold(["MemberA-insight-test"], () => threshold);
        var msgs = new List<ChatMessage> { new(ChatRole.User, new string('u', 10_000)) };
        Assert.Equal("[mux context: 4k/8k (50%), auto-compact at 8k]", ContextGauge.SubAgentLine(InsightLevel.Basic, "membera-insight-test", msgs));
        threshold = 200_000;   // read live, e.g. after a config change
        Assert.Contains("/200k (2%)", ContextGauge.SubAgentLine(InsightLevel.Basic, "MemberA-insight-test", msgs));
    }

    [Fact]
    public void SubAgentLine_Full_ShowsAgentAndToolShare_NotLeadOnlyFigures()
    {
        var msgs = new List<ChatMessage> { new(ChatRole.User, new string('u', 100)), ToolResult("c", new string('t', 300)) };
        string full = ContextGauge.SubAgentLine(InsightLevel.Full, "CodeAgent", msgs)!;
        Assert.EndsWith("| agent CodeAgent | tool output 75%]", full);
        Assert.DoesNotContain("prev turn", full);
        Assert.DoesNotContain("compactions", full);
        Assert.DoesNotContain("dropped", full);
        Assert.Null(ContextGauge.SubAgentLine(InsightLevel.Off, "CodeAgent", msgs));
    }

    [Fact]
    public void SubAgentStamp_LeavesTheLeadPromptBaselineAlone()
    {
        // A sub-agent's turn start must not reset the lead's "prev turn" baseline.
        ContextGauge.PromptStarted(0);
        ContextGauge.PromptStarted(30_000);
        InsightClient.Stamp([new ChatMessage(ChatRole.User, "sub task")], InsightLevel.Full, subAgent: "WebAgent");
        Assert.Contains("| prev turn +30k |", ContextGauge.Line(InsightLevel.Full, 30_000, 200_000, []));
    }

    [Fact]
    public void SubAgentStamp_GoesOnTheToolResult_WithTheSubAgentsNumbers()
    {
        var msgs = new List<ChatMessage> { new(ChatRole.User, "task"), ToolResult("c", new string('t', 5000)) };
        InsightClient.Stamp(msgs, InsightLevel.Basic, subAgent: "WebAgent");
        string result = ((FunctionResultContent)msgs[1].Contents[0]).Result!.ToString()!;
        Assert.EndsWith("[mux context: 2k, auto-compact off]", result);
    }

}

/// <summary>End to end through the real BuildSpecialists: specialists get the wrapper + note only when insight is on.</summary>
[Collection("ConsoleState")]
public class InsightSpecialistBuildTests : IDisposable
{
    private readonly AppConfig _config = App.Config;
    private readonly Dictionary<string, (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def)> _specialists = MultiAgentOrchestrator.Specialists;
    private readonly bool _stdio = MuxConsole.StdioMode;
    private readonly TextWriter _out = Console.Out;
    private static readonly System.Reflection.FieldInfo AgentDefsField =
        typeof(MultiAgentOrchestrator).GetField("AgentDefs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    private readonly object? _agentDefs = AgentDefsField.GetValue(null);

    public InsightSpecialistBuildTests()
    {
        MuxConsole.StdioMode = true;
        Console.SetOut(new StringWriter());
        AgentDefsField.SetValue(null, new List<Common.AgentDefinition>
        {
            new("WebAgent", "research", "unused-prompt.md", false, tools => tools),
        });
    }

    public void Dispose()
    {
        App.Config = _config; MultiAgentOrchestrator.Specialists = _specialists; AgentDefsField.SetValue(null, _agentDefs);
        MuxConsole.StdioMode = _stdio; Console.SetOut(_out);
    }

    private sealed class SeenModel : IChatClient
    {
        public readonly List<string> Seen = new();
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m,
            ChatOptions? o = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            Seen.Add((o?.Instructions ?? "") + "\n---\n" + string.Join("\n", m.Select(x => x.Text)));
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private async Task<string> RunOne(string level)
    {
        App.Config = new AppConfig { Insight = { Level = level } };
        var model = new SeenModel();
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Orchestrator"] = "m" };
        await MultiAgentOrchestrator.BuildSpecialists(models, _ => model.AsBuilder().UseFunctionInvocation().Build(), new List<AITool>());
        var (agent, session, _) = MultiAgentOrchestrator.Specialists.Values.First();
        await foreach (var _ in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "do the sub task")], session)) { }
        return model.Seen.Last();
    }

    [Fact]
    public async Task Specialist_GetsTheLineAndNote_WhenOn_AndNothing_WhenOff()
    {
        string on = await RunOne("basic");
        Assert.Contains("## Context awareness (/insight basic)", on);
        Assert.Contains("do the sub task\n\n" + InsightClient.Marker, on);
        Assert.Contains("auto-compact off]", on);

        string off = await RunOne("off");
        Assert.DoesNotContain("Context awareness", off);
        Assert.DoesNotContain(InsightClient.Marker, off);
    }
}
