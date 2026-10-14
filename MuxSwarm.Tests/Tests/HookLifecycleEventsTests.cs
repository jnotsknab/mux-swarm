using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Lifecycle hook events: the data bag and correlation stamping, each new event's payload, and the
// session_end/turn_complete outcome contracts. Events are captured through HookWorker.Observed.
[Collection("ConsoleState")]
public sealed class HookLifecycleEventsTests : IDisposable
{
    private readonly List<HookEvent> _seen = [];
    private readonly object _lock = new();

    public HookLifecycleEventsTests() => HookWorker.Observed = e => { lock (_lock) _seen.Add(e); };

    public void Dispose() => HookWorker.Observed = null;

    private List<HookEvent> Seen(string name) { lock (_lock) return _seen.Where(e => e.Event == name).ToList(); }

    [Fact]
    public void Emit_DropsNullData_AndSerializesTheBagCamelCase()
    {
        HookWorker.Emit("hle_probe", "A", summary: "s", data: [("count", 3), ("missing", null)]);
        var e = Assert.Single(Seen("hle_probe"));
        Assert.Equal(new Dictionary<string, object?> { ["count"] = 3 }, e.Data);
        var json = JsonSerializer.Serialize(e, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        Assert.Contains("\"data\":{\"count\":3}", json);
        Assert.DoesNotContain("flowId", json);   // unset correlation is omitted

        HookWorker.Emit("hle_empty");
        Assert.Null(Assert.Single(Seen("hle_empty")).Data);
    }

    [Fact]
    public async Task BeginFlow_StampsParentAndFlowId_IncludingChildTasks_ThenRestores()
    {
        await Task.Run(async () =>
        {
            using (HookWorker.BeginFlow("Lead", "d900"))
            {
                HookWorker.Emit("hle_in");
                await Task.Run(() => HookWorker.Emit("hle_child"));
                using (HookWorker.BeginFlow("CodeAgent", "d901")) HookWorker.Emit("hle_nested");
                HookWorker.Emit("hle_back");
            }
            HookWorker.Emit("hle_out");
        });
        Assert.Equal(("Lead", "d900"), (Seen("hle_in")[0].ParentAgent, Seen("hle_in")[0].FlowId));
        Assert.Equal("d900", Seen("hle_child")[0].FlowId);
        Assert.Equal(("CodeAgent", "d901"), (Seen("hle_nested")[0].ParentAgent, Seen("hle_nested")[0].FlowId));
        Assert.Equal("d900", Seen("hle_back")[0].FlowId);
        Assert.Null(Seen("hle_out")[0].FlowId);
        Assert.NotEqual(HookWorker.NextFlowId("d"), HookWorker.NextFlowId("d"));
    }

    [Theory]
    [InlineData("/compact keep the plan", "/compact", "keep the plan")]
    [InlineData("/THEME dracula", "/theme", "dracula")]
    [InlineData("/set provider.apiKey sk-secret", "/set", null)]
    [InlineData("/join mux://host?code=123", "/join", null)]
    [InlineData("/help", "/help", null)]
    public void CommandExecuted_NamesTheCommand_AndHidesArgsThatCanBeSecrets(string line, string cmd, string? args)
    {
        HookWorker.CommandExecuted(line, "agent", "Lead");
        var e = Assert.Single(Seen("command_executed"));
        Assert.Equal(cmd, e.Summary);
        Assert.Equal("Lead", e.Agent);
        Assert.Equal("agent", e.Data!["scope"]);
        Assert.Equal(args, e.Data.TryGetValue("args", out var a) ? a : null);
        if (args is null) Assert.DoesNotContain("sk-secret", JsonSerializer.Serialize(e));
    }

    [Fact]
    public void CommandExecuted_IgnoresGoals()
    {
        HookWorker.CommandExecuted("fix the build", "agent");
        HookWorker.CommandExecuted("/", "agent");
        Assert.Empty(Seen("command_executed"));
    }

    [Fact]
    public void DelegationEnd_PairsWithItsDelegation_AndDefaultsToError()
    {
        MuxConsole.WriteDelegation("Lead", "CodeAgent", "write tests", flowId: "d77");
        HookWorker.DelegationEnd("Lead", "CodeAgent", "d77", null, 1234, attempt: 2);
        var start = Assert.Single(Seen("delegation"));
        var end = Assert.Single(Seen("delegation_end"));
        Assert.Equal("CodeAgent", start.Data!["to"]);
        Assert.Equal(start.FlowId, end.FlowId);
        Assert.Equal("error", end.Summary);
        Assert.Equal(1234L, end.Data!["durationMs"]);
        Assert.Equal(2, end.Data["attempt"]);
    }

    [Theory]
    [InlineData(false, false, false, "ok")]
    [InlineData(true, false, false, "interrupted")]
    [InlineData(true, true, true, "timeout")]
    [InlineData(false, false, true, "failed")]
    public void TurnOutcome_Labels(bool interrupted, bool timedOut, bool failed, string expected)
        => Assert.Equal(expected, SingleAgentOrchestrator.TurnOutcome(interrupted, timedOut, failed));

    [Fact]
    public void TaskComplete_CarriesStatusWhenKnown()
    {
        MuxConsole.WriteTaskComplete("CodeAgent", "done", "partial");
        MuxConsole.WriteTaskComplete("CodeAgent", "done");
        var events = Seen("task_complete");
        Assert.Equal("partial", events[0].Data!["status"]);
        Assert.Null(events[1].Data);
    }

    [Fact]
    public void EffortChanged_FiresOnRealChangesOnly_NotWhenBuiltFromConfig()
    {
        var opts = new ChatOptions();
        ReasoningEffortControl.TryParse("high", out var high);
        ReasoningEffortControl.ApplyQuiet(opts, high!);   // config load
        Assert.Empty(Seen("effort_changed"));
        ReasoningEffortControl.TryParse("low", out var low);
        ReasoningEffortControl.Apply(opts, low!);
        ReasoningEffortControl.Apply(opts, low!);          // same label: no event
        var e = Assert.Single(Seen("effort_changed"));
        Assert.Equal("high", e.Data!["from"]);
        Assert.Equal("low", e.Data["to"]);
        ReasoningEffortControl.Cycle(opts);                // Shift+Tab path
        Assert.Equal(2, Seen("effort_changed").Count);
    }

    // session_end used to be emitted after the session loop, so an Esc/Ctrl+C exit (which throws out of
    // the loop) skipped it. It is now in ChatAgentAsync's finally; assert it stays there.
    [Fact]
    public void SessionEnd_IsEmittedFromFinally_InEveryOrchestrator()
    {
        var root = SourceRoot();
        foreach (var (file, method) in new[]
        {
            ("SingleAgentOrchestrator.cs", "public static async Task ChatAgentAsync("),
            ("MultiAgentOrchestrator.cs", "public static async Task RunAsync("),
            ("ParallelSwarmOrchestrator.cs", "public static async Task RunAsync("),
        })
        {
            var src = File.ReadAllText(Path.Combine(root, "Engine", file));
            int start = src.IndexOf(method, StringComparison.Ordinal);
            int fin = src.IndexOf("finally", start, StringComparison.Ordinal);
            int end = src.IndexOf("Event = \"session_end\"", start, StringComparison.Ordinal);
            int core = src.IndexOf("CoreAsync(", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && core > start && fin > core && end > fin, $"{file}: session_end must be in the wrapper's finally");
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(src, "Event = \"session_end\""));
        }
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
