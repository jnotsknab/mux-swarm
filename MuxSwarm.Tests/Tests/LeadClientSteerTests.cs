using Microsoft.Extensions.AI;
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
            Assert.NotNull(App.CreateLeadChatClient("m").GetService(typeof(MidTurnSteerClient)));
            Assert.Null(App.CreateChatClient("m").GetService(typeof(MidTurnSteerClient)));
        }
        finally { App.ActiveProvider = prior; }
    }

    [Fact]
    public void AgentLaunch_PassesTheLeadFactory_ForModelSwitches()
    {
        // The /agent launch must hand ChatAgentAsync a lead factory; without one a /setmodel rebuild
        // falls back to the plain client and Ctrl+N only lands after the turn ends.
        string app = File.ReadAllText(Path.Combine(SourceRoot(), "App.cs"));
        int launch = app.IndexOf("agentHandle.ChatTask = SingleAgentOrchestrator.ChatAgentAsync(", StringComparison.Ordinal);
        Assert.True(launch > 0);
        int end = app.IndexOf(");", app.IndexOf("interactiveHandle: agentHandle", launch, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("leadClientFactory: modelId => CreateLeadChatClient(modelId)", app[launch..end]);
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
