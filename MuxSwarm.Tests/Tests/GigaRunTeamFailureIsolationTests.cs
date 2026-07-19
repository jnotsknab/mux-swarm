using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MuxSwarm.Utils;
using MuxSwarm.Utils.Teams;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// run_team failure isolation (giga mode): one member failing must NOT nuke the batch. The tool
/// surfaces that member's error inline and lets siblings' results land - the same contract
/// delegate_parallel already honored. Regression guard for the all-or-nothing batch bug.
/// </summary>
// Mutates process-wide shared statics (App.SwarmConfig, MultiAgentOrchestrator.Specialists,
// App.McpTools) -> serialize with the other shared-state tests.
[Collection("ConsoleState")]
public class GigaRunTeamFailureIsolationTests : IDisposable
{
    private readonly Dictionary<string, (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def)> _prevSpecialists;
    private readonly SwarmConfig? _prevSwarm;
    private readonly IList<ModelContextProtocol.Client.McpClientTool>? _prevMcp;

    public GigaRunTeamFailureIsolationTests()
    {
        _prevSpecialists = MultiAgentOrchestrator.Specialists;
        _prevSwarm = App.SwarmConfig;
        _prevMcp = App.McpTools;
        App.McpTools = new List<ModelContextProtocol.Client.McpClientTool>();   // spawn_team requires non-null
        GigaMode.Reset();
    }

    public void Dispose()
    {
        MultiAgentOrchestrator.Specialists = _prevSpecialists;
        App.SwarmConfig = _prevSwarm;
        App.McpTools = _prevMcp;
        GigaMode.Reset();
    }

    // Streams a fixed reply (the "good" member).
    private sealed class GoodClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // Throws from the stream (the "bad" member - a provider/rate-limit style failure).
    private sealed class BadClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken ct = default)
            => throw new InvalidOperationException("provider blew up");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
            => throw new InvalidOperationException("provider blew up");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static AIFunction? Find(IList<AITool> tools, string name)
        => tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == name);

    [Fact]
    public async Task RunTeam_OneMemberFails_BatchStillCompletes()
    {
        // Minimal swarm: a lead (singleAgent) + two members.
        App.SwarmConfig = new SwarmConfig
        {
            SingleAgent = new AgentConfig { Name = "MuxAgent", PromptPath = "x", Model = "good" },
            Agents = new List<AgentConfig>
            {
                new() { Name = "GoodAgent", Description = "g", PromptPath = "x", Model = "good" },
                new() { Name = "BadAgent",  Description = "b", PromptPath = "x", Model = "bad"  },
            },
        };

        var models = new Dictionary<string, string>
        {
            ["MuxAgent"] = "good", ["GoodAgent"] = "good", ["BadAgent"] = "bad",
        };
        IChatClient Factory(string model) =>
            model == "bad" ? new BadClient() : new GoodClient();

        var tools = GigaMode.BuildTools(Factory, models, CancellationToken.None);

        var spawn = Find(tools, "spawn_team");
        var run = Find(tools, "run_team");
        Assert.NotNull(spawn);
        Assert.NotNull(run);

        // Materialize the team.
        var spawnRes = await spawn!.InvokeAsync(new AIFunctionArguments
        {
            ["name"] = "t", ["members"] = "GoodAgent,BadAgent",
            ["coordination"] = "fanout", ["persist"] = false,
        });
        Assert.Contains("live", spawnRes?.ToString());

        // spawn_team rebuilds the specialist registry from the on-disk swarm.json (not our in-memory
        // config), so seed the members' specialists with canned agents directly: good streams a reply,
        // bad throws from its stream (the provider-failure case under test).
        MultiAgentOrchestrator.Specialists = new Dictionary<string, (AIAgent, AgentSession, Common.AgentDefinition)>();
        foreach (var (name, client) in new[] { ("GoodAgent", (IChatClient)new GoodClient()), ("BadAgent", (IChatClient)new BadClient()) })
        {
            var def = new Common.AgentDefinition(name, "d", "x", false, t => t);
            var agent = client.AsAIAgent(new ChatClientAgentOptions { Name = name });
            var session = await agent.CreateSessionAsync();
            MultiAgentOrchestrator.Specialists[name] = (agent, session, def);
        }

        // Fire a batch where BadAgent's client throws.
        var assignments = "[{\"agent\":\"GoodAgent\",\"task\":\"say ok\"},{\"agent\":\"BadAgent\",\"task\":\"boom\"}]";
        var res = await run!.InvokeAsync(new AIFunctionArguments
        {
            ["name"] = "giga:t", ["assignments"] = assignments,
        });
        var text = res?.ToString() ?? "";

        // The batch completed (did not throw), the good member's result landed, and the bad
        // member's failure is surfaced inline as an [ERROR ...] line.
        Assert.Contains("BATCH COMPLETED", text);
        Assert.Contains("[ERROR BadAgent]", text);
    }
}
