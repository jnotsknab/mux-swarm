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
    public void Follow_NeverRendersActiveFiles_OrStealsFocus()
    {
        int a = Html.IndexOf("<!-- ===== Workspace:", StringComparison.Ordinal), b = Html.IndexOf("<!-- ===== /Workspace", a, StringComparison.Ordinal);
        var ws = Html[a..b];
        // A file the agent writes (maybe a downloaded page) opens as text under Follow; the user's click renders it.
        Assert.Contains("if (auto && (k === 'html' || k === 'svg')) k = 'text';", ws);
        Assert.Contains("if (auto) opts.focus = false;", ws);
        Assert.Contains("open(meta.path, meta.kind, true);", ws);
        Assert.Contains("show(src, opts) { openPanel(opts && opts.focus);", Html);
        Assert.Contains("if (focus !== false) setTimeout(() => urlIn.focus(), 50);", Html);
    }

    [Fact]
    public void DockWidth_IsCappedEverywhereItIsUsed()
    {
        // A width saved on a wide window must not push the chat and composer off a narrower one.
        Assert.Contains("body.mux-ws-docked .app { margin-right: min(var(--mux-workspace-width, 44vw), 80vw, calc(100vw - 420px)); }", Html);
        Assert.Contains("--composer-right-inset: min(var(--mux-workspace-width, 44vw), 80vw, calc(100vw - 420px)); }", Html);
        Assert.DoesNotContain("margin-right: var(--mux-workspace-width, 44vw);", Html);
    }

    [Fact]
    public void EventTap_FiresAfterTheTranscriptHandler()
    {
        int handled = Html.IndexOf("try { _ev = JSON.parse(line); handleEvent(_ev); }", StringComparison.Ordinal);
        int tap = Html.IndexOf("new CustomEvent('mux:ws-event'", StringComparison.Ordinal);
        Assert.True(handled > 0 && tap > handled);
    }
}
