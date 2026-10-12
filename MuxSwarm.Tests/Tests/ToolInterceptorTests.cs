using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Tool-call interceptors: decision parsing, rule validation, and a real python extension driven
// through FunctionInvokingChatClient. Collection: ExtensionHost.Running is process-global.
[Collection("ConsoleState")]
public sealed class ToolInterceptorTests : IDisposable
{
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";

    // Denies Shell_* calls containing "rm", rewrites echo's text to upper case, redacts "secret".
    private const string Script = """
        import sys, json, time
        for line in sys.stdin:
            msg = json.loads(line)
            if "id" not in msg: continue
            p = msg["params"]
            if msg["method"] == "tool/before":
                args = p["arguments"]
                if p["tool"] == "slow": time.sleep(5); continue
                if "rm" in str(args.get("cmd", "")): out = {"decision": "deny", "reason": "no rm"}
                elif "text" in args: out = {"decision": "allow", "arguments": {"text": str(args["text"]).upper()}}
                else: out = {"decision": "allow"}
            else:
                out = {"result": (p.get("result") or "").replace("secret", "[redacted]")}
            print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": out}), flush=True)
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-intercept-" + Guid.NewGuid().ToString("N"));

    public ToolInterceptorTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "guard.py"), Script);
    }

    public void Dispose()
    {
        ExtensionHost.StopAll();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void StartGuard(params InterceptRule[] rules)
    {
        var ext = new ExtensionRegistry.Extension("guard", null, null, _dir, [])
        {
            Process = new ExtensionProcessSpec { Command = Python, Args = ["-u", "guard.py"] },
            Intercepts = rules,
        };
        Assert.Empty(ExtensionHost.Restart([ext], new HashSet<string> { "guard" }));
    }

    private static InterceptRule Rule(string tool, bool before = true, bool after = false, int ms = 10000, bool deny = false) =>
        new(tool, before, after, TimeSpan.FromMilliseconds(ms), deny);

    private static Task<object?> Invoke(AIFunction fn, params (string Key, object? Value)[] args) =>
        ToolInterceptor.InvokeAsync(new FunctionInvocationContext
        {
            Function = fn,
            Arguments = new AIFunctionArguments(args.ToDictionary(a => a.Key, a => a.Value)),
        }, CancellationToken.None).AsTask();

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Theory]
    [InlineData("""{"id":1,"result":{"decision":"deny","reason":"nope"}}""", true, "nope")]
    [InlineData("""{"id":1,"result":{"decision":"allow"}}""", false, "")]
    [InlineData("""{"id":1,"result":{}}""", false, "")]
    [InlineData("""{"id":1,"error":{"code":-1,"message":"x"}}""", false, "interceptor did not answer in time")]
    public void Decide_ReadsReplies(string reply, bool denied, string reason)
    {
        var expected = denied ? ToolInterceptor.Decision.Deny : ToolInterceptor.Decision.Allow;
        Assert.Equal(expected, ToolInterceptor.Decide(Json(reply), Rule("*"), out var why, out _));
        Assert.Equal(reason, why);
    }

    [Fact]
    public void Decide_NoAnswer_FollowsOnTimeout()
    {
        Assert.Equal(ToolInterceptor.Decision.Allow, ToolInterceptor.Decide(default, Rule("*"), out _, out _));
        Assert.Equal(ToolInterceptor.Decision.Deny, ToolInterceptor.Decide(default, Rule("*", deny: true), out _, out _));
    }

    [Theory]
    [InlineData("Shell_*", "shell_execute", true)]
    [InlineData("Shell_*", "Filesystem_read", false)]
    [InlineData("*", "anything", true)]
    [InlineData("echo", "ECHO", true)]
    [InlineData("echo", "echo2", false)]
    public void Rule_Matches(string pattern, string tool, bool expected) =>
        Assert.Equal(expected, Rule(pattern).Matches(tool));

    [Fact]
    public void BuildIntercepts_ValidatesEntries()
    {
        var problems = new List<string>();
        var process = new ExtensionProcessSpec { Command = "x" };
        var rules = ExtensionRegistry.BuildIntercepts("g", process,
        [
            new InterceptSpec { Tool = "Shell_*", On = "both", TimeoutMs = 999999, OnTimeout = "deny" },
            new InterceptSpec { Tool = "a*b" },
            new InterceptSpec { Tool = "x", On = "sometimes" },
            new InterceptSpec { Tool = "x", OnTimeout = "maybe" },
            new InterceptSpec { Tool = "x", TimeoutMs = 0 },
        ], problems);

        var rule = Assert.Single(rules);
        Assert.True(rule.Before && rule.After && rule.DenyOnTimeout);
        Assert.Equal(ToolInterceptor.MaxTimeout, rule.Timeout);
        Assert.Equal(4, problems.Count);

        problems.Clear();
        Assert.Empty(ExtensionRegistry.BuildIntercepts("g", null, [new InterceptSpec { Tool = "x" }], problems));
        Assert.Contains("need a process", Assert.Single(problems));
    }

    [Fact]
    public async Task NoInterceptors_PassesStraightThrough()
    {
        var fn = AIFunctionFactory.Create((string text) => "got " + text, "echo");
        Assert.Equal("got hi", (await Invoke(fn, ("text", "hi")))?.ToString());
    }

    [Fact]
    public async Task Before_DeniesWithReason_AndToolNeverRuns()
    {
        StartGuard(Rule("Shell_*"));
        int runs = 0;
        var fn = AIFunctionFactory.Create((string cmd) => { runs++; return "ran"; }, "Shell_execute");

        var result = await Invoke(fn, ("cmd", "rm -rf /"));

        Assert.Equal("[BLOCKED by extension guard] no rm", result);
        Assert.Equal(0, runs);
        Assert.Equal("ran", (await Invoke(fn, ("cmd", "ls")))?.ToString());
    }

    [Fact]
    public async Task Before_RewritesArguments_After_RedactsResult()
    {
        StartGuard(Rule("echo", before: true, after: true));
        var fn = AIFunctionFactory.Create((string text) => $"secret {text}", "echo");

        Assert.Equal("[redacted] HELLO", (await Invoke(fn, ("text", "hello")))?.ToString());
    }

    [Fact]
    public async Task Timeout_AppliesOnTimeoutPolicy()
    {
        StartGuard(Rule("slow", ms: 200), Rule("slow", ms: 200, deny: true));
        var fn = AIFunctionFactory.Create(() => "ran", "slow");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await Invoke(fn);

        Assert.StartsWith("[BLOCKED by extension guard]", result?.ToString());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Pipeline_ModelSeesTheDenial()
    {
        StartGuard(Rule("Shell_*"));
        var tool = AIFunctionFactory.Create((string cmd) => "ran", "Shell_execute");
        var inner = new ScriptedClient();
        var client = new ChatClientBuilder(inner)
            .UseFunctionInvocation(configure: c => c.FunctionInvoker = ToolInterceptor.InvokeAsync)
            .Build();

        await client.GetResponseAsync("go", new ChatOptions { Tools = [tool] });

        var toolResult = inner.Seen.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Equal("[BLOCKED by extension guard] no rm", toolResult.Result?.ToString());
    }

    // First turn asks for Shell_execute(rm), second turn answers with text and records what it saw.
    private sealed class ScriptedClient : IChatClient
    {
        private int _turn;
        public List<ChatMessage> Seen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Seen.Clear();
            Seen.AddRange(messages);
            var reply = _turn++ == 0
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "Shell_execute", new Dictionary<string, object?> { ["cmd"] = "rm x" })])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void UnmatchedPatterns_FlagsOnlyPatternsWithoutAnyTool()
    {
        var ext = new ExtensionRegistry.Extension("guard", null, null, _dir, [])
        {
            Intercepts = [Rule("Shell_*"), Rule("execute_command_async"), Rule("repl_*")],
        };
        var warnings = ToolInterceptor.UnmatchedPatterns([ext], ["execute_command_async", "repl_shell_exec"]).ToList();
        Assert.Contains("'Shell_*'", Assert.Single(warnings));
    }
}
