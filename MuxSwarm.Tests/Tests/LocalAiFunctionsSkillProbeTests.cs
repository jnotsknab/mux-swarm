using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Utils;
using Microsoft.Extensions.AI;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Coverage for the list_skills probe layers: names-only by default (cheap), with an optional
/// withDescriptions flag that restores the name + one-line description dump. read_skill is the
/// full-content path and is unchanged.
/// </summary>
public class LocalAiFunctionsSkillProbeTests
{
    private static async Task<string> CallListSkills(bool? withDescriptions)
    {
        var fn = LocalAiFunctions.ListSkillsTool;
        var dict = new AIFunctionArguments();
        if (withDescriptions is { } wd) dict["withDescriptions"] = wd;
        var res = await fn.InvokeAsync(dict, CancellationToken.None);
        return res?.ToString() ?? string.Empty;
    }

    [Fact]
    public async Task ListSkills_Default_ReturnsNamesWithoutDescriptions()
    {
        SkillLoader.LoadSkills();
        var skills = SkillLoader.GetSkillMetadata();
        if (skills.Count == 0) return; // no bundled skills available in this environment

        var output = await CallListSkills(null);
        var lines = output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        // Names-only: every line is exactly "- {Name}" with no ": description" tail.
        foreach (var line in lines)
        {
            Assert.StartsWith("- ", line);
            var withDesc = skills.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.Description) && line == $"- {s.Name}: {s.Description}");
            Assert.Null(withDesc);
        }
    }

    [Fact]
    public async Task ListSkills_WithDescriptions_IncludesDescription()
    {
        SkillLoader.LoadSkills();
        var skills = SkillLoader.GetSkillMetadata();
        var described = skills.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Description));
        if (described is null) return; // nothing with a description to assert on

        var output = await CallListSkills(true);
        Assert.Contains($"- {described.Name}: {described.Description}", output);
    }
}
