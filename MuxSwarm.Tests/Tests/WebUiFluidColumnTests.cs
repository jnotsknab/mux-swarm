using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Broadsheet reading column grows with the window instead of a fixed 760px.</summary>
public class WebUiFluidColumnTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void Broadsheet_ColumnIsFluid_AndCapped()
    {
        Assert.Contains("--mux-content-max: clamp(760px, 62vw, calc(1180px * var(--ui-font-scale, 1)));", Html);
        Assert.DoesNotContain("--mux-content-max: 760px;", Html);
    }
}
