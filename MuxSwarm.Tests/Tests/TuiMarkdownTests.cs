using MuxSwarm.Utils.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Coverage for the Markdown -> Spectre-markup bridge used to render assistant text in the
/// live TUI. Verifies block prefixes (headings, lists, quotes) and inline transforms
/// (bold/italic/code), plus that literal Spectre brackets in source text are escaped so they
/// are not misparsed as markup.
/// </summary>
public class TuiMarkdownTests
{
    [Fact]
    public void Heading_BecomesBoldAccent_NoHashes()
    {
        var m = TuiMarkdown.ToMarkup("## Hello World");
        Assert.Contains("bold", m);
        Assert.Contains("Hello World", m);
        Assert.DoesNotContain("##", m);
        // Renders to plain text without the markup tags or hashes.
        Assert.Equal("Hello World", TuiMarkup.Plain(m));
    }

    [Fact]
    public void UnorderedList_GetsBulletGlyph()
    {
        var m = TuiMarkdown.ToMarkup("- item one");
        Assert.Contains("\u2022", TuiMarkup.Plain(m));
        Assert.Contains("item one", TuiMarkup.Plain(m));
    }

    [Fact]
    public void OrderedList_KeepsNumber()
    {
        var m = TuiMarkdown.ToMarkup("3. third");
        Assert.Contains("3.", TuiMarkup.Plain(m));
        Assert.Contains("third", TuiMarkup.Plain(m));
    }

    [Fact]
    public void Bold_RendersAndStripsMarkers()
    {
        var m = TuiMarkdown.ToMarkup("this is **strong** text");
        var plain = TuiMarkup.Plain(m);
        Assert.Equal("this is strong text", plain);
        Assert.Contains("bold", m); // a bold tag is present
    }

    [Fact]
    public void InlineCode_RendersWithoutBackticks()
    {
        var m = TuiMarkdown.ToMarkup("run `dotnet build` now");
        var plain = TuiMarkup.Plain(m);
        Assert.Equal("run dotnet build now", plain);
        Assert.DoesNotContain("`", plain);
    }

    [Fact]
    public void LiteralBrackets_AreEscaped_NotParsedAsMarkup()
    {
        // "[red]" must survive as visible text, not be swallowed as a color tag.
        var m = TuiMarkdown.ToMarkup("see [red] and [/] markers");
        var plain = TuiMarkup.Plain(m);
        Assert.Contains("[red]", plain);
        Assert.Contains("[/]", plain);
    }

    [Fact]
    public void PlainText_PassesThroughUnchanged()
    {
        var m = TuiMarkdown.ToMarkup("just a normal sentence.");
        Assert.Equal("just a normal sentence.", TuiMarkup.Plain(m));
    }

    [Fact]
    public void Blockquote_GetsBar()
    {
        var m = TuiMarkdown.ToMarkup("> quoted");
        Assert.Contains("\u2502", TuiMarkup.Plain(m));
        Assert.Contains("quoted", TuiMarkup.Plain(m));
    }

    [Fact]
    public void Table_HeaderRow_RendersCellsJoinedByBar()
    {
        var m = TuiMarkdown.ToMarkup("| Name | Role |");
        var plain = TuiMarkup.Plain(m);
        Assert.Contains("Name", plain);
        Assert.Contains("Role", plain);
        Assert.Contains("\u2502", plain);          // cells joined by a vertical bar
        Assert.DoesNotContain("|", plain);          // raw pipes are gone
    }

    [Fact]
    public void Table_SeparatorRow_BecomesRule()
    {
        var m = TuiMarkdown.ToMarkup("|---|:--:|");
        var plain = TuiMarkup.Plain(m);
        Assert.Contains("\u2500", plain);           // horizontal rule
        Assert.DoesNotContain("-", plain);          // dashes consumed
    }

    [Fact]
    public void Table_DataRow_RendersCells()
    {
        var m = TuiMarkdown.ToMarkup("| Alice | Admin |");
        var plain = TuiMarkup.Plain(m);
        Assert.Contains("Alice", plain);
        Assert.Contains("Admin", plain);
    }

