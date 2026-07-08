using System.IO;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// SkillInstaller normalizer: aligns an installed skill to mux conventions WITHOUT destroying upstream
/// content. Guards the two guarantees -- (1) frontmatter name == install dir, (2) provenance stamped
/// under metadata -- while every other frontmatter field and the entire body survive byte-for-byte.
/// </summary>
public class SkillInstallerNormalizeTests
{
    private static string NewSkillDir(string dirName, string skillMd)
    {
        var root = Path.Combine(Path.GetTempPath(), "mux-skilltest-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMd);
        return dir;
    }

    [Fact]
    public void Normalize_StampsProvenance_PreservesBody()
    {
        var body = "# Title\n\nDo the thing.\n\n## Steps\n1. a\n2. b\n";
        var md = "---\nname: my-skill\ndescription: does a thing\nlicense: Apache-2.0\n---\n" + body;
        var dir = NewSkillDir("my-skill", md);

        SkillInstaller.NormalizeInstalledSkill(dir, "my-skill", "owner/repo",
            "https://github.com/owner/repo/tree/main/skills/my-skill", "owner/repo");

        var outText = File.ReadAllText(Path.Combine(dir, "SKILL.md")).Replace("\r\n", "\n");
        Assert.Contains("name: my-skill", outText);
        Assert.Contains("description: does a thing", outText);   // preserved
        Assert.Contains("license: Apache-2.0", outText);          // preserved
        Assert.Contains("metadata:", outText);
        Assert.Contains("mux_source_repo: owner/repo", outText);
        Assert.Contains("mux_source_url:", outText);
        Assert.Contains("mux_installed_at:", outText);
        Assert.Contains(body.TrimEnd(), outText);                 // body intact
    }

    [Fact]
    public void Normalize_FixesNameToMatchDir()
    {
        // Upstream name disagrees with the install dir -> the spec's hard rule forces dir name.
        var md = "---\nname: wrong-name\ndescription: x\n---\n\nBody.\n";
        var dir = NewSkillDir("correct-name", md);

        SkillInstaller.NormalizeInstalledSkill(dir, "correct-name", "o/r", "https://github.com/o/r", "o/r");

        var outText = File.ReadAllText(Path.Combine(dir, "SKILL.md")).Replace("\r\n", "\n");
        Assert.Contains("name: correct-name", outText);
        Assert.DoesNotContain("name: wrong-name", outText);
    }

    [Fact]
    public void Normalize_PreservesExistingMetadataAndAppendsMux()
    {
        var md = "---\nname: s\ndescription: d\nmetadata:\n  author: someone\n  version: \"2.0\"\n---\n\nBody.\n";
        var dir = NewSkillDir("s", md);

        SkillInstaller.NormalizeInstalledSkill(dir, "s", "o/r", "https://github.com/o/r", "o/r");

        var outText = File.ReadAllText(Path.Combine(dir, "SKILL.md")).Replace("\r\n", "\n");
        Assert.Contains("author: someone", outText);   // existing metadata untouched
        Assert.Contains("version: \"2.0\"", outText);
        Assert.Contains("mux_source_repo: o/r", outText); // appended under the same block
    }

    [Fact]
    public void Normalize_PreservesClaudeCodeSupersetFields()
    {
        // Non-portable Claude Code fields must pass through verbatim (no key-separator normalization).
        var md = "---\nname: s\ndescription: d\ndisable-model-invocation: true\nwhen_to_use: on foo\nallowed-tools: Bash(git:*) Read\n---\n\nBody.\n";
        var dir = NewSkillDir("s", md);

        SkillInstaller.NormalizeInstalledSkill(dir, "s", "o/r", "https://github.com/o/r", "o/r");

        var outText = File.ReadAllText(Path.Combine(dir, "SKILL.md")).Replace("\r\n", "\n");
        Assert.Contains("disable-model-invocation: true", outText);
        Assert.Contains("when_to_use: on foo", outText);
        Assert.Contains("allowed-tools: Bash(git:*) Read", outText);
    }
}
