using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MuxSwarm.Engine;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Hook hygiene: when.event accepts a string or a list, explicit args bypass command splitting,
/// hook output is captured (never inherited), and crashed persistent hooks back off.
/// </summary>
public class HookHygieneTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static HookConfig Parse(string json) => JsonSerializer.Deserialize<HookConfig>(json, Opts)!;

    [Fact]
    public void Event_SingleString_ParsesAndRoundTripsAsString()
    {
        const string json = """{"id":"a","mode":"Async","persistent":false,"command":"x","when":{"event":"turn_end"},"timeoutSeconds":30}""";
        var hook = Parse(json);
        Assert.Equal(["turn_end"], hook.When.Events);
        Assert.Equal(json, JsonSerializer.Serialize(hook, Opts));
    }

    [Fact]
    public void Event_List_ParsesAndRoundTripsAsArray()
    {
        var hook = Parse("""{"id":"a","command":"x","when":{"event":["turn_end","task_complete"]}}""");
        Assert.Equal(["turn_end", "task_complete"], hook.When.Events);
        Assert.Contains("\"event\":[\"turn_end\",\"task_complete\"]", JsonSerializer.Serialize(hook, Opts));
    }

    [Fact]
    public void Event_NonString_IsRejected()
    {
        Assert.Throws<JsonException>(() => Parse("""{"id":"a","command":"x","when":{"event":42}}"""));
    }

    [Fact]
    public void Matches_AnyListedEvent_CaseInsensitive_WithFilters()
    {
        var hook = Parse("""{"id":"a","command":"x","when":{"event":["turn_end","Task_Complete"],"agent":"CodeAgent"}}""");
        Assert.True(HookWorker.Matches(hook, new HookEvent { Event = "turn_end", Agent = "CodeAgent" }));
        Assert.True(HookWorker.Matches(hook, new HookEvent { Event = "task_complete", Agent = "codeagent" }));
        Assert.False(HookWorker.Matches(hook, new HookEvent { Event = "tool_call", Agent = "CodeAgent" }));
        Assert.False(HookWorker.Matches(hook, new HookEvent { Event = "turn_end", Agent = "WebAgent" }));
    }

    [Fact]
    public void StartInfo_Args_PassedVerbatim_PathWithSpace()
    {
        var hook = Parse("""{"id":"a","command":"python","args":["C:/My Hooks/notify.py","--flag"],"when":{"event":"turn_end"}}""");
        var psi = HookWorker.BuildStartInfo(hook)!;
        Assert.Equal("python", psi.FileName);
        Assert.Equal(["C:/My Hooks/notify.py", "--flag"], psi.ArgumentList);
        Assert.Equal("", psi.Arguments);
    }

    [Fact]
    public void StartInfo_LegacyCommand_SplitsOnceAtFirstSpace()
    {
        var psi = HookWorker.BuildStartInfo(new HookConfig { Command = "bash \"/my hooks/x.sh\" -v" })!;
        Assert.Equal("bash", psi.FileName);
        Assert.Equal("\"/my hooks/x.sh\" -v", psi.Arguments);
        Assert.Empty(psi.ArgumentList);
        Assert.Null(HookWorker.BuildStartInfo(new HookConfig { Command = "  " }));
    }

    [Fact]
    public void StartInfo_RedirectsAllStreams()
    {
        var psi = HookWorker.BuildStartInfo(new HookConfig { Command = "x" })!;
        Assert.True(psi.RedirectStandardInput);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void RestartDelay_ImmediateThenDoublingCappedAt60s()
    {
        int[] expected = [0, 0, 1, 2, 4, 8, 16, 32, 60, 60, 60];
        for (int failures = 0; failures < expected.Length; failures++)
            Assert.Equal(TimeSpan.FromSeconds(expected[failures]), HookWorker.RestartDelay(failures));
    }
}
