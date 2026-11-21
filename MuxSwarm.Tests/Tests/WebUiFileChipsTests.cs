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
    }

    [Fact]
    public void Chips_ShowTheFileName_AndCopyTheFullPath()
    {
        Assert.Contains("frag.appendChild(chip(m, m.name));", Html); // never the raw path, even in inline code
        Assert.Contains("data-act=\"copy\" title=\"Copy path\"", Html);
        Assert.Contains("if (what === 'copy') { copyText(path); return; }", Html);
        Assert.DoesNotContain("/api/file/reveal", Html);           // the endpoint is gone (W11)
    }

    [Fact]
    public void CopyingAReply_GivesThePath_NotTheChipLabel()
    {
        // /copy and a mouse selection read the chip back as its data-path; the action glyphs are never selectable.
        Assert.Contains("function plainText(node, ws)", Html);
        Assert.Contains("ch.replaceWith(document.createTextNode(ch.dataset.path || ''))", Html);
        Assert.Contains("document.addEventListener('copy', function(e)", Html);
        Assert.Contains("window.MuxFileChips.plainText(body)", Html);
        // The clone keeps the source block's white-space (tool output / bubbles are pre-wrap), so line breaks survive.
        Assert.Contains("c.style.whiteSpace = ws || (node.isConnected ? getComputedStyle(node).whiteSpace : '') || 'normal';", Html);
        Assert.Contains("plainText(box, anc ? getComputedStyle(anc).whiteSpace : '')", Html);
        Assert.Contains("user-select: none; }", Html[Html.IndexOf(".mux-fchip .mux-fchip-act {", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Preview_RunsFileContentWithoutTheAppsOrigin()
    {
        // A chip click opens agent-written files: an HTML file renders in an opaque-origin iframe and an SVG as <img>,
        // so neither can reach the app's storage, token or API.
        int a = Html.IndexOf("function renderIframe(src, opts)", StringComparison.Ordinal);
        var iframe = Html[a..Html.IndexOf("body.appendChild(f);", a, StringComparison.Ordinal)];
        Assert.Contains("f.sandbox = 'allow-scripts allow-forms allow-popups allow-modals allow-downloads' + (opts.srcdoc ? '' : ' allow-same-origin');", iframe);
        Assert.DoesNotContain("allow-popups allow-same-origin allow-modals", iframe);
        int s = Html.IndexOf("} else if (kind === 'svg') {", StringComparison.Ordinal);
        var svg = Html[s..Html.IndexOf("} else if (kind === 'video') {", s, StringComparison.Ordinal)];
        Assert.Contains("document.createElement('img')", svg);
        Assert.DoesNotContain("innerHTML = txt", svg);
    }

    [Fact]
    public void Chips_CanBeTurnedOff() =>
        Assert.Contains("toggleRow('fileChips'", Html);
}
