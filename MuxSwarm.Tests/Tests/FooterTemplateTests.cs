using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Footer template override (extension.json "footer.template"). ConsoleState collection because
// Load() publishes into the process-global footer template.
[Collection("ConsoleState")]
public sealed class FooterTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-footertpl-tests-" + Guid.NewGuid().ToString("N"));

    public FooterTemplateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void AddExtension(string folder, string manifestJson)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), manifestJson);
    }

    private static readonly List<(int Col, int Width, string Id)> Badges = new();

    // A footer with every common chip: two mode badges, timers, meter, cached/sys/tools, calls, model, effort.
    private static string Render(int width = 0) => TuiComponents.Footer(120_000, 200_000, plan: true, ultra: false, psub: true,
        sub: false, effort: "high", modeCycleHint: true, cached: 30_000, sysTokens: 1_000, toolTokens: 2_000,
        sessionElapsed: TimeSpan.FromMinutes(5), giga: false, turnElapsed: TimeSpan.FromSeconds(9), lastTurn: null,
        toolCalls: 3, model: "prov/m1", width: width, pulseFrame: -1, activeMode: "swarm", badges: Badges);

    private static TuiComponents.FooterSegment Seg(string id, string text) => new(id, text);

    [Fact]
    public void Apply_ReordersFilters_KeepsDuplicateIdsInOrder_AndForcesShare()
    {
        var segs = new List<TuiComponents.FooterSegment> { Seg("share", "S"), Seg("mode", "m1"), Seg("mode", "m2"), Seg("context", "c"), Seg("model", "x") };

        var applied = TuiComponents.ApplyFooterTemplate(segs, ["model", "mode"]);

        Assert.Equal(["S", "x", "m1", "m2"], applied.Select(s => s.Markup));
        Assert.Equal(["x", "S"], TuiComponents.ApplyFooterTemplate(segs, ["model", "share"]).Select(s => s.Markup));
        Assert.Same(segs, TuiComponents.ApplyFooterTemplate(segs, null));
    }

    [Fact]
    public void Parse_UnknownIdsAreProblems_AndRepeatsDropped()
    {
        var problems = new List<string>();
        var ids = ExtensionRegistry.ParseFooterTemplate("{Model} | {nope} {effort}{model} text", problems, "ext");
        Assert.Equal(["model", "effort"], ids);
        Assert.Contains(problems, p => p.Contains("unknown segment '{nope}'"));

        problems.Clear();
        Assert.Null(ExtensionRegistry.ParseFooterTemplate("no placeholders", problems, "ext"));
        Assert.Contains(problems, p => p.Contains("names no known segment"));
    }

    [Fact]
    public void Load_PublishesTemplate_BadgesMatchComposedString_AndDisabledRestoresDefault()
    {
        string baseline = Render();
        AddExtension("lean", """{ "footer": { "template": "{effort} {model} {context}" } }""");

        var result = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        Assert.True(Assert.Single(result.Extensions).Footer);
        string line = Render();
        string plain = TuiMarkup.Plain(line);

        Assert.StartsWith("  \u25d0 high \u00b7 m1 \u00b7 ", plain);
        Assert.DoesNotContain("swarm", plain);
        Assert.DoesNotContain("calls", plain);
        foreach (var (col, w, id) in Badges)
            Assert.Equal(id == TuiComponents.BadgeModel ? "m1" : "\u25d0 high", plain.Substring(col, w));
        Assert.Equal(2, Badges.Count);

        ExtensionRegistry.Load(null);
        Assert.Equal(baseline, Render());
    }

    [Fact]
    public void Template_StillDegradesUnderWidthPressure()
    {
        AddExtension("lean", """{ "footer": { "template": "{context} {model} {calls}" } }""");
        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });

        string narrow = TuiMarkup.Plain(Render(40));

        Assert.DoesNotContain("calls", narrow);   // calls drops at level 4, before the meter compacts
        Assert.True(TuiMarkup.Width(narrow) <= 40, narrow);
    }

    [Fact]
    public void TwoExtensions_LaterNameWins_WithAProblem()
    {
        AddExtension("aaa", """{ "footer": { "template": "{model}" } }""");
        AddExtension("bbb", """{ "footer": { "template": "{effort}" } }""");

        var result = ExtensionRegistry.Scan([_root]);

        Assert.Equal(["effort"], result.FooterTemplate);
        Assert.Contains(result.Problems, p => p.Contains("footer template replaces"));
    }
}
