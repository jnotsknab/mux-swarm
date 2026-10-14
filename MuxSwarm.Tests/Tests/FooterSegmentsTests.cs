using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Golden characterization of <c>TuiComponents.Footer</c>:
/// a fixed input matrix is rendered per terminal width and hashed (markup line + badge extents).
/// The hashes were captured from the pre-refactor implementation, so any byte-level change to
/// the footer (chips, order, separators, degradation, badge columns) fails here.
/// </summary>
[Collection("ConsoleState")]
public class FooterSegmentsTests
{
    private static readonly int[] Widths = { 0, 40, 60, 80, 100, 120, 160, 220 };

    // Expected SHA-256 (first 16 hex chars) of the rendered matrix, per width.
    private static readonly Dictionary<int, string> Golden = new()
    {
        [0] = "350F31C754DE4730",
        [40] = "5F1BD05FCD87896A",
        [60] = "5512BCC03BA25948",
        [80] = "7B68EF08333A6E09",
        [100] = "68A8DFF25CE4DCED",
        [120] = "606B6EDAEF405111",
        [160] = "F27532AC378FF2AC",
        [220] = "350F31C754DE4730",
    };

    private static string RenderMatrix(int width)
    {
        var modes = new (bool Plan, bool Ultra, bool Psub, bool Sub, bool Giga, string? Active)[]
        {
            (false, false, false, false, false, null), (true, false, true, true, false, "swarm"),
            (true, true, false, false, false, "pswarm"), (false, false, false, false, true, null),
            (false, false, false, true, false, "SWARM"),
        };
        var contexts = new (uint Tokens, uint Threshold, uint Cached)[]
        {
            (0, 0, 0), (1234, 0, 0), (50_000, 200_000, 0), (120_000, 200_000, 30_000), (190_000, 200_000, 5_500),
        };
        var extras = new (uint Sys, uint Tools, uint Calls)[] { (0, 0, 0), (12_000, 8_500, 42) };
        var timers = new (TimeSpan? Session, TimeSpan? Turn, TimeSpan? Last)[]
        {
            (null, null, null),
            (TimeSpan.FromSeconds(30), null, TimeSpan.FromSeconds(12)),
            (new TimeSpan(1, 4, 0), new TimeSpan(0, 2, 5), null),
            (TimeSpan.FromMinutes(5), null, TimeSpan.FromSeconds(75)),
        };
        string?[] models = { null, "openrouter/anthropic/claude-opus-5-5-very-long-model-name" };
        string?[] efforts = { null, "xhigh" };

        var sb = new StringBuilder();
        var badges = new List<(int Col, int Width, string Id)>();
        foreach (var m in modes)
        foreach (var c in contexts)
        foreach (var x in extras)
        foreach (var t in timers)
        foreach (var model in models)
        foreach (var effort in efforts)
        foreach (bool hint in new[] { false, true })
        foreach (int pulse in new[] { -1, 3 })
        {
            string line = TuiComponents.Footer(c.Tokens, c.Threshold, m.Plan, m.Ultra, m.Psub, m.Sub, effort, hint,
                c.Cached, x.Sys, x.Tools, t.Session, m.Giga, t.Turn, t.Last, x.Calls, model, width, pulse, m.Active, badges);
            sb.Append(line).Append('|');
            foreach (var b in badges) sb.Append(b.Col).Append(',').Append(b.Width).Append(',').Append(b.Id).Append(';');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Hash(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    private static T WithPinnedState<T>(Func<T> body)
    {
        var prevTheme = Theme.Active;
        var prevCulture = CultureInfo.CurrentCulture;
        try
        {
            Theme.Set(Theme.Default);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            return body();
        }
        finally { Theme.Set(prevTheme); CultureInfo.CurrentCulture = prevCulture; }
    }

    [Fact]
    public void Footer_Matrix_IsByteIdenticalToGolden()
    {
        var actual = WithPinnedState(() => Widths.ToDictionary(w => w, w => Hash(RenderMatrix(w))));
        string report = string.Join(", ", actual.Select(kv => $"[{kv.Key}] = \"{kv.Value}\""));
        foreach (int w in Widths)
            Assert.True(Golden[w] == actual[w], $"footer output changed at width {w}. actual: {report}");
    }

    [Fact]
    public void FooterSegments_FullInput_HasStableIdsInRenderOrder()
    {
        var segs = TuiComponents.FooterSegments(120_000, 200_000, plan: true, ultra: false, psub: true, sub: false,
            effort: "high", modeCycleHint: true, cached: 30_000, sysTokens: 1_000, toolTokens: 2_000,
            sessionElapsed: TimeSpan.FromMinutes(5), giga: false, turnElapsed: TimeSpan.FromSeconds(9), lastTurn: null,
            toolCalls: 3, model: "prov/m1", pulseFrame: -1, activeMode: "swarm");
        Assert.Equal(
            new[] { "orchestration", "mode", "mode", "turn", "session", "context", "context", "context",
                    "cached", "sys", "tools", "calls", "model", "effort", "hint" },
            segs.Select(s => s.Id));
        Assert.Equal(TuiComponents.BadgeModel, segs.Single(s => s.Id == "model").BadgeId);
        Assert.Equal(TuiComponents.BadgeEffort, segs.Single(s => s.Id == "effort").BadgeId);
    }

    [Fact]
    public void FooterSegments_DropOrder_MatchesDegradationLevels()
    {
        var segs = TuiComponents.FooterSegments(0, 200_000, false, false, false, false, "high", true, 5_000,
            1_000, 2_000, TimeSpan.FromMinutes(5), false, null, TimeSpan.FromSeconds(9), 3, "m", -1, null);
        int DropOf(string id) => segs.First(s => s.Id == id).DropLevel;
        Assert.Equal(1, DropOf("hint"));
        Assert.Equal(2, DropOf("sys"));
        Assert.Equal(2, DropOf("tools"));
        Assert.Equal(3, DropOf("cached"));
        Assert.Equal(4, DropOf("session"));
        Assert.Equal(4, DropOf("calls"));
        Assert.Equal(5, DropOf("model"));
        Assert.Equal(7, DropOf("turn"));    // idle last-turn chip
        Assert.Equal(7, DropOf("effort"));
        // Exactly one context-meter form is visible at every level.
        for (int lvl = 0; lvl <= 7; lvl++)
            Assert.Single(segs, s => s.Id == "context" && s.VisibleAt(lvl));
    }
}
