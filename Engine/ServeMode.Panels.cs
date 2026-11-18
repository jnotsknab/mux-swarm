using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MuxSwarm.Engine.Extensions;

namespace MuxSwarm.Engine;

// Extension panels on the web UI: ext_panel events + GET /api/ext/panels + POST /api/ext/panels/event.
public static partial class ServeMode
{
    private static int _panelsWired;

    /// <summary>
    /// Let the web UI draw extension panels: accept <c>ui/open</c> while serving and forward every
    /// change as an <c>ext_panel</c> event (<c>op</c> = <c>open</c> | <c>update</c> | <c>close</c>). Idempotent.
    /// </summary>
    internal static void WirePanels()
    {
        if (Interlocked.Exchange(ref _panelsWired, 1) != 0) return;
        ExtensionPanels.Surface = () => IsServing ? null : MuxConsole.PanelsUnavailable();
        var opened = new System.Collections.Concurrent.ConcurrentDictionary<(string, string), byte>();
        ExtensionPanels.Changed += p => EmitEvent(new
        {
            type = "ext_panel", op = opened.TryAdd((p.Extension, p.Id), 0) ? "open" : "update",
            extension = p.Extension, id = p.Id, title = p.Title, version = p.Version, view = p.View,
        });
        ExtensionPanels.Closed += (ext, id) =>
        {
            opened.TryRemove((ext, id), out _);
            EmitEvent(new { type = "ext_panel", op = "close", extension = ext, id });
        };
    }

    // GET /api/ext/panels -> the open panels (for a page that loads after they opened).
    private static Task HandleExtPanels(HttpContext context) =>
        WriteJson(context, 200, ExtensionPanels.All().Select(p => new { extension = p.Extension, id = p.Id, title = p.Title, version = p.Version, view = p.View }));

    // POST /api/ext/panels/event { extension, id, key?, type, value? } -> forwards a user action as ui/event.
    private static async Task HandleExtPanelEvent(HttpContext context)
    {
        string? ext, id, key, type, value;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var r = doc.RootElement;
            string? S(string k) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            (ext, id, key, type, value) = (S("extension"), S("id"), S("key"), S("type"), S("value"));
        }
        catch (JsonException) { await WriteJson(context, 400, new { error = "Invalid JSON body" }); return; }
        if (ext is null || id is null || ExtensionPanels.Get(ext, id) is null) { await WriteJson(context, 404, new { error = "no such panel" }); return; }
        switch (type)
        {
            case "close":
                ExtensionPanels.Close(ext, id, byUser: true);
                break;
            case "press" or "submit" or "select":
                if (key is null) { await WriteJson(context, 400, new { error = "'key' is required" }); return; }
                ExtensionPanels.SendEvent(ext, id, key, type, value is { Length: > 4000 } ? value[..4000] : value);
                break;
            default:
                await WriteJson(context, 400, new { error = "'type' must be press, submit, select or close" });
                return;
        }
        await WriteJson(context, 200, new { ok = true });
    }
}
