using MuxSwarm.Utils;
using MuxSwarm.Utils.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Headless coverage for the g11.5 TUI features: the session-header tool badge + expandable
/// "/tools" palette, the Ctrl+E large-result expand affordance, and the vim modal navigation
/// layer (Insert/Normal motions + transcript NAV signal). Pure/headless - no real console.
/// </summary>
public class TuiVimFeatureTests
{
    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static ConsoleKeyInfo K(ConsoleKey k, bool shift = false, bool ctrl = false)
        => new('\0', k, shift, false, ctrl);

    private static LineEditor WithText(string s)
    {
        var ed = new LineEditor();
        foreach (var c in s) ed.Feed(Ch(c));
        return ed;
    }

    // --- session-header tool badge -------------------------------------------

    [Fact]
    public void SessionHeader_NoToolCount_OmitsBadge()
    {
        var h = string.Join("\n", TuiComponents.SessionHeader("Agent", "m", "p"));
        Assert.DoesNotContain("tool", h);
    }

    [Fact]
    public void SessionHeader_WithToolCount_ShowsBadge()
    {
        var h = string.Join("\n", TuiComponents.SessionHeader("Agent", "m", "p", 88));
        Assert.Contains("88 tools", h);
    }

    [Fact]
    public void SessionHeader_SingleTool_IsSingular()
    {
        var h = string.Join("\n", TuiComponents.SessionHeader("Agent", "m", "p", 1));
        Assert.Contains("1 tool", h);
        Assert.DoesNotContain("1 tools", h);
    }

    // --- /tools palette ------------------------------------------------------

    [Fact]
    public void RankTools_FiltersByNameAndDesc()
    {
        var tools = new List<(string, string)> { ("read_file", "reads a file"), ("web_search", "search the web") };
        Assert.Equal(new[] { "read_file" }, TuiComponents.RankTools("read", tools));
        Assert.Equal(new[] { "web_search" }, TuiComponents.RankTools("web", tools));
        // description match
        Assert.Contains("read_file", TuiComponents.RankTools("reads", tools));
    }

    [Fact]
    public void ToolsPreview_EmptyCatalog_SaysNoTools()
    {
        var rows = TuiComponents.ToolsPreview("", new List<(string, string)>(), 80);
        Assert.Contains(rows, r => r.Contains("no tools"));
    }

    [Fact]
    public void ToolsPreview_ShowsHeaderWithCount()
    {
        var tools = new List<(string, string)> { ("a", "x"), ("b", "y") };
        var rows = TuiComponents.ToolsPreview("", tools, 80);
        Assert.Contains(rows, r => r.Contains("tools") && r.Contains("(2)"));
    }

    [Fact]
    public void LineEditor_DetectsToolsFilter()
    {
        var ed = WithText("/tools re");
        Assert.True(ed.IsToolsFilter);
        Assert.Equal("re", ed.ToolsFilter);
    }

    // --- Ctrl+E expand affordance -------------------------------------------

    [Fact]
    public void Merged_NotExpandable_OmitsHint()
    {
        var line = string.Join("\n", TuiComponents.ToolCallResultMerged("t", null, "one\ntwo", error: false, expandable: false));
        Assert.DoesNotContain("ctrl+e", line);
    }

