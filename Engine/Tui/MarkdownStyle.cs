namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Glyphs the markdown renderer draws: the table border set, the bullet, and the horizontal rule.
/// The built-in style is heavy table borders, <c>•</c> and <c>─</c>; an extension can replace it
/// (<c>markdown</c> in extension.json). Swapped as a whole, never mutated (rendering runs on several threads).
/// </summary>
internal sealed record MarkdownStyle(string Border, string Bullet, string Rule)
{
    /// <summary>Table border style names.</summary>
    internal static readonly IReadOnlyList<string> BorderStyles = ["heavy", "light", "rounded", "ascii"];

    /// <summary>The built-in style.</summary>
    internal static readonly MarkdownStyle Default = new("heavy", "\u2022", "\u2500");

    private static volatile MarkdownStyle _active = Default;

    /// <summary>The style in effect.</summary>
    internal static MarkdownStyle Active => _active;

    /// <summary>Use <paramref name="style"/> (null = built-in). Called by the extension loader.</summary>
    internal static void Set(MarkdownStyle? style) => _active = style ?? Default;

    /// <summary>
    /// The 11 border glyphs for <see cref="Border"/>: horizontal, vertical, top-left, top-mid, top-right,
    /// mid-left, cross, mid-right, bottom-left, bottom-mid, bottom-right.
    /// </summary>
    internal string[] BorderGlyphs => Border switch
    {
        "light"   => ["\u2500", "\u2502", "\u250c", "\u252c", "\u2510", "\u251c", "\u253c", "\u2524", "\u2514", "\u2534", "\u2518"],
        "rounded" => ["\u2500", "\u2502", "\u256d", "\u252c", "\u256e", "\u251c", "\u253c", "\u2524", "\u2570", "\u2534", "\u256f"],
        "ascii"   => ["-", "|", "+", "+", "+", "+", "+", "+", "+", "+", "+"],
        _         => ["\u2501", "\u2503", "\u250f", "\u2533", "\u2513", "\u2523", "\u254b", "\u252b", "\u2517", "\u253b", "\u251b"],
    };

    /// <summary>True when <paramref name="glyph"/> is one printable, non-space terminal cell.</summary>
    internal static bool IsOneCell(string? glyph) =>
        glyph is { Length: > 0 and <= 4 } && !glyph.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) && TuiMarkup.Width(glyph) == 1;
}
