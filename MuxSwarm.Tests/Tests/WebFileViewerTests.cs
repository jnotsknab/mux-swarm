using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Sandbox file viewer uses the Skills / Tools catalog look (shared search head and rows).</summary>
public class WebFileViewerTests
{
    private static string Html()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "Runtime", "mux-web-app", "index.html"));
    }

    [Fact]
    public void FindBar_SharesTheCatalogSearch_WithACount()
    {
        var html = Html();
        Assert.Contains("<div class=\"mux-cat-root mux-files\"><div class=\"mux-cat-head mux-find-bar\">", html);
        Assert.Contains("<input id=\"sbxFindInput\" class=\"mux-cat-search\"", html);
        Assert.Contains("<span class=\"mux-cat-count\" title=\"Items in this folder\">", html);
        Assert.DoesNotContain("'.mux-find-bar input {", html);     // no separate (unthemed) input style
    }

    [Fact]
    public void Rows_AreMonoHairlines_FoldersAccentIcon_AndABreadcrumb()
    {
        var html = Html();
        Assert.Contains(".file-item.dir .icon { color: var(--accent); }", html);
        Assert.DoesNotContain(".file-item.dir { color: var(--blue); }", html);
        Assert.Contains("border-bottom: 1px solid rgba(127,127,127,0.10);", html);
        Assert.Contains("<span class=\"go\">${icons.chevronRight}</span>", html);
        Assert.Contains("chevronRight: `<svg", html);
        Assert.Contains("sidebarContent.querySelectorAll('.file-path [data-path]')", html);
    }
}
