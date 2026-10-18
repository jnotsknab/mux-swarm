using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Static guards for the W15 web UI cleanup (browser behavior is covered by the PR's Playwright run).</summary>
public class WebUiCleanupTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void StatusPill_NoLongerRestartsOnClick_ButRestartStillExists()
    {
        Assert.Contains("statusMenu.classList.toggle('open')", Html);
        Assert.Contains("fetch('/api/restart', { method: 'POST' })", Html);
        Assert.Contains("confirm('Are you sure you want to restart the server?')", Html);
    }

    [Fact]
    public void NodeGraph_DeleteSitsApartFromPing_AndStillConfirms()
    {
        Assert.Contains("mng-row mng-row-quiet", Html);
        Assert.Contains("if (!confirm('Delete this node?')) return;", Html);
    }

    [Fact]
    public void Settings_HaveSearchAndSectionNav() => Assert.Contains("buildSettingsNav(body);", Html);

    [Fact]
    public void Offline_ShowsRetryInsteadOfAnEndlessSkeleton() => Assert.Contains("reach the Mux runtime", Html);
}
