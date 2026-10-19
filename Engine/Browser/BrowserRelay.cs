using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MuxSwarm.Engine.Browser;

/// <summary>
/// HTTP + WebSocket surface for the web UI's Browser tab. <c>/api/browser*</c> sits behind the
/// normal serve auth gate. The viewer socket carries a tiny JSON protocol:
/// client -&gt; <c>{op: tabs|open|close|activate|navigate|back|forward|reload|mouse|wheel|key|text|resize}</c>,
/// server -&gt; <c>{type: tabs|frame|status|error}</c>. The engine holds one DevTools page session for
/// the tab being viewed and forwards <c>Page.screencastFrame</c> images as JPEG.
/// </summary>
internal static class BrowserRelay
{
    /// <summary>Map the browser endpoints.</summary>
    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/browser", async ctx => await Json(ctx, 200, BrowserHost.Status()));
        app.MapPost("/api/browser/start", async ctx =>
        {
            if (!App.Config.Serve.Browser.Enabled) { await Json(ctx, 403, new { error = "serve.browser.enabled is false" }); return; }
            _ = Task.Run(async () => { try { await BrowserHost.EnsureRunningAsync(); } catch { /* reported via status */ } });
            await Json(ctx, 202, BrowserHost.Status());
        });
        app.Map("/api/browser/ws", Viewer);
    }

    private static async Task Json(HttpContext ctx, int code, object body)
    {
        ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    private static async Task Viewer(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest || !App.Config.Serve.Browser.Enabled) { ctx.Response.StatusCode = 400; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var sendLock = new SemaphoreSlim(1, 1);
        async Task Send(object o)
        {
            if (ws.State != WebSocketState.Open) return;
            var b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o));
            await sendLock.WaitAsync();
            try { await ws.SendAsync(b, WebSocketMessageType.Text, true, CancellationToken.None); } catch { } finally { sendLock.Release(); }
        }
        using var cts = new CancellationTokenSource();
        CdpPage? page = null;
        int w = 1280, h = 800;
        try
        {
            await Send(new { type = "status", status = "starting" });
            int port;
            try { port = await BrowserHost.EnsureRunningAsync(cts.Token); }
            catch (Exception ex) { await Send(new { type = "error", message = ex.Message }); return; }

            async Task SendTabs(string? activeId)
            {
                var pages = await BrowserHost.PagesAsync();
                await Send(new { type = "tabs", active = activeId, tabs = pages.Select(p => new { id = p.GetProperty("id").GetString(), title = p.GetProperty("title").GetString(), url = p.GetProperty("url").GetString() }) });
            }
            async Task Attach(string id)
            {
                var prev = page; page = null;
                if (prev != null)
                {
                    prev.OnEvent = null;
                    try { await prev.CallAsync("Page.stopScreencast"); } catch { }
                    await prev.DisposeAsync();
                }
                var pages = await BrowserHost.PagesAsync();
                var t = pages.FirstOrDefault(p => p.GetProperty("id").GetString() == id);
                if (t.ValueKind == JsonValueKind.Undefined) { await SendTabs(null); return; }
                var pg = await CdpPage.ConnectAsync(t.GetProperty("webSocketDebuggerUrl").GetString()!, cts.Token);
                pg.OnEvent = async (method, prm) =>
                {
                    if (method == "Page.screencastFrame")
                    {
                        await Send(new { type = "frame", data = prm?["data"]?.GetValue<string>() });
                        _ = pg.CallAsync("Page.screencastFrameAck", new JsonObject { ["sessionId"] = prm?["sessionId"]?.GetValue<int>() ?? 0 });
                    }
                    else if (method is "Page.frameNavigated" or "Page.loadEventFired") await SendTabs(id);
                };
                page = pg;
                await pg.CallAsync("Page.enable");
                await pg.CallAsync("Emulation.setUserAgentOverride", BrowserIdentity.Current.OverrideParams(BrowserHost.AcceptLanguage));
                // Headless Chrome only paints the foreground tab: a background tab never sends a frame.
                await pg.CallAsync("Page.bringToFront");
                await pg.CallAsync("Emulation.setDeviceMetricsOverride", new JsonObject { ["width"] = w, ["height"] = h, ["deviceScaleFactor"] = 1, ["mobile"] = false });
                await pg.CallAsync("Page.startScreencast", new JsonObject { ["format"] = "jpeg", ["quality"] = 70, ["maxWidth"] = w, ["maxHeight"] = h, ["everyNthFrame"] = 1 });
                // The screencast only sends frames on change; a static page needs one picture now.
                try
                {
                    var shot = await pg.CallAsync("Page.captureScreenshot", new JsonObject { ["format"] = "jpeg", ["quality"] = 70 });
                    if (shot?["data"]?.GetValue<string>() is { } d) await Send(new { type = "frame", data = d });
                }
                catch { }
                await SendTabs(id);
            }
            async Task<string?> NewTab(string url)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?{Uri.EscapeDataString(url)}");
                var r = await http.SendAsync(req);
                return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
            }

            var first = (await BrowserHost.PagesAsync()).FirstOrDefault();
            var firstId = first.ValueKind == JsonValueKind.Undefined ? await NewTab(BrowserHost.HomeUrl) : first.GetProperty("id").GetString();
            await Attach(firstId!);
            // The browser starts on about:blank; that first tab opens the home page instead.
            if (first.ValueKind != JsonValueKind.Undefined && first.GetProperty("url").GetString() == "about:blank" && page != null)
                await page.CallAsync("Page.navigate", new JsonObject { ["url"] = BrowserHost.HomeUrl });
            await Send(new { type = "status", status = "ready" });

            var buf = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var sb = new StringBuilder(); WebSocketReceiveResult res;
                do { res = await ws.ReceiveAsync(buf, cts.Token); if (res.MessageType == WebSocketMessageType.Close) return; sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count)); }
                while (!res.EndOfMessage);
                JsonNode? m; try { m = JsonNode.Parse(sb.ToString()); } catch { continue; }
                var op = m?["op"]?.GetValue<string>();
                // One failed input/navigation op is reported quietly (the panel shows a short notice);
                // only a broken browser (no session) replaces the view with an error.
                try { await Handle(op, m!); }
                catch (Exception ex) { await Send(new { type = page == null ? "error" : "notice", op, message = (ex.InnerException ?? ex).Message }); }
            }

            async Task Handle(string? op, JsonNode m)
            {
                string S(string k) => m[k]?.GetValue<string>() ?? "";
                double D(string k) => m[k]?.GetValue<double>() ?? 0;
                switch (op)
                {
                    case "tabs": await SendTabs(page?.TargetId); break;
                    case "open": { var id = await NewTab(string.IsNullOrWhiteSpace(S("url")) ? BrowserHost.HomeUrl : NormalizeUrl(S("url"))); if (id != null) await Attach(id); break; }
                    case "activate": await Attach(S("id")); break;
                    case "close":
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        await http.GetAsync($"http://127.0.0.1:{port}/json/close/{Uri.EscapeDataString(S("id"))}");
                        await Task.Delay(150);
                        var rest = await BrowserHost.PagesAsync();
                        if (rest.Count == 0) await Attach((await NewTab(BrowserHost.HomeUrl))!);
                        else if (page?.TargetId == S("id")) await Attach(rest[0].GetProperty("id").GetString()!);
                        else await SendTabs(page?.TargetId);
                        break;
                    }
                    case "navigate": if (page != null) await page.CallAsync("Page.navigate", new JsonObject { ["url"] = NormalizeUrl(S("url")) }); break;
                    case "back": case "forward":
                        if (page != null && await HistoryEntryAsync(page, op == "back" ? -1 : 1) is { } entry)
                            await page.CallAsync("Page.navigateToHistoryEntry", new JsonObject { ["entryId"] = entry }); break;
                    case "home": if (page != null) await page.CallAsync("Page.navigate", new JsonObject { ["url"] = BrowserHost.HomeUrl }); break;
                    case "reload": if (page != null) await page.CallAsync("Page.reload"); break;
                    case "resize":
                        w = Math.Clamp((int)D("w"), 320, 3840); h = Math.Clamp((int)D("h"), 240, 2160);
                        if (page != null) await Attach(page.TargetId); break;
                    case "mouse":
                        if (page != null) await page.CallAsync("Input.dispatchMouseEvent", new JsonObject
                        {
                            ["type"] = S("type"), ["x"] = D("x"), ["y"] = D("y"), ["button"] = string.IsNullOrEmpty(S("button")) ? "none" : S("button"),
                            ["clickCount"] = (int)D("clicks"), ["modifiers"] = (int)D("mods"),
                        }); break;
                    case "wheel":
                        if (page != null) await page.CallAsync("Input.dispatchMouseEvent", new JsonObject
                        { ["type"] = "mouseWheel", ["x"] = D("x"), ["y"] = D("y"), ["deltaX"] = D("dx"), ["deltaY"] = D("dy") }); break;
                    case "key":
                        if (page != null) await page.CallAsync("Input.dispatchKeyEvent", KeyParams(S("type"), S("key"), S("code"), (int)D("keyCode"), (int)D("mods"), S("text"))); break;
                    case "text": if (page != null) await page.CallAsync("Input.insertText", new JsonObject { ["text"] = S("text") }); break;
                }
            }
        }
        catch (Exception) when (cts.IsCancellationRequested || ws.State != WebSocketState.Open) { }
        catch (WebSocketException) { }
        finally
        {
            cts.Cancel();
            if (page != null) { try { await page.CallAsync("Page.stopScreencast"); } catch { } await page.DisposeAsync(); }
        }
    }

    /// <summary>
    /// <c>Input.dispatchKeyEvent</c> params. DevTools rejects a null string field ("Invalid
    /// parameters"), so <c>text</c> is only included for keys that produce text.
    /// </summary>
    internal static JsonObject KeyParams(string type, string key, string code, int keyCode, int mods, string text)
    {
        var p = new JsonObject { ["type"] = type, ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = keyCode, ["modifiers"] = mods };
        if (!string.IsNullOrEmpty(text) && type != "keyUp") { p["text"] = text; p["unmodifiedText"] = text; }
        return p;
    }

    /// <summary>History entry id <paramref name="delta"/> steps from the current one, or null at either end.</summary>
    private static async Task<int?> HistoryEntryAsync(CdpPage page, int delta)
    {
        var h = await page.CallAsync("Page.getNavigationHistory");
        var i = (h?["currentIndex"]?.GetValue<int>() ?? 0) + delta;
        var entries = h?["entries"] as JsonArray;
        return entries is not null && i >= 0 && i < entries.Count ? entries[i]?["id"]?.GetValue<int>() : null;
    }

    /// <summary>Bare host or path -&gt; https URL; keeps explicit schemes (http, https, about, file, data).</summary>
    internal static string NormalizeUrl(string input)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) return "about:blank";
        // host:port (localhost:5173, 127.0.0.1:8080) is an address, not a scheme; local dev servers are plain http.
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(localhost|127\.0\.0\.1|\[::1\])(:\d+)?(/|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "http://" + t;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[a-zA-Z][a-zA-Z0-9+.-]*:(?!\d)")) return t;
        if (t.Contains(' ') || !t.Contains('.') && !t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(t);
        return "https://" + t;
    }
}

