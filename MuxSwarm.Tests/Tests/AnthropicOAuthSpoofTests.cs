using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using MuxSwarm.Utils.Auth;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers the native Claude OAuth request path (M3): the ClaudeCodeSpoofChatClient must guarantee the
/// mandatory "You are Claude Code..." identity is the FIRST system message (its own block) before the
/// request reaches Anthropic - without it Opus/Sonnet 400 on a subscription token. Uses a fake inner
/// IChatClient to capture exactly what messages are forwarded.
/// </summary>
public class AnthropicOAuthSpoofTests
{
    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatMessage>? Captured;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Captured = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Captured = messages.ToList();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }
        public object? GetService(System.Type t, object? key = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Spoof_InsertsIdentity_AsFirstSystemMessage_WhenAbsent()
    {
        var cap = new CapturingChatClient();
        var client = new ClaudeCodeSpoofChatClient(cap);
        await client.GetResponseAsync(new[]
        {
            new ChatMessage(ChatRole.System, "You are a helpful Mux agent."),
            new ChatMessage(ChatRole.User, "hi"),
        });

        Assert.NotNull(cap.Captured);
        Assert.Equal(ChatRole.System, cap.Captured![0].Role);
        Assert.StartsWith(AnthropicOAuthChatClientFactory.ClaudeCodeIdentity, cap.Captured[0].Text);
        // The real system prompt must still be present as a LATER message (not concatenated).
        Assert.Contains(cap.Captured, m => m.Text != null && m.Text.Contains("helpful Mux agent"));
        // The identity must be its OWN block, not merged into the real system prompt.
        Assert.DoesNotContain("helpful Mux agent", cap.Captured[0].Text!);
    }

    [Fact]
    public async Task Spoof_IsIdempotent_WhenIdentityAlreadyFirst()
    {
        var cap = new CapturingChatClient();
        var client = new ClaudeCodeSpoofChatClient(cap);
        await client.GetResponseAsync(new[]
        {
            new ChatMessage(ChatRole.System, AnthropicOAuthChatClientFactory.ClaudeCodeIdentity),
            new ChatMessage(ChatRole.User, "hi"),
        });
        // Exactly one system message carrying the identity - not doubled.
        int idCount = cap.Captured!.Count(m => m.Role == ChatRole.System &&
            m.Text != null && m.Text.StartsWith(AnthropicOAuthChatClientFactory.ClaudeCodeIdentity));
        Assert.Equal(1, idCount);
    }

    [Fact]
    public async Task Spoof_AppliesToStreamingPath_Too()
    {
        var cap = new CapturingChatClient();
        var client = new ClaudeCodeSpoofChatClient(cap);
        await foreach (var _ in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }))
        {
        }
        Assert.NotNull(cap.Captured);
        Assert.Equal(ChatRole.System, cap.Captured![0].Role);
        Assert.StartsWith(AnthropicOAuthChatClientFactory.ClaudeCodeIdentity, cap.Captured[0].Text);
    }
}
