using System.Text.Json;
using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Extension panels: element-tree validation, the open/update/close registry, ui/* host methods and
// the TUI view (layout, focus, keys, clicks). ConsoleState: the registry and its seams are global.
[Collection("ConsoleState")]
public sealed class ExtensionPanelsTests : IDisposable
{
    private readonly Func<string?> _surface = ExtensionPanels.Surface;
    private readonly Action<string, string, object> _notify = ExtensionPanels.Notify;
    private readonly List<(string Ext, string Method, string Json)> _sent = [];

    public ExtensionPanelsTests()
    {
        ExtensionPanels.Surface = () => null;
        ExtensionPanels.Notify = (ext, m, p) => { lock (_sent) _sent.Add((ext, m, JsonSerializer.Serialize(p))); };
    }

    public void Dispose()
    {
        foreach (var p in ExtensionPanels.All()) ExtensionPanels.Close(p.Extension, p.Id, byUser: false);
        ExtensionPanels.Surface = _surface;
        ExtensionPanels.Notify = _notify;
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 }).RootElement.Clone();

    private const string Notes = """
        { "type": "box", "children": [
            { "type": "text", "text": "Notes", "bold": true, "color": "accent" },
            { "type": "input", "key": "new", "placeholder": "add a note" },
            { "type": "list", "key": "notes", "items": [ { "value": "1", "label": "buy milk", "detail": "today" }, { "value": "2", "label": "ship it" } ] },
            { "type": "box", "dir": "row", "gap": 2, "children": [
                { "type": "button", "key": "clear", "label": "Clear", "hotkey": "c" },
                { "type": "progress", "value": 0.5, "label": "half" } ] } ] }
        """;

    [Theory]
    [InlineData("""{ "type": "nope" }""", "unknown element type 'nope'")]
    [InlineData("""{ "type": "text", "text": "x", "size": 3 }""", "text has no prop 'size'")]
    [InlineData("""{ "type": "button", "label": "x" }""", "button needs a key")]
    [InlineData("""{ "type": "box", "children": [ { "type": "button", "key": "a" }, { "type": "input", "key": "a" } ] }""", "key 'a' is used twice")]
    [InlineData("""{ "type": "text", "text": "x", "color": "#ff0000" }""", "color must be one of")]
    [InlineData("""{ "type": "box", "dir": "grid" }""", "dir must be")]
    [InlineData("""{ "type": "list", "key": "l", "items": [ { "label": "no value" } ] }""", "needs a string value")]
    [InlineData("\"text\"", "must be an element object")]
    public void Parse_RejectsBadTrees_NamingTheProblem(string json, string expected)
    {
        Assert.Contains(expected, ExtensionPanels.Parse(J(json), out var root));
        Assert.Null(root);
    }

    [Fact]
    public void Parse_EnforcesDepthAndNodeCaps()
    {
        string deep = string.Concat(Enumerable.Repeat("""{ "type": "box", "children": [""", 40)) + """{ "type": "text" }""" + string.Concat(Enumerable.Repeat("]}", 40));
        Assert.Contains("nested deeper", ExtensionPanels.Parse(J(deep), out _));
        string wide = """{ "type": "box", "children": [""" + string.Join(",", Enumerable.Repeat("""{ "type": "text", "text": "x" }""", 2100)) + "]}";
        Assert.Contains("more than 2000 elements", ExtensionPanels.Parse(J(wide), out _));
        Assert.Null(ExtensionPanels.Parse(J(Notes), out var ok));
        Assert.Equal(4, ok!.Kids.Count);
    }

    [Fact]
    public void HostMethods_OpenUpdateClose_AndProcessExitClosesItsPanels()
    {
        var open = ExtensionHostApi.Handle("notes", "ui/open", J($$"""{ "id": "main", "title": "My notes", "view": {{Notes}} }"""));
        Assert.Null(open.Error);
        var p = Assert.Single(ExtensionPanels.All());
        Assert.Equal(("notes", "main", "My notes"), (p.Extension, p.Id, p.Title));

        Assert.Null(ExtensionHostApi.Handle("notes", "ui/update", J("""{ "id": "main", "view": { "type": "text", "text": "v2" } }""")).Error);
        Assert.Equal("v2", ExtensionPanels.Get("notes", "main")!.Root.Text);
        Assert.Contains("not open", ExtensionHostApi.Handle("notes", "ui/update", J("""{ "id": "other", "view": { "type": "text" } }""")).Error);
        Assert.Contains("unknown element", ExtensionHostApi.Handle("notes", "ui/update", J("""{ "id": "main", "view": { "type": "x" } }""")).Error);

        var closed = JsonSerializer.SerializeToElement(ExtensionHostApi.Handle("notes", "ui/close", J("""{ "id": "main" }""")).Result);
        Assert.True(closed.GetProperty("closed").GetBoolean());
        Assert.Empty(ExtensionPanels.All());
        Assert.Empty(_sent);   // closed by the extension: no close event back

        ExtensionPanels.Open("notes", "a", null, J("""{ "type": "text" }"""));
        ExtensionPanels.Open("other", "b", null, J("""{ "type": "text" }"""));
        ExtensionPanels.CloseAll("notes");
        Assert.Equal("other", Assert.Single(ExtensionPanels.All()).Extension);
    }

    [Fact]
    public void Open_IsRefused_WhereNoSurfaceDrawsPanels()
    {
        ExtensionPanels.Surface = () => "panels are not available on this surface (classic)";
        Assert.Contains("classic", ExtensionHostApi.Handle("x", "ui/open", J("""{ "id": "a", "view": { "type": "text" } }""")).Error);
        Assert.Empty(ExtensionPanels.All());
    }
}
