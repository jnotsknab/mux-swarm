using System.Text.Json;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// <c>/layout</c>: pick the compose box shape from the fixed built-in set (<see cref="Tui.ComposeShape.Names"/>;
/// <c>standard</c> = the stock <c>classic</c> look). The choice is saved as <c>console.tui.regions.compose.shape</c>,
/// which wins over every extension per property; extensions are never enabled or disabled, so their colours,
/// footer position and other region styles still mix in. Colours stay with <c>/theme</c>.
/// </summary>
internal static class LayoutCommand
{
    /// <summary>The choice that removes the user override (the stock look, unless an extension sets a shape).</summary>
    internal const string Standard = "standard";

    /// <summary>Every choice: <see cref="Standard"/> then the non-default built-in shapes, in their declared order.</summary>
    internal static List<string> Choices() =>
        [Standard, .. Tui.ComposeShape.Names.Where(n => n != "classic")];

    /// <summary>The shape in effect: the user's override, else what extensions set, else <see cref="Standard"/>.</summary>
    internal static string Active()
    {
        string shape = Tui.RegionStyle.Choice("compose.shape");
        return shape == "classic" ? Standard : shape;
    }

    /// <summary>Set (or for <see cref="Standard"/> remove) the user's <c>compose.shape</c> in <paramref name="console"/>.
    /// False when nothing changed. Other user region styles are kept.</summary>
    internal static bool Apply(ConsoleConfig console, string choice)
    {
        var regions = console.Tui?.Regions;
        string? current = regions is not null && regions.TryGetValue("compose", out var c) && c.TryGetValue("shape", out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (choice == Standard)
        {
            if (current is null) return false;
            regions!["compose"].Remove("shape");
            if (regions["compose"].Count == 0) regions.Remove("compose");
            return true;
        }
        if (current == choice) return false;
        console.Tui ??= new TuiManifest();
        console.Tui.Regions ??= new(StringComparer.OrdinalIgnoreCase);
        if (!console.Tui.Regions.TryGetValue("compose", out var compose))
            console.Tui.Regions["compose"] = compose = new(StringComparer.OrdinalIgnoreCase);
        compose["shape"] = JsonSerializer.SerializeToElement(choice);
        return true;
    }
}