    [Fact]
    public void Merged_Expandable_AdvertisesCtrlE()
    {
        var body = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}"));
        var line = string.Join("\n", TuiComponents.ToolCallResultMerged("t", null, body, error: false, expandable: true));
        Assert.Contains("ctrl+e expand", line);
    }

    // --- vim modal layer -----------------------------------------------------

    [Fact]
    public void Editor_DefaultsToInsertMode()
        => Assert.Equal(EditorMode.Insert, new LineEditor().Mode);

    [Fact]
    public void Esc_OnNonEmpty_EntersNormalMode()
    {
        var ed = WithText("hello");
        var sig = ed.Feed(K(ConsoleKey.Escape));
        Assert.Equal(LineEditSignal.ModeChanged, sig);
        Assert.Equal(EditorMode.Normal, ed.Mode);
    }

    [Fact]
    public void Esc_OnEmpty_EntersNavView()
    {
        var ed = new LineEditor();
        // Empty buffer + Esc opens the transcript NAV (view) overlay; quitting is Ctrl+C.
        Assert.Equal(LineEditSignal.NavEnter, ed.Feed(K(ConsoleKey.Escape)));
    }

    [Fact]
    public void CtrlC_OnEmpty_StillCancels()
    {
        var ed = new LineEditor();
        Assert.Equal(LineEditSignal.Cancel, ed.Feed(K(ConsoleKey.C, ctrl: true)));
    }

    [Fact]
    public void Esc_InNormalMode_EntersNavView()
    {
        var ed = WithText("hi");
        ed.Feed(K(ConsoleKey.Escape));   // -> Normal
        Assert.Equal(EditorMode.Normal, ed.Mode);
        Assert.Equal(LineEditSignal.NavEnter, ed.Feed(K(ConsoleKey.Escape)));   // -> NAV
    }

    [Fact]
    public void Normal_I_ReturnsToInsert()
    {
        var ed = WithText("hi");
        ed.Feed(K(ConsoleKey.Escape));
        var sig = ed.Feed(K(ConsoleKey.I));
        Assert.Equal(LineEditSignal.ModeChanged, sig);
        Assert.Equal(EditorMode.Insert, ed.Mode);
    }

    [Fact]
    public void Normal_NormalKeysDoNotInsertLiterals()
    {
        var ed = WithText("hi");
        ed.Feed(K(ConsoleKey.Escape));
        ed.Feed(K(ConsoleKey.J));   // should NOT type 'j'
        Assert.Equal("hi", ed.Buffer);
    }

    [Fact]
    public void Normal_Dollar_And_Zero_MoveCursorEnds()
    {
        var ed = WithText("hello");
        ed.Feed(K(ConsoleKey.Escape));
        ed.Feed(Ch('0'));
        Assert.Equal(0, ed.Cursor);
        ed.Feed(Ch('$'));
        Assert.Equal(4, ed.Cursor);   // rests on last char in Normal
    }

    [Fact]
    public void Normal_X_DeletesCharUnderCursor()
    {
        var ed = WithText("abc");
        ed.Feed(K(ConsoleKey.Escape));   // cursor clamps to 2 ('c')
        ed.Feed(Ch('0'));                // back to start
        ed.Feed(K(ConsoleKey.X));        // delete 'a'
        Assert.Equal("bc", ed.Buffer);
    }

    [Fact]
    public void Normal_DD_DeletesWholeLine()
    {
        var ed = WithText("delete me");
        ed.Feed(K(ConsoleKey.Escape));
        ed.Feed(K(ConsoleKey.D));
        ed.Feed(K(ConsoleKey.D));
        Assert.Equal("", ed.Buffer);
    }

    [Fact]
    public void Normal_CapC_ChangesToEnd_EntersInsert()
    {
        var ed = WithText("keepXXXX");
        ed.Feed(K(ConsoleKey.Escape));
        ed.Feed(Ch('0'));
        // move right 4 to land after "keep"
        for (int i = 0; i < 4; i++) ed.Feed(K(ConsoleKey.L));
        ed.Feed(K(ConsoleKey.C, shift: true));   // C = change to end
        Assert.Equal("keep", ed.Buffer);
        Assert.Equal(EditorMode.Insert, ed.Mode);
    }

    [Fact]
    public void Normal_CtrlD_SignalsNavEnter()
    {
        var ed = WithText("anything");
        ed.Feed(K(ConsoleKey.Escape));
        var sig = ed.Feed(K(ConsoleKey.D, ctrl: true));
        Assert.Equal(LineEditSignal.NavEnter, sig);
    }

    [Fact]
    public void Reset_ReturnsToInsertMode()
    {
        var ed = WithText("x");
        ed.Feed(K(ConsoleKey.Escape));
        Assert.Equal(EditorMode.Normal, ed.Mode);
        ed.Reset();
        Assert.Equal(EditorMode.Insert, ed.Mode);
    }

    [Fact]
    public void InputRow_NormalMode_ShowsBadge()
    {
        var row = TuiComponents.InputRowWithCursor("hi", 0, EditorMode.Normal);
        Assert.Contains("NORMAL", row);
    }

    [Fact]
    public void InputRow_InsertMode_NoBadge()
    {
        var row = TuiComponents.InputRowWithCursor("hi", 0, EditorMode.Insert);
        Assert.DoesNotContain("NORMAL", row);
    }

    [Fact]
    public void Normal_HL_MoveSingleChar_NoLargeJumps()
    {
        var ed = WithText("abcde");
        ed.Feed(K(ConsoleKey.Escape));   // Normal; cursor clamps to last char (4)
        ed.Feed(Ch('0'));                // start
        Assert.Equal(0, ed.Cursor);
        ed.Feed(K(ConsoleKey.L));
        Assert.Equal(1, ed.Cursor);      // single-char right
        ed.Feed(K(ConsoleKey.L));
        Assert.Equal(2, ed.Cursor);
        ed.Feed(K(ConsoleKey.H));
        Assert.Equal(1, ed.Cursor);      // single-char left
    }

    [Fact]
    public void Normal_L_ClampsAtLastChar()
    {
        var ed = WithText("ab");
        ed.Feed(K(ConsoleKey.Escape));
        ed.Feed(Ch('0'));
        ed.Feed(K(ConsoleKey.L));
        ed.Feed(K(ConsoleKey.L));        // try to go past end
        Assert.Equal(1, ed.Cursor);      // clamped to last char index
    }

    [Fact]
    public void Normal_JK_DoNotYankHistory()
    {
        var ed = WithText("current");
        ed.Remember("older command");    // history exists
        ed.SetBuffer("current");
        ed.Feed(K(ConsoleKey.Escape));   // Normal
        ed.Feed(K(ConsoleKey.K));        // bare k must NOT load "older command"
        Assert.Equal("current", ed.Buffer);
        ed.Feed(K(ConsoleKey.J));
        Assert.Equal("current", ed.Buffer);
    }

    [Fact]
    public void ThinkingLine_AnimatesAndShowsText()
    {
        // Text with no leading spinner -> fallback braille glyph is prepended and animates.
        var a = TuiComponents.ThinkingLine("Reasoning about the plan", 0);
        var b = TuiComponents.ThinkingLine("Reasoning about the plan", 1);
        Assert.Contains("Reasoning about the plan", TuiMarkup.Plain(a));
        // Different frame -> different fallback spinner glyph (animation).
        Assert.NotEqual(a, b);

        // Text that already carries a Braille spinner (as the live ThinkingIndicator bakes in)
        // must NOT get a second glyph prepended -> single icon only.
        var withSpin = TuiComponents.ThinkingLine("\u2836 MuxAgent Conjuring...", 0);
        var plainSpin = TuiMarkup.Plain(withSpin);
        int brailleCount = plainSpin.Count(ch => ch >= '\u2800' && ch <= '\u28FF');
        Assert.Equal(1, brailleCount);
    }

    [Fact]
    public void ThinkingLine_EmptyText_FallsBackToWorking()
    {
        var plain = TuiMarkup.Plain(TuiComponents.ThinkingLine("", 0));
        Assert.Contains("Working", plain);
    }

    [Fact]
    public void ToolResultPanel_Expanded_DoesNotTruncate()
    {
        var big = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"line {i} xxxxxxxx"));
        var rows = TuiComponents.ToolResultPanel("read", big, error: false, width: 80, cap: 200, expanded: true);
        var joined = string.Join("\n", rows);
        Assert.DoesNotContain("truncated", joined);
        Assert.Contains("line 499", joined);
    }

    [Fact]
    public void ToolResultPanel_Compact_TruncatesPastCap()
    {
        var big = new string('x', 5000);
        var rows = TuiComponents.ToolResultPanel("read", big, error: false, width: 80, cap: 200, expanded: false);
        Assert.Contains(rows, r => r.Contains("truncated"));
    }

    [Fact]
    public void ToolResultPanel_NoTrailingBlankLine()
    {
        var rows = TuiComponents.ToolResultPanel("t", "body", error: false, width: 80);
        Assert.False(string.IsNullOrEmpty(rows[^1]));   // last line is the closing border, not a blank
    }

    // --- G11 batch: table rendering, footer token breakdown, config commands ---------

    [Fact]
    public void Table_RendersBorderedAlignedBlock()
    {
        var rows = new[]
        {
            "| Name | Age |",
            "|------|----:|",
            "| Bob  | 30  |",
            "| Alicia | 7 |",
        };
        var outp = TuiTable.Render(rows, width: 80);
        var plain = string.Join("\n", outp.Select(TuiMarkup.Plain));
        Assert.Contains("Name", plain);
        Assert.Contains("Alicia", plain);
        // Box-drawing chrome present (top + bottom borders).
        Assert.Contains("\u256d", plain);   // top-left corner
        Assert.Contains("\u2570", plain);   // bottom-left corner
    }

    [Fact]
    public void Table_WrapsLongCells_NoTruncation_FitsWidth()
    {
        var rows = new[]
        {
            "| Commit | Root cause |",
            "|--------|-----------|",
            "| 3fbb732 | TuiCommitBlock stamped a corner glyph on every row so it was changed to a dim middot uniformly across all panels and indices right-aligned |",
            "| ba71cfb | – |",
        };
        const int W = 60;
        var outp = TuiTable.Render(rows, W);
        var plain = outp.Select(TuiMarkup.Plain).ToList();
        // No content is lost to an ellipsis, and a distinctive tail word survives the wrap.
        var joined = string.Join(" ", plain);
        Assert.DoesNotContain("\u2026", joined);   // no horizontal ellipsis
        Assert.Contains("panels", joined);
        // Every physical line fits within the terminal width (closing border never wraps).
        foreach (var l in plain)
            Assert.True(TuiMarkup.Width(l) <= W, $"row width {TuiMarkup.Width(l)} > {W}: {l}");
        // The long cell forced the row to span multiple physical lines.
        Assert.True(outp.Count > 6);
    }

    [Fact]
    public void Table_IsTableRow_And_Separator_Detection()
    {
        Assert.True(TuiTable.IsTableRow("| a | b |"));
        Assert.True(TuiTable.IsTableRow("a | b | c"));
        Assert.False(TuiTable.IsTableRow("just prose"));
        Assert.True(TuiTable.IsSeparatorRow("|---|:--:|---:|"));
        Assert.False(TuiTable.IsSeparatorRow("| a | b |"));
    }

    [Fact]
    public void Footer_ShowsSysAndToolBreakdown_WhenProvided()
    {
        var plain = TuiMarkup.Plain(
            TuiComponents.Footer(tokens: 4000, threshold: 80000, plan: false, ultra: false,
                psub: false, sysTokens: 8000, toolTokens: 30000));
        Assert.Contains("sys", plain);
        Assert.Contains("tools", plain);
        Assert.Contains("8k", plain);
        Assert.Contains("30k", plain);
    }

    [Fact]
    public void Footer_OmitsBreakdown_WhenZero()
    {
        var plain = TuiMarkup.Plain(
            TuiComponents.Footer(tokens: 4000, threshold: 80000, plan: false, ultra: false, psub: false));
        Assert.DoesNotContain("sys", plain);
        Assert.DoesNotContain("tools", plain);
    }

    [Fact]
    public void ConfigCommand_Recognizes_SetAndConfig()
    {
        Assert.True(TuiConfigCommands.IsConfigCommand("/set collapse 10"));
        Assert.True(TuiConfigCommands.IsConfigCommand("/config"));
        Assert.False(TuiConfigCommands.IsConfigCommand("/compact"));
        Assert.False(TuiConfigCommands.IsConfigCommand("hello world"));
    }

    [Fact]
    public void ConfigCommand_Set_BadValue_ReturnsError()
    {
        var r = TuiConfigCommands.Handle("/set collapse notanumber");
        Assert.True(r.Handled);
        Assert.False(r.Ok);
        Assert.Contains("integer", r.Message);
    }

    [Fact]
    public void ConfigCommand_Set_MissingArgs_ShowsUsage()
    {
        var r = TuiConfigCommands.Handle("/set");
        Assert.True(r.Handled);
        Assert.False(r.Ok);
        Assert.Contains("Usage", r.Message);
    }

    [Fact]
    public void ConfigCommand_Config_ListsKnownKeys()
    {
        var r = TuiConfigCommands.Handle("/config");
        Assert.True(r.Ok);
        Assert.Contains("collapseToolLines", r.Message);
        Assert.Contains("renderMode", r.Message);
        Assert.Contains("toolOutput", r.Message);
        Assert.Contains("dockedFooter", r.Message);
    }

    [Fact]
    public void Table_StripsInlineMarkdownInCells()
    {
        var rows = new[]
        {
            "| Component | Detail |",
            "|-----------|--------|",
            "| **CPU** | AMD Ryzen 7 |",
            "| *GPU* | RTX 5080 |",
        };
        var plain = string.Join("\n", TuiTable.Render(rows, 80).Select(TuiMarkup.Plain));
        Assert.Contains("CPU", plain);
        Assert.Contains("GPU", plain);
        // No raw markdown markers leak into the rendered cells.
        Assert.DoesNotContain("**", plain);
        Assert.DoesNotContain("*GPU*", plain);
    }

    [Fact]
    public void Markdown_StripInline_RemovesMarkers()
    {
        Assert.Equal("CPU", TuiMarkdown.StripInline("**CPU**"));
        Assert.Equal("GPU", TuiMarkdown.StripInline("*GPU*"));
        Assert.Equal("code", TuiMarkdown.StripInline("`code`"));
        Assert.Equal("bold", TuiMarkdown.StripInline("__bold__"));
        Assert.Equal("gone", TuiMarkdown.StripInline("~~gone~~"));
        Assert.Equal("plain text", TuiMarkdown.StripInline("plain text"));
    }

    [Fact]
    public void ConfigCommands_AreReplScoped_NotSession()
    {
        Assert.True(TuiCommands.IsReplOnly("/set"));
        Assert.True(TuiCommands.IsReplOnly("/config"));
        Assert.False(TuiCommands.IsSessionNative("/set"));
        Assert.False(TuiCommands.IsSessionNative("/config"));
    }

    [Fact]
    public void InputRows_Multiline_SplitsAndPlacesCursor()
    {
        var rows = TuiComponents.InputRowsWithCursor("line one\nline two", 13, EditorMode.Insert);
        Assert.Equal(2, rows.Count);
        Assert.Contains("line one", TuiMarkup.Plain(rows[0]));
        Assert.Contains("line two", TuiMarkup.Plain(rows[1]));
    }

    [Fact]
    public void InputRows_SingleLine_IsOneRow()
    {
        var rows = TuiComponents.InputRowsWithCursor("hello", 5, EditorMode.Insert);
        Assert.Single(rows);
        Assert.Contains("hello", TuiMarkup.Plain(rows[0]));
    }

    [Fact]
    public void InputRows_LongLine_WrapsWithGutter()
    {
        // A single logical line longer than the content width wraps to multiple visual rows; the
        // wrapped (continuation) rows carry the dim gutter and the joined plain text is preserved.
        var text = new string('x', 200);
        var rows = TuiComponents.InputRowsWithCursor(text, text.Length, EditorMode.Insert, width: 40);
        Assert.True(rows.Count > 1);
        // All 200 input chars survive across the wrapped rows (gutter/prompt glyphs aside).
        int xCount = rows.Sum(r => TuiMarkup.Plain(r).Count(c => c == 'x'));
        Assert.Equal(200, xCount);
        // Continuation rows hang-indent under the prompt via the dim gutter glyph.
        Assert.Contains("│", TuiMarkup.Plain(rows[1]));
        foreach (var r in rows)
            Assert.True(TuiMarkup.MarkupWidth(r) <= 40, $"row exceeded width: {TuiMarkup.MarkupWidth(r)}");
    }

    [Fact]
    public void InputRows_NoWidth_SingleRowUnchanged()
    {
        // width=0 (default) keeps the legacy single-row behaviour for a long line (no wrapping).
        var text = new string('y', 120);
        var rows = TuiComponents.InputRowsWithCursor(text, text.Length, EditorMode.Insert);
        Assert.Single(rows);
    }

    [Fact]
    public void LineEditor_AltEnter_InsertsNewline_NotSubmit()
    {
        var ed = new LineEditor();
        foreach (var ch in "hi") ed.Feed(new ConsoleKeyInfo(ch, ConsoleKey.NoName, false, false, false));
        var sig = ed.Feed(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, alt: true, control: false));
        Assert.Equal(LineEditSignal.Continue, sig);
        Assert.Contains("\n", ed.Buffer);
        var sig2 = ed.Feed(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        Assert.Equal(LineEditSignal.Submit, sig2);
    }

    [Fact]
    public void LineEditor_CtrlJ_InsertsNewline()
    {
        var ed = new LineEditor();
        foreach (var ch in "a") ed.Feed(new ConsoleKeyInfo(ch, ConsoleKey.NoName, false, false, false));
        var sig = ed.Feed(new ConsoleKeyInfo('\n', ConsoleKey.J, false, false, control: true));
        Assert.Equal(LineEditSignal.Continue, sig);
        Assert.Contains("\n", ed.Buffer);
    }

    [Fact]
    public void NewAgentCommand_RecognizedAndReplScoped()
    {
        Assert.True(TuiConfigCommands.IsConfigCommand("/newagent researcher"));
        Assert.True(TuiCommands.IsReplOnly("/newagent"));
        Assert.False(TuiCommands.IsSessionNative("/newagent"));
    }

    [Fact]
    public void NewAgentCommand_MissingName_ShowsUsage()
    {
        var r = TuiConfigCommands.Handle("/newagent");
        Assert.True(r.Handled);
        Assert.False(r.Ok);
        Assert.Contains("Usage", r.Message);
    }

    [Fact]
    public void NewAgentCommand_InvalidName_Rejected()
    {
        // An invalid filesystem character in the name is rejected (path-injection guard).
        var r = TuiConfigCommands.Handle("/newagent bad/name");
        Assert.True(r.Handled);
        Assert.False(r.Ok);
        Assert.Contains("Invalid", r.Message);
    }

    [Fact]
    public void Help_IncludesNewCommands()
    {
        Assert.Contains("/newagent", Help.HelpText);
        Assert.Contains("/config", Help.HelpText);
        Assert.Contains("/set", Help.HelpText);
        Assert.Contains("Alt+Enter", Help.HelpText);
    }

    [Fact]
    public void ConfigSet_NowCoversUltraAndServeKeys()
    {
        // The bug: /config listed these but /set rejected them. Now every shown key is settable.
        var r1 = TuiConfigCommands.Handle("/set ultra.thinkingBudget 31998");
        Assert.True(r1.Ok, r1.Message);
        var r2 = TuiConfigCommands.Handle("/set serve.editable true");
        Assert.True(r2.Ok, r2.Message);
        var r3 = TuiConfigCommands.Handle("/set isUsingDockerForExec off");
        Assert.True(r3.Ok, r3.Message);
        var r4 = TuiConfigCommands.Handle("/set ultra.autoSubAgents false");
        Assert.True(r4.Ok, r4.Message);
    }

    [Fact]
    public void ConfigSet_EveryConfigKey_IsSettable()
    {
        // Parse the keys /config lists and assert /set recognizes each (no "Unknown setting").
        var listing = TuiConfigCommands.Handle("/config").Message;
        foreach (var line in listing.Split('\n'))
        {
            var trimmed = line.Trim();
            // config rows look like: "<key>   <value>   [hint]" - take the first token, skip headers.
            if (!trimmed.Contains('[') || trimmed.StartsWith("Edit") || trimmed.StartsWith("Current")) continue;
            var key = trimmed.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)[0];
            // A bogus value should fail with a VALIDATION message, never "Unknown setting".
            var res = TuiConfigCommands.Handle($"/set {key} __bogus__");
            Assert.False(res.Message.Contains("Unknown setting"), $"/set did not recognize key '{key}'");
        }
    }

    [Fact]
    public void ConfigSet_BareSet_NeedsInteractive()
    {
        Assert.True(TuiConfigCommands.NeedsInteractive("/set"));
        Assert.True(TuiConfigCommands.NeedsInteractive("/set renderMode"));   // key but no value -> picker
        Assert.False(TuiConfigCommands.NeedsInteractive("/set renderMode tui")); // full form -> direct
        Assert.True(TuiConfigCommands.NeedsInteractive("/newagent"));
    }

    // --- v0.12.0 M1-polish + bracketed paste (g12.03) ------------------------

    [Fact]
    public void LineEditor_InsertText_KeepsNewlinesAsMultilineCompose()
    {
        var ed = new LineEditor();
        ed.InsertText("first line\nsecond line\nthird");
        // Multi-line paste lands as a single compose buffer with literal newlines, cursor at end.
        Assert.Equal("first line\nsecond line\nthird", ed.Buffer);
        Assert.Equal(ed.Buffer.Length, ed.Cursor);
        // Renders as three input rows (not a truncated single line).
        var rows = TuiComponents.InputRowsWithCursor(ed.Buffer, ed.Cursor, EditorMode.Insert);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void LineEditor_InsertText_NormalizesCrlf()
    {
        var ed = new LineEditor();
        ed.InsertText("a\r\nb\rc");
        Assert.Equal("a\nb\nc", ed.Buffer);
    }

    [Fact]
    public void InputRows_Highlight_ShadesEveryRow()
    {
        // highlight=false (default) is unchanged; highlight=true wraps each row in the InputBg band.
        var plain = TuiComponents.InputRowsWithCursor("hello", 5, EditorMode.Insert, width: 40, highlight: false);
        Assert.DoesNotContain(TuiComponents.InputBg, string.Join("\n", plain));
        var shaded = TuiComponents.InputRowsWithCursor("hello", 5, EditorMode.Insert, width: 40, highlight: true);
        Assert.All(shaded, r => Assert.Contains(TuiComponents.InputBg, r));
        // Highlighting must not change the visible text.
        Assert.Contains("hello", TuiMarkup.Plain(shaded[0]));
    }

    [Fact]
    public void ToolResultPanel_Markdown_RendersHeadingNotRaw()
    {
        var rows = TuiComponents.ToolResultPanel("delegate", "### BATCH DONE\nbody text",
            error: false, width: 60, expanded: true, markdown: true);
        var plain = TuiMarkup.Plain(string.Join("\n", rows));
        Assert.Contains("BATCH DONE", plain);
        Assert.DoesNotContain("### BATCH DONE", plain);   // ATX hashes consumed by markdown rendering
        // markdown=false leaves the raw text intact (old behavior).
        var rawRows = TuiComponents.ToolResultPanel("delegate", "### BATCH DONE\nbody text",
            error: false, width: 60, expanded: true, markdown: false);
        Assert.Contains("### BATCH DONE", TuiMarkup.Plain(string.Join("\n", rawRows)));
    }

    [Fact]
    public void DelegationSummary_IsOneCollapsibleLine()
    {
        string s = TuiComponents.DelegationSummary("Orchestrator", "WebAgent",
            "Do a very long task prompt that should be truncated in the collapsed summary row so it stays scannable");
        var plain = TuiMarkup.Plain(s);
        Assert.Contains("Orchestrator", plain);
        Assert.Contains("WebAgent", plain);
        Assert.Contains("ctrl+e expand", plain);
        Assert.DoesNotContain("\n", s);   // single line
    }

    [Fact]
    public void InputRows_Highlight_SubtleRail_FillsRowWidth()
    {
        // Option B (g12.04 follow-up): each shaded row carries a thin accent left-rail glyph and a
        // FAINT shade that fills the full field width (the earlier text-width-clipped band looked
        // broken). The faint colour keeps a full-width fill from reading as a heavy strip.
        var rows = TuiComponents.InputRowsWithCursor("hi", 2, EditorMode.Insert, width: 120, highlight: true);
        var first = rows[0];
        Assert.Contains("\u2502", TuiMarkup.Plain(first));        // left rail present
        Assert.Contains(TuiComponents.InputBg, first);            // faint shade present
        // The field row fills the configured width (rail + shaded body span the whole row).
        Assert.Equal(120, TuiMarkup.MarkupWidth(first));
        // highlight=false stays unshaded.
        var plain = TuiComponents.InputRowsWithCursor("hi", 2, EditorMode.Insert, width: 120, highlight: false);
        Assert.DoesNotContain(TuiComponents.InputBg, string.Join("\n", plain));
    }
}
