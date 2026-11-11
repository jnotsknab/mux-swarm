using System.Reflection;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

// /agent <name>: docked completion (Tab) + best-match choice for a name that isn't exact.
public class AgentCompleteTests
{
    private static readonly string[] Names =
        ["MuxAgent", "WebAgent", "BrowserAgent", "CodeAgent", "MemoryAgent", "DataAnalysisAgent", "SecretaryAgent", "DocumentationAgent", "PentestAgent"];

    [Theory]
    [InlineData("CodeAgent", "CodeAgent")]        // exact
    [InlineData("codeagent", "CodeAgent")]        // case
    [InlineData("code", "CodeAgent")]             // the "Agent" suffix is optional
    [InlineData("co", "CodeAgent")]               // prefix
    [InlineData("doc", "DocumentationAgent")]
    [InlineData("analysis", "DataAnalysisAgent")] // substring
    [InlineData("dta", "DataAnalysisAgent")]      // letters in order
    [InlineData("coed", "CodeAgent")]             // typo (edit distance)
    [InlineData("brwoser", "BrowserAgent")]       // transposition
    [InlineData("pentets agent", "PentestAgent")]
    [InlineData("muxagnet", "MuxAgent")]          // typo across the "agent" suffix
    [InlineData("codeagnet", "CodeAgent")]
    public void Best_PicksTheClosestAgent(string query, string expected) =>
        Assert.Equal(expected, AgentMatch.Best(query, Names));

    [Theory]
    [InlineData("zzz")]
    [InlineData("kubernetes")]
    public void Best_IsNull_WhenNothingIsClose(string query) => Assert.Null(AgentMatch.Best(query, Names));

    [Fact]
    public void Rank_OrdersTiers_PrefixBeforeSubstringBeforeLetters()
    {
        var ranked = AgentMatch.Rank("m", Names);
        Assert.Equal("MuxAgent", ranked[0]);       // prefix "m", shortest name first
        Assert.Equal("MemoryAgent", ranked[1]);
        Assert.Equal(Names, AgentMatch.Rank("", Names));   // empty filter keeps the configured order
    }

    private sealed class Terminal : ITuiTerminal
    {
        public int Width => 100;
        public int Height => 30;
        public void Write(string text) { }
        public void Flush() { }
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tab_CompletesTheAgentName_AndThePreviewIsDocked(bool frame)
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: frame);
        driver.SetAgentsCatalog(Names.Select(n => (n, n + " role")).ToList());
        var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", Flags)!.GetValue(driver)!;
        typeof(TuiDriver).GetField("_inInput", Flags)!.SetValue(driver, true);   // the prompt is up
        editor.SetBuffer("/agent code");
        Assert.True(editor.IsAgentFilter);
        var live = driver.BuildLiveFrame(98).Select(TuiMarkup.Plain).ToList();
        Assert.Contains(live, l => l.Contains("agents") && l.Contains("(1)"));
        Assert.Contains(live, l => l.Contains("CodeAgent") && l.Contains("CodeAgent role"));
        typeof(TuiDriver).GetMethod("AcceptCompletion", Flags)!.Invoke(driver, null);
        Assert.Equal("/agent CodeAgent", editor.Buffer);
    }

    [Fact]
    public void BareAgent_KeepsTheCommandPalette_AndTabLeavesASpace()
    {
        var editor = new LineEditor();
        editor.SetBuffer("/agent");
        Assert.False(editor.IsAgentFilter);
        Assert.True(TuiCommands.TakesArgument("/agent"));
        editor.SetBuffer("/agent ");
        Assert.True(editor.IsAgentFilter);
        Assert.Equal("", editor.AgentFilter);
    }

    [Fact]
    public void AgentsPreview_NoMatch_SaysSo_AndFitsTheWidth()
    {
        var agents = Names.Select(n => (n, "a long description that keeps going and going past the edge")).ToList();
        Assert.Contains(TuiComponents.AgentsPreview("zzz", agents, 60).Select(TuiMarkup.Plain), l => l.Contains("no agent matches 'zzz'"));
        foreach (var row in TuiComponents.AgentsPreview("", agents, 60, 2))
            Assert.InRange(TuiMarkup.MarkupWidth(row), 0, 60);
    }

    /// <summary>Web palette: "/agent " fetches /api/agents at most once per palette session. An empty or failed
    /// answer used to re-render into another fetch, looping requests back to back while the palette was open.</summary>
    [Fact]
    public void WebPalette_AgentFetch_IsOncePerPaletteSession()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MuxSwarm.csproj"))) dir = dir.Parent;
        string html = File.ReadAllText(Path.Combine(dir!.FullName, "Runtime", "mux-web-app", "index.html"));
        int a = html.IndexOf("const agentMatch = query.match(", StringComparison.Ordinal);
        int b = html.IndexOf("const scored = _modelSlots", a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a, "/agent palette branch not found");
        string branch = html[a..b];
        int guard = branch.IndexOf("if (!_agentPaletteLoad)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "the /api/agents fetch must be guarded by the once-per-palette flag");
        Assert.True(branch.IndexOf("loadModelSlots(", StringComparison.Ordinal) > guard, "fetch must sit inside the guard");
        int close = html.IndexOf("function closePalette()", StringComparison.Ordinal);
        Assert.Contains("_agentPaletteLoad = null", html[close..html.IndexOf('}', close)]);
    }
}
