using System.Text.RegularExpressions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Theme-token guard for the web UI: dark-only color literals that broke light themes must not come back.
/// Literals inside <c>var(--x, fallback)</c> and inside the preset table are allowed.
/// </summary>
public class WebUiThemeTokenTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    // White-alpha tints vanish on light themes; zinc literals are dark slabs; the pastels are illegible on light.
    [Theory]
    [InlineData(@"rgba\(\s*255\s*,\s*255\s*,\s*255\s*,")]
    [InlineData(@"rgba\(\s*161\s*,\s*161\s*,\s*170\s*,")]
    [InlineData(@"rgba\(\s*(20|16|36|39|63)\s*,\s*(20|16|36|39|63)\s*,\s*(24|20|42|70)\s*,")]
    [InlineData(@"rgb\(\s*20\s*,\s*20\s*,\s*24\s*\)")]
    [InlineData(@"#(c8a6ff|a3c79c|d2b46a|f0a868|fbbf24|9ca3af)\b")]
    public void NoDarkOnlyLiterals_OutsideFallbacks(string pattern)
    {
        var hits = Regex.Matches(Html, pattern, RegexOptions.IgnoreCase)
            .Where(m => !Regex.IsMatch(Html[Math.Max(0, m.Index - 40)..m.Index], @"var\(--[\w-]+,\s*$"))
            .Where(m => !InThemeDefinitions(m.Index))
            .Select(m => LineOf(m.Index))
            .ToList();
        Assert.True(hits.Count == 0, $"dark-only literal '{pattern}' on lines {string.Join(", ", hits)}");
    }

    [Fact]
    public void LightScheme_FlipsTheTintTokens()
    {
        Assert.Contains(":root[data-mux-scheme=\"light\"] {", Html);
        Assert.Contains("--mux-tint-rgb: 0, 0, 0;", Html);
    }

    [Fact]
    public void Monaco_FollowsTheThemeEvent() =>
        Assert.Contains("window.addEventListener('mux:theme-changed',function(){ applyTheme(); });", Html);

    // The :root color defaults and the preset table define themes; literals there are the point.
    private static bool InThemeDefinitions(int index)
    {
        int rootStart = Html.IndexOf(":root {", StringComparison.Ordinal);
        int rootEnd = Html.IndexOf('}', rootStart);
        int presetStart = Html.IndexOf("const themes = [", StringComparison.Ordinal);
        int presetEnd = Html.IndexOf("];", presetStart, StringComparison.Ordinal);
        return (index > rootStart && index < rootEnd) || (index > presetStart && index < presetEnd);
    }

    private static int LineOf(int index) => Html.AsSpan(0, index).Count('\n') + 1;
}
