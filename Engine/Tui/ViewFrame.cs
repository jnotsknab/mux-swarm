namespace MuxSwarm.Engine.Tui;

/// <summary>
/// The optional whole-view frame (<c>panel.frame</c>) for full-screen views: the view renders at the inner size and
/// its rows are boxed; the hit map gets a one-cell origin so the view's own click math stays view-relative.
/// </summary>
internal static class ViewFrame
{
    /// <summary>The active frame style, or null when views draw edge to edge (the default) or the screen is too
    /// small to spare two columns and rows.</summary>
    internal static string[]? Glyphs(int width, int height) =>
        RegionStyle.Choice("panel.frame") is var f && f != "none" && width >= 24 && height >= 8 ? ComposeShape.Borders[f] : null;

    /// <summary>The size a view gets inside the frame (unchanged without one).</summary>
    internal static (int Width, int Height) Inner(int width, int height) =>
        Glyphs(width, height) is null ? (width, height) : (width - 2, height - 2);

    /// <summary>Prepare <paramref name="hits"/> for a view rendered at <see cref="Inner"/> (origin 1,1 inside a frame).</summary>
    internal static void Prepare(MouseHitMap hits, int width, int height)
    {
        bool framed = Glyphs(width, height) is not null;
        hits.SetOrigin(framed ? 1 : 0, framed ? 1 : 0);
    }

    /// <summary>Box the view's <paramref name="rows"/> (markup, inner size) to <paramref name="width"/> x
    /// <paramref name="height"/>; unchanged without a frame.</summary>
    internal static List<string> Wrap(List<string> rows, int width, int height)
    {
        if (Glyphs(width, height) is not { } g) return rows;
        string fg = RegionStyle.Color("panel.frameFg");
        string E(string s) => Spectre.Console.Markup.Escape(s);
        int inner = width - 2;
        var outp = new List<string>(height) { $"[{fg}]{E(g[0] + string.Concat(Enumerable.Repeat(g[4], inner)) + g[1])}[/]" };
        for (int i = 0; i < height - 2; i++)
        {
            string row = i < rows.Count ? TuiMarkup.TruncateMarkup(rows[i], inner, "") : "";
            outp.Add($"[{fg}]{E(g[5])}[/]{row}{new string(' ', Math.Max(0, inner - TuiMarkup.MarkupWidth(row)))}[{fg}]{E(g[5])}[/]");
        }
        outp.Add($"[{fg}]{E(g[2] + string.Concat(Enumerable.Repeat(g[4], inner)) + g[3])}[/]");
        return outp;
    }
}
