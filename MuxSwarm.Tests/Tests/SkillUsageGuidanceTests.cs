using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

/// <summary>Regression coverage for conditional skill discovery and reuse across prompt/tool surfaces.</summary>
[Collection("ConsoleState")]
public class SkillUsageGuidanceTests
{
    /// <summary>The advertised list tool requires both task relevance and an unknown skill name.</summary>
    [Fact]
    public void ListTool_RequiresRelevantTaskAndUnknownName()
    {
        var description = LocalAiFunctions.ListSkillsTool.Description;
        Assert.Contains("task may need a skill", description);
        Assert.Contains("name is not already in context", description);
        Assert.Contains("do not repeat", description);
        Assert.DoesNotContain("Call this first", description);
        Assert.Contains("withDescriptions=true", description);
    }

    /// <summary>Reading by a known name does not require listing or reloading an existing definition.</summary>
    [Fact]
    public void ReadTool_AllowsDirectLookupAndDefinitionReuse()
    {
        var description = LocalAiFunctions.ReadSkillTool.Description;
        Assert.Contains("directly by name", description);
        Assert.Contains("list_skills is not a prerequisite", description);
        Assert.Contains("Reuse a definition already in context", description);
        Assert.Contains("missing (e.g. after compaction)", description);
        Assert.Contains("changed", description);
        Assert.Contains("explicit refresh is requested", description);
        Assert.Contains("Skip skill tools when no skill is relevant", description);
        var parameter = LocalAiFunctions.ReadSkillTool.JsonSchema
            .GetProperty("properties").GetProperty("skillName").GetProperty("description").GetString();
        Assert.Contains("already in context", parameter);
        Assert.DoesNotContain("Call list_skills first", parameter);
    }

    /// <summary>Every runtime registration shares the same policy without changing tool signatures.</summary>
    [Theory]
    [InlineData("Engine/LocalAiFunctions.cs", 1)]
    [InlineData("Engine/SingleAgentOrchestrator.cs", 1)]
    [InlineData("Engine/MultiAgentOrchestrator.cs", 2)]
    [InlineData("Engine/ParallelSwarmOrchestrator.cs", 1)]
    public void ToolRegistrations_UseSharedGuidance(string path, int registrations)
    {
        var source = ReadSource(path);
        Assert.Equal(registrations, Regex.Matches(source,
            "description: SkillUsageGuidance.ListDescription").Count);
        Assert.Equal(registrations, Regex.Matches(source,
            "description: SkillUsageGuidance.ReadDescription").Count);
        Assert.Equal(registrations, Regex.Matches(source,
            "Description\\(SkillUsageGuidance.SkillNameDescription\\)").Count);
        Assert.DoesNotContain("Call this first to discover", source);
        Assert.DoesNotContain("Call list_skills first", source);
    }

    /// <summary>Shipped role prompts do not reintroduce unconditional listing through delegation.</summary>
    [Theory]
    [InlineData("chat_prompt.md")]
    [InlineData("code_agent.md")]
    [InlineData("od.md")]
    [InlineData("od_fast.md")]
    [InlineData("od_inf.md")]
    public void BundledPrompts_UseConditionalDiscoveryAndContextReuse(string name)
    {
        var prompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "Agents", name));
        Assert.Contains("task may need a skill", prompt);
        Assert.Contains("name is not already in context", prompt);
        Assert.Contains("read_skill", prompt);
        Assert.Contains("Reuse", prompt);
        Assert.Contains("definition", prompt);
        Assert.Contains("missing (e.g. after compaction)", prompt);
        Assert.Contains("changed", prompt);
        Assert.Contains("explicit refresh is requested", prompt);
        Assert.Contains("skip skill tools when no skill is relevant", prompt);
        if (name.StartsWith("od", StringComparison.Ordinal))
            Assert.Contains("recipient's own context, not the lead's", prompt);
        Assert.DoesNotContain("Skills check is mandatory", prompt);
        Assert.DoesNotContain("check skills first", prompt);
        Assert.DoesNotContain("Call list_skills;", prompt);
        Assert.DoesNotContain("Before starting any task, check your available skills", prompt);
        Assert.DoesNotContain("shell tool via `list_skills`", prompt);
    }

    /// <summary>Retry hints reuse task-relevant guidance rather than requiring another catalog dump.</summary>
    [Theory]
    [InlineData(typeof(MultiAgentOrchestrator))]
    [InlineData(typeof(ParallelSwarmOrchestrator))]
    public void RetryHints_DoNotForceRediscovery(Type orchestrator)
    {
        var method = orchestrator.GetMethod("InjectRetryHint", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var hint = Assert.IsType<string>(method.Invoke(null, ["retry this task", 2, "test failure"]));
        Assert.Contains("Reuse relevant skill instructions already in context", hint);
        Assert.DoesNotContain("Check available skills before proceeding", hint);
    }

    private static string ReadSource(string relativePath, [CallerFilePath] string testFile = "")
    {
        // Resolve the checked-out source, not an assumed ancestor of redirected build outputs.
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        return File.ReadAllText(Path.Combine(root, relativePath));
    }
}
