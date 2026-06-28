using System.Text.Json;
using System.Text.Json.Nodes;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers the in-house cliproxy-filter transforms (CliProxyClaudePolicy.Transform): A) strip blocked
/// sampling params on Claude/Opus, B) fold system/developer messages into the first user turn for Claude.
/// Maps to the handoff's acceptance criteria. Pure-function tests; no network.
/// </summary>
public class CliProxyClaudePolicyTests
{
    private static JsonObject Parse(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Theory]
    [InlineData("claude-opus-4-8", true, true)]
    [InlineData("claude-sonnet-4-6", true, false)]
    [InlineData("gpt-5-codex", false, false)]
    [InlineData("CLAUDE-3-OPUS", true, true)]
    public void ModelMatchers(string model, bool claude, bool opus)
    {
        Assert.Equal(claude, CliProxyClaudePolicy.IsClaude(model));
        Assert.Equal(opus, CliProxyClaudePolicy.IsOpus(model));
    }

    [Fact]
    public void TransformA_StripsBlockedSamplingParams_ForClaude()
    {
        string json = """
        {"model":"claude-opus-4-8","temperature":0.7,"top_p":0.9,"top_k":40,"max_tokens":100,
         "messages":[{"role":"user","content":"hi"}]}
        """;
        var o = Parse(CliProxyClaudePolicy.Transform(json));
        Assert.False(o.ContainsKey("temperature"));
        Assert.False(o.ContainsKey("top_p"));
        Assert.False(o.ContainsKey("top_k"));
        Assert.True(o.ContainsKey("max_tokens")); // non-blocked param preserved
    }

    [Fact]
    public void NonClaudeNonOpus_IsUntouched()
    {
        string json = """{"model":"gpt-5-codex","temperature":0.7,"messages":[{"role":"system","content":"sys"},{"role":"user","content":"hi"}]}""";
        string outp = CliProxyClaudePolicy.Transform(json);
        Assert.Equal(json, outp); // same reference / unchanged
        var o = Parse(outp);
        Assert.True(o.ContainsKey("temperature"));
        Assert.Equal(2, ((JsonArray)o["messages"]!).Count); // system NOT merged
    }

    [Fact]
    public void TransformB_MergesSystemAndDeveloper_IntoFirstUser()
    {
        string json = """
        {"model":"claude-opus-4-8","messages":[
          {"role":"system","content":"You are Mux."},
          {"role":"developer","content":"Follow BRAIN.md."},
          {"role":"user","content":"What is 2+2?"}
        ]}
        """;
        var o = Parse(CliProxyClaudePolicy.Transform(json));
        var msgs = (JsonArray)o["messages"]!;
        Assert.Single(msgs); // only the user message remains
        var um = (JsonObject)msgs[0]!;
        Assert.Equal("user", um["role"]!.GetValue<string>());
        string c = um["content"]!.GetValue<string>();
        Assert.StartsWith("For this conversation, relevant trusted application context:", c);
        Assert.Contains("[SYSTEM]\nYou are Mux.", c);
        Assert.Contains("[DEVELOPER]\nFollow BRAIN.md.", c);
        Assert.Contains("User request: What is 2+2?", c);
    }

    [Fact]
    public void TransformB_NoUserMessage_SynthesizesOne()
    {
        string json = """{"model":"claude-sonnet-4-6","messages":[{"role":"system","content":"sys ctx"}]}""";
        var o = Parse(CliProxyClaudePolicy.Transform(json));
        var msgs = (JsonArray)o["messages"]!;
        Assert.Single(msgs);
        var um = (JsonObject)msgs[0]!;
        Assert.Equal("user", um["role"]!.GetValue<string>());
        string c = um["content"]!.GetValue<string>();
        Assert.Contains("[SYSTEM]\nsys ctx", c);
        Assert.EndsWith("Please acknowledge and continue.", c);
    }

    [Fact]
    public void TransformB_HandlesMultiPartListContent()
    {
        string json = """
        {"model":"claude-opus-4-8","messages":[
          {"role":"system","content":[{"type":"text","text":"part1"},{"type":"text","text":"part2"}]},
          {"role":"user","content":[{"type":"text","text":"hello"}]}
        ]}
        """;
        var o = Parse(CliProxyClaudePolicy.Transform(json));
        var um = (JsonObject)((JsonArray)o["messages"]!)[0]!;
        string c = um["content"]!.GetValue<string>();
        Assert.Contains("[SYSTEM]\npart1\npart2", c);
        Assert.Contains("User request: hello", c);
    }

    [Fact]
    public void NoSystemMessages_OnlyStripsParams_LeavesMessages()
    {
        string json = """{"model":"claude-opus-4-8","temperature":0.5,"messages":[{"role":"user","content":"hi"}]}""";
        var o = Parse(CliProxyClaudePolicy.Transform(json));
        Assert.False(o.ContainsKey("temperature"));
        Assert.Single((JsonArray)o["messages"]!); // user message untouched
        Assert.Equal("hi", ((JsonObject)((JsonArray)o["messages"]!)[0]!)["content"]!.GetValue<string>());
    }

    [Fact]
    public void InvalidJson_ReturnedUnchanged()
    {
        Assert.Equal("not json {", CliProxyClaudePolicy.Transform("not json {"));
    }

    [Fact]
    public void NoModel_ReturnedUnchanged()
    {
        string json = """{"messages":[{"role":"user","content":"hi"}]}""";
        Assert.Equal(json, CliProxyClaudePolicy.Transform(json));
    }
}
