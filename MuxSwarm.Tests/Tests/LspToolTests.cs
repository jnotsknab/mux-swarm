using MuxSwarm.Engine.Lsp;
using MuxSwarm.Engine.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// The native lsp tool on top of the client: gating (virtual server Lsp, absent while off), line +
// symbol addressing, the read-only actions and their compact text. LspManager is process-global.
[Collection("ConsoleState")]
public sealed class LspToolTests : IDisposable
{
    // Navigation answers for the fake server: fixed locations so the formatting can be checked.
    private const string NavigationHandlers = """
            elif m == "textDocument/definition":
                send({"jsonrpc": "2.0", "id": mid, "result": {"uri": p["textDocument"]["uri"], "range": {"start": {"line": 0, "character": 4}, "end": {"line": 0, "character": 8}}}})
            elif m == "textDocument/references":
                pos = p["position"]
                send({"jsonrpc": "2.0", "id": mid, "result": [{"uri": p["textDocument"]["uri"], "range": {"start": pos, "end": pos}},
                      {"uri": p["textDocument"]["uri"], "range": {"start": {"line": 0, "character": 4}, "end": {"line": 0, "character": 8}}}]})
            elif m == "textDocument/hover":
                pos = p["position"]
                send({"jsonrpc": "2.0", "id": mid, "result": {"contents": {"kind": "plaintext", "value": "hover at %d:%d" % (pos["line"], pos["character"])}}})
            elif m == "textDocument/documentSymbol":
                send({"jsonrpc": "2.0", "id": mid, "result": [{"name": "main", "kind": 12,
                      "range": {"start": {"line": 0, "character": 0}, "end": {"line": 2, "character": 0}},
                      "selectionRange": {"start": {"line": 0, "character": 4}, "end": {"line": 0, "character": 8}},
                      "children": [{"name": "inner", "kind": 13, "range": {"start": {"line": 1, "character": 0}, "end": {"line": 1, "character": 5}},
                                    "selectionRange": {"start": {"line": 1, "character": 0}, "end": {"line": 1, "character": 5}}}]}]})
            elif m == "workspace/symbol":
                send({"jsonrpc": "2.0", "id": mid, "result": [{"name": p["query"] + "Thing", "kind": 5,
                      "location": {"uri": next(iter(docs), "file:///x"), "range": {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 1}}}}]})
        """;

    private readonly LspTestRig _rig;

    public LspToolTests()
    {
        FakeLspServer.Handlers = NavigationHandlers + "\n";
        _rig = new LspTestRig();
        FakeLspServer.Handlers = "";
    }

    public void Dispose() => _rig.Dispose();

    private static Task<string> Run(string action, string? file = null, int? line = null, string? symbol = null, string? query = null) =>
        LspTool.RunAsync(action, file, line, symbol, query, CancellationToken.None);

    [Fact]
    public async Task Off_NoTool_OnOnlyWithAServer()
    {
        _rig.Use(enabled: false);
        Assert.Empty(LspTool.Build());
        Assert.Equal("Lsp", NativeToolRegistry.VirtualServer("lsp"));
        Assert.Contains("LSP is off", await Run("diagnostics", _rig.Src("a.fake", "x")));
        _rig.Use();
        Assert.Equal("lsp", Assert.Single(LspTool.Build()).Name);
        Assert.True(NativeToolRegistry.ServerMatches("lsp", "Lsp"));
    }

    [Theory]
    [InlineData("    var main = main2();", "main", 8)]
    [InlineData("    var main = main2();", "main#1", 8)]
    [InlineData("foo(foo, foo)", "foo#3", 9)]
    [InlineData("Main main", "MAIN", 0)]
    [InlineData("abc", "zzz", null)]
    public void ResolveColumn_PrefersWholeWordsAndCountsOccurrences(string text, string symbol, int? col) =>
        Assert.Equal(col, LspTool.ResolveColumn(text, symbol));

    [Fact]
    public async Task Diagnostics_ErrorsFirst_ThenFreshAfterEdit()
    {
        _rig.Use();
        var f = _rig.Src("a.fake", "line one\nhas BUG here\nfine");
        var first = await Run("diagnostics", f);
        Assert.Contains("a.fake:2:5 error: found a BUG (fake)", first);
        Assert.True(first.IndexOf("error", StringComparison.Ordinal) < first.IndexOf("warning", StringComparison.Ordinal));
        File.WriteAllText(f, "line one\nfixed\nfine\nnew BUG\n");
        var second = await Run("diagnostics", f);
        Assert.Contains(":4:5 error", second);
        Assert.DoesNotContain(":2:5 error", second);
    }

    [Fact]
    public async Task Diagnostics_Timeout_SaysPending_NeverNoProblems()
    {
        _rig.SetEnv("FAKE_LSP_NO_DIAG", "1");
        _rig.Use(waitMs: 300);
        var text = await Run("diagnostics", _rig.Src("a.fake", "has BUG"));
        Assert.Contains("pending", text);
        Assert.DoesNotContain("no problems", text);
    }

    [Fact]
    public async Task Navigation_UsesLinePlusSymbol_AndFormatsLocations()
    {
        _rig.Use();
        var f = _rig.Src("a.fake", "def main():\n  inner()\n");
        Assert.Contains("hover at 0:4", await Run("hover", f, 1, "main"));
        Assert.Matches(@"a\.fake:1:5\s+def main\(\):", await Run("definition", f, 2, "inner"));
        var refs = await Run("references", f, 2, "inner");
        Assert.Contains("a.fake:2:3", refs);
        Assert.Contains("a.fake:1:5", refs);
        Assert.Contains("'nothere' is not on line 1", await Run("hover", f, 1, "nothere"));
        Assert.Contains("needs a 1-based line", await Run("hover", f, null, "main"));
        var outline = await Run("symbols", f);
        Assert.Contains("function main  :1", outline);
        Assert.Contains("  variable inner  :2", outline);
        Assert.Contains("class WidgetThing", await Run("symbols", query: "Widget"));
        Assert.Contains("fake", await Run("status"));
    }
}