    [Fact]
    public void ThematicBreak_BecomesRule()
    {
        var plain = TuiMarkup.Plain(TuiMarkdown.ToMarkup("---"));
        Assert.Contains("\u2500", plain);
    }

    [Fact]
    public void Strikethrough_IsStyled_TextPreserved()
    {
        var m = TuiMarkdown.ToMarkup("this is ~~gone~~ now");
        Assert.Contains("strikethrough", m);
        Assert.Equal("this is gone now", TuiMarkup.Plain(m));
    }

    [Fact]
    public void Prose_WithSinglePipe_IsNotMistakenForTable()
    {
        // One interior pipe, no leading/trailing pipe -> stays prose, not a table.
        var plain = TuiMarkup.Plain(TuiMarkdown.ToMarkup("choose a path or b"));
        Assert.Equal("choose a path or b", plain);
    }
    [Fact]
    public void Link_RendersLabel_DropsUrl_Underlined()
    {
        var m = TuiMarkdown.ToMarkup("see [the docs](https://example.com/x) here");
        var plain = TuiMarkup.Plain(m);
        Assert.Equal("see the docs here", plain);
        Assert.DoesNotContain("https://example.com", plain);   // URL dropped in terminal
        Assert.DoesNotContain("(", plain);
        Assert.Contains("underline", m);                        // styled as a link
    }

    [Fact]
    public void Link_EmptyLabel_FallsBackToUrl()
    {
        var plain = TuiMarkup.Plain(TuiMarkdown.ToMarkup("bare [](https://example.com/y) link"));
        Assert.Contains("https://example.com/y", plain);
    }

    [Fact]
    public void Image_RendersAltText_DropsUrl()
    {
        var m = TuiMarkdown.ToMarkup("look ![a cat](https://img/cat.png) there");
        var plain = TuiMarkup.Plain(m);
        Assert.Equal("look a cat there", plain);
        Assert.DoesNotContain("img/cat.png", plain);
        Assert.DoesNotContain("!", plain);                      // the image bang is consumed
        Assert.Contains("italic", m);                           // alt text styled italic
    }

    [Fact]
    public void TaskList_Checked_GetsCheckGlyph_NoBrackets()
    {
        var plain = TuiMarkup.Plain(TuiMarkdown.ToMarkup("- [x] done thing"));
        Assert.Contains("\u2713", plain);                       // check mark
        Assert.Contains("done thing", plain);
        Assert.DoesNotContain("[x]", plain);
        Assert.DoesNotContain("[ ]", plain);
    }

    [Fact]
    public void TaskList_Unchecked_GetsBoxGlyph_NoBrackets()
    {
        var plain = TuiMarkup.Plain(TuiMarkdown.ToMarkup("- [ ] pending thing"));
        Assert.Contains("\u2610", plain);                       // empty ballot box
        Assert.Contains("pending thing", plain);
        Assert.DoesNotContain("[ ]", plain);
    }

    [Fact]
    public void Fence_IsDetected_WithAndWithoutLanguage()
    {
        Assert.True(TuiMarkdown.IsFence("```"));
        Assert.True(TuiMarkdown.IsFence("```python"));
        Assert.True(TuiMarkdown.IsFence("~~~"));
        Assert.True(TuiMarkdown.IsFence("   ```js"));
        Assert.False(TuiMarkdown.IsFence("`not a fence`"));
        Assert.False(TuiMarkdown.IsFence("plain text"));
    }

    [Fact]
    public void Fence_Info_ReturnsLanguage()
    {
        Assert.Equal("python", TuiMarkdown.FenceInfo("```python"));
        Assert.Equal("", TuiMarkdown.FenceInfo("```"));
        Assert.Equal("", TuiMarkdown.FenceInfo("not a fence"));
    }

    [Fact]
    public void CodeLine_RendersVerbatim_NoMarkdownTransforms()
    {
        // Markdown-special chars inside code must NOT be transformed and brackets must survive.
        var plain = TuiMarkup.Plain(TuiMarkdown.CodeLine("x = a[0] ** 2  # **not bold**"));
        Assert.Contains("a[0]", plain);
        Assert.Contains("**not bold**", plain);                 // literal, not bolded
        Assert.Contains("# ", plain);
    }
}
