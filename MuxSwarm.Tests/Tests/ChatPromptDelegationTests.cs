using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

/// <summary>Shipped main-agent persona supports direct work and only available delegation capabilities.</summary>
[Collection("ConsoleState")]
public class ChatPromptDelegationTests
{
    private static string Prompt() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Prompts", "Agents", "chat_prompt.md"));

    [Fact]
    public void DefaultPersona_SupportsDirectWorkAndConditionalDelegation()
    {
        string prompt = Prompt();
        Assert.Contains("lead agent in Mux-Swarm's main agentic interface", prompt);
        Assert.Contains("Execute work directly with your available tools", prompt);
        Assert.Contains("When delegation or coordination tools are available", prompt);
        Assert.Contains("when requested or appropriate", prompt);
        Assert.Contains("If a required tool is unavailable", prompt);
        Assert.Contains("Do not infer tool availability from an interface or mode name", prompt);
        Assert.DoesNotContain("do not delegate", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never expand scope beyond the request", prompt);
        Assert.Contains("No destructive actions without confirmation", prompt);
    }

    [Fact]
    public void WorkflowExecutionStep_AgreesWithPersonaAndKeepsPythonGuidance()
    {
        string step = Assert.Single(Prompt().Split('\n'), line => line.StartsWith("3. EXECUTE"));
        Assert.Contains("Work directly or delegate suitable subtasks", step);
        Assert.Contains("tools available in this session", step);
        Assert.Contains("Run Python in a venv", step);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CombinedRuntimeGuidance_HasNoDefaultPersonaDelegationProhibition(bool ultra, bool autoSubAgents)
    {
        var savedConfig = App.Config;
        var savedInjection = AutoInject.Current;
        try
        {
            App.Config = new AppConfig { Ultra = new UltraConfig { AutoSubAgents = autoSubAgents } };
            AutoInject.Current = AutoInject.Mode.None;
            string combined = PreambleBuilder.Build("MuxAgent", shouldPlan: true, ultra: ultra) + "\r\n\r\n" + Prompt();
            Assert.Equal(ultra && autoSubAgents, combined.Contains("### Aggressive Delegation (Ultra)"));
            Assert.Contains("When delegation or coordination tools are available", combined);
            Assert.DoesNotContain("do not delegate", combined, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("EXECUTE NOTHING UNTIL APPROVED", combined);
        }
        finally { App.Config = savedConfig; AutoInject.Current = savedInjection; }
    }
}
