namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// What the <c>/extensions</c> panel shows and changes: one <see cref="Item"/> per installed
/// extension (enabled or not) with its contributions, conflicts and problems, plus the config edits
/// behind the panel keys (enable, trust). Pure, for tests; persisting and reloading is the caller's job.
/// </summary>
internal static class ExtensionCatalog
{
    /// <summary>One installed extension as the panel shows it.</summary>
    internal sealed record Item(string Name, string? Version, string? Description, string Dir, bool Enabled,
        bool HasProcess, bool Trusted, string Process, IReadOnlyList<string> Contributes,
        IReadOnlyList<ExtensionRegistry.Conflict> Conflicts, IReadOnlyList<string> Problems, IReadOnlyList<string> Stderr)
    {
        /// <summary>The manifest path.</summary>
        internal string Manifest => Path.Combine(Dir, ExtensionRegistry.ManifestFileName);
    }

    /// <summary>
    /// Items for every extension in <paramref name="loaded"/> (loaded and disabled), in name order.
    /// Each problem line goes to the extension it names (<c>name: ...</c>); the rest come back in
    /// <paramref name="other"/>.
    /// </summary>
    internal static List<Item> Build(ExtensionRegistry.Result loaded, ExtensionsConfig cfg, IEnumerable<string> problems,
        IReadOnlyList<ExtensionProcess> running, out List<string> other)
    {
        var all = problems.Distinct().ToList();
        var names = loaded.Extensions.Select(e => e.Name).Concat(loaded.Disabled.Select(e => e.Name)).ToHashSet(StringComparer.Ordinal);
        other = all.Where(p => !names.Any(n => p.StartsWith(n + ":", StringComparison.Ordinal))).ToList();
        var trusted = cfg.Trusted ?? [];
        var items = new List<Item>();
        foreach (var ext in loaded.Extensions)
        {
            var proc = running.FirstOrDefault(p => p.Name == ext.Name);
            bool trust = trusted.Contains(ext.Name);
            string state = ext.Process is null ? "" : proc is null ? (trust ? "not running" : "not trusted")
                : (proc.IsRunning ? "running" : "exited") + $" ({proc.Isolation})";
            var contributes = Contributions(ext);
            if (ext.Process is not null && ExtensionHost.ScopeOf(ext.Name, cfg.Agents) is { } scope)
                contributes.Insert(0, "agents: " + string.Join(", ", scope.Order(StringComparer.OrdinalIgnoreCase)));
            items.Add(new Item(ext.Name, ext.Version, ext.Description, ext.Dir, true, ext.Process is not null, trust, state,
                contributes, loaded.Conflicts.Where(c => c.Winner == ext.Name || c.Loser == ext.Name).ToList(),
                Strip(all, ext.Name), proc?.StderrTail.TakeLast(5).ToList() ?? []));
        }
        foreach (var ext in loaded.Disabled)
            items.Add(new Item(ext.Name, ext.Version, ext.Description, ext.Dir, false, false, trusted.Contains(ext.Name), "",
                [], [], Strip(all, ext.Name), []));
        return items.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    // Problems of one extension, without the "name: " prefix.
    private static List<string> Strip(List<string> problems, string name) =>
        problems.Where(p => p.StartsWith(name + ":", StringComparison.Ordinal)).Select(p => p[(name.Length + 1)..].Trim()).ToList();

    private static List<string> Contributions(ExtensionRegistry.Extension e)
    {
        var lines = new List<string>();
        if (e.Themes.Count > 0) lines.Add("themes: " + string.Join(", ", e.Themes));
        if (e.Overrides.Count > 0) lines.Add("overrides: " + string.Join(", ", e.Overrides));
        if (e.Footer) lines.Add("footer template");
        if (e.Markdown) lines.Add("markdown style");
        if (e.TuiRegions.Count > 0) lines.Add("tui styles: " + string.Join(", ", e.TuiRegions));
        if (e.Commands.Count > 0)
            lines.Add("commands: " + string.Join(", ", e.Commands.Select(c =>
                c.Subcommands.Count > 0 ? $"{c.Cmd} ({string.Join(" ", c.Subcommands.Select(s => s.Name))})" : c.Cmd)));
        var chips = ExtensionFooter.Snapshot().Where(c => c.Id.StartsWith(e.Name + ".", StringComparison.Ordinal)).Select(c => c.Id).ToList();
        if (chips.Count > 0) lines.Add("footer chips: " + string.Join(", ", chips));
        if (e.Tools.Count > 0)
            lines.Add("tools: " + string.Join(", ", e.Tools.Select(t => t.Agents is { Count: > 0 } a ? $"{t.Name} (agents: {string.Join(" ", a)})" : t.Name)));
        if (e.Intercepts.Count > 0)
            lines.Add("intercepts: " + string.Join(", ", e.Intercepts.Select(i => $"{i.Tool} ({(i.Before && i.After ? "both" : i.Before ? "before" : "after")})")));
        if (e.Events.Count > 0) lines.Add("events: " + string.Join(", ", e.Events));
        return lines;
    }

    /// <summary>Add or remove <paramref name="name"/> in <c>extensions.disabled</c>. False when nothing changed.</summary>
    internal static bool SetEnabled(ExtensionsConfig cfg, string name, bool enabled) => SetListed(cfg.Disabled ??= [], name, !enabled);

    /// <summary>Add or remove <paramref name="name"/> in <c>extensions.trusted</c>. False when nothing changed.</summary>
    internal static bool SetTrusted(ExtensionsConfig cfg, string name, bool trusted) => SetListed(cfg.Trusted ??= [], name, trusted);

    private static bool SetListed(List<string> list, string name, bool listed)
    {
        if (listed == list.Contains(name)) return false;
        if (listed) list.Add(name); else list.RemoveAll(n => n == name);
        return true;
    }
}
