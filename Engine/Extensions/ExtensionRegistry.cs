using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Loads extensions from disk and hands their contributions to the components that use them.
/// Data-only today (themes). Inert unless <c>extensions.enabled</c> is true: nothing is scanned,
/// <see cref="Loaded"/> is empty, and every consumer falls back to built-in behavior.
/// </summary>
internal static partial class ExtensionRegistry
{
    /// <summary>Manifest file name expected at the root of every extension folder.</summary>
    internal const string ManifestFileName = "extension.json";

    private const long MaxManifestBytes = 256 * 1024;

    /// <summary>One successfully loaded extension, for listing.</summary>
    internal sealed record Extension(string Name, string? Version, string? Description, string Dir, IReadOnlyList<string> Themes)
    {
        /// <summary>True when this extension's footer template is the one applied.</summary>
        internal bool Footer { get; init; }
    }

    /// <summary>Result of a scan: what loaded, the merged contributions, and human-readable problems.</summary>
    internal sealed record Result(IReadOnlyList<Extension> Extensions, IReadOnlyList<Theme> Themes, IReadOnlyList<string> Problems)
    {
        /// <summary>Footer segment ids in display order; null = built-in footer.</summary>
        internal IReadOnlyList<string>? FooterTemplate { get; init; }
    }

    private static readonly Result Empty = new([], [], []);

    /// <summary>The last load result. Empty when extensions are off.</summary>
    internal static Result Loaded { get; private set; } = Empty;

    /// <summary>The default extensions folder: <c>&lt;install&gt;/Extensions</c>.</summary>
    internal static string DefaultDirectory => Path.Combine(PlatformContext.BaseDirectory, "Extensions");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// (Re)load extensions per <paramref name="cfg"/> and publish their contributions. Safe to call
    /// repeatedly (startup and <c>/refresh</c>); when disabled it clears everything it published.
    /// Never throws: per-extension failures become <see cref="Result.Problems"/>.
    /// </summary>
    internal static Result Load(ExtensionsConfig? cfg)
    {
        Loaded = cfg is { Enabled: true } ? Scan(SearchDirectories(cfg)) : Empty;
        Theme.SetExtensionThemes(Loaded.Themes);
        Tui.TuiComponents.SetFooterTemplate(Loaded.FooterTemplate);
        return Loaded;
    }

