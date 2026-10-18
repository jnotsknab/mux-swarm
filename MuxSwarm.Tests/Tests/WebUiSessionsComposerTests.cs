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

    [Fact]
    public void Composer_Collapses_AndComesBackForQuestions()
    {
        Assert.Contains("document.body.classList.toggle('mux-composer-collapsed', cst.collapsed);", Html);
        Assert.Contains("e.detail.type === 'prompt' && cst.collapsed) setCollapsed(false, true);", Html);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", Html);
    }

    [Fact]
    public void SessionActions_SendExistingCommands_AndConfirmWipe()
    {
        Assert.Contains("['Compact', '/compact', true], ['Undo last exchange', '/undo', true], ['Retry last message', '/retry', true]", Html);
        Assert.Contains("if (a[1] === '/wipe' && !window.confirm(", Html);
    }

    [Fact]
    public void Rail_IsResizable_WithPinsAndCurrentSession()
    {
        Assert.Contains("Math.max(220, Math.min(520,", Html);
        Assert.Contains("function togglePin(n)", Html);
        Assert.Contains("if (c === '/resume' && w[1]) { current = w[1]; render(); }", Html);
    }
}
