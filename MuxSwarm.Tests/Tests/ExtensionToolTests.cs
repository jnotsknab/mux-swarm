using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Tools served by extension processes: manifest validation, tool/call round trips through the
// AIFunction, and per-agent gating by extension name. Needs `python` on PATH like ExtensionProcessTests.
[Collection("ConsoleState")]
public sealed class ExtensionToolTests : IDisposable
{
    // Answers tool/call: "echo" returns its text, "boom" returns an error, "hang" never answers.
    private const string Script = """
        import sys, json
        for line in sys.stdin:
            msg = json.loads(line)
            if msg.get("method") != "tool/call": continue
            p = msg["params"]
            if p["name"] == "hang": continue
            if p["name"] == "boom":
                reply = {"error": {"code": 1, "message": "kaboom"}}
            else:
                reply = {"result": {"content": "echo:" + p["arguments"]["text"]}}
            print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], **reply}), flush=True)
        """;

    private const string Manifest = """
        { "process": { "command": "PY", "args": ["-u", "tools.py"] },
          "tools": [
            { "name": "echo", "description": "Echo text.",
              "parameters": { "type": "object", "properties": { "text": { "type": "string" } } } },
            { "name": "boom", "description": "Always fails." },
            { "name": "hang", "description": "Never answers.", "timeoutSeconds": 1 } ] }
        """;

    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-exttools-" + Guid.NewGuid().ToString("N"));

    public ExtensionToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ExtensionHost.StopAll();
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void AddExtension(string folder, string manifest, string? script = null)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), manifest.Replace("PY", Python));
        if (script is not null) File.WriteAllText(Path.Combine(dir, "tools.py"), script);
    }

    private Dictionary<string, AIFunction> StartTools()
    {
        var result = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        Assert.Empty(ExtensionHost.Restart(result.Extensions, new HashSet<string> { "my-ext" }));
        return NativeToolRegistry.BuildPool(new AppConfig()).OfType<ExtensionTool>().ToDictionary(t => t.Name, t => (AIFunction)t);
    }

    [Fact]
    public async Task ToolCall_ReturnsContent_ErrorMessage_OrTimeout()
    {
        AddExtension("my-ext", Manifest, Script);
        var tools = StartTools();
        Assert.Equal(["my-ext_boom", "my-ext_echo", "my-ext_hang"], tools.Keys.Order());
        Assert.Equal("Echo text.", tools["my-ext_echo"].Description);
        Assert.Equal("object", tools["my-ext_boom"].JsonSchema.GetProperty("type").GetString());

        var ok = await tools["my-ext_echo"].InvokeAsync(new AIFunctionArguments { ["text"] = "hi" });
        Assert.Equal("echo:hi", ok);
        var failed = await tools["my-ext_boom"].InvokeAsync(new AIFunctionArguments());
        Assert.Equal("[extension my-ext] tool failed: kaboom", failed);
        var timedOut = await tools["my-ext_hang"].InvokeAsync(new AIFunctionArguments());
        Assert.Equal("[extension my-ext] tool failed: no answer within 1s", timedOut);
    }

    [Fact]
    public void NoTools_WhenProcessIsNotTrusted()
    {
        AddExtension("my-ext", Manifest, Script);
        var result = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root] });
        ExtensionHost.Restart(result.Extensions, new HashSet<string>());
        Assert.Equal(3, Assert.Single(result.Extensions).Tools.Count);
        Assert.Empty(NativeToolRegistry.BuildPool(new AppConfig()).OfType<ExtensionTool>());
    }

    [Fact]
    public void ExposedName_IsGatedByExtensionName_LikeAnMcpServer()
    {
        Assert.True(NativeToolRegistry.ServerMatches("my-ext_echo", "my-ext"));
        Assert.False(NativeToolRegistry.ServerMatches("my-ext_echo", "other"));
        Assert.False(NativeToolRegistry.ServerMatches("my-ext_echo", "my"));
    }

    [Theory]
    [InlineData("""{ "name": "Bad-Name", "description": "d" }""", "is invalid")]
    [InlineData("""{ "name": "x", "description": "" }""", "needs a description")]
    [InlineData("""{ "name": "x", "description": "d", "parameters": [1] }""", "JSON Schema object")]
    [InlineData("""{ "name": "x", "description": "d", "timeoutSeconds": 601 }""", "timeoutSeconds")]
    public void Scan_RejectsInvalidTools(string tool, string expected)
    {
        AddExtension("pack", $$"""{ "process": { "command": "x" }, "tools": [ {{tool}} ] }""");
        var result = ExtensionRegistry.Scan([_root]);
        Assert.Empty(Assert.Single(result.Extensions).Tools);
        Assert.Contains(result.Problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scan_DuplicatesAndProcesslessTools_AreReported_AndSchemaDefaults()
    {
        AddExtension("dup", """{ "process": { "command": "x" }, "tools": [ { "name": "a", "description": "1" }, { "name": "a", "description": "2" } ] }""");
        AddExtension("noproc", """{ "tools": [ { "name": "a", "description": "d" } ] }""");
        var result = ExtensionRegistry.Scan([_root]);

        var tool = Assert.Single(result.Extensions.Single(e => e.Name == "dup").Tools);
        Assert.Equal("""{"type":"object","properties":{}}""", tool.Parameters.GetRawText());
        Assert.Equal(ExtensionRegistry.DefaultToolTimeoutSeconds, tool.TimeoutSeconds);
        Assert.Empty(result.Extensions.Single(e => e.Name == "noproc").Tools);
        Assert.Contains(result.Problems, p => p.Contains("duplicate tool 'a'"));
        Assert.Contains(result.Problems, p => p.Contains("tools need a process"));
    }
}
