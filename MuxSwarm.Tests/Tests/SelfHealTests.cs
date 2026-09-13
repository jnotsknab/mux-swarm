using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;

[Collection("ConsoleState")]
public class SelfHealTests
{
    private static string SkillJson(string name, string description, string? body) =>
        JsonSerializer.Serialize(new[] { new { type = "SKILL", key = name, content = description, skillBody = body } });

    [Fact]
    public void ParseProposals_ParsesJsonAndSkipsInvalidItems()
    {
        var result = SelfHeal.ParseProposals("""
            [
              {"type":" brain ","key":" EOL discipline ","content":"Detect per-file EOL"},
              null,
              {"type":"BOGUS","key":"key","content":"ignored"},
              {"type":"SKILL","key":"missing-body","content":"ignored"},
              {"type":"SKILL","key":"bad-body-type","content":"ignored","skillBody":7},
              {"type":"MEMORY","key":"bad-content","content":"line1\nline2"},
              {"type":"MEMORY","key":"build hash","content":"publish SHA256 abc123"}
            ]
            """);
        Assert.Equal(2, result.Count);
        Assert.Equal("BRAIN", result[0].Type);
        Assert.Equal("EOL discipline", result[0].Key);
        Assert.Equal("MEMORY", result[1].Type);
        Assert.Null(result[0].SkillBody);
    }

    [Fact]
    public void ParseProposals_KeepsPipesQuotesAndMultilineSkillBodySeparate()
    {
        const string body = "# Diagnose\r\n\r\n```powershell\r\n  rg \"ERROR\" app.log | sort\r\n```\r\n\n| State | Result |\n| --- | --- |\n| ✓ | 漢字 |\n";
        var skill = Assert.Single(SelfHeal.ParseProposals(SkillJson("trace-logs", "Inspect logs", body)));
        Assert.Equal("Inspect logs", skill.Content);
        Assert.Equal(body, skill.SkillBody);
        Assert.DoesNotContain("rg", skill.Label);
        var brain = Assert.Single(SelfHeal.ParseProposals("""[{"type":"BRAIN","key":"key","content":"a | b | c"}]"""));
        Assert.Equal("a | b | c", brain.Content);
        var memory = Assert.Single(SelfHeal.ParseProposals("""[{"type":"MEMORY","key":"key","content":"a | b","skillBody":"not applicable"}]"""));
        Assert.Null(memory.SkillBody);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"SKILL\"}")]
    [InlineData("[{\"type\":\"SKILL\",\"key\":\"x\",\"content\":\"d\",\"skillBody\":\" \\n\"}]")]
    [InlineData("[{\"type\":\"BRAIN\",\"key\":7,\"content\":\"d\"}]")]
    [InlineData("SKILL|old|legacy|body")]
    public void ParseProposals_MalformedOrIncomplete_ReturnsNoProposal(string text)
        => Assert.Empty(SelfHeal.ParseProposals(text));

    [Fact]
    public void Proposal_Label_IsHumanReadable()
    {
        var p = new SelfHeal.Proposal("BRAIN", "k", "v");
        Assert.Equal("[BRAIN] k: v", p.Label);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesSingleJsonContractWithoutMutatingOptions()
    {
        using var client = new ReviewerClient();
        var options = new ChatOptions { Temperature = 0.25f };
        var proposals = await SelfHeal.AnalyzeAsync([new(ChatRole.User, "a useful procedure")], client, chatOptions: options);
        var skill = Assert.Single(proposals);
        Assert.Equal("# Steps\n\n1. Run it.\n", skill.SkillBody);
        Assert.Contains("JSON array", client.Instructions);
        Assert.Contains("skillBody", client.Instructions);
        Assert.DoesNotContain("pipe format", client.Instructions);
        Assert.Same(options, client.Options);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task ApplySkill_RoundTripsBodyAndDescription_NoIncompleteFilesOrOverwrite()
    {
        // Production ApplyAsync resolves to this test binary's isolated Skills directory, never the live install.
        string name = "heal-json-" + Guid.NewGuid().ToString("N");
        string dir = Path.Combine(PlatformContext.SkillsDirectory, name);
        string missing = dir + "-empty";
        const string description = "Log procedure: preserve \"quotes\", | pipes, # hashes, --- delimiters and C:\\logs";
        const string body = "# Log review\n\n```powershell\n  rg \"ERROR\" app.log | sort\n```\n\n| A | B |\n| --- | --- |\n| ✓ | 漢字 |\n";
        try
        {
            await SelfHeal.ApplyAsync([new("SKILL", name + "-empty", "No body", null)]);
            Assert.False(Directory.Exists(missing));
            await SelfHeal.ApplyAsync(SelfHeal.ParseProposals(SkillJson(name, description, body)));
            string file = Path.Combine(dir, "SKILL.md");
            Assert.True(File.Exists(file));
            Assert.Equal(body, SkillLoader.ReadSkill(name));
            var metadata = Assert.Single(SkillLoader.GetSkillMetadata(), x => x.Name == name);
            Assert.Equal(description, metadata.Description);
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("Flesh out", text);
            Assert.EndsWith(body, text);
            await SelfHeal.ApplyAsync(SelfHeal.ParseProposals(SkillJson(name, "replacement", "# Not the original")));
            Assert.Equal(text, File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (Directory.Exists(missing)) Directory.Delete(missing, true);
            SkillLoader.LoadSkills();
        }
    }

    private sealed class ReviewerClient : IChatClient
    {
        internal string Instructions = "";
        internal ChatOptions? Options;
        internal int Calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; Instructions = messages.First().Text; Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                SkillJson("procedure", "Reusable steps", "# Steps\n\n1. Run it.\n"))));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
