using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Frame-engine render caching: scroll, NAV exit and expand/collapse reuse per-entry rendered
/// rows instead of re-wrapping history, invalidation still follows width/theme/expansion, and the NAV
/// row renderer emits one SGR per styled run with the same per-cell styling as before.</summary>
[Collection("ConsoleState")]
public class TuiRenderCacheTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 80;
        public int Height { get; set; } = 20;
        public StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    private const BindingFlags PI = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Regex Csi = new("\u001b\\[[0-9;?]*[A-Za-z]");

    /// <summary>Commits <paramref name="count"/> width-dependent entries whose layout records every
    /// width it is rendered at, so tests can count re-renders at the frame width.</summary>
    private static List<int> CommitCounted(TuiDriver d, int count, int linesEach = 3)
    {
        var calls = new List<int>();
        var commit = typeof(TuiDriver).GetMethod("CommitLayout", PI)!;
        for (int i = 0; i < count; i++)
        {
            int id = i;
            Func<int, List<string>> layout = w =>
            {
                calls.Add(w);
                return Enumerable.Range(0, linesEach).Select(k => $"entry {id} line {k}").ToList();
            };
            commit.Invoke(d, new object[] { layout });
        }
        return calls;
    }

    private static List<string> Compose(TuiDriver d) => d.ComposeFrameRows().Select(r => Csi.Replace(r, "")).ToList();
    private static int TotalRows(TuiDriver d) => (int)typeof(TuiDriver).GetMethod("GetFrameTotalRows", PI)!.Invoke(d, new object[] { d.Width })!;
    private static void InvalidateAll(TuiDriver d) => typeof(TuiDriver).GetMethod("InvalidateFrameRowCounts", PI)!.Invoke(d, null);
    private static List<string> EntryRows(TuiDriver d, int index)
    {
        var entry = ((IList)typeof(TuiDriver).GetField("_transcript", PI)!.GetValue(d)!)[index]!;
        return (List<string>)typeof(TuiDriver).GetMethod("RenderEntryRows", PI)!.Invoke(d, new[] { entry, (object)d.Width })!;
    }

    [Fact]
    public void ScrollingBack_SlicesCachedRows_WithoutReRenderingHistory()
    {
        var term = new Terminal();
        var d = new TuiDriver(term, frameEngine: true);
        var calls = CommitCounted(d, 60);
        Compose(d);
        int total = TotalRows(d);
        calls.Clear();
        for (int step = 1; step <= 12; step++)
        {
            d.FrameScrollBy(3);
            var rows = Compose(d).Select(r => r.Trim()).ToList();
            // Window correctness: the bottom transcript row is exactly `scroll` rows above the tail.
            int bottom = total - 1 - 3 * step;
            Assert.Contains($"entry {bottom / 3} line {bottom % 3}", rows);
            int below = bottom + 1;
            Assert.DoesNotContain($"entry {below / 3} line {below % 3}", rows);
            int top = bottom - (rows.Count(r => r.StartsWith("entry ")) - 1);
            Assert.Contains($"entry {top / 3} line {top % 3}", rows);
        }
        Assert.Equal(0, calls.Count(w => w == d.Width));
    }

    [Fact]
    public void ExpandToggle_ReRendersOnlyThatEntry_AndKeepsTheExactTotal()
    {
        var term = new Terminal();
        var d = new TuiDriver(term, frameEngine: true);
        d.SetMouseTrackingPreset("buttons");
        long tick = 1000;
        d.MouseClock = () => tick;
        var calls = CommitCounted(d, 20);
        d.CommitCollapsed("· CodeAgent finished (ctrl+e)", "CodeAgent", string.Join("\n", Enumerable.Range(0, 8).Select(i => $"body {i}")));
        Compose(d);
        int collapsed = TotalRows(d);
        calls.Clear();
        int row = Enumerable.Range(1, term.Height).First(r => d.FrameHitMap!.TryHit(r, 4, out var h, out _, out _) && h.Kind == MouseTargetKind.TranscriptEntry);
        d.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, 4, row, false));
        d.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(0, 4, row, true));
        int expanded = TotalRows(d);
        Assert.True(expanded > collapsed);
        Assert.Equal(0, calls.Count(w => w == d.Width));
        InvalidateAll(d);
        Assert.Equal(expanded, TotalRows(d));   // incremental delta == full recount
    }

    [Fact]
    public void Resize_And_ThemeSwitch_InvalidateCachedRows()
    {
        var term = new Terminal();
        var d = new TuiDriver(term, frameEngine: true);
        var calls = CommitCounted(d, 4, linesEach: 1);
        d.CommitTurnHeader("CodeAgent");
        Compose(d);
        calls.Clear();
        term.Width = 50;
        Compose(d);
        Assert.Contains(d.Width, calls);   // re-rendered at the new width, not served stale
        Assert.True(Csi.Replace(EntryRows(d, 4)[1], "").Length <= d.Width);

        var prior = Theme.Active;
        try
        {
            var a = Theme.Find("nord")!; var b = Theme.Find("dracula")!;
            Theme.Set(a);
            string before = string.Concat(EntryRows(d, 4));
            Theme.Set(b);
            string after = string.Concat(EntryRows(d, 4));
            Assert.NotEqual(before, after);
            var c = TuiMarkup.Parse($"[{b.Agent}]x[/]")[0].Style.ToAnsi();
            Assert.Contains(c, after);
        }
        finally { Theme.Set(prior); }
    }

    // --- NAV ---------------------------------------------------------------------------------

    /// <summary>Runs NAV with <paramref name="keys"/> then q; returns only what NAV itself wrote (the
    /// frame repaint after exit follows the final auto-wrap restore).</summary>
    private static string RunNav(TuiDriver d, Terminal term, params ConsoleKeyInfo[] keys)
    {
        int from = term.Output.Length;
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var current = typeof(ConsoleInputPump).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previous = current.GetValue(null);
        try
        {
            current.SetValue(null, pump);
            foreach (var k in keys) pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(k));
            pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false)));
            typeof(TuiDriver).GetMethod("EnterNavMode", PI)!.Invoke(d, new object[] { -1 });
        }
        finally { current.SetValue(null, previous); }
        string written = term.Output.ToString(from, term.Output.Length - from);
        return written[..written.LastIndexOf(Ansi.AutoWrapOn, StringComparison.Ordinal)];
    }

    /// <summary>Union: an extension / layout reload (RegionStyle.Set) bumps RegionStyle.Version, which must
    /// drop cached rows, or the transcript would keep stale glyphs after /refresh.</summary>
    [Fact]
    public void RegionStyleReload_InvalidatesCachedRows()
    {
        var term = new Terminal();
        var d = new TuiDriver(term, frameEngine: true);
        var calls = CommitCounted(d, 3);
        Compose(d);
        int before = calls.Count;
        Compose(d);
        Assert.Equal(before, calls.Count);                 // cache hit
        try
        {
            RegionStyle.Set(null);                         // same content, new version
            Compose(d);
            Assert.True(calls.Count > before);             // re-rendered after the reload
        }
        finally { RegionStyle.Set(null); }
    }

    [Fact]
    public void NavOpenClose_WithoutExpansion_KeepsTheFrameRowCache()
    {
        var term = new Terminal();
        var d = new TuiDriver(term, frameEngine: true);
        var calls = CommitCounted(d, 40);
        Compose(d);
        calls.Clear();
        RunNav(d, term);   // NAV builds its own (narrower) display lines; its exit repaints the frame
        Assert.Equal(0, calls.Count(w => w == d.Width));
    }

    [Fact]
    public void NavToggles_ThenExit_KeepTheExactTotal()
    {
        var term = new Terminal();
        var d = new TuiDriver(term, frameEngine: true);
        CommitCounted(d, 10);
        d.CommitCollapsed("· CodeAgent finished (ctrl+e)", "CodeAgent", string.Join("\n", Enumerable.Range(0, 30).Select(i => $"body {i}")));
        Compose(d);
        int collapsed = TotalRows(d);
        // NAV opens on the last line (the card): expand, collapse, expand; exit collapses it again.
        var enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);
        RunNav(d, term, enter, enter, enter);
        Compose(d);
        Assert.Equal(collapsed, TotalRows(d));
        InvalidateAll(d);
        Assert.Equal(collapsed, TotalRows(d));
        // A click expand after NAV still lands the exact total (the delta path).
        RunNav(d, term, enter);   // leaves it collapsed again on exit
        Compose(d);
        Assert.Equal(collapsed, TotalRows(d));
    }

    /// <summary>Replays an ANSI row into per-cell (char, active SGR) pairs: the terminal's view.</summary>
    private static List<(char Ch, string Style)> Cells(string ansi)
    {
        var cells = new List<(char, string)>();
        string style = "";
        for (int i = 0; i < ansi.Length; i++)
        {
            var m = Csi.Match(ansi, i);
            if (m.Success && m.Index == i)
            {
                if (m.Value.EndsWith('m')) style = m.Value == Ansi.Reset ? "" : style + m.Value;
                i += m.Length - 1;
                continue;
            }
            cells.Add((ansi[i], style));
        }
        return cells;
    }

    [Fact]
    public void NavRows_EmitOneSgrPerRun_WithIdenticalCellStyling()
    {
        var term = new Terminal { Width = 60, Height = 12 };
        var d = new TuiDriver(term, frameEngine: true);
        d.CommitLine("[#FF0000 italic]alpha beta gamma[/] tail");
        d.CommitLine("end");
        // First NAV paint: rows are addressed with MoveTo(row, 1).
        var rows = Regex.Split(RunNav(d, term), "\u001b\\[\\d+;1H").ToList();
        string styled = rows.First(r => Csi.Replace(r, "").StartsWith("alpha beta gamma"));
        string cursorRow = rows.First(r => Csi.Replace(r, "").StartsWith("end"));

        string sgr = TuiMarkup.Parse("[#FF0000 italic]x[/]")[0].Style.ToAnsi();
        Assert.Single(Regex.Matches(styled, Regex.Escape(sgr)));   // was once per character

        // Per-cell styling is what the terminal shows: identical to the per-character form.
        var cells = Cells(styled);
        Assert.Equal("alpha beta gamma tail", new string(cells.Select(c => c.Ch).ToArray()).TrimEnd());
        Assert.All(cells.Take(16), c => Assert.Equal(sgr, c.Style));
        Assert.All(cells.Skip(16), c => Assert.Equal("", c.Style));
        Assert.Equal(term.Width - 1, cells.Count);
        var cur = Cells(cursorRow);
        Assert.Equal(Ansi.Invert, cur[0].Style);   // NAV opens with the cursor on the last line
        Assert.All(cur.Skip(1), c => Assert.Equal("", c.Style));
    }

    [Fact]
    public void NavSelection_BreaksRunsAtTheSelectionEdges()
    {
        var term = new Terminal { Width = 40, Height = 10 };
        var d = new TuiDriver(term, frameEngine: true);
        d.CommitLine("[#00FF00]abcdefgh[/]");
        // Cursor on that line (col 0), char-select, move right 2: cells 0..2 inverted.
        string nav = RunNav(d, term,
            new ConsoleKeyInfo('v', ConsoleKey.V, false, false, false),
            new ConsoleKeyInfo('l', ConsoleKey.L, false, false, false),
            new ConsoleKeyInfo('l', ConsoleKey.L, false, false, false));
        var paints = Regex.Split(nav, "\u001b\\[\\d+;1H").Where(r => Csi.Replace(r, "").StartsWith("abcdefgh")).ToList();
        var cells = Cells(paints[^1]);
        string green = TuiMarkup.Parse("[#00FF00]x[/]")[0].Style.ToAnsi();
        Assert.Equal(new[] { Ansi.Invert, Ansi.Invert, Ansi.Invert, green, green, green, green, green }, cells.Take(8).Select(c => c.Style));
        Assert.Single(Regex.Matches(paints[^1], Regex.Escape(green)));
    }
}
