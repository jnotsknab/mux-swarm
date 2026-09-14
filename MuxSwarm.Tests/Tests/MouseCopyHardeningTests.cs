using System.Reflection;
using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Batch 9 dogfood fixes: full-row selection inversion across embedded SGR resets,
/// chrome-classified clipboard text, mid-turn drag-copy, and triple-click Apply.</summary>
[Collection("ConsoleState")]
public class MouseCopyHardeningTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 90;
        public int Height { get; set; } = 24;
        public StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static List<string> ComposeRows(TuiDriver driver)
        => (List<string>)typeof(TuiDriver).GetMethod("ComposeFrameRows", PrivateInstance | BindingFlags.Public)!.Invoke(driver, null)!;

    private static void Mouse(TuiDriver driver, int button, int col, int row, bool release = false)
        => driver.RouteMouse(ConsoleInputPump.InputEvent.OfMouse(button, col, row, release));

    private static string CopiedText(Terminal term)
    {
        string output = term.Output.ToString();
        int at = output.LastIndexOf("]52;c;", StringComparison.Ordinal);
        Assert.True(at >= 0, "expected an OSC 52 copy");
        string b64 = output[(at + 6)..output.IndexOf('\a', at)];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    // ---- overlay inversion across styled rows ----

    [Fact]
    public void SelectionOverlay_SurvivesEmbeddedSgrResets_WholeRowInverted()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        // Styled content: markup spans compile to SGR runs each ENDING in ESC[0m.
        for (int i = 0; i < 30; i++)
            driver.CommitLine($"[#64B4DC]agent[/] [#3A3A3A]\u00b7[/] styled line {i} [#F0F0F0]tail[/]");
        ComposeRows(driver);
        Mouse(driver, 0, 10, 5);
        Mouse(driver, 32, 10, 7);
        var rows = ComposeRows(driver);
        for (int r = 5; r <= 7; r++)
        {
            string row = rows[r - 1];
            Assert.StartsWith(Ansi.Invert, row);
            // EVERY embedded reset must be immediately followed by a re-assert of the invert,
            // except the FINAL reset that closes the overlay.
            int idx = 0;
            while ((idx = row.IndexOf(Ansi.Reset, idx, StringComparison.Ordinal)) >= 0)
            {
                int after = idx + Ansi.Reset.Length;
                if (after == row.Length) break;   // trailing overlay close
                Assert.Equal(Ansi.Invert, row.Substring(after, Math.Min(Ansi.Invert.Length, row.Length - after)));
                idx = after;
            }
        }
        Mouse(driver, 0, 10, 7, release: true);
    }

    // ---- clipboard chrome classification ----

    [Theory]
    [InlineData("  \u2502 This is WebAgent reporting in", "This is WebAgent reporting in")]
    [InlineData("  \u25b8 MemoryAgent \u00b7 done", "MemoryAgent \u00b7 done")]
    [InlineData("    \u221f \u2713 WebAgent finished", "WebAgent finished")]
    [InlineData("  \u25cf streaming tail text", "streaming tail text")]
    [InlineData("  \u00b7 System sleep \u2014 Slept for 15 seconds.", "System sleep \u2014 Slept for 15 seconds.")]
    public void ClipboardText_StripsLeadingChrome_KeepsContent(string rendered, string expected)
        => Assert.Equal(expected, TuiComponents.ClipboardText(rendered));

    [Theory]
    [InlineData("  \u256d\u2500\u2500\u2500\u2500\u2500\u2500\u256e")]
    [InlineData("\u2570\u2500\u2500\u2500\u2500\u2500\u2500\u256f")]
    [InlineData("  \u2502  \u2502")]
    [InlineData("  \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500")]
    public void ClipboardText_DropsDecorationOnlyRows(string rendered)
        => Assert.Null(TuiComponents.ClipboardText(rendered));

    [Fact]
    public void ClipboardText_PreservesBlankLines_TrailingHint_And_TableInteriors()
    {
        Assert.Equal("", TuiComponents.ClipboardText(""));
        Assert.Equal("", TuiComponents.ClipboardText("   "));
        Assert.Equal("CodeAgent finished", TuiComponents.ClipboardText("  \u00b7 CodeAgent finished (ctrl+e expand)"));
        // Interior pipes/glyphs are CONTENT (table cells), untouched beyond the leading strip.
        Assert.Equal("Agent \u2502 Cycles \u2502 Result", TuiComponents.ClipboardText("  \u2502 Agent \u2502 Cycles \u2502 Result"));
    }

    [Fact]
    public void DragCopy_ProducesCleanText_NoBordersNoGutters()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        driver.SetLaneTint("#64B4DC");   // lane gutter bars on every committed row
        for (int i = 0; i < 30; i++) driver.CommitLine($"gutter line {i}");
        driver.SetLaneTint(null);
        ComposeRows(driver);
        var rows = ComposeRows(driver);
        int r0 = -1;
        for (int r = 1; r <= rows.Count && r0 < 0; r++)
            if (TuiMarkup.StripAnsi(rows[r - 1]).Contains("gutter line 10")) r0 = r;
        Assert.True(r0 > 0);
        term.Output.Clear();
        Mouse(driver, 0, 10, r0);
        Mouse(driver, 32, 10, r0 + 1);
        Mouse(driver, 0, 10, r0 + 1, release: true);
        Assert.Equal("gutter line 10\ngutter line 11", CopiedText(term));
    }

    // ---- mid-turn drag-copy ----

    [Fact]
    public void MidTurnDrag_SelectsAndCopies()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        for (int i = 0; i < 30; i++) driver.CommitLine($"stream line {i}");
        ComposeRows(driver);
        var rows = ComposeRows(driver);
        int r0 = -1;
        for (int r = 1; r <= rows.Count && r0 < 0; r++)
            if (TuiMarkup.StripAnsi(rows[r - 1]).Contains("stream line 20")) r0 = r;   // bottom-anchored: high lines are on screen
        Assert.True(r0 > 0);
        term.Output.Clear();
        Assert.Null(driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 10, r0, false)));
        Assert.Null(driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(32, 10, r0 + 1, false)));
        Assert.True(driver.DragSelecting);
        Assert.Null(driver.RouteMouseMidTurn(ConsoleInputPump.InputEvent.OfMouse(0, 10, r0 + 1, true)));
        Assert.False(driver.DragSelecting);
        Assert.Equal("stream line 20\nstream line 21", CopiedText(term));
    }

    // ---- triple-click chain (driver-side router: transcript untouched by triples) ----

    [Fact]
    public void TranscriptTripleClick_ThirdClickStaysSuppressed_NoReToggle()
    {
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        long tick = 1000;
        driver.MouseClock = () => tick;
        driver.CommitCollapsed("\u00b7 CodeAgent finished (ctrl+e)", "CodeAgent", "body");
        ComposeRows(driver);
        var map = driver.FrameHitMap!;
        int row = -1; HitRegion hit = default;
        for (int r = 1; r <= term.Height && row < 0; r++)
            if (map.TryHit(r, 4, out hit, out _, out _) && hit.Kind == MouseTargetKind.TranscriptEntry) row = r;
        var transcript = (System.Collections.IList)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!;
        var entry = transcript[hit.Payload]!;
        var expandedField = entry.GetType().GetField("Expanded")!;
        // click 1: expand
        Mouse(driver, 0, 4, row); Mouse(driver, 0, 4, row, release: true);
        Assert.True((bool)expandedField.GetValue(entry)!);
        // click 2 (double): suppressed, stays open
        tick += 200;
        var (row2, _) = FindEntry(driver, term);
        Mouse(driver, 0, 4, row2); Mouse(driver, 0, 4, row2, release: true);
        Assert.True((bool)expandedField.GetValue(entry)!);
        // click 3 (fast triple): also suppressed for transcript (only 1-chains toggle)
        tick += 200;
        var (row3, _) = FindEntry(driver, term);
        Mouse(driver, 0, 4, row3); Mouse(driver, 0, 4, row3, release: true);
        Assert.True((bool)expandedField.GetValue(entry)!);
    }

    private static (int Row, HitRegion Hit) FindEntry(TuiDriver driver, Terminal term)
    {
        var map = driver.FrameHitMap!;
        for (int r = 1; r <= term.Height; r++)
            if (map.TryHit(r, 4, out var hit, out _, out _) && hit.Kind == MouseTargetKind.TranscriptEntry)
                return (r, hit);
        throw new Xunit.Sdk.XunitException("no TranscriptEntry region");
    }

    // ---- round 3: position-union chaining (triple-click survives UI mutation under the pointer) ----

    [Fact]
    public void ClickChain_SurvivesTargetChange_AtSameSpot()
    {
        // The dogfood failure: the double-click's own action mutates the UI (pane advance, edit
        // mode), so click 3 lands on a DIFFERENT region. Same-spot chaining must carry it.
        var map1 = new MouseHitMap();
        map1.Begin(80, 24);
        map1.Add(new HitRegion(5, 1, 1, 80, MouseTargetKind.PickerItem, 3));
        var map2 = new MouseHitMap();
        map2.Begin(80, 24);
        map2.Add(new HitRegion(1, 1, 24, 80, MouseTargetKind.PickerSearchBox, -1));   // backdrop only
        long tick = 1000;
        var t = new MouseClickTracker(() => tick);
        ConsoleInputPump.InputEvent Press(int col, int row) => ConsoleInputPump.InputEvent.OfMouse(0, col, row, false);
        ConsoleInputPump.InputEvent Release(int col, int row) => ConsoleInputPump.InputEvent.OfMouse(0, col, row, true);

        t.Feed(Press(10, 5), map1, out _, out _, out _, out _);
        Assert.True(t.Feed(Release(10, 5), map1, out _, out _, out _, out int c1));
        Assert.Equal(1, c1);
        tick += 200;
        t.Feed(Press(10, 5), map1, out _, out _, out _, out _);
        Assert.True(t.Feed(Release(10, 5), map1, out _, out _, out _, out int c2));
        Assert.Equal(2, c2);
        tick += 200;
        // UI mutated: only the backdrop remains where the item was. Same spot -> chain holds.
        t.Feed(Press(11, 5), map2, out _, out _, out _, out _);   // 1 cell of horizontal wobble
        Assert.True(t.Feed(Release(11, 5), map2, out var hit3, out _, out _, out int c3));
        Assert.Equal(3, c3);
        Assert.Equal(MouseTargetKind.PickerSearchBox, hit3.Kind);
    }

    [Fact]
    public void ClickChain_DifferentSpotAndTarget_DoesNotChain()
    {
        var map = new MouseHitMap();
        map.Begin(80, 24);
        map.Add(new HitRegion(5, 1, 1, 80, MouseTargetKind.PickerItem, 3));
        map.Add(new HitRegion(9, 1, 1, 80, MouseTargetKind.PickerItem, 7));
        long tick = 1000;
        var t = new MouseClickTracker(() => tick);
        ConsoleInputPump.InputEvent Press(int col, int row) => ConsoleInputPump.InputEvent.OfMouse(0, col, row, false);
        ConsoleInputPump.InputEvent Release(int col, int row) => ConsoleInputPump.InputEvent.OfMouse(0, col, row, true);
        t.Feed(Press(10, 5), map, out _, out _, out _, out _);
        t.Feed(Release(10, 5), map, out _, out _, out _, out _);
        tick += 200;
        t.Feed(Press(40, 9), map, out _, out _, out _, out _);   // different row AND different payload
        Assert.True(t.Feed(Release(40, 9), map, out _, out _, out _, out int c2));
        Assert.Equal(1, c2);
    }

    [Fact]
    public void DriverChain_SameSpot_TripleCountsAcrossRegionChange()
    {
        // Transcript card: click 1 expands (rows shift so the region under the pointer can
        // change), clicks 2-3 on the same SPOT keep the chain: counts 2 and 3 both suppress.
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        long tick = 1000;
        driver.MouseClock = () => tick;
        driver.CommitCollapsed("· CodeAgent finished (ctrl+e)", "CodeAgent", "line1\nline2\nline3");
        ComposeRows(driver);
        var (row, hit) = FindEntry(driver, term);
        var transcript = (System.Collections.IList)typeof(TuiDriver).GetField("_transcript", PrivateInstance)!.GetValue(driver)!;
        var entry = transcript[hit.Payload]!;
        var expandedField = entry.GetType().GetField("Expanded")!;
        Mouse(driver, 0, 4, row); Mouse(driver, 0, 4, row, release: true);
        Assert.True((bool)expandedField.GetValue(entry)!);
        tick += 200;
        Mouse(driver, 0, 4, row); Mouse(driver, 0, 4, row, release: true);   // same spot, rows may have shifted
        Assert.True((bool)expandedField.GetValue(entry)!);                   // double: suppressed
        tick += 200;
        Mouse(driver, 0, 4, row); Mouse(driver, 0, 4, row, release: true);
        Assert.True((bool)expandedField.GetValue(entry)!);                   // triple: still suppressed
    }
}
