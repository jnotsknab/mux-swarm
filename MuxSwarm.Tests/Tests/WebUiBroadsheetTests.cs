using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Default web theme (Broadsheet) and its bundled reading font. Saved themes must keep winning; the font
/// must ship offline with its license.
/// </summary>
public class WebUiBroadsheetTests
{
    private static readonly string Root = RepoRoot();
    private static readonly string Html = File.ReadAllText(Path.Combine(Root, "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void Broadsheet_IsTheDefault_AndClassicStaysAvailable()
    {
        Assert.Contains("let currentThemeId = 'broadsheet';", Html);
        Assert.Contains("{ id: 'broadsheet', name: 'Broadsheet'", Html);
        Assert.Contains("{ id: 'zinc', name: 'Classic'", Html); // id unchanged: saved 'zinc' prefs still resolve
        Assert.Contains("const saved = JSON.parse(localStorage.getItem('mux-theme-state'));", Html);
    }

    [Fact]
    public void BuiltIns_ShareTheBroadsheetLayout_ExceptClassicAndExtensions()
    {
        Assert.Contains("_root.dataset.muxStyle = (baseTheme.ext || baseTheme.id === 'zinc') ? 'classic' : 'broadsheet';", Html);
        Assert.DoesNotContain("html[data-mux-theme=\"broadsheet\"]", Html); // layout keys on the style, not one theme id
        var block = Html[Html.IndexOf("html[data-mux-style=\"broadsheet\"] {", StringComparison.Ordinal)..][..200];
        Assert.Contains("--font-sans: var(--font-serif);", block);                  // whole UI in the reading serif
    }

    [Fact]
    public void MonospacePick_OnlySetsProseInTheClassicLayout() =>
        Assert.Contains(":root[data-mux-style=\"classic\"] { ' + _d(classicOnly)", Html); // Broadsheet prose stays the reading font

    [Fact]
    public void BroadsheetComposer_StacksTheTextareaAboveTheControls() =>
        Assert.Contains("flex-direction: column; align-items: stretch; gap: 6px;", Html);

    [Fact]
    public void Newsreader_IsBundled_WithItsLicense()
    {
        var fonts = Path.Combine(Root, "Runtime", "mux-web-app", "fonts");
        foreach (var f in new[] { "newsreader-latin.woff2", "newsreader-latin-ext.woff2", "newsreader-italic-latin.woff2", "newsreader-italic-latin-ext.woff2" })
            Assert.True(new FileInfo(Path.Combine(fonts, f)).Length > 10_000, f);
        Assert.Contains("SIL Open Font License", File.ReadAllText(Path.Combine(fonts, "Newsreader-OFL.md")));
        Assert.Contains("font-family: 'Newsreader'", File.ReadAllText(Path.Combine(fonts, "fonts.css")));
        Assert.Contains("'Newsreader'", Html[Html.IndexOf("BUNDLED_FONTS", StringComparison.Ordinal)..][..120]);
    }
}
