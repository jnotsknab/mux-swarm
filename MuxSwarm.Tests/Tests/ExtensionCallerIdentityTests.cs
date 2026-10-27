using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Caller identity for extensions: the ambient agent set where each agent's model stream starts is
// stamped onto agent-less hook events and sent with tool/before, tool/after and tool/call, so a
// gate can apply per-agent rules. A real python extension echoes what it received.
[Collection("ConsoleState")]
public sealed class ExtensionCallerIdentityTests : IDisposable
{
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";

    // Denies a call unless the caller fields came through; tool/call echoes them back as content.
    private const string Script = """
        import sys, json
        for line in sys.stdin:
            msg = json.loads(line)
            if "id" not in msg: continue
            p = msg["params"]
            who = {"agent": p.get("agent"), "parentAgent": p.get("parentAgent"), "flowId": p.get("flowId")}
            if msg["method"] == "tool/before":
                out = {"decision": "deny", "reason": json.dumps(who)}
            elif msg["method"] == "tool/call":
                out = {"content": json.dumps(who)}
            else:
                out = {}
            print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": out}), flush=True)
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-caller-" + Guid.NewGuid().ToString("N"));
    private readonly List<HookEvent> _seen = [];

    public ExtensionCallerIdentityTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "who.py"), Script);
        HookWorker.Observed = e => { lock (_seen) _seen.Add(e); };
    }

    public void Dispose()
    {
        HookWorker.Observed = null;
        ExtensionHost.StopAll();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ExtensionRegistry.Extension Start()
    {
        var ext = new ExtensionRegistry.Extension("who", null, null, _dir, [])
        {
            Process = new ExtensionProcessSpec { Command = Python, Args = ["-u", "who.py"] },
            Intercepts = [new InterceptRule("gated", true, false, TimeSpan.FromSeconds(10), false)],
            Tools = [new ExtensionToolSpec { Name = "whoami", Description = "echo caller" }],
        };
        Assert.Empty(ExtensionHost.Restart([ext], new HashSet<string> { "who" }));
        return ext;
    }

    private static async Task<JsonElement> Denial(AIFunction fn)
    {
        var result = (await ToolInterceptor.InvokeAsync(new FunctionInvocationContext
        {
            Function = fn,
            Arguments = new AIFunctionArguments(),
        }, CancellationToken.None))?.ToString() ?? "";
        const string prefix = "[BLOCKED by extension who] ";
        Assert.StartsWith(prefix, result);
        return JsonDocument.Parse(result[prefix.Length..]).RootElement.Clone();
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Fact]
    public async Task ToolBefore_CarriesTheRunningAgent_AndItsDelegationFlow()
    {
        Start();
        var fn = AIFunctionFactory.Create(() => "ran", "gated");

        var none = await Denial(fn);
        Assert.Null(Str(none, "agent"));

        await Task.Run(async () =>
        {
            using (HookWorker.BeginAgent("MuxAgent"))
            {
                var lead = await Denial(fn);
                Assert.Equal("MuxAgent", Str(lead, "agent"));
                Assert.Null(Str(lead, "flowId"));

                using (HookWorker.BeginFlow("MuxAgent", "d77"))
                using (HookWorker.BeginAgent("ConciergeAgent"))
                {
                    var sub = await Denial(fn);
                    Assert.Equal(("ConciergeAgent", "MuxAgent", "d77"), (Str(sub, "agent"), Str(sub, "parentAgent"), Str(sub, "flowId")));
                }
                Assert.Equal("MuxAgent", Str(await Denial(fn), "agent"));   // restored after the sub-agent
            }
        });
    }

    [Fact]
    public async Task ParallelAgents_EachInterceptSeesItsOwnCaller()
    {
        Start();
        var fn = AIFunctionFactory.Create(() => "ran", "gated");
        string[] agents = ["CodeAgent", "WebAgent", "ConciergeAgent", "MemoryAgent"];

        var results = await Task.WhenAll(agents.Select(a => Task.Run(async () =>
        {
            using var flow = HookWorker.BeginFlow("Orchestrator", "p-" + a);
            using var scope = HookWorker.BeginAgent(a);
            var seen = new List<string?>();
            for (int i = 0; i < 5; i++) { await Task.Yield(); seen.Add(Str(await Denial(fn), "agent")); }
            return (a, seen);
        })));

        foreach (var (a, seen) in results) Assert.All(seen, s => Assert.Equal(a, s));
    }

    [Fact]
    public async Task ToolCall_CarriesTheCaller()
    {
        var ext = Start();
        var tool = new ExtensionTool(ExtensionHost.Running.Single(), ext.Tools.Single());
        string? content = null;
        await Task.Run(async () =>
        {
            using var scope = HookWorker.BeginAgent("ConciergeAgent");
            content = (await tool.InvokeAsync(new AIFunctionArguments()))?.ToString();
        });
        Assert.Equal("ConciergeAgent", Str(JsonDocument.Parse(content!).RootElement, "agent"));
    }

    [Fact]
    public async Task Enqueue_StampsTheAmbientAgent_OnlyWhenTheEventHasNone()
    {
        await Task.Run(() =>
        {
            using var scope = HookWorker.BeginAgent("CodeAgent");
            HookWorker.Emit("cid_plain");
            HookWorker.Emit("cid_named", "Orchestrator");
        });
        HookWorker.Emit("cid_outside");
        lock (_seen)
        {
            Assert.Equal("CodeAgent", _seen.Single(e => e.Event == "cid_plain").Agent);
            Assert.Equal("Orchestrator", _seen.Single(e => e.Event == "cid_named").Agent);
            Assert.Null(_seen.Single(e => e.Event == "cid_outside").Agent);
        }
    }
}
