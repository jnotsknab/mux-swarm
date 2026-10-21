using System.Text.Json;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
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

    private ExtensionPanelView View()
    {
        Assert.Null(ExtensionPanels.Open("notes", "main", "Notes", J(Notes)));
        return new ExtensionPanelView(ExtensionPanels.Get("notes", "main")!);
    }

    private static ConsoleKeyInfo K(ConsoleKey k, char c = '\0', bool shift = false) => new(c, k, shift, false, false);

    [Fact]
    public void View_Renders_ThemedRows_AndFocusMovesInTreeOrder()
    {
        var v = View();
        var plain = v.Render(70, 20).Select(TuiMarkup.Plain).ToList();
        Assert.Contains(plain, r => r.Contains("NOTES") && r.Contains("notes"));
        Assert.Contains(plain, r => r.Contains("buy milk") && r.Contains("today"));
        Assert.Contains(plain, r => r.Contains("c: Clear"));
        Assert.Contains(plain, r => r.Contains(" 50%") && r.Contains("half"));
        Assert.Equal(20, plain.Count);

        Assert.Equal("new", v.FocusedKey);
        v.Handle(K(ConsoleKey.Tab), 5);
        Assert.Equal("notes", v.FocusedKey);
        v.Handle(K(ConsoleKey.Tab), 5);
        Assert.Equal("clear", v.FocusedKey);
        v.Handle(K(ConsoleKey.Tab, shift: true), 5);
        Assert.Equal("notes", v.FocusedKey);
    }

    [Fact]
    public void View_Keys_ProduceEvents_InputSubmit_ListSelect_Hotkey_Esc()
    {
        var v = View();
        foreach (char c in "hi c") v.Handle(K(ConsoleKey.NoName, c), 5);   // 'c' types: the input has focus
        var submit = v.Handle(K(ConsoleKey.Enter), 5);
        Assert.Equal(("new", "submit", "hi c"), (submit.Key, submit.Type, submit.Value));

        v.Handle(K(ConsoleKey.Tab), 5);
        v.Handle(K(ConsoleKey.DownArrow), 5);   // inside the list: moves the row, not the focus
        var select = v.Handle(K(ConsoleKey.Enter), 5);
        Assert.Equal(("notes", "select", "2"), (select.Key, select.Type, select.Value));

        var hot = v.Handle(K(ConsoleKey.C, 'c'), 5);   // outside an input the hotkey presses Clear
        Assert.Equal(("clear", "press"), (hot.Key, hot.Type));
        Assert.True(v.Handle(K(ConsoleKey.Escape), 5).Close);
    }

    [Fact]
    public void View_Clicks_HitTheControlsTheyDrew()
    {
        var v = View();
        var hits = new MouseHitMap();
        var rows = v.Render(70, 20, hits).Select(TuiMarkup.Plain).ToList();
        int milk = rows.FindIndex(r => r.Contains("ship it")) + 1;   // 1-based screen row
        int col = rows[milk - 1].IndexOf("ship it", StringComparison.Ordinal) + 1;
        Assert.True(hits.TryHit(milk, col, out var hit, out _, out _));
        var (index, row) = ExtensionPanelView.DecodeHit(hit.Payload);
        var o = v.Click(index, row);
        Assert.Equal(("notes", "select", "2"), (o.Key, o.Type, o.Value));

        int btn = rows.FindIndex(r => r.Contains("c: Clear")) + 1;
        Assert.True(hits.TryHit(btn, rows[btn - 1].IndexOf("Clear", StringComparison.Ordinal) + 1, out hit, out _, out _));
        Assert.Equal("press", v.Click(ExtensionPanelView.DecodeHit(hit.Payload).Index).Type);
    }

    [Fact]
    public void View_Update_KeepsFocusAndTypedText_AndCloseByUserNotifies()
    {
        var v = View();
        v.Handle(K(ConsoleKey.NoName, 'x'), 5);
        v.Handle(K(ConsoleKey.Tab), 5);
        ExtensionPanels.Update("notes", "main", J(Notes.Replace("ship it", "shipped")));
        v.Update(ExtensionPanels.Get("notes", "main")!);
        Assert.Equal("notes", v.FocusedKey);
        Assert.Contains(v.Render(70, 20).Select(TuiMarkup.Plain), r => r.Contains("shipped"));
        Assert.Contains(v.Render(70, 20).Select(TuiMarkup.Plain), r => r.Contains("\u203a x"));

        ExtensionPanels.Close("notes", "main", byUser: true);
        var (ext, method, json) = Assert.Single(_sent);
        Assert.Equal(("notes", "ui/event"), (ext, method));
        Assert.Contains("\"type\":\"close\"", json);
    }

    private sealed class Terminal : ITuiTerminal
    {
        private readonly System.Text.StringBuilder _out = new();
        public int Width => 80;
        public int Height => 24;
        public void Write(string text) { lock (_out) _out.Append(text); }
        public void Flush() { }
        internal string Plain() { lock (_out) return System.Text.RegularExpressions.Regex.Replace(_out.ToString(), "\u001b\\[[0-9;?]*[A-Za-z]", ""); }
    }

    // The idle prompt shows an open panel, routes keys to it as ui/event, then returns to the prompt.
    [Fact]
    public async Task IdlePrompt_ShowsOpenPanel_SendsEvents_AndReturnsOnEsc()
    {
        var term = new Terminal();
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var driver = new TuiDriver(term, frameEngine: false);
        Assert.Null(ExtensionPanels.Open("notes", "main", "Notes", J(Notes)));
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            for (int i = 0; i < 50 && !term.Plain().Contains("NOTES"); i++) await Task.Delay(50);
            Assert.Contains("buy milk", term.Plain());
            foreach (char c in "ok") pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(K(ConsoleKey.NoName, c)));
            pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(K(ConsoleKey.Enter, '\r')));
            pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(K(ConsoleKey.Escape)));
            for (int i = 0; i < 50 && ExtensionPanels.All().Count > 0; i++) await Task.Delay(50);
            Assert.Empty(ExtensionPanels.All());
            lock (_sent)
            {
                Assert.Contains(_sent, s => s.Json.Contains("\"type\":\"submit\"") && s.Json.Contains("\"value\":\"ok\""));
                Assert.Contains(_sent, s => s.Json.Contains("\"type\":\"close\""));
            }
            // Back at the prompt: typing reaches the editor again.
            foreach (char c in "hi") pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(K(ConsoleKey.NoName, c)));
            pump.Enqueue(ConsoleInputPump.InputEvent.OfKey(K(ConsoleKey.Enter, '\r')));
            Assert.Equal("hi", await read.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            pump.Dispose();
            try { await read.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            driver.Shutdown();
        }
    }

    [Fact]
    public void View_SmallTerminal_AndLongContentScrolls()
    {
        var v = View();
        Assert.Contains("Resize", string.Join("\n", v.Render(20, 5).Select(TuiMarkup.Plain)));
        ExtensionPanels.Update("notes", "main", J("""{ "type": "text", "text": "\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\nlast line" }"""));
        v.Update(ExtensionPanels.Get("notes", "main")!);
        Assert.DoesNotContain(v.Render(60, 10).Select(TuiMarkup.Plain), r => r.Contains("last line"));
        v.Handle(K(ConsoleKey.PageDown), 50);
        Assert.Contains(v.Render(60, 10).Select(TuiMarkup.Plain), r => r.Contains("last line"));
    }
}
