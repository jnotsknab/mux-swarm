using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// extensions.agents: a scoped extension process only intercepts its agents' calls (and their
// sub-agents'), only receives their events plus agent-less ones, and its tools reach only them.
// Unscoped extensions behave exactly as before. A real python process records what it saw.
[Collection("ConsoleState")]
public sealed class ExtensionAgentScopeTests : IDisposable
{
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";

    // tool/before denies with the caller; every event is appended to seen.txt; tool/call answers "ok".
    private const string Script = """
        import sys, json
        for line in sys.stdin:
            msg = json.loads(line)
            p = msg.get("params") or {}
            if msg.get("method") == "event":
                with open("seen.txt", "a") as f: f.write(p.get("event", "") + "|" + str(p.get("agent")) + "\n")
                continue
            if "id" not in msg: continue
            if msg["method"] == "tool/before": out = {"decision": "deny", "reason": str(p.get("agent"))}
            elif msg["method"] == "tool/call": out = {"content": "ok"}
            else: out = {}
            print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": out}), flush=True)
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-scope-" + Guid.NewGuid().ToString("N"));

    public ExtensionAgentScopeTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "gate.py"), Script);
    }

    public void Dispose()
    {
        ExtensionHost.StopAll();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Start(params string[] agents)
    {
        var ext = new ExtensionRegistry.Extension("gate", null, null, _dir, [])
        {
            Process = new ExtensionProcessSpec { Command = Python, Args = ["-u", "gate.py"] },
            Intercepts = [new InterceptRule("*", true, false, TimeSpan.FromSeconds(10), false)],
            Events = ["*"],
            Tools = [new ExtensionToolSpec { Name = "probe", Description = "d", Agents = ["*"] }],
        };
        var scopes = agents.Length == 0 ? null : new Dictionary<string, List<string>> { ["gate"] = [.. agents] };
        Assert.Empty(ExtensionHost.Restart([ext], new HashSet<string> { "gate" }, scopes: scopes));
    }

    private static async Task<string?> Call(string? agent, string? parent = null) => await Task.Run(async () =>
    {
        using var flow = parent is null ? null : HookWorker.BeginFlow(parent, "d1");
        using var scope = HookWorker.BeginAgent(agent);
        var fn = AIFunctionFactory.Create(() => "ran", "Filesystem_write_file");
        return (await ToolInterceptor.InvokeAsync(new FunctionInvocationContext { Function = fn, Arguments = new AIFunctionArguments() },
            CancellationToken.None))?.ToString();
    });

    [Fact]
    public async Task Unscoped_InterceptsEveryAgent()
    {
        Start();
        Assert.Equal("[BLOCKED by extension gate] WebAgent", await Call("WebAgent"));
        Assert.Equal("[BLOCKED by extension gate] ConciergeAgent", await Call("ConciergeAgent"));
    }

    [Fact]
    public async Task Scoped_InterceptsOnlyItsAgents_TheirSubAgents_AndUnknownCallers()
    {
        Start("conciergeagent");   // case-insensitive
        Assert.Equal("[BLOCKED by extension gate] ConciergeAgent", await Call("ConciergeAgent"));
        Assert.Equal("ran", await Call("WebAgent"));
        Assert.Equal("[BLOCKED by extension gate] WebAgent", await Call("WebAgent", parent: "ConciergeAgent"));
        Assert.Equal("[BLOCKED by extension gate] None", await Call(null));   // cannot attribute: still gated
    }

    [Fact]
    public async Task Scoped_EventsOnlyForItsAgents_PlusAgentlessOnes()
    {
        Start("ConciergeAgent");
        HookWorker.Emit("scope_mine", "ConciergeAgent");
        HookWorker.Emit("scope_other", "WebAgent");
        HookWorker.Emit("scope_global");
        await Task.Run(() => { using var f = HookWorker.BeginFlow("ConciergeAgent", "d9"); HookWorker.Emit("scope_sub", "WebAgent"); });

        var seen = Path.Combine(_dir, "seen.txt");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string text = "";
        while (DateTime.UtcNow < deadline)
        {
            text = File.Exists(seen) ? File.ReadAllText(seen) : "";
            if (text.Contains("scope_mine") && text.Contains("scope_global") && text.Contains("scope_sub")) break;
            await Task.Delay(50);
        }
        await Task.Delay(300);   // let a wrongly delivered event land before asserting it did not
        text = File.Exists(seen) ? File.ReadAllText(seen) : "";
        Assert.Contains("scope_mine|ConciergeAgent", text);
        Assert.Contains("scope_global|None", text);
        Assert.Contains("scope_sub|WebAgent", text);
        Assert.DoesNotContain("scope_other", text);
    }

    [Fact]
    public void Scoped_ToolsReachOnlyItsAgents_EvenWhenTheManifestGrantsEveryone()
    {
        Start("ConciergeAgent");
        var pool = NativeToolsFromRunning();
        var defs = Common.ParseAgentDefinitions(new SwarmConfig
        {
            Agents = [new AgentConfig { Name = "ConciergeAgent", McpServers = ["gate"] }, new AgentConfig { Name = "WebAgent", McpServers = ["gate"] }],
        });
        Assert.Equal(["gate_probe"], defs[0].ToolFilter(pool).Select(t => t.Name));
        Assert.Empty(defs[1].ToolFilter(pool));
        Assert.Empty(Common.ApplyToolFilter(pool, [], [], includeAllWhenEmpty: true, agentName: "Orchestrator"));
        Assert.Single(Common.ApplyToolFilter(pool, [], [], includeAllWhenEmpty: true, agentName: null));
    }

    [Fact]
    public void Unscoped_ToolsUnchanged()
    {
        Start();
        var pool = NativeToolsFromRunning();
        Assert.Equal(["gate_probe"], Common.ApplyToolFilter(pool, [], [], agentName: "WebAgent").Select(t => t.Name));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(new string[0], null)]
    [InlineData(new[] { " ", "" }, null)]
    [InlineData(new[] { "A", " b " }, "A,b")]
    public void ScopeOf_IgnoresEmptyLists(string[]? agents, string? expected)
    {
        var scopes = agents is null ? null : new Dictionary<string, List<string>> { ["x"] = [.. agents] };
        var scope = ExtensionHost.ScopeOf("x", scopes);
        Assert.Equal(expected, scope is null ? null : string.Join(",", scope.Order(StringComparer.Ordinal)));
    }

    // The registry is not loaded in these tests, so build the tool straight from the running process.
    private static List<AITool> NativeToolsFromRunning() =>
        [new ExtensionTool(ExtensionHost.Running.Single(), new ExtensionToolSpec { Name = "probe", Description = "d", Agents = ["*"] })];
}
