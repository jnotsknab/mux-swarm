using MuxSwarm.Engine;
using MuxSwarm.Engine.Memory;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Every lead session (/stateless, /teams lead, /resume, ACP, --agent) takes a mid-turn steer (Ctrl+N)
/// on its next model call; helper and specialist clients never drain the shared steer queue.</summary>
[Collection("ConsoleState")]
public class LeadSteerAllPathsTests
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
}
