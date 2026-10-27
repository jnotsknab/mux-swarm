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
        /// <summary>The declared process, when the manifest has a valid one.</summary>
        internal ExtensionProcessSpec? Process { get; init; }

        /// <summary>Lifecycle events the process subscribes to.</summary>
        internal IReadOnlyList<string> Events { get; init; } = [];

        /// <summary>Validated tool-call interceptors served by the process.</summary>
        internal IReadOnlyList<InterceptRule> Intercepts { get; init; } = [];

        /// <summary>Validated tools the process serves (see <see cref="ExtensionTool"/>).</summary>
        internal IReadOnlyList<ExtensionToolSpec> Tools { get; init; } = [];

        /// <summary>Cosmetic manifest keys this extension applied (spinner, quips, toolLabels, splash).</summary>
        internal IReadOnlyList<string> Overrides { get; init; } = [];

        /// <summary>True when this extension's footer template is the one applied.</summary>
        internal bool Footer { get; init; }

        /// <summary>True when this extension's markdown style is the one applied.</summary>
        internal bool Markdown { get; init; }

        /// <summary>TUI regions this extension styles (its valid <c>tui</c> block), in name order.</summary>
        internal IReadOnlyList<string> TuiRegions { get; init; } = [];

        /// <summary>Validated slash commands the process serves.</summary>
        internal IReadOnlyList<ExtensionCommand> Commands { get; init; } = [];

        /// <summary>Validated web UI contributions (themes, CSS, fonts), or null.</summary>
        internal ExtensionWeb.Contribution? Web { get; init; }
    }

    /// <summary>A validated extension slash command: <c>/{Name}</c>, owned by <paramref name="Extension"/>.</summary>
    internal sealed record ExtensionCommand(string Extension, string Name, string Description, int TimeoutSeconds, bool Immediate = false)
    {
        /// <summary>The command as typed, e.g. <c>/standup</c>.</summary>
        internal string Cmd => "/" + Name;

        /// <summary>Argument hint shown after the description, or empty.</summary>
        internal string Usage { get; init; } = "";

        /// <summary>Subcommands (name, description), each shown as its own palette row.</summary>
        internal IReadOnlyList<(string Name, string Description)> Subcommands { get; init; } = [];
    }

    /// <summary>Most subcommands one extension command may list.</summary>
    internal const int MaxSubcommands = 24;

    /// <summary>Default and maximum <c>timeoutSeconds</c> for an extension command.</summary>
    internal const int DefaultCommandTimeoutSeconds = 60, MaxCommandTimeoutSeconds = 3600;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex CommandNamePattern();

    // Built-in command words that are not in TuiCommands.All (aliases and hidden commands).
    private static readonly string[] ReservedCommands =
        ["/bg", "/da", "/cont", "/dbg", "/nodbg", "/dv", "/sub", "/psub", "/sav", "/keys", "/quit",
         "/removeagent", "/disabletools", "/startargs", "/ultraplan"];

    /// <summary>True when <paramref name="cmd"/> (e.g. <c>/help</c>) is a built-in command word.</summary>
    internal static bool IsBuiltInCommand(string cmd) =>
        ReservedCommands.Contains(cmd, StringComparer.OrdinalIgnoreCase)
        || Tui.TuiCommands.All.Any(e => string.Equals(e.Cmd.Split(' ', 2)[0], cmd, StringComparison.OrdinalIgnoreCase));

    /// <summary>Merged cosmetic overrides. Null members leave the built-ins in place.</summary>
    internal sealed record Cosmetics(string[]? Spinner, string[]? Quips, string[]? Splash, IReadOnlyDictionary<string, string>? ToolLabels)
    {
        /// <summary>Thinking-indicator timing from a <c>thinking</c> block; null = built-in.</summary>
        internal ThinkingIndicator.Timing? Timing { get; init; }
    }

    /// <summary>Default and maximum <c>timeoutSeconds</c> for an extension tool call.</summary>
    internal const int DefaultToolTimeoutSeconds = 60, MaxToolTimeoutSeconds = 600;

    // Exposed names are "{extension}_{tool}"; providers cap function names at 64 chars.
    private const int MaxExposedToolName = 64;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$")]
    private static partial Regex ToolNamePattern();

    /// <summary>Result of a scan: what loaded, the merged contributions, and human-readable problems.</summary>
    /// <summary>
    /// Two extensions set the same slot (<paramref name="What"/>, e.g. <c>footer template</c>):
    /// <paramref name="Winner"/>'s value applies and <paramref name="Loser"/>'s is ignored.
    /// </summary>
    internal sealed record Conflict(string What, string Winner, string Loser);

    internal sealed record Result(IReadOnlyList<Extension> Extensions, IReadOnlyList<Theme> Themes, IReadOnlyList<string> Problems)
    {
        /// <summary>Slots set by more than one extension, and which one won.</summary>
        internal IReadOnlyList<Conflict> Conflicts { get; init; } = [];

        /// <summary>Installed extensions skipped because <c>extensions.disabled</c> lists them.</summary>
        internal IReadOnlyList<Extension> Disabled { get; init; } = [];

        /// <summary>Merged cosmetic overrides; all-null members = built-ins.</summary>
        internal Cosmetics Cosmetics { get; init; } = new(null, null, null, null);

        /// <summary>Footer segment ids in display order; null = built-in footer.</summary>
        internal IReadOnlyList<string>? FooterTemplate { get; init; }

        /// <summary>Markdown glyph style; null = built-in.</summary>
        internal Tui.MarkdownStyle? Markdown { get; init; }

        /// <summary>Merged per-region TUI styles (<c>region.property</c> -&gt; value); null = built-in look.</summary>
        internal IReadOnlyDictionary<string, string>? TuiStyles { get; init; }
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
    internal static Result Load(ExtensionsConfig? cfg, TuiManifest? userStyle = null)
    {
        Loaded = cfg is { Enabled: true } ? Scan(SearchDirectories(cfg), cfg.Disabled) : Empty;
        if (userStyle is not null) Loaded = WithUserStyle(Loaded, userStyle);
        Theme.SetExtensionThemes(Loaded.Themes);
        var c = Loaded.Cosmetics;
        ThinkingIndicator.SetExtensionCosmetics(c.Spinner, c.Quips, c.Timing);
        Tui.ToolActionLabel.SetExtensionVerbs(c.ToolLabels);
        SplashMessages.SetExtensionLines(c.Splash);
        Tui.TuiComponents.SetFooterTemplate(Loaded.FooterTemplate);
        Tui.MarkdownStyle.Set(Loaded.Markdown);
        Tui.RegionStyle.Set(Loaded.TuiStyles);
        ExtensionCommands.Set(Loaded.Extensions.SelectMany(e => e.Commands));
        return Loaded;
    }

    /// <summary>Name used for the user's <c>console.tui</c> styles in problems and conflicts.</summary>
    internal const string UserStyleOwner = "console.tui";

    // console.tui wins over every extension per property (the web's mux.user beats mux.ext the same way).
    private static Result WithUserStyle(Result loaded, TuiManifest user)
    {
        var problems = new List<string>(loaded.Problems);
        if (BuildTuiStyles(user, problems, UserStyleOwner) is not { } mine) return loaded with { Problems = problems };
        var styles = new Dictionary<string, string>(loaded.TuiStyles ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in mine) styles[k] = v;
        return loaded with { Problems = problems, TuiStyles = styles };
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

    /// <summary>True when <paramref name="name"/> is a valid extension name (a-z, 0-9, '-', max 32).</summary>
    internal static bool IsValidName(string? name) => name is not null && IdPattern().IsMatch(name);

    /// <summary>
    /// Scan <paramref name="searchDirs"/> for extension folders and merge them in name order.
    /// Extensions named in <paramref name="disabled"/> are listed but contribute nothing.
    /// Missing directories are ignored. Pure apart from file reads (no global state), for tests.
    /// </summary>
    internal static Result Scan(IEnumerable<string> searchDirs, IEnumerable<string>? disabled = null)
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
        string[]? spinner = null, quips = null, splash = null;
        ThinkingIndicator.Timing? timing = null;
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string>? footer = null;
        Tui.MarkdownStyle? markdown = null;
        var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var styleOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // region.property -> extension
        var takenCommands = new Dictionary<string, string>(StringComparer.Ordinal);   // command -> owning extension
        var conflicts = new List<Conflict>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);          // slot -> extension that set it
        var off = new HashSet<string>(disabled ?? [], StringComparer.Ordinal);
        var skipped = new List<Extension>();
        foreach (var (name, dir, manifest) in found.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (off.Contains(name))
            {
                skipped.Add(new Extension(name, manifest.Version, manifest.Description, dir, []));
                continue;
            }
            // Later names win a slot; the earlier owner is recorded as the loser.
            void Claim(string slot)
            {
                if (owners.TryGetValue(slot, out var prior)) conflicts.Add(new Conflict(slot, name, prior));
                owners[slot] = name;
            }
            var mine = new List<string>();
            foreach (var spec in manifest.Themes ?? [])
            {
                if (BuildTheme(spec, out var error) is not { } theme)
                {
                    problems.Add($"{name}: {error}");
                    continue;
                }
                int prior = themes.FindIndex(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
                if (prior >= 0) themes.RemoveAt(prior);
                Claim($"theme '{theme.Name.ToLowerInvariant()}'");
                themes.Add(theme);
                mine.Add(theme.Name);
            }
            var overrides = new List<string>();
            // List overrides: the last extension (by name) that sets one wins.
            void TakeList(string key, List<string>? items, int maxItems, int maxChars, bool frames, ref string[]? slot)
            {
                if (items is null) return;
                if (CheckTexts(items, maxItems, maxChars, frames) is { } err) { problems.Add($"{name}: {key} {err}; skipped."); return; }
                Claim(key);
                slot = [.. items];
                overrides.Add(key);
            }
            // thinking.frames / thinking.quips win over the top-level spinner / quips of the same extension.
            TakeList("spinner", manifest.Thinking?.Frames ?? manifest.Spinner, 64, 32, frames: true, ref spinner);
            TakeList("quips", manifest.Thinking?.Quips ?? manifest.Quips, 200, 80, frames: false, ref quips);
            if (manifest.Thinking is { } thinking)
            {
                var (t, err) = BuildTiming(thinking);
                if (err is not null) problems.Add($"{name}: thinking {err}; timing skipped.");
                else
                {
                    Claim("thinking");
                    timing = t;
                    overrides.Add("thinking");
                }
            }
            TakeList("splash", manifest.Splash, 200, 80, frames: false, ref splash);
            if (manifest.ToolLabels is { } toolLabels)
            {
                if (CheckToolLabels(toolLabels) is { } err) problems.Add($"{name}: toolLabels {err}; skipped.");
                else
                {
                    foreach (var (verb, label) in toolLabels) labels[verb.Trim()] = label.Trim();
                    overrides.Add("toolLabels");
                }
            }
            bool hasFooter = false;
            if (manifest.Footer?.Template is { } template)
            {
                if (ParseFooterTemplate(template, problems, name) is { } ids)
                {
                    Claim("footer template");
                    footer = ids;
                    hasFooter = true;
                }
            }
            var process = manifest.Process;
            if (process is not null && string.IsNullOrWhiteSpace(process.Command))
            {
                problems.Add($"{name}: process.command is empty; process ignored.");
                process = null;
            }
            bool hasMarkdown = false;
            if (manifest.Markdown is { } md && BuildMarkdown(md, problems, name) is { } style)
            {
                Claim("markdown style");
                markdown = style;
                hasMarkdown = true;
            }
            var tuiRegions = new List<string>();
            if (manifest.Tui is { } tui && BuildTuiStyles(tui, problems, name) is { } mineStyles)
            {
                // Later names win per property; one conflict per region and loser.
                foreach (var (key, value) in mineStyles)
                {
                    var region = key[..key.IndexOf('.')];
                    if (styleOwners.TryGetValue(key, out var prior)
                        && !conflicts.Any(c => c.What == $"tui region '{region}'" && c.Winner == name && c.Loser == prior))
                        conflicts.Add(new Conflict($"tui region '{region}'", name, prior));
                    styleOwners[key] = name;
                    styles[key] = value;
                    if (!tuiRegions.Contains(region)) tuiRegions.Add(region);
                }
            }
            var commands = new List<ExtensionCommand>();
            foreach (var c in manifest.Commands ?? [])
            {
                var cname = (c.Name ?? "").Trim().TrimStart('/').ToLowerInvariant();
                if (process is null) { problems.Add($"{name}: command '/{cname}' needs a process; skipped."); continue; }
                if (!CommandNamePattern().IsMatch(cname)) { problems.Add($"{name}: invalid command name '{c.Name}' (a-z, 0-9, '-', max 32); skipped."); continue; }
                if (IsBuiltInCommand("/" + cname)) { problems.Add($"{name}: command '/{cname}' is a built-in; skipped."); continue; }
                if (takenCommands.TryGetValue(cname, out var owner)) { conflicts.Add(new Conflict($"command /{cname}", owner, name)); continue; }
                takenCommands[cname] = name;
                int timeout = Math.Clamp(c.TimeoutSeconds ?? DefaultCommandTimeoutSeconds, 1, MaxCommandTimeoutSeconds);
                commands.Add(new ExtensionCommand(name, cname, OneLine(c.Description ?? "", 120), timeout, c.Immediate == true)
                {
                    Usage = OneLine(c.Usage ?? "", 60),
                    Subcommands = BuildSubcommands(name, cname, c.Subcommands, problems),
                });
            }
            var web = manifest.Web is { } wm ? ExtensionWeb.Build(name, dir, wm, problems) : null;
            foreach (var wt in web?.Themes ?? []) Claim($"web theme '{wt.Id}'");
            extensions.Add(new Extension(name, manifest.Version, manifest.Description, dir, mine)
            {
                Web = web,
                Markdown = hasMarkdown,
                TuiRegions = tuiRegions,
                Commands = commands,
                Overrides = overrides,
                Footer = hasFooter,
                Process = process,
                Events = (manifest.Events ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList(),
                Intercepts = BuildIntercepts(name, process, manifest.Intercepts, problems),
                Tools = ValidateTools(name, manifest.Tools ?? [], process is not null, problems),
            });
        }
        var cosmetics = new Cosmetics(spinner, quips, splash, labels.Count == 0 ? null : labels) { Timing = timing };
        return new Result(extensions, themes, problems)
        {
            Cosmetics = cosmetics, FooterTemplate = footer, Markdown = markdown, Conflicts = conflicts, Disabled = skipped,
            TuiStyles = styles.Count == 0 ? null : styles,
        };
    }

    /// <summary>
    /// Validate a <c>tui</c> block into <c>region.property</c> -&gt; value. Any bad key reports every problem
    /// and skips the whole block (null), so a half-applied style never shows.
    /// </summary>
    internal static Dictionary<string, string>? BuildTuiStyles(TuiManifest tui, List<string> problems, string extension)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var set = tui.Glyphs?.Trim().ToLowerInvariant();
        if (set is not null && !Tui.RegionStyle.GlyphSets.Contains(set))
            errors.Add($"glyphs '{tui.Glyphs}' is not one of {string.Join(", ", Tui.RegionStyle.GlyphSets)}");
        else if (set == "ascii")
            foreach (var (k, v) in Tui.RegionStyle.AsciiGlyphs) result[k] = v;
        foreach (var (region, props) in tui.Regions ?? [])
            foreach (var (prop, value) in props ?? [])
            {
                var key = $"{region.Trim()}.{prop.Trim()}";
                if (Tui.RegionStyle.Validate(key, value, out var normalized) is { } err) errors.Add(err);
                else result[Tui.RegionStyle.Props.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase))] = normalized;
            }
        if (errors.Count > 0)
        {
            foreach (var e in errors) problems.Add($"{extension}: tui {e}; tui block skipped.");
            return null;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Validate a <c>markdown</c> block over the built-in style. Invalid keys are reported and keep
    /// their built-in glyph; null when nothing valid was set.
    /// </summary>
    internal static Tui.MarkdownStyle? BuildMarkdown(MarkdownManifest md, List<string> problems, string extension)
    {
        var style = Tui.MarkdownStyle.Default;
        bool any = false;
        if (md.TableBorder is { } border)
        {
            var b = border.Trim().ToLowerInvariant();
            if (Tui.MarkdownStyle.BorderStyles.Contains(b)) { style = style with { Border = b }; any = true; }
            else problems.Add($"{extension}: markdown.tableBorder '{border}' is not one of {string.Join(", ", Tui.MarkdownStyle.BorderStyles)}; ignored.");
        }
        if (md.Bullet is { } bullet)
        {
            if (Tui.MarkdownStyle.IsOneCell(bullet)) { style = style with { Bullet = bullet }; any = true; }
            else problems.Add($"{extension}: markdown.bullet must be one visible terminal cell; ignored.");
        }
        if (md.Rule is { } rule)
        {
            if (Tui.MarkdownStyle.IsOneCell(rule)) { style = style with { Rule = rule }; any = true; }
            else problems.Add($"{extension}: markdown.rule must be one visible terminal cell; ignored.");
        }
        return any ? style : null;
    }

    /// <summary>
    /// Validate a command's <c>subcommands</c>: names like command names, no duplicates, at most
    /// <see cref="MaxSubcommands"/>. Bad entries become problems and are dropped.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Description)> BuildSubcommands(string ext, string command,
        IEnumerable<ExtensionSubcommandSpec>? specs, List<string> problems)
    {
        var subs = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs ?? [])
        {
            var sub = (spec.Name ?? "").Trim().ToLowerInvariant();
            string? error =
                !CommandNamePattern().IsMatch(sub) ? $"invalid subcommand '{spec.Name}' (a-z, 0-9, '-', max 32)" :
                !seen.Add(sub) ? $"duplicate subcommand '{sub}'" :
                subs.Count >= MaxSubcommands ? $"more than {MaxSubcommands} subcommands" :
                null;
            if (error is not null) { problems.Add($"{ext}: /{command}: {error}; skipped."); continue; }
            subs.Add((sub, OneLine(spec.Description ?? "", 120)));
        }
        return subs;
    }

    /// <summary>Validate <c>intercepts</c> entries; invalid ones become problems and are dropped.</summary>
    internal static IReadOnlyList<InterceptRule> BuildIntercepts(string name, ExtensionProcessSpec? process,
        IEnumerable<InterceptSpec>? specs, List<string> problems)
    {
        var rules = new List<InterceptRule>();
        foreach (var spec in specs ?? [])
        {
            var tool = spec.Tool?.Trim() ?? "";
            var on = (spec.On ?? "before").Trim().ToLowerInvariant();
            var onTimeout = (spec.OnTimeout ?? "allow").Trim().ToLowerInvariant();
            string? error =
                process is null ? "intercepts need a process" :
                tool.Length == 0 || tool.IndexOf('*') is >= 0 and var star && star != tool.Length - 1 ? $"intercept tool '{tool}' is invalid ('*' only at the end)" :
                on is not ("before" or "after" or "both") ? $"intercept 'on' must be before, after or both (got '{spec.On}')" :
                onTimeout is not ("allow" or "deny") ? $"intercept 'onTimeout' must be allow or deny (got '{spec.OnTimeout}')" :
                spec.TimeoutMs is <= 0 ? "intercept timeoutMs must be positive" :
                null;
            if (error is not null) { problems.Add($"{name}: {error}; skipped."); continue; }
            var timeout = TimeSpan.FromMilliseconds(Math.Min(spec.TimeoutMs ?? 2000, (int)ToolInterceptor.MaxTimeout.TotalMilliseconds));
            rules.Add(new InterceptRule(tool, on is "before" or "both", on is "after" or "both", timeout, onTimeout == "deny"));
        }
        return rules;
    }

    // Keep the well-formed tools; report the rest. Tools need a process to answer them.
    private static List<ExtensionToolSpec> ValidateTools(string ext, List<ExtensionToolSpec> specs, bool hasProcess, List<string> problems)
    {
        var ok = new List<ExtensionToolSpec>();
        if (specs.Count > 0 && !hasProcess) { problems.Add($"{ext}: tools need a process; tools ignored."); return ok; }
        foreach (var spec in specs)
        {
            var toolName = spec?.Name?.Trim() ?? "";
            string? error =
                spec is null || !ToolNamePattern().IsMatch(toolName) ? $"tool name '{toolName}' is invalid (a-z, 0-9, '_', starting with a letter, max 48)"
                : ext.Length + 1 + toolName.Length > MaxExposedToolName ? $"tool '{toolName}': '{ext}_{toolName}' is longer than {MaxExposedToolName} characters"
                : ok.Any(t => t.Name == toolName) ? $"duplicate tool '{toolName}'"
                : string.IsNullOrWhiteSpace(spec.Description) || spec.Description.Length > 1024 ? $"tool '{toolName}' needs a description (max 1024 chars)"
                : spec.Parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null) ? $"tool '{toolName}': parameters must be a JSON Schema object"
                : spec.TimeoutSeconds is < 1 or > MaxToolTimeoutSeconds ? $"tool '{toolName}': timeoutSeconds must be 1-{MaxToolTimeoutSeconds}"
                : null;
            if (error is not null) { problems.Add($"{ext}: {error}; skipped."); continue; }
            ok.Add(new ExtensionToolSpec
            {
                Name = toolName,
                Description = spec!.Description.Trim(),
                Parameters = spec.Parameters.ValueKind == JsonValueKind.Object ? spec.Parameters.Clone() : EmptySchema,
                TimeoutSeconds = spec.TimeoutSeconds ?? DefaultToolTimeoutSeconds,
                Agents = spec.Agents?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
            });
        }
        return ok;
    }

    private static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

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
            if (!Tui.RegionStyle.IsColor(color)) { error = $"theme '{name}': '{color}' is not a valid color for '{key}'."; return null; }
            prop.SetValue(theme, color);
        }
        error = "";
        return theme;
    }

    /// <summary>Validate the timing keys of a <c>thinking</c> block over the built-in timing. Error = the first bad key.</summary>
    internal static (ThinkingIndicator.Timing Timing, string? Error) BuildTiming(ThinkingManifest m)
    {
        var d = ThinkingIndicator.Timing.Default;
        if (m.IntervalMs is < 40 or > 1000) return (d, "intervalMs must be 40-1000");
        if (m.QuipIntervalMs is < 500 or > 60000) return (d, "quipIntervalMs must be 500-60000");
        if (m.Suffix is { } s && (s.Length > 8 || s.Any(char.IsControl))) return (d, "suffix must be at most 8 characters without control characters");
        return (new(m.IntervalMs ?? d.IntervalMs, m.QuipIntervalMs ?? d.QuipIntervalMs, m.Shuffle ?? d.Shuffle, m.Suffix ?? d.Suffix), null);
    }

    // Plain-text list check: 1..maxItems non-blank items, no control chars, at most maxChars each.
    // Spinner frames must also be 1-8 terminal cells wide. Null = valid, else the reason.
    private static string? CheckTexts(List<string> items, int maxItems, int maxChars, bool frames)
    {
        if (items.Count == 0 || items.Count > maxItems) return $"needs 1-{maxItems} entries";
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item)) return "has an empty entry";
            if (item.Any(char.IsControl)) return "has an entry with control characters";
            if (item.Length > maxChars) return $"has an entry longer than {maxChars} characters";
            if (frames && Tui.TuiMarkup.Width(item) is < 1 or > 8) return $"frame '{item}' must be 1-8 terminal cells wide";
        }
        return null;
    }

    [GeneratedRegex("^[a-z0-9]{1,32}$", RegexOptions.IgnoreCase)]
    private static partial Regex VerbPattern();

    // toolLabels check: keys are single verb stems (letters/digits), labels are short plain text.
    private static string? CheckToolLabels(Dictionary<string, string> labels)
    {
        if (labels.Count == 0 || labels.Count > 200) return "needs 1-200 entries";
        foreach (var (verb, label) in labels)
        {
            if (!VerbPattern().IsMatch(verb.Trim())) return $"key '{verb}' must be a single word of letters or digits";
            if (string.IsNullOrWhiteSpace(label) || label.Any(char.IsControl) || label.Trim().Length > 40)
                return $"label for '{verb}' must be 1-40 characters without control characters";
        }
        return null;
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
        // "||" between placeholders splits the footer: ids before it align left, ids after it align right.
        // (A single "|" stays plain decoration, as before.)
        int split = template.IndexOf("||", StringComparison.Ordinal);
        foreach (Match m in Placeholder().Matches(template))
        {
            var id = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (split >= 0 && m.Index > split && !ids.Contains(Tui.TuiComponents.FooterSplit)) ids.Add(Tui.TuiComponents.FooterSplit);
            if (!Tui.TuiComponents.FooterSegmentIds.Contains(id) && !Tui.TuiComponents.IsExtensionChipId(id))
                problems.Add($"{extension}: footer template has unknown segment '{{{m.Groups[1].Value}}}'; ignored.");
            else if (!ids.Contains(id)) ids.Add(id);
        }
        if (ids.Any(i => i != Tui.TuiComponents.FooterSplit)) return ids;
        problems.Add($"{extension}: footer template names no known segment; skipped.");
        return null;
    }

    private static string OneLine(string text, int max)
    {
        var s = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max];
    }

}
