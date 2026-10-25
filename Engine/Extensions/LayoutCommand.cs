namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// <c>/layout</c>: pick one TUI layout (an installed extension whose <c>tui</c> block sets <c>compose.shape</c>, e.g.
/// bubble, smooth, crt) or <c>standard</c> (none). Picking one enables it and disables the other layouts, so two
/// layouts never merge; colours stay with <c>/theme</c>. Other extensions are never touched.
/// </summary>
internal static class LayoutCommand
{
    /// <summary>The choice that turns every layout off (the built-in look).</summary>
    internal const string Standard = "standard";

    /// <summary>Installed layouts (enabled or disabled), in name order.</summary>
    internal static List<string> Layouts(ExtensionRegistry.Result loaded) =>
        loaded.Extensions.Concat(loaded.Disabled)
            .Where(e => SetsShape(e.Dir))
            .Select(e => e.Name).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>The enabled layout, or <see cref="Standard"/> (several enabled = the last one, which wins the merge).</summary>
    internal static string Active(ExtensionRegistry.Result loaded, IReadOnlyCollection<string> layouts) =>
        loaded.Extensions.Select(e => e.Name).Where(layouts.Contains).Order(StringComparer.Ordinal).LastOrDefault() ?? Standard;

    /// <summary>Enable <paramref name="choice"/> (or none for <see cref="Standard"/>) and disable the other layouts.
    /// False when nothing changed.</summary>
    internal static bool Apply(ExtensionsConfig cfg, IEnumerable<string> layouts, string choice)
    {
        bool changed = false;
        foreach (var name in layouts)
            changed |= ExtensionCatalog.SetEnabled(cfg, name, name == choice);
        return changed;
    }

    // True when the folder's manifest has tui.regions.compose.shape (read directly: disabled extensions are not parsed).
    private static bool SetsShape(string dir)
    {
        try
        {
            var path = Path.Combine(dir, ExtensionRegistry.ManifestFileName);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(path), Setup.Setup.CfgSerialOpts);
            return manifest?.Tui?.Regions is { } regions
                && regions.TryGetValue("compose", out var compose) && compose.ContainsKey("shape");
        }
        catch (Exception) { return false; }
    }
}
