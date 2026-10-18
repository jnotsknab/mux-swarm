using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// The <c>web</c> block of <c>extension.json</c>: web UI themes with CSS, fonts and light/dark
/// variants, plus unscoped CSS. Static content only (CSS, fonts, images); extension JS is never served.
/// </summary>
internal sealed class WebManifest
{
    /// <summary>Web UI themes. Each becomes <c>ext-{id}</c> in the web theme picker.</summary>
    [JsonPropertyName("themes")]
    public List<WebThemeManifest> Themes { get; set; } = [];

    /// <summary>Unscoped stylesheets (paths under <see cref="Assets"/>), applied whenever the extension is enabled.</summary>
    [JsonPropertyName("css")]
    public List<string> Css { get; set; } = [];

    /// <summary>Folder (relative to the extension) served under <c>/ext/{name}/</c>. Default <c>web/</c>.</summary>
    [JsonPropertyName("assets")]
    public string? Assets { get; set; }
}

/// <summary>One web UI theme declared in an extension's <c>web</c> block.</summary>
internal sealed class WebThemeManifest
{
    /// <summary>Theme id: a-z, 0-9, '-', max 32. The web id is <c>ext-{id}</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Display name in the picker (defaults to the id).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>CSS variables for single-mode themes (used when there are no variants).</summary>
    [JsonPropertyName("vars")]
    public Dictionary<string, string>? Vars { get; set; }

    /// <summary>Optional <c>light</c> / <c>dark</c> variants, each with its own <c>vars</c>.</summary>
    [JsonPropertyName("variants")]
    public Dictionary<string, WebVariantManifest>? Variants { get; set; }

    /// <summary><c>light</c>, <c>dark</c> or <c>system</c> (follow the OS). Default <c>system</c> when variants exist.</summary>
    [JsonPropertyName("defaultVariant")]
    public string? DefaultVariant { get; set; }

    /// <summary>Stylesheets applied only while this theme is active (paths under the assets folder).</summary>
    [JsonPropertyName("css")]
    public List<string> Css { get; set; } = [];

    /// <summary>Font faces loaded while this theme is active.</summary>
    [JsonPropertyName("fonts")]
    public List<WebFontManifest> Fonts { get; set; } = [];
}

/// <summary>A light or dark variant of a web theme.</summary>
internal sealed class WebVariantManifest
{
    /// <summary>CSS variables for this variant.</summary>
    [JsonPropertyName("vars")]
    public Dictionary<string, string>? Vars { get; set; }

    /// <summary>Name of the matching TUI theme (metadata only; picking a web theme never changes <c>console.theme</c>).</summary>
    [JsonPropertyName("tui")]
    public string? Tui { get; set; }
}

/// <summary>One <c>@font-face</c> an extension web theme ships.</summary>
internal sealed class WebFontManifest
{
    /// <summary>CSS font-family name.</summary>
    [JsonPropertyName("family")]
    public string Family { get; set; } = "";

    /// <summary>Font file path under the assets folder (woff2, woff, ttf or otf).</summary>
    [JsonPropertyName("src")]
    public string Src { get; set; } = "";

    /// <summary>CSS font-weight (e.g. <c>400</c> or <c>100 900</c>). Default <c>400</c>.</summary>
    [JsonPropertyName("weight")]
    public string? Weight { get; set; }

    /// <summary>CSS font-style. Default <c>normal</c>.</summary>
    [JsonPropertyName("style")]
    public string? Style { get; set; }
}

/// <summary>
/// Validation, serving and <c>/api/themes</c> shaping for extension web content. Everything is
/// validated at load time; the HTTP handler re-checks the path against the extension folder.
/// </summary>
internal static partial class ExtensionWeb
{
    /// <summary>Per-file and per-extension size caps for web content.</summary>
    internal const long MaxCssBytes = 256 * 1024, MaxFontBytes = 2 * 1024 * 1024, MaxTotalBytes = 8 * 1024 * 1024;