/// <summary>Minimal DevTools page session: request/response by id, events to <see cref="OnEvent"/>.</summary>
internal sealed class CdpPage : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _id;
    internal string TargetId { get; private set; } = "";
    internal Func<string, JsonNode?, Task>? OnEvent;

    internal static async Task<CdpPage> ConnectAsync(string wsUrl, CancellationToken ct)
    {
        var p = new CdpPage { TargetId = wsUrl[(wsUrl.LastIndexOf('/') + 1)..] };
        p._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await p._ws.ConnectAsync(new Uri(wsUrl), ct);
        _ = p.ReadLoop();
        return p;
    }

    internal async Task<JsonNode?> CallAsync(string method, JsonObject? prm = null)
    {
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = prm ?? new JsonObject() };
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _send.WaitAsync();
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); } finally { _send.Release(); }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        _pending.TryRemove(id, out _);
        if (done != tcs.Task) throw new TimeoutException(method + " timed out");
        return await tcs.Task;   // await (not .Result) so a DevTools error surfaces as itself, not an AggregateException
    }

    private async Task ReadLoop()
    {
        var buf = new byte[1 << 20];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream(); WebSocketReceiveResult r;
                do { r = await _ws.ReceiveAsync(buf, _cts.Token); if (r.MessageType == WebSocketMessageType.Close) return; ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                var node = JsonNode.Parse(ms.ToArray());
                if (node?["id"] is { } idn && _pending.TryGetValue(idn.GetValue<int>(), out var tcs))
                {
                    if (node["error"] is { } err) tcs.TrySetException(new InvalidOperationException(err["message"]?.GetValue<string>() ?? "CDP error"));
                    else tcs.TrySetResult(node["result"]);
                }
                else if (node?["method"]?.GetValue<string>() is { } method && OnEvent is { } on)
                    _ = on(method, node["params"]);
            }
        }
        catch { }
        finally { foreach (var t in _pending.Values) t.TrySetCanceled(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        _ws.Dispose();
    }
}
