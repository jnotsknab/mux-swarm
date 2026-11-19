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

    /// <summary>The shape the enabled extensions set on their own (without the user's <c>console.tui</c>), or <c>classic</c>.</summary>
    internal static string ExtensionShape() =>
        ExtensionRegistry.Loaded.ExtensionTuiStyles is { } s && s.TryGetValue("compose.shape", out var v) ? v : "classic";

    /// <summary>Set the user's <c>compose.shape</c> in <paramref name="console"/>. For <see cref="Standard"/> the override is
    /// removed, or set to <c>classic</c> when an extension sets another shape (<paramref name="extensionShape"/>), since
    /// removing it would bring that shape back. False when nothing changed. Other user region styles are kept.</summary>
    internal static bool Apply(ConsoleConfig console, string choice, string extensionShape = "classic")
    {
        var regions = console.Tui?.Regions;
        string? current = regions is not null && regions.TryGetValue("compose", out var c) && c.TryGetValue("shape", out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (choice == Standard && extensionShape != "classic") choice = "classic";
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
