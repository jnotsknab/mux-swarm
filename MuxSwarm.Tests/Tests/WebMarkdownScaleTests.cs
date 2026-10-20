using System.Text.RegularExpressions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Web UI markdown follows the font-size setting: no fixed pixel sizes inside renderMarkdown.</summary>
public class WebMarkdownScaleTests
{
    private static string RenderMarkdownSource()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) d = d.Parent;
        var html = File.ReadAllText(Path.Combine(d!.FullName, "Runtime", "mux-web-app", "index.html"));
        int a = html.IndexOf("function renderMarkdown(raw)", StringComparison.Ordinal);
        int b = html.IndexOf("function inlineMd(s)", a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a, "renderMarkdown not found");
        return html[a..b];
    }

    [Fact]
    public void Headings_AndTables_UseRelativeSizes()
    {
        var src = RenderMarkdownSource();
        // A px font-size here ignores the user's font-size setting (headings and table rows were 13-20px fixed).
        Assert.Empty(Regex.Matches(src, @"font-size:\s*\d+(\.\d+)?px"));
        Assert.Contains("let sizes  = ['1.3em'", src);
    }
}
