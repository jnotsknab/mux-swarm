using System.Collections.Generic;
using System.Linq;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.AI;
using MuxSwarm.Utils.Auth;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers the native Claude OAuth request path (M3) system-prompt spoof: BuildSystemBlocks must put the
/// mandatory "You are Claude Code..." identity as the FIRST system block (its own SystemMessage) - without
/// it Opus/Sonnet 400 on a subscription token. The caller's real system prompt must follow as a separate
/// block (never concatenated).
/// </summary>
public class AnthropicOAuthSpoofTests
{
    [Fact]
    public void SystemBlocks_IdentityIsFirst_AndRealPromptFollows()
    {
        var blocks = AnthropicNativeChatClient.BuildSystemBlocks(new[]
        {
            new ChatMessage(ChatRole.System, "You are a helpful Mux agent."),
            new ChatMessage(ChatRole.User, "hi"),
        });

        Assert.Equal(AnthropicOAuthChatClientFactory.ClaudeCodeIdentity, blocks[0].Text);
        // The real system prompt is a SEPARATE, later block - not merged into the identity block.
        Assert.Contains(blocks, b => b.Text == "You are a helpful Mux agent.");
        Assert.DoesNotContain("helpful Mux agent", blocks[0].Text);
    }

    [Fact]
    public void SystemBlocks_Idempotent_WhenCallerAlreadyLeadsWithIdentity()
    {
        var blocks = AnthropicNativeChatClient.BuildSystemBlocks(new[]
        {
            new ChatMessage(ChatRole.System, AnthropicOAuthChatClientFactory.ClaudeCodeIdentity),
        });
        // Only one identity block - the caller's identical lead is not re-added.
        int idCount = blocks.Count(b => b.Text.StartsWith(AnthropicOAuthChatClientFactory.ClaudeCodeIdentity, System.StringComparison.Ordinal));
        Assert.Equal(1, idCount);
    }

    [Fact]
    public void SystemBlocks_AlwaysPresent_EvenWithNoCallerSystem()
    {
        var blocks = AnthropicNativeChatClient.BuildSystemBlocks(new[]
        {
            new ChatMessage(ChatRole.User, "hi"),
        });
        Assert.Single(blocks);
        Assert.Equal(AnthropicOAuthChatClientFactory.ClaudeCodeIdentity, blocks[0].Text);
    }
}
