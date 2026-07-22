using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers the v0.12.4 in-frame prompt modal model (PromptModalView): the pure state/render
/// core behind the frame engine's no-alt-screen-flip ask_user/confirm/select/text prompts.
/// Selection movement, multi-select checking, the scrollable question-body viewport, width
/// clamping, and the choice window.
/// </summary>
public class PromptModalViewTests
{
    private static PromptModalView Open(PromptModalView.Kind kind, string q, string[]? choices = null,
        string? def = null, bool secret = false)
    {
        var v = new PromptModalView();
        v.Open(kind, q, choices, def, secret);
        return v;
    }

    private static string PlainOf(PromptModalView v, int width = 100, int height = 40)
        => string.Join("\n", v.Render(width, height).Select(TuiMarkup.Plain));

    [Fact]
    public void Select_RendersChoices_AndMovesSelection()
    {
        var v = Open(PromptModalView.Kind.Select, "Proceed with the plan?", new[] { "Yes", "No" });
        string plain = PlainOf(v);
        Assert.Contains("input requested", plain);
        Assert.Contains("Proceed with the plan?", plain);
        Assert.Contains("Yes", plain);
        Assert.Contains("No", plain);
        Assert.Equal(0, v.Sel);
        v.MoveSel(+1);
        Assert.Equal(1, v.Sel);
        v.MoveSel(+1);   // clamped at the end
        Assert.Equal(1, v.Sel);
        v.MoveSel(-5);   // clamped at the start
        Assert.Equal(0, v.Sel);
    }

    [Fact]
    public void MultiSelect_TogglesChecks()
    {
        var v = Open(PromptModalView.Kind.MultiSelect, "Pick some", new[] { "a", "b", "c" });
        v.ToggleChecked();
        v.MoveSel(+2);
        v.ToggleChecked();
        Assert.Equal(new[] { 0, 2 }, v.CheckedIndices.OrderBy(i => i));
        v.ToggleChecked();   // un-check
        Assert.Equal(new[] { 0 }, v.CheckedIndices.ToArray());
        Assert.Contains("space toggle", PlainOf(v));
    }

    [Fact]
    public void Text_InputEditing_AndDefaultHint()
    {
        var v = Open(PromptModalView.Kind.Text, "Name?", def: "fallback");
        Assert.Contains("default: fallback", PlainOf(v));
        v.InputAppend('h'); v.InputAppend('i');
        Assert.Equal("hi", v.InputText);
        Assert.DoesNotContain("default:", PlainOf(v));
        v.InputBackspace();
        Assert.Equal("h", v.InputText);
        v.InputAppend("\tpasted\ntext");
        Assert.Equal("h pasted text", v.InputText);
    }

    [Fact]
    public void Text_Secret_MasksInput()
    {
        var v = Open(PromptModalView.Kind.Text, "Key?", secret: true);
        v.InputAppend("abcd");
        string plain = PlainOf(v);
        Assert.DoesNotContain("abcd", plain);
        Assert.Contains("\u2022\u2022\u2022\u2022", plain);
    }

    [Fact]
    public void LongBody_TailAnchored_AndScrollable()
    {
        // A "plan dump" far taller than the screen: the END (the actionable ask) must be
        // visible by default, with earlier lines reachable by scrolling UP.
        string q = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"plan line {i}")) + "\nGo ahead?";
        var v = Open(PromptModalView.Kind.Select, q, new[] { "Yes", "No" });
        string plain = PlainOf(v, width: 100, height: 24);
        Assert.Contains("Go ahead?", plain);
        Assert.Contains("earlier line", plain);
        Assert.DoesNotContain("plan line 1\n", plain);
        Assert.Contains("pgup/pgdn scroll", plain);

        // Scroll up: earlier lines come into view, a "more below" marker appears.
        v.ScrollBody(+50);
        plain = PlainOf(v, width: 100, height: 24);
        Assert.Contains("plan line 1", plain);
        Assert.DoesNotContain("Go ahead?", plain);
        Assert.Contains("more line", plain);

        // Over-scroll clamps; scrolling back down restores the tail.
        v.ScrollBody(+999);
        v.ScrollBody(-9999);
        plain = PlainOf(v, width: 100, height: 24);
        Assert.Contains("Go ahead?", plain);
    }

    [Fact]
    public void Rows_AreClampedToWidth_AndBoundedByHeight()
    {
        string q = "a question with a very long unbroken token " + new string('y', 300);
        var v = Open(PromptModalView.Kind.Select, q, new[] { "Yes", "No", new string('z', 200) });
        var rows = v.Render(48, 20);
        Assert.All(rows, r => Assert.True(TuiMarkup.MarkupWidth(r) <= 48,
            $"row exceeds width ({TuiMarkup.MarkupWidth(r)} > 48): {TuiMarkup.Plain(r)}"));
        Assert.True(rows.Count <= 20, $"composed {rows.Count} rows for height 20");
    }

    [Fact]
    public void ChoiceWindow_FollowsSelection()
    {
        var choices = Enumerable.Range(1, 25).Select(i => $"option {i}").ToArray();
        var v = Open(PromptModalView.Kind.Select, "Pick", choices);
        string plain = PlainOf(v);
        Assert.Contains("option 1", plain);
        Assert.DoesNotContain("option 11\n", plain);
        for (int i = 0; i < 20; i++) v.MoveSel(+1);
        plain = PlainOf(v);
        Assert.Contains("option 21", plain);
        Assert.Contains("of 25", plain);
    }

    [Fact]
    public void TruncateMarkup_PreservesStyle_AndWidth()
    {
        string m = "[#64B4DC]hello[/] [#5A5A5A]world wide row[/]";
        Assert.Equal(m, TuiMarkup.TruncateMarkup(m, 80));   // fits -> untouched
        string cut = TuiMarkup.TruncateMarkup(m, 8);
        Assert.True(TuiMarkup.MarkupWidth(cut) <= 8);
        Assert.EndsWith("\u2026", TuiMarkup.Plain(cut));
        Assert.Contains("[#64B4DC]", cut);   // style survives
        Assert.Equal("", TuiMarkup.TruncateMarkup(m, 0));
    }
}
