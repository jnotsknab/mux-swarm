using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Static guards for the workspace panel (browser behavior is covered by the PR's Playwright run).</summary>
public class WebUiWorkspaceTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void Workspace_DefaultsToTheFloatingPanel()
    {
        Assert.Contains("window.MuxWorkspace", Html);
        Assert.Contains("{ docked: false, follow: false, width: 0 }", Html); // opt-in: existing float stays the default
    }

    [Fact]
    public void EventTap_FiresAfterTheTranscriptHandler()
    {
        int handled = Html.IndexOf("try { _ev = JSON.parse(line); handleEvent(_ev); }", StringComparison.Ordinal);
        int tap = Html.IndexOf("new CustomEvent('mux:ws-event'", StringComparison.Ordinal);
        Assert.True(handled > 0 && tap > handled);
    }
}
