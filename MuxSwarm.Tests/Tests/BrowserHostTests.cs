using MuxSwarm.Engine;
using MuxSwarm.Engine.Browser;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Built-in browser: platform map, launch flags, Playwright routing, URL handling, web panel wiring.</summary>
public class BrowserHostTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void EveryPublishedPlatform_HasAPinnedChecksum()
    {
        foreach (var p in new[] { "win64", "mac-arm64", "mac-x64", "linux64", "linux-arm64" })
            Assert.Matches("^[0-9A-F]{64}$", BrowserHost.Sha256[p]);
        Assert.NotNull(BrowserHost.Platform());   // every CI/dev host we run on is covered
    }

    [Fact]
    public void LaunchArgs_AreLoopbackOnly_WithAPersistentProfile()
    {
        var a = BrowserHost.LaunchArgs(9333, "/p");
        Assert.Contains("--remote-debugging-address=127.0.0.1", a);
        Assert.Contains("--remote-debugging-port=9333", a);
        Assert.Contains("--user-data-dir=/p", a);
        Assert.Contains("--headless=new", a);
        Assert.DoesNotContain("--headless=new", BrowserHost.LaunchArgs(9333, "/p", headless: false));
    }

    [Fact]
    public void Identity_LooksLikeARegularBrowser_NotHeadless()
    {
        var id = new BrowserIdentity("Google Chrome", "155.0.8059.80", "test");
        Assert.Matches(@"^Mozilla/5\.0 \(.+\) AppleWebKit/537\.36 \(KHTML, like Gecko\) Chrome/155\.0\.0\.0 Safari/537\.36$", id.UserAgent);
        Assert.DoesNotContain("Headless", id.UserAgent);
        var meta = id.OverrideParams("en-US,en;q=0.9")["userAgentMetadata"]!;
        Assert.Equal("Google Chrome", meta["brands"]![0]!["brand"]!.GetValue<string>());
        Assert.Equal("155", meta["brands"]![0]!["version"]!.GetValue<string>());
        Assert.Equal("155.0.8059.80", meta["fullVersionList"]![0]!["version"]!.GetValue<string>());
        Assert.EndsWith(" Edg/155.0.0.0", new BrowserIdentity("Microsoft Edge", "155.0.4283.62", "t").UserAgent);
        Assert.Equal(2, ((System.Text.Json.Nodes.JsonArray)new BrowserIdentity("Chromium", "155.0.1.2", "t").OverrideParams("en")["userAgentMetadata"]!["brands"]!).Count);
    }

    [Fact]
    public void KeyParams_OmitTextForNonTextKeys()
    {
        // DevTools rejects "text": null with "Invalid parameters" (every Backspace/Shift/Ctrl/keyUp).
        var shift = BrowserRelay.KeyParams("rawKeyDown", "Shift", "ShiftLeft", 16, 8, "");
        Assert.False(shift.ContainsKey("text"));
        Assert.False(BrowserRelay.KeyParams("keyUp", "a", "KeyA", 65, 0, "a").ContainsKey("text"));
        var a = BrowserRelay.KeyParams("keyDown", "a", "KeyA", 65, 0, "a");
        Assert.Equal("a", a["text"]!.GetValue<string>());
        Assert.Equal(65, a["windowsVirtualKeyCode"]!.GetValue<int>());
        foreach (var kv in shift) Assert.NotNull(kv.Value);
    }

    [Fact]
    public void HomePage_DefaultsToASearchPage_AndBlankMeansBlank()
    {
        Assert.Equal("https://duckduckgo.com", new ServeBrowserConfig().HomePage);
        Assert.Equal("about:blank", BrowserRelay.NormalizeUrl(""));
        Assert.Equal("https://example.com", BrowserRelay.NormalizeUrl("example.com"));
    }

    [Fact]
    public void AcceptLanguage_IsAPlainList_ChromeAddsWeights()
    {
        Assert.DoesNotContain(";q=", BrowserHost.AcceptLanguage);
        Assert.Matches(@"^[a-z]{2,3}(-[A-Za-z0-9]+)*(,[a-z]{2,3})?$", BrowserHost.AcceptLanguage);
    }

    [Fact]
    public void Identity_FallsBackToTheBundledVersion()
    {
        Assert.NotNull(BrowserIdentity.Current);   // detected or computed, never null
        Assert.DoesNotContain(BrowserHost.LaunchArgs(1, "/p"), a => a.Contains("HeadlessChrome"));
        Assert.Contains(BrowserHost.LaunchArgs(1, "/p"), a => a.StartsWith("--user-agent=Mozilla/5.0"));
    }

    [Fact]
    public void PlaywrightArgs_DropOwnBrowserFlags_AndAttachOverCdp()
    {
        var args = new[] { "-y", "@playwright/mcp@latest", "--browser", "chrome", "--executable-path", "C:/c.exe",
            "--user-data-dir=C:/prof", "--headless", "--no-sandbox", "--caps", "core,pdf", "--output-dir", "C:/out" };
        var outp = BrowserHost.PlaywrightArgs(args, "http://127.0.0.1:9333");
        Assert.Equal(new[] { "-y", "@playwright/mcp@latest", "--caps", "core,pdf", "--output-dir", "C:/out",
            "--cdp-endpoint", "http://127.0.0.1:9333" }, outp);
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("localhost:5173", "http://localhost:5173")]
    [InlineData("example.com:8443/x", "https://example.com:8443/x")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("mux swarm docs", "https://duckduckgo.com/?q=mux%20swarm%20docs")]
    [InlineData("", "about:blank")]
    public void NormalizeUrl(string input, string expected) => Assert.Equal(expected, BrowserRelay.NormalizeUrl(input));

    [Fact]
    public void Config_DefaultsOn_AndCanBeDisabled()
    {
        var d = new ServeConfig().Browser;
        Assert.True(d.Enabled); Assert.True(d.Playwright); Assert.True(d.Headless);
        var off = System.Text.Json.JsonSerializer.Deserialize<ServeConfig>("{\"browser\":{\"enabled\":false}}")!;
        Assert.False(off.Browser.Enabled);
    }

    [Fact]
    public void Panel_HasTheBrowserMode_AndNoReaderOrPopout()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));
        Assert.Contains("btn('muxBrowserMode', 'Browser: the built-in browser the agent uses'", html);
        Assert.Contains("/api/browser/ws", html);
        Assert.DoesNotContain("muxPreviewReader", html);
        Assert.DoesNotContain("muxPreviewPopout", html);
        Assert.DoesNotContain("r.jina.ai", html);
        Assert.Contains(".mux-preview-btn[hidden] { display: none; }", html);
        Assert.Contains("s.status === 'starting' || s.status === 'idle') setTimeout(pollDownload, 500);", html);
        var relay = File.ReadAllText(Path.Combine(RepoRoot(), "Engine", "Browser", "BrowserRelay.cs"));
        Assert.Contains("await pg.CallAsync(\"Page.bringToFront\");", relay);       // background tabs never paint
        Assert.Contains("\"Page.captureScreenshot\"", relay);                        // static page: send one picture now
        Assert.DoesNotContain("tcs.Task.Result", relay);                              // no AggregateException-wrapped errors
        Assert.Contains("[\"buttons\"] = (int)D(\"buttons\"),", relay);                 // held buttons: a move is a drag, not a hover
        Assert.Contains("try { cv.setPointerCapture(e.pointerId); } catch(_) {}", html);   // release reaches the page outside the view
        Assert.Contains("buttons: e.buttons, mods: mods(e) });", html);
        Assert.Contains("btn('muxBrowserReload', 'Refresh'", html);
        Assert.Contains("btn('muxBrowserHome', 'Home page'", html);
        Assert.Contains("send({ op: 'open', url: '' })", html);                        // new tab = home page   // keep polling until the download starts   // .mux-preview-btn sets display:flex
    }
}
