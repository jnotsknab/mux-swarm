using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>/api/status names the active single agent + model; the web picker reflects it.</summary>
public class WebUiActiveAgentTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    private static SwarmConfig Config() => new()
    {
        SingleAgent = new AgentConfig { Name = "MuxAgent", Model = "opus-x" },
        Agents = [new AgentConfig { Name = "CodeAgent", Model = "code-y" }],
    };

    [Fact]
    public void ActiveSingleAgent_DefaultsToTheConfiguredSingleAgent()
    {
        var saved = SingleAgentOrchestrator.AgentDef;
        try
        {
            SingleAgentOrchestrator.AgentDef = null;
            Assert.Equal(("MuxAgent", "opus-x"), ServeMode.ActiveSingleAgent(Config()));
            Assert.Equal((null, null), ServeMode.ActiveSingleAgent(new SwarmConfig()));
        }
        finally { SingleAgentOrchestrator.AgentDef = saved; }
    }

    [Fact]
    public void ActiveSingleAgent_FollowsASwap_ToThatAgentsModel()
    {
        var saved = SingleAgentOrchestrator.AgentDef;
        try
        {
            SingleAgentOrchestrator.AgentDef = new Common.AgentDefinition("CodeAgent", "", "", false, t => t);
            Assert.Equal(("CodeAgent", "code-y"), ServeMode.ActiveSingleAgent(Config()));
        }
        finally { SingleAgentOrchestrator.AgentDef = saved; }
    }

    [Fact]
    public void Picker_SeedsTheActiveAgentFromStatus_AndListsTheSingleAgentRow()
    {
        Assert.Contains("setActiveAgent(st.agent, st.model);", Html);
        Assert.Contains("slots = [{ name: _activeAgentName, model: _activeAgentModel }].concat(slots);", Html);
        Assert.Contains("best = _activeAgentModel;", Html);
    }
}
