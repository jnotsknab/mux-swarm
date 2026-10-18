using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Static guards for the web UI file chips (browser behavior is covered by the PR's Playwright run).</summary>
public class WebUiFileChipsTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void Chips_UseTheFileApi_AndOnlyConfirmedFiles()
    {
        Assert.Contains("window.MuxFileChips", Html);
        Assert.Contains("fetch('/api/file/meta?path='", Html);        // a chip only appears once the file exists
        Assert.Contains("'/api/file?path=' + encodeURIComponent(path)", Html);
        Assert.Contains("fetch('/api/file/reveal'", Html);
    }

    [Fact]
    public void Chips_CanBeTurnedOff() =>
        Assert.Contains("toggleRow('fileChips'", Html);
}
