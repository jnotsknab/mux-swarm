using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Extension "web" block: manifest validation, the /ext/ asset handler (traversal, types, headers)
// and the additive /api/themes shape. ConsoleState collection: Load() publishes process-global state.
[Collection("ConsoleState")]
public sealed class ExtensionWebTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-extweb-tests-" + Guid.NewGuid().ToString("N"));

    public ExtensionWebTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string AddExtension(string folder, string manifestJson, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(Path.Combine(dir, "web"));
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), manifestJson);
        foreach (var (p, c) in files)
        {
            var full = Path.Combine(dir, p.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, c);
        }
        return dir;
    }

    private ExtensionRegistry.Result Load() => ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });

    private const string Skin = """
        { "web": {
            "themes": [ { "id": "warm", "name": "Warm",
              "variants": { "light": { "vars": { "--bg": "#faf9f5", "--accent": "#d97757" }, "tui": "warm-light" },
                            "dark":  { "vars": { "--bg": "#262624", "--accent": "#d97757" } } },
              "css": ["skin.css"],
              "fonts": [ { "family": "Inter", "src": "fonts/inter.woff2", "weight": "100 900" } ] } ],
            "css": ["global.css"] } }
        """;

    private static readonly (string, string)[] SkinFiles =
        [("web/skin.css", "[data-mux=\"composer\"] { --mux-composer-radius: 20px; }"), ("web/global.css", ":root{}"),
         ("web/fonts/inter.woff2", "woff2"), ("web/evil.js", "alert(1)"), ("web/icon.svg", "<svg xmlns='http://www.w3.org/2000/svg'/>")];

    [Fact]
    public void WebBlock_BuildsThemeWithVariantsCssAndFonts()
    {
        AddExtension("warm", Skin, SkinFiles);
        var result = Load();
        Assert.Empty(result.Problems);
        var web = Assert.Single(result.Extensions).Web;
        Assert.NotNull(web);
        var t = Assert.Single(web.Themes);
        Assert.Equal("ext-warm", t.Id);
        Assert.Equal("system", t.DefaultVariant);
        Assert.False(t.IsLight);                                        // has a dark variant
        Assert.Equal("#262624", t.Vars["--bg"]);                        // vars = dark variant for older web apps
        Assert.Equal(["/ext/warm/skin.css"], t.Css);
        Assert.Equal("/ext/warm/fonts/inter.woff2", Assert.Single(t.Fonts).Url);
        Assert.Equal(["/ext/warm/global.css"], web.GlobalCss);
    }

    [Theory]
    [InlineData("../outside.css")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("missing.css")]
    [InlineData("evil.js")]
    public void WebBlock_RejectsBadCssPaths_AsProblems(string css)
    {
        File.WriteAllText(Path.Combine(_root, "outside.css"), "x");
        AddExtension("bad", $$"""{ "web": { "css": ["{{css}}"] } }""", ("web/evil.js", "x"));
        var result = Load();
        Assert.Null(Assert.Single(result.Extensions).Web);
        Assert.Contains(result.Problems, p => p.StartsWith("bad: web css", StringComparison.Ordinal));
    }

    [Fact]
    public void WebBlock_RejectsOversizeCss_AndInvalidVars()
    {
        AddExtension("big", """
            { "web": { "themes": [ { "id": "b", "vars": { "--bg": "#000000", "--x": "red; } body { color: red", "bad": "1" }, "css": ["big.css"] } ] } }
            """, ("web/big.css", new string('a', (int)ExtensionWeb.MaxCssBytes + 1)));
        var result = Load();
        var t = Assert.Single(Assert.Single(result.Extensions).Web!.Themes);
        Assert.Empty(t.Css);
        Assert.Equal(["--bg"], t.Vars.Keys);
        Assert.Contains(result.Problems, p => p.Contains("larger than"));
        Assert.Equal(2, result.Problems.Count(p => p.Contains("var '")));
    }

    [Fact]
    public void ResolveInside_RejectsTraversalVariants()
    {
        var dir = AddExtension("r", "{}", ("web/a.css", "x"));
        var web = Path.Combine(dir, "web");
        Assert.NotNull(ExtensionWeb.ResolveInside(web, "a.css"));
        foreach (var bad in new[] { "../extension.json", "..\\extension.json", "./a.css/../../extension.json", "", "/a.css", "C:a.css", "a.css\0" })
            Assert.Null(ExtensionWeb.ResolveInside(web, bad));
    }

    [Fact]
    public void ResolveInside_RejectsSymlinkEscape()
    {
        var dir = AddExtension("s", "{}");
        var secret = Path.Combine(_root, "secret.css");
        File.WriteAllText(secret, "x");
        var link = Path.Combine(dir, "web", "link.css");
        try { File.CreateSymbolicLink(link, secret); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no symlink privilege on this host
        Assert.Null(ExtensionWeb.ResolveInside(Path.Combine(dir, "web"), "link.css"));
    }

    private static async Task<(int Status, IHeaderDictionary Headers, string Body)> Get(string name, string path, string? ifNoneMatch = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.RouteValues["name"] = name;
        ctx.Request.RouteValues["path"] = path;
        if (ifNoneMatch is not null) ctx.Request.Headers.IfNoneMatch = ifNoneMatch;
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await ExtensionWeb.HandleAsset(ctx);
        return (ctx.Response.StatusCode, ctx.Response.Headers, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task ExtAsset_ServesCssWithHeaders_AndRevalidates()
    {
        AddExtension("warm", Skin, SkinFiles);
        Load();
        var (status, h, body) = await Get("warm", "skin.css");
        Assert.Equal(200, status);
        Assert.Contains("--mux-composer-radius", body);
        Assert.Equal("nosniff", h["X-Content-Type-Options"]);
        Assert.Equal("no-cache", h["Cache-Control"]);
        var (again, _, _) = await Get("warm", "skin.css", h["ETag"]);
        Assert.Equal(304, again);
        var (svg, sh, _) = await Get("warm", "icon.svg");
        Assert.Equal(200, svg);
        Assert.Equal("script-src 'none'", sh["Content-Security-Policy"]);
    }

    [Theory]
    [InlineData("warm", "evil.js")]                 // JS is never served
    [InlineData("warm", "../extension.json")]
    [InlineData("warm", "%2e%2e/extension.json")]  // URL-encoded traversal
    [InlineData("warm", "..%2fextension.json")]
    [InlineData("nope", "skin.css")]               // unknown extension
    public async Task ExtAsset_Returns404(string name, string path)
    {
        AddExtension("warm", Skin, SkinFiles);
        Load();
        Assert.Equal(404, (await Get(name, path)).Status);
    }

    [Fact]
    public async Task ExtAsset_DisabledExtension_ServesNothing()
    {
        AddExtension("warm", Skin, SkinFiles);
        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root], Disabled = ["warm"] });
        Assert.Equal(404, (await Get("warm", "skin.css")).Status);
        ExtensionRegistry.Load(new ExtensionsConfig { Enabled = false, Dirs = [_root] });
        Assert.Equal(404, (await Get("warm", "skin.css")).Status);
    }

    [Fact]
    public void ThemesPayload_KeepsOldFields_AndAddsWebFields()
    {
        AddExtension("warm", Skin, SkinFiles);
        AddExtension("tui", """{ "themes": [ { "name": "plain", "accent": "#112233" } ] }""");
        Load();
        var (items, globalCss) = ServeMode.ThemesPayload();
        var json = JsonSerializer.SerializeToElement(new { items, globalCss }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var arr = json.GetProperty("items").EnumerateArray().ToList();
        var plain = arr.Single(i => i.GetProperty("id").GetString() == "ext-plain");
        foreach (var key in new[] { "id", "name", "dot", "isLight", "vars" })
        {
            Assert.True(plain.TryGetProperty(key, out _), key);
            Assert.True(arr.Single(i => i.GetProperty("id").GetString() == "ext-warm").TryGetProperty(key, out _), key);
        }
        Assert.False(plain.TryGetProperty("css", out _));                // TUI-mapped shape unchanged
        var warm = arr.Single(i => i.GetProperty("id").GetString() == "ext-warm");
        Assert.Equal("#faf9f5", warm.GetProperty("variants").GetProperty("light").GetProperty("vars").GetProperty("--bg").GetString());
        Assert.Equal("extension:warm", warm.GetProperty("source").GetString());
        Assert.Equal("/ext/warm/global.css", json.GetProperty("globalCss")[0].GetString());
    }

    [Fact]
    public void WebTheme_SameIdInTwoExtensions_LaterNameWins_AndIsAConflict()
    {
        AddExtension("a", """{ "web": { "themes": [ { "id": "x", "vars": { "--bg": "#000000" } } ] } }""");
        AddExtension("b", """{ "web": { "themes": [ { "id": "x", "vars": { "--bg": "#ffffff" } } ] } }""");
        var result = Load();
        Assert.Contains(new ExtensionRegistry.Conflict("web theme 'ext-x'", "b", "a"), result.Conflicts);
        var (themes, _) = ExtensionWeb.Collect();
        Assert.Equal("#ffffff", Assert.Single(themes).Vars["--bg"]);
        Assert.True(Assert.Single(themes).IsLight);
    }
}
