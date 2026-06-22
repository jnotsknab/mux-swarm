using MuxSwarm.Utils.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Pure-model coverage for the alt-screen NAV cursor (v0.11.0 Workstream G): 2D movement +
/// clamping, char/line visual selection spans, and the yank payload. No console needed.
/// </summary>
public class NavCursorModelTests
{
    private static NavCursorModel Make(params string[] lines) => new(lines);

    [Fact]
    public void StartsTopLeft_ByDefaultAtRow0()
    {
        var m = Make("hello", "world");
        Assert.Equal(0, m.Row);
        Assert.Equal(0, m.Col);
    }

    [Fact]
    public void MoveRight_ClampsToLineLength_AllowsOnePastForAppend()
    {
        var m = Make("abc");
        m.MoveRight(); m.MoveRight(); m.MoveRight();       // c=3 == length (append spot)
        Assert.Equal(3, m.Col);
        m.MoveRight();                                     // clamped, no overflow
        Assert.Equal(3, m.Col);
    }

    [Fact]
    public void MoveDown_KeepsDesiredColumn_ClampedPerLine()
    {
        var m = Make("longline", "ab", "anotherlong");
        for (int i = 0; i < 6; i++) m.MoveRight();         // col 6 on line 0
        m.MoveDown();                                      // line 1 only len 2 -> clamps to 2
        Assert.Equal(1, m.Row);
        Assert.Equal(2, m.Col);
        m.MoveDown();                                      // line 2 long -> desired col 6 restored
        Assert.Equal(2, m.Row);
        Assert.Equal(6, m.Col);
    }

    [Fact]
    public void Bottom_And_Top_Jump()
    {
        var m = Make("a", "b", "c");
        m.Bottom();
        Assert.Equal(2, m.Row);
        m.Top();
        Assert.Equal(0, m.Row);
    }

    [Fact]
    public void SeekRow_ClampsAndKeepsDesiredColumn()
    {
        var m = Make("aaaa", "bb", "cccc");
        for (int i = 0; i < 3; i++) m.MoveRight();   // desired col 3
        m.SeekRow(1);                                // line 1 len 2 -> col clamps to 2
        Assert.Equal(1, m.Row);
        Assert.Equal(2, m.Col);
        m.SeekRow(99);                               // clamps to last row
        Assert.Equal(2, m.Row);
        Assert.Equal(3, m.Col);                      // desired col 3 restored on a long line
    }

    [Fact]
    public void CharSelect_SingleLine_InclusiveOfCursorChar()
    {
        var m = Make("hello world");
        m.MoveRight(); m.MoveRight();          // col 2 (second 'l' index 2 -> 'l')
        m.ToggleSelect(NavSelect.Char);        // anchor at col 2
        for (int i = 0; i < 3; i++) m.MoveRight(); // cursor now col 5 ('  '), inclusive -> "llo w"? compute
        // anchor col2='l', cursor col5 -> chars 2,3,4,5 inclusive => "llo "
        Assert.Equal("llo ", m.SelectedText());
    }

    [Fact]
    public void CharSelect_AcrossLines_JoinsPartials()
    {
        var m = Make("abcdef", "ghijkl");
        for (int i = 0; i < 3; i++) m.MoveRight();   // col 3 line0 ('d')
        m.ToggleSelect(NavSelect.Char);              // anchor (0,3)
        m.MoveDown();                                // (1,3)
        // span (0,3)->(1,3) inclusive: "def" + "ghij"
        Assert.Equal("def\nghij", m.SelectedText());
    }

    [Fact]
    public void CharSelect_BackwardOrder_NormalizesSpan()
    {
        var m = Make("abcdef");
        for (int i = 0; i < 4; i++) m.MoveRight();   // col 4 ('e')
        m.ToggleSelect(NavSelect.Char);              // anchor (0,4)
        m.MoveLeft(); m.MoveLeft();                  // cursor col 2 ('c')
        // normalized lo=(0,2) hi=(0,4)+1 inclusive => chars 2,3,4 => "cde"
        Assert.Equal("cde", m.SelectedText());
    }

    [Fact]
    public void LineSelect_WholeLines()
    {
        var m = Make("first", "second", "third");
        m.ToggleSelect(NavSelect.Line);              // anchor line 0
        m.MoveDown();                                // to line 1
        Assert.Equal("first\nsecond", m.SelectedText());
    }

    [Fact]
    public void ToggleSameKind_ClearsSelection()
    {
        var m = Make("abc");
        m.ToggleSelect(NavSelect.Char);
        Assert.Equal(NavSelect.Char, m.Select);
        m.ToggleSelect(NavSelect.Char);
        Assert.Equal(NavSelect.None, m.Select);
        Assert.Equal("", m.SelectedText());
    }

    [Fact]
    public void InSelection_CharRange_Highlights()
    {
        var m = Make("abcdef");
        m.MoveRight();                                // col 1
        m.ToggleSelect(NavSelect.Char);               // anchor (0,1)
        m.MoveRight(); m.MoveRight();                 // cursor (0,3) inclusive -> cols 1,2,3
        Assert.True(m.InSelection(0, 1));
        Assert.True(m.InSelection(0, 3));
        Assert.False(m.InSelection(0, 4));
        Assert.False(m.InSelection(0, 0));
    }

    [Fact]
    public void Load_RebuildsAndClampsCursor()
    {
        var m = Make("aaaa", "bbbb", "cccc");
        m.Bottom(); m.MoveRight(); m.MoveRight();      // (2,2)
        m.Load(new[] { "x" });                          // shrink -> cursor clamps into range
        Assert.Equal(0, m.Row);
        Assert.True(m.Col <= 1);
    }

    [Fact]
    public void MarkupStripped_SelectionIsPlainText()
    {
        var m = Make("[red]hello[/] world");           // markup line
        m.ToggleSelect(NavSelect.Line);
        Assert.Equal("hello world", m.SelectedText()); // tags stripped
    }

    [Fact]
    public void DisplayLine_RetainsMarkup_ForColorRender()
    {
        var m = Make("[red]hello[/] world");
        Assert.Equal("[red]hello[/] world", m.DisplayLine(0)); // markup preserved for styled render
        Assert.Equal("hello world", m.PlainLine(0));           // plain basis for column math
    }

    [Fact]
    public void InSelection_ColumnsAlignToPlainText_NotMarkup()
    {
        // 'world' starts at plain col 6; markup tags must not shift the selection columns.
        var m = Make("[red]hello[/] world");
        for (int i = 0; i < 6; i++) m.MoveRight();      // plain col 6 = 'w'
        m.ToggleSelect(NavSelect.Char);
        for (int i = 0; i < 4; i++) m.MoveRight();      // through 'world'
        Assert.Equal("world", m.SelectedText());
    }
}

/// <summary>OSC 52 clipboard sequence shape (pure, no shell-out).</summary>
public class TuiClipboardTests
{
    [Fact]
    public void Osc52_EncodesBase64_WithStEnvelope()
    {
        string seq = TuiClipboard.Osc52("hi");
        string b64 = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hi"));
        Assert.Equal($"\u001b]52;c;{b64}\u0007", seq);
    }

    [Fact]
    public void Osc52_EmptyString_StillValid()
    {
        string seq = TuiClipboard.Osc52("");
        Assert.StartsWith("\u001b]52;c;", seq);
        Assert.EndsWith("\u0007", seq);
    }
}
