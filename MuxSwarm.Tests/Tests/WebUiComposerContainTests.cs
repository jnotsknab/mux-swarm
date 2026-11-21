using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Composer controls stay inside the box when the docked panel / side rail narrow the column.</summary>
public class WebUiComposerContainTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void Controls_WrapInsideTheComposer()
    {
        Assert.Contains("html[data-mux-style=\"broadsheet\"] .input-area .btn-group { align-self: stretch; flex-wrap: wrap; min-width: 0; row-gap: 6px; }", Html);
        Assert.Contains(".input-area .btn-group { align-self: flex-end; flex-wrap: wrap; justify-content: flex-end; min-width: 0; max-width: 100%; }", Html);
        Assert.Contains("body.mux-ws-docked .input-area, body.mux-rail-open.mux-ws-docked .input-area { flex-direction: column; align-items: stretch; }", Html);
    }

    [Fact]
    public void DockedPanel_LeavesTheChatColumnUsable()
    {
        // The dock width is capped so the composer keeps ~420px next to it (and the side rail).
        Assert.Contains("body.mux-ws-docked { --mux-ws-dock: max(320px, min(var(--mux-workspace-width, 44vw), calc(100vw - var(--mux-rail-dock, 0px) - 420px), 80vw)); }", Html);
        Assert.Contains("body.mux-ws-docked .input-area { --composer-right-inset: var(--mux-ws-dock); }", Html);
        Assert.Contains("width: var(--mux-ws-dock, var(--mux-workspace-width, 44vw)) !important;", Html);
    }
}