    /// <summary>Content types for the file kinds <c>/ext/</c> serves; anything else is a 404.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css; charset=utf-8", [".woff2"] = "font/woff2", [".woff"] = "font/woff",
        [".ttf"] = "font/ttf", [".otf"] = "font/otf", [".svg"] = "image/svg+xml", [".png"] = "image/png",
        [".webp"] = "image/webp", [".json"] = "application/json; charset=utf-8",
    };

    private static readonly string[] FontExts = [".woff2", ".woff", ".ttf", ".otf"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex ThemeIdPattern();

    [GeneratedRegex("^--[A-Za-z0-9-]{1,64}$")]
    private static partial Regex VarNamePattern();

    [GeneratedRegex("^[A-Za-z0-9 ._-]{1,64}$")]
    private static partial Regex FamilyPattern();

    [GeneratedRegex("^[a-z0-9 ]{1,16}$")]
    private static partial Regex FontTokenPattern();

    /// <summary>A validated web theme, ready for <c>/api/themes</c> (URLs already resolved).</summary>
    internal sealed record Theme(string Id, string Name, string Dot, bool IsLight, IReadOnlyDictionary<string, string> Vars,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Variants, string? DefaultVariant,
        IReadOnlyList<string> Css, IReadOnlyList<Font> Fonts, string Source);

    /// <summary>A validated font face with its <c>/ext/</c> URL.</summary>
    internal sealed record Font(string Family, string Url, string Weight, string Style);

    /// <summary>The validated web contributions of one extension.</summary>
    internal sealed record Contribution(string AssetsDir, IReadOnlyList<Theme> Themes, IReadOnlyList<string> GlobalCss);

    /// <summary>
    /// Resolve <paramref name="relative"/> under <paramref name="root"/>, rejecting absolute paths,
    /// <c>..</c> segments, and anything whose real path (after following symlinks) leaves the root.
    /// Returns null when rejected or missing.
    /// </summary>
    internal static string? ResolveInside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var rel = relative.Replace('\\', '/');
        if (rel.StartsWith('/') || Path.IsPathRooted(rel) || rel.Contains(':')) return null;
        var segs = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0 || segs.Any(s => s is ".." or "." || s.Contains('\0'))) return null;
        var full = Path.GetFullPath(Path.Combine([root, .. segs]));
        if (!IsUnder(Path.GetFullPath(root), full) || !File.Exists(full)) return null;
        // Symlink escape: every existing link on the way must still resolve inside the real root.
        var realRoot = RealPath(Path.GetFullPath(root));
        var realFull = RealPath(full);
        return realRoot is not null && realFull is not null && IsUnder(realRoot, realFull) ? full : null;
    }

    private static bool IsUnder(string root, string path)
    {
        var cmp = PlatformContext.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = Path.TrimEndingDirectorySeparator(root);
        return path.Length > r.Length && path.StartsWith(r, cmp) && (path[r.Length] == Path.DirectorySeparatorChar || path[r.Length] == '/');
    }

    // Canonical path with every symlink component resolved (File/Directory.ResolveLinkTarget per segment).
    private static string? RealPath(string path)
    {
        try
        {
            var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string cur = Path.GetPathRoot(path) ?? "";
            int start = cur.Length == 0 ? 0 : cur.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
            for (int i = start; i < parts.Length; i++)
            {
                cur = Path.Combine(cur, parts[i]);
                FileSystemInfo fi = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
                if (fi.LinkTarget is not null)
                {
                    var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null) return null;
                    cur = Path.GetFullPath(target.FullName);
                }
            }
            return cur;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Validate an extension's <c>web</c> block. Invalid entries become <paramref name="problems"/>
    /// and are skipped; never throws. Returns null when nothing valid remains.
    /// </summary>
    internal static Contribution? Build(string name, string dir, WebManifest web, List<string> problems)
    {
        var assetsRel = string.IsNullOrWhiteSpace(web.Assets) ? "web" : web.Assets.Trim().TrimEnd('/', '\\');
        var assets = Path.GetFullPath(Path.Combine(dir, assetsRel.Replace('\\', '/')));
        if (!IsUnder(Path.GetFullPath(dir), assets) || !Directory.Exists(assets))
        {
            problems.Add($"{name}: web.assets '{assetsRel}' must be an existing folder inside the extension; web block skipped.");
            return null;
        }
        long total = 0;
        string? File_(string rel, string what, long cap, string[]? exts)
        {
            var path = ResolveInside(assets, rel);
            var ext = Path.GetExtension(rel);
            if (path is null) { problems.Add($"{name}: {what} '{rel}' is missing or outside {assetsRel}/; skipped."); return null; }
            if (!ContentTypes.ContainsKey(ext) || (exts is not null && !exts.Contains(ext, StringComparer.OrdinalIgnoreCase)))
            { problems.Add($"{name}: {what} '{rel}' has an unsupported file type; skipped."); return null; }
            var len = new FileInfo(path).Length;
            if (len > cap) { problems.Add($"{name}: {what} '{rel}' is larger than {cap / 1024} KB; skipped."); return null; }
            if (total + len > MaxTotalBytes) { problems.Add($"{name}: web content exceeds {MaxTotalBytes / (1024 * 1024)} MB; '{rel}' skipped."); return null; }
            total += len;
            return $"/ext/{name}/{string.Join('/', rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString))}";
        }

        var themes = new List<Theme>();
        foreach (var t in web.Themes ?? [])
        {
            var id = (t?.Id ?? "").Trim();
            if (t is null || !ThemeIdPattern().IsMatch(id)) { problems.Add($"{name}: web theme id '{id}' is invalid (a-z, 0-9, '-', max 32); skipped."); continue; }
            if (themes.Any(x => x.Id == "ext-" + id)) { problems.Add($"{name}: duplicate web theme '{id}'; skipped."); continue; }
            Dictionary<string, IReadOnlyDictionary<string, string>>? variants = null;
            foreach (var (vname, v) in t.Variants ?? [])
            {
                var key = vname.Trim().ToLowerInvariant();
                if (key is not ("light" or "dark")) { problems.Add($"{name}: web theme '{id}' variant '{vname}' must be light or dark; skipped."); continue; }
                (variants ??= [])[key] = Vars(v?.Vars, $"web theme '{id}' {key}");
            }
            var dv = (t.DefaultVariant ?? (variants is null ? null : "system"))?.Trim().ToLowerInvariant();
            if (dv is not null && dv is not ("light" or "dark" or "system"))
            { problems.Add($"{name}: web theme '{id}' defaultVariant must be light, dark or system; using system."); dv = "system"; }
            if (variants is not null && dv is "light" or "dark" && !variants.ContainsKey(dv)) dv = variants.Keys.First();
            // vars: dark variant, else light, else top-level vars (what an older web app will apply).
            var baseVars = variants is null ? Vars(t.Vars, $"web theme '{id}'")
                : variants.TryGetValue("dark", out var d) ? d : variants["light"];
            bool isLight = variants is null ? IsLightBg(baseVars) : !variants.ContainsKey("dark");
            var css = (t.Css ?? []).Select(c => File_(c, $"web theme '{id}' css", MaxCssBytes, [".css"])).OfType<string>().ToList();
            var fonts = new List<Font>();
            foreach (var f in t.Fonts ?? [])
            {
                if (f is null || !FamilyPattern().IsMatch(f.Family ?? "")) { problems.Add($"{name}: web theme '{id}' font family '{f?.Family}' is invalid; skipped."); continue; }
                var weight = (f.Weight ?? "400").Trim(); var style = (f.Style ?? "normal").Trim();
                if (!FontTokenPattern().IsMatch(weight) || !FontTokenPattern().IsMatch(style)) { problems.Add($"{name}: web theme '{id}' font '{f.Family}' has an invalid weight/style; skipped."); continue; }
                if (File_(f.Src ?? "", $"web theme '{id}' font", MaxFontBytes, FontExts) is { } url) fonts.Add(new Font(f.Family!, url, weight, style));
            }
            var dot = baseVars.TryGetValue("--accent", out var acc) ? acc : baseVars.TryGetValue("--bg", out var bg) ? bg : "#888888";
            themes.Add(new Theme("ext-" + id, string.IsNullOrWhiteSpace(t.Name) ? id : t.Name.Trim(), dot, isLight, baseVars,
                variants, dv, css, fonts, "extension:" + name));
        }
        var global = (web.Css ?? []).Select(c => File_(c, "web css", MaxCssBytes, [".css"])).OfType<string>().ToList();
        return themes.Count == 0 && global.Count == 0 ? null : new Contribution(assets, themes, global);

        IReadOnlyDictionary<string, string> Vars(Dictionary<string, string>? vars, string what)
        {
            var ok = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in vars ?? [])
            {
                // Values land inside a <style> rule: reject anything that could close it or start a new rule.
                if (!VarNamePattern().IsMatch(k) || v is null || v.Length > 512 || v.IndexOfAny(['{', '}', ';', '<', '>']) >= 0)
                { problems.Add($"{name}: {what} var '{k}' is invalid; skipped."); continue; }
                ok[k] = v.Trim();
            }
            return ok;
        }
    }

    private static bool IsLightBg(IReadOnlyDictionary<string, string> vars)
    {
        if (!vars.TryGetValue("--bg", out var bg) || bg is not { Length: 7 } || bg[0] != '#' || !bg.Skip(1).All(char.IsAsciiHexDigit)) return false;
        int r = Convert.ToInt32(bg[1..3], 16), g = Convert.ToInt32(bg[3..5], 16), b = Convert.ToInt32(bg[5..7], 16);
        return (0.299 * r + 0.587 * g + 0.114 * b) / 255 > 0.5;
    }

    /// <summary>
    /// <c>GET /ext/{name}/{**path}</c>: a static file from an enabled extension's assets folder.
    /// 404 for unknown or disabled extensions, unsupported types (never <c>.js</c>/<c>.html</c>) and
    /// any path that leaves the folder. Sends <c>nosniff</c>, <c>no-cache</c> and an ETag; SVG also gets
    /// <c>Content-Security-Policy: script-src 'none'</c>.
    /// </summary>
    internal static async Task HandleAsset(HttpContext context)
    {
        var name = context.Request.RouteValues["name"]?.ToString() ?? "";
        var rel = context.Request.RouteValues["path"]?.ToString() ?? "";
        var ext = ExtensionRegistry.Loaded.Extensions.FirstOrDefault(e => e.Name == name);
        var path = ext?.Web is { } web ? ResolveInside(web.AssetsDir, Uri.UnescapeDataString(rel)) : null;
        if (path is null || !ContentTypes.TryGetValue(Path.GetExtension(path), out var type))
        {
            context.Response.StatusCode = 404;
            return;
        }
        var bytes = await File.ReadAllBytesAsync(path);
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"";
        var h = context.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Cache-Control"] = "no-cache";
        h["ETag"] = etag;
        if (type.StartsWith("image/svg", StringComparison.Ordinal)) h["Content-Security-Policy"] = "script-src 'none'";
        if (context.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            context.Response.StatusCode = 304;
            return;
        }
        context.Response.ContentType = type;
        await context.Response.Body.WriteAsync(bytes);
    }

    /// <summary>Web themes and unscoped CSS from every enabled extension; later extension names win an id.</summary>
    internal static (List<Theme> Themes, List<string> GlobalCss) Collect()
    {
        var themes = new List<Theme>();
        var css = new List<string>();
        foreach (var e in ExtensionRegistry.Loaded.Extensions)
        {
            if (e.Web is not { } w) continue;
            foreach (var t in w.Themes)
            {
                themes.RemoveAll(x => x.Id == t.Id);
                themes.Add(t);
            }
            css.AddRange(w.GlobalCss);
        }
        return (themes, css);
    }
}