    /// <summary>The install folder followed by <see cref="ExtensionsConfig.Dirs"/>, resolved and de-duplicated.</summary>
    internal static IReadOnlyList<string> SearchDirectories(ExtensionsConfig cfg)
    {
        var dirs = new List<string> { DefaultDirectory };
        foreach (var d in cfg.Dirs ?? [])
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            dirs.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(d.Trim()), PlatformContext.BaseDirectory));
        }
        var cmp = PlatformContext.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return dirs.Select(Path.TrimEndingDirectorySeparator).Distinct(cmp).ToList();
    }

    /// <summary>
    /// Scan <paramref name="searchDirs"/> for extension folders and merge them in name order.
    /// Missing directories are ignored. Pure apart from file reads (no global state), for tests.
    /// </summary>
    internal static Result Scan(IEnumerable<string> searchDirs)
    {
        var problems = new List<string>();
        var found = new List<(string Name, string Dir, ExtensionManifest Manifest)>();

        foreach (var root in searchDirs)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            {
                var manifestPath = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifestPath)) continue;
                var folder = Path.GetFileName(dir);
                try
                {
                    if (new FileInfo(manifestPath).Length > MaxManifestBytes)
                    {
                        problems.Add($"{folder}: {ManifestFileName} is larger than {MaxManifestBytes / 1024} KB; skipped.");
                        continue;
                    }
                    var manifest = JsonSerializer.Deserialize<ExtensionManifest>(
                        File.ReadAllText(manifestPath), Setup.Setup.CfgSerialOpts) ?? new ExtensionManifest();
                    var name = (manifest.Name ?? folder).Trim();
                    if (!IdPattern().IsMatch(name))
                    {
                        problems.Add($"{folder}: invalid name '{name}' (use a-z, 0-9 and '-', max 32 chars); skipped.");
                        continue;
                    }
                    if (found.Any(f => f.Name == name))
                    {
                        problems.Add($"{name}: duplicate extension name at {dir}; skipped.");
                        continue;
                    }
                    found.Add((name, dir, manifest));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{folder}: could not read {ManifestFileName}: {ex.Message}");
                }
            }
        }

        var extensions = new List<Extension>();
        var themes = new List<Theme>();
        IReadOnlyList<string>? footer = null;
        foreach (var (name, dir, manifest) in found.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var mine = new List<string>();
            foreach (var spec in manifest.Themes ?? [])
            {
                if (BuildTheme(spec, out var error) is not { } theme)
                {
                    problems.Add($"{name}: {error}");
                    continue;
                }
                int prior = themes.FindIndex(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
                if (prior >= 0)
                {
                    problems.Add($"{name}: theme '{theme.Name}' replaces one from an earlier extension.");
                    themes.RemoveAt(prior);
                }
                themes.Add(theme);
                mine.Add(theme.Name);
            }
            bool hasFooter = false;
            if (manifest.Footer?.Template is { } template)
            {
                if (ParseFooterTemplate(template, problems, name) is { } ids)
                {
                    if (footer is not null) problems.Add($"{name}: footer template replaces the one from an earlier extension.");
                    footer = ids;
                    hasFooter = true;
                }
            }
            extensions.Add(new Extension(name, manifest.Version, manifest.Description, dir, mine) { Footer = hasFooter });
        }
        return new Result(extensions, themes, problems) { FooterTemplate = footer };
    }

    // Theme color roles settable from a manifest: every string property except Name.
    private static readonly Dictionary<string, PropertyInfo> ThemeRoles = typeof(Theme)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string) && p.Name != nameof(Theme.Name) && p.CanWrite)
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Build a theme from a manifest entry: <c>name</c> (required, must not shadow a built-in),
    /// optional <c>base</c> preset, and color-role overrides. Null + <paramref name="error"/> when invalid.
    /// </summary>
    internal static Theme? BuildTheme(Dictionary<string, string> spec, out string error)
    {
        var values = new Dictionary<string, string>(spec, StringComparer.OrdinalIgnoreCase);
        values.TryGetValue("name", out var name);
        name = name?.Trim() ?? "";
        if (!IdPattern().IsMatch(name)) { error = $"theme name '{name}' is invalid (use a-z, 0-9 and '-')."; return null; }
        if (Theme.Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        { error = $"theme '{name}' would shadow a built-in theme; skipped."; return null; }

        var baseName = values.TryGetValue("base", out var b) && !string.IsNullOrWhiteSpace(b) ? b.Trim() : "default";
        var basePreset = Theme.Presets.FirstOrDefault(p => string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase));
        if (basePreset is null) { error = $"theme '{name}': unknown base '{baseName}'."; return null; }

        var theme = basePreset with { Name = name };
        foreach (var (key, value) in values)
        {
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase) || key.Equals("base", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ThemeRoles.TryGetValue(key, out var prop)) { error = $"theme '{name}': unknown color role '{key}'."; return null; }
            var color = value?.Trim() ?? "";
            if (!IsColor(color)) { error = $"theme '{name}': '{color}' is not a valid color for '{key}'."; return null; }
            prop.SetValue(theme, color);
        }
        error = "";
        return theme;
    }

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// Read the <c>{id}</c> placeholders of a footer template in order. Unknown ids are reported to
    /// <paramref name="problems"/> and ignored; repeats are dropped. Null when nothing usable remains.
    /// </summary>
    internal static IReadOnlyList<string>? ParseFooterTemplate(string template, List<string> problems, string extension)
    {
        if (template.Length > 512) { problems.Add($"{extension}: footer template is longer than 512 characters; skipped."); return null; }
        var ids = new List<string>();
        foreach (Match m in Placeholder().Matches(template))
        {
            var id = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (!Tui.TuiComponents.FooterSegmentIds.Contains(id))
                problems.Add($"{extension}: footer template has unknown segment '{{{m.Groups[1].Value}}}'; ignored.");
            else if (!ids.Contains(id)) ids.Add(id);
        }
        if (ids.Count > 0) return ids;
        problems.Add($"{extension}: footer template names no known segment; skipped.");
        return null;
    }

    // A single Spectre color token (hex, named color or "default"); rejects styles and markup.
    private static bool IsColor(string value) =>
        value.Length is > 0 and <= 32
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '#' || c == '_')
        && Spectre.Console.Style.TryParse(value, out _);
}
