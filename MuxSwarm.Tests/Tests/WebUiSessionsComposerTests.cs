using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>/api/status effort field + web UI sessions rail / composer chips (static guards).</summary>
public class WebUiSessionsComposerTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void SetTuiEffort_RecordsTheLiveLabel_WithoutATuiDriver()
    {
        try
        {
            MuxConsole.SetTuiEffort("high");
            Assert.Equal("high", MuxConsole.CurrentEffort);
            MuxConsole.SetTuiEffort(null);
            Assert.Null(MuxConsole.CurrentEffort);
        }
        finally { MuxConsole.SetTuiEffort(null); }
    }

    [Fact]
    public void Composer_UsesExistingCommands_ForEffortAndCompaction()
    {
        Assert.Contains("send('/effort ' + l[0])", Html);
        Assert.Contains("send('/compact')", Html);
        Assert.Contains("send('/resume ' + s.n)", Html);
    }
}
