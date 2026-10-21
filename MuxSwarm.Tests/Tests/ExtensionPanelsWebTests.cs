using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Extension panels on the web: ext_panel events and the two endpoints. ConsoleState: global registry.
[Collection("ConsoleState")]
public sealed class ExtensionPanelsWebTests : IDisposable
{
    private readonly Func<string?> _surface = ExtensionPanels.Surface;
    private readonly Action<string, string, object> _notify = ExtensionPanels.Notify;
    private readonly List<string> _sent = [];

    public ExtensionPanelsWebTests()
    {
        ExtensionPanels.Surface = () => null;
        ExtensionPanels.Notify = (ext, m, p) => { lock (_sent) _sent.Add($"{ext} {m} {JsonSerializer.Serialize(p)}"); };
    }

    public void Dispose()
    {
        foreach (var p in ExtensionPanels.All()) ExtensionPanels.Close(p.Extension, p.Id, byUser: false);
        ExtensionPanels.Surface = _surface;
        ExtensionPanels.Notify = _notify;
    }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static async Task<(int Status, string Body)> Call(string handler, string? json = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        if (json is not null) ctx.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var m = typeof(ServeMode).GetMethod(handler, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        await (Task)m.Invoke(null, [ctx])!;
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, new StreamReader(ctx.Response.Body).ReadToEnd());
    }

    [Fact]
    public async Task List_ReturnsOpenPanels_WithTheirTrees()
    {
        Assert.Null(ExtensionPanels.Open("notes", "main", "Notes", J("""{ "type": "text", "text": "hi" }""")));
        var (status, body) = await Call("HandleExtPanels");
        Assert.Equal(200, status);
        var p = Assert.Single(JsonDocument.Parse(body).RootElement.EnumerateArray());
        Assert.Equal("notes", p.GetProperty("extension").GetString());
        Assert.Equal("hi", p.GetProperty("view").GetProperty("text").GetString());
        Assert.Equal("screen", p.GetProperty("placement").GetString());
    }

    [Fact]
    public async Task Event_ForwardsActions_ClosesOnClose_AndRejectsUnknownPanelsOrTypes()
    {
        ExtensionPanels.Open("notes", "main", null, J("""{ "type": "button", "key": "go" }"""));
        Assert.Equal(200, (await Call("HandleExtPanelEvent", """{ "extension": "notes", "id": "main", "key": "go", "type": "press" }""")).Status);
        Assert.Equal(400, (await Call("HandleExtPanelEvent", """{ "extension": "notes", "id": "main", "type": "press" }""")).Status);
        Assert.Equal(400, (await Call("HandleExtPanelEvent", """{ "extension": "notes", "id": "main", "key": "go", "type": "hack" }""")).Status);
        Assert.Equal(404, (await Call("HandleExtPanelEvent", """{ "extension": "notes", "id": "nope", "key": "go", "type": "press" }""")).Status);
        Assert.Equal(400, (await Call("HandleExtPanelEvent", "not json")).Status);
        Assert.Equal(200, (await Call("HandleExtPanelEvent", """{ "extension": "notes", "id": "main", "type": "close" }""")).Status);
        Assert.Empty(ExtensionPanels.All());
        lock (_sent)
        {
            Assert.Equal(2, _sent.Count);
            Assert.Contains("\"type\":\"press\"", _sent[0]);
            Assert.Contains("\"type\":\"close\"", _sent[1]);
        }
    }

    [Fact]
    public void WebUi_HandlesExtPanelEvents_AndDrawsEveryElementType()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Runtime", "mux-web-app", "index.html"))) dir = Path.GetDirectoryName(dir);
        var html = File.ReadAllText(Path.Combine(dir!, "Runtime", "mux-web-app", "index.html"));
        Assert.Contains("case 'ext_panel':", html);
        int start = html.IndexOf("<!-- ===== Extension panels", StringComparison.Ordinal);
        var block = html[start..html.IndexOf("<!-- ===== /Extension panels", StringComparison.Ordinal)];
        // The side-dock push must be unlayered: the unlayered global reset would beat a layered margin.
        Assert.StartsWith("body.mux-xp-docked .app {", block[block.IndexOf("body.mux-xp-docked .app", StringComparison.Ordinal)..]);
        Assert.True(block.IndexOf("body.mux-xp-docked .app", StringComparison.Ordinal) < block.IndexOf("@layer", StringComparison.Ordinal));
        Assert.All(ExtensionPanels.Types, t => Assert.Contains($"case '{t}':", block));
        Assert.All(ExtensionPanels.Colors.Where(c => c != "text"), c => Assert.Contains($".c-{c}", block));
        Assert.DoesNotContain("innerHTML = n.", block);   // extension text is never parsed as HTML
        Assert.Contains("mux-xp-band", block);              // placement band
        Assert.Contains("body.mux-xp-docked .app", block);  // placement side pushes the chat
    }
}
