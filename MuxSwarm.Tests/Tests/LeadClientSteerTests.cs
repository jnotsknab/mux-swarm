using MuxSwarm.Engine;
using MuxSwarm.Engine.Memory;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>The /agent lead keeps mid-turn steering (Ctrl+N) after a model switch rebuilds its client.</summary>
[Collection("ConsoleState")]
public class LeadClientSteerTests
{
    [Fact]
    public void LeadClient_HasTheSteerWrapper_PlainClientDoesNot()
    {
        var prior = App.ActiveProvider;
        try
        {
            App.ActiveProvider = new ProviderConfig { Name = "test-provider", Endpoint = "http://127.0.0.1:9/v1" };
            Assert.NotNull(App.CreateAgentLeadClient("m").GetService(typeof(MidTurnSteerClient)));
            Assert.Null(App.CreateChatClient("m").GetService(typeof(MidTurnSteerClient)));
            Assert.NotNull(App.CreateAgentLeadClient("m").GetService(typeof(MuxSwarm.Engine.Insight.InsightClient)));
        }
        finally { App.ActiveProvider = prior; }
    }

    [Theory]
    [InlineData("agentHandle.ChatTask = SingleAgentOrchestrator.ChatAgentAsync(", "client: CreateAgentLeadClient(singleAgentModel)")]
    [InlineData("client: CreateAgentLeadClient(resumeModel)", "resumedSession: resumeData.Value.data")]
    public void AgentAndResume_UseTheLeadClient_AndPassItForModelSwitches(string start, string inCall)
    {
        // /agent and /resume start with the steer-wrapped lead client and hand ChatAgentAsync the same factory;
        // without it a /setmodel rebuild falls back to the plain client and Ctrl+N only lands after the turn ends.
        string app = File.ReadAllText(Path.Combine(SourceRoot(), "App.cs")).Replace("\r\n", "\n");
        int launch = app.IndexOf(start, StringComparison.Ordinal);
        Assert.True(launch > 0, start);
        string call = app[launch..app.IndexOf(");", launch, StringComparison.Ordinal)];
        Assert.Contains(inCall, call);
        Assert.Contains("leadClientFactory: modelId => CreateAgentLeadClient(modelId)", call);
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
