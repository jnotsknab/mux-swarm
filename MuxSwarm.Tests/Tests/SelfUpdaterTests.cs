using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// SelfUpdater pure-logic guards: version comparison, user-owned path exclusion, and platform asset
/// naming. These lock the behavior that keeps an update from clobbering user data or applying the
/// wrong/older release, without touching the network.
/// </summary>
public class SelfUpdaterTests
{
    [Theory]
    [InlineData("v0.12.2", "0.12.1", true)]   // newer patch
    [InlineData("v0.13.0", "0.12.1", true)]   // newer minor
    [InlineData("v1.0.0", "0.12.1", true)]    // newer major
    [InlineData("v0.12.1", "0.12.1", false)]  // identical
    [InlineData("v0.12.0", "0.12.1", false)]  // older
    [InlineData("v0.12.0-alpha", "0.12.1", false)] // older with prerelease suffix
    public void IsNewer_ComparesSemverCore(string latestTag, string current, bool expected)
    {
        Assert.Equal(expected, SelfUpdater.IsNewer(latestTag, current));
    }

    [Theory]
    [InlineData("v0.16.0-nightly.20261012", "0.15.2", false)]                // stable never takes a nightly
    [InlineData("v0.16.0-alpha", "0.15.2", true)]                            // stable/alpha releases still offered
    [InlineData("v0.16.0-nightly.20261013", "0.16.0-nightly.20261012", true)] // a nightly follows nightlies
    [InlineData("v0.16.0-alpha", "0.16.0-nightly.20261012", true)]           // ...and the matching release
    public void IsCandidate_NightlyOnlyForNightlyBuilds(string tag, string current, bool expected)
    {
        Assert.Equal(expected, SelfUpdater.IsCandidate(tag, current));
    }

    [Fact]
    public void PickRelease_SkipsNightliesForStableBuilds()
    {
        string[] newestFirst = ["v0.16.0-nightly.20261012", "v0.15.2-alpha", "v0.15.1-alpha"];
        Assert.Equal("v0.15.2-alpha", SelfUpdater.PickRelease(newestFirst, "0.15.2"));
        Assert.Equal("v0.16.0-nightly.20261012", SelfUpdater.PickRelease(newestFirst, "0.16.0-nightly.20261011"));
    }

    [Fact]
    public void IsNewer_EqualCore_SameTag_IsNotNewer()
    {
        // Identical core AND identical tag text (ignoring a leading v) is NOT an update.
        Assert.False(SelfUpdater.IsNewer("v0.12.1", "0.12.1"));
    }

    [Theory]
    [InlineData("Configs/Config.json", true)]
    [InlineData("Configs/Swarm.json", true)]
    [InlineData("Sessions/2026-01-01_foo/agent.json", true)]
    [InlineData("Teams/myteam/tasks/1.json", true)]
    [InlineData("Context/reflections.json", true)]
    [InlineData("Context/MEMORY.md", true)]
    [InlineData("Context/BRAIN.md", true)]
    [InlineData("MuxSwarm.exe", false)]
    [InlineData("Runtime/mux-web-app/index.html", false)]
    [InlineData("Prompts/Agents/CompanionAgent.md", false)]
    [InlineData("Context/DOCS.md", false)]           // shipped doc, NOT user-owned
    [InlineData("Configs/Config.json.bak", false)]   // not an exact match / prefix
    [InlineData("Skills/bundled/alpaca-trading/SKILL.md", false)]
    public void IsUserOwned_ProtectsUserDataOnly(string relPath, bool expected)
    {
        // Normalize to the running OS separator so the test is platform-agnostic.
        var p = relPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
        Assert.Equal(expected, SelfUpdater.IsUserOwned(p));
    }

    [Fact]
    public void PlatformAssetName_MatchesReleaseNamingScheme()
    {
        var name = SelfUpdater.PlatformAssetName();
        Assert.StartsWith("mux-swarm-", name);
        Assert.True(name.EndsWith(".zip") || name.EndsWith(".tar.gz"));
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? "win-" : (
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX) ? "osx-" : "linux-"), name);
    }
}
