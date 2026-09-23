using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Proxy;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Offline coverage for the --selftest harness plumbing: selector resolution and the proxy-home
/// isolation override. The checks themselves are end-to-end and run in CI against the published binary.
/// </summary>
public class SelfTestTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("proxy,ALL")]
    public void Resolve_EmptyOrAll_SelectsEveryCheck(string? selector)
    {
        var (checks, unknown) = SelfTest.Resolve(selector);
        Assert.Empty(unknown);
        Assert.Equal(SelfTest.All.Select(c => c.Name), checks.Select(c => c.Name));
    }

    [Fact]
    public void Resolve_NamedChecks_CaseInsensitive_Deduplicated()
    {
        var (checks, unknown) = SelfTest.Resolve(" Proxy , proxy ");
        Assert.Empty(unknown);
        Assert.Equal(new[] { "proxy" }, checks.Select(c => c.Name));
    }

    [Fact]
    public void Resolve_UnknownName_IsReportedNotSkipped()
    {
        var (_, unknown) = SelfTest.Resolve("proxy,nope");
        Assert.Equal(new[] { "nope" }, unknown);
    }

    [Fact]
    public async Task Run_UnknownCheck_ExitsNonZero()
    {
        Assert.Equal(1, await SelfTest.RunAsync("definitely-not-a-check"));
    }

    [Fact]
    public void Checks_HaveUniqueNamesAndDescriptions()
    {
        var names = SelfTest.All.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(SelfTest.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public void ProxyHome_Override_RelocatesEverything_DefaultUnchanged()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "mux-selftest-home");
        Assert.Equal(Path.GetFullPath(scratch), CliProxyManager.ResolveConfigDir(scratch));
        Assert.Equal(Path.GetFullPath(scratch), CliProxyManager.ResolveConfigDir("  " + scratch + "  "));

        // Blank/absent override keeps the long-standing per-user location.
        string def = CliProxyManager.ResolveConfigDir(null);
        Assert.Equal(def, CliProxyManager.ResolveConfigDir("   "));
        Assert.EndsWith(Path.Combine("Mux-Swarm", "cliproxy"), def);
    }
}
