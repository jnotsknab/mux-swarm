using System.Net.Http;
using System.Net.Http.Headers;
using Anthropic.SDK;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// Builds a native Claude <see cref="IChatClient"/> that talks DIRECTLY to api.anthropic.com using a
/// captured Max/Pro subscription OAuth bearer (no CLIProxyAPI, no x-api-key). Uses the tghamm
/// Anthropic.SDK (whose <c>client.Messages</c> is an IChatClient) and overrides auth via a delegating
/// handler that (1) strips the SDK's x-api-key, (2) injects a FRESH OAuth bearer per request (pulled
/// from <see cref="OAuthManager"/>, auto-refreshing), and (3) sets the Claude-Code identity headers.
/// A wrapping IChatClient prepends the MANDATORY Claude-Code system-spoof block so Opus/Sonnet accept
/// the subscription token (without it the API 400s for non-Haiku models).
/// </summary>
internal static class AnthropicOAuthChatClientFactory
{
    /// <summary>
    /// The mandatory identity preamble. Must be the literal START of the system prompt (its own first
    /// system block) for every non-Haiku model on a subscription token, else HTTP 400.
    /// </summary>
    public const string ClaudeCodeIdentity = "You are Claude Code, Anthropic's official CLI for Claude.";

    /// <summary>The Claude-Code + OAuth beta header value. oauth-2025-04-20 is mandatory for the OAuth path.</summary>
    public const string DefaultBetaHeader = "claude-code-20250219,oauth-2025-04-20";

    /// <summary>Build the wrapped native Claude IChatClient for the given model id.</summary>
    public static IChatClient Create(string modelId, int toolIterations)
    {
        var handler = new OAuthBearerHandler(providerId: "claude")
        {
            InnerHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) },
        };
        var http = new HttpClient(handler);

        // The SDK requires a non-null Auth.ApiKey to pass its null check; the handler strips the
        // resulting x-api-key and substitutes the OAuth bearer, so this placeholder is never sent.
        var anthropic = new AnthropicClient(apiKeys: new APIAuthentication("oauth-bearer-placeholder"), client: http)
        {
            AnthropicBetaVersion = DefaultBetaHeader,
            AnthropicVersion = "2023-06-01",
        };

        IChatClient inner = anthropic.Messages
            .AsBuilder()
            .UseFunctionInvocation(configure: c =>
                c.MaximumIterationsPerRequest = toolIterations > 0 ? toolIterations : int.MaxValue)
            .Build();

        return new ClaudeCodeSpoofChatClient(inner);
    }

    /// <summary>
    /// Delegating handler that converts the SDK's x-api-key auth into the OAuth-subscription auth path:
    /// removes x-api-key, sets Authorization: Bearer with a freshly-valid token, and stamps the Claude
    /// Code identity headers. Token refresh is handled transparently by <see cref="OAuthManager"/>.
    /// </summary>
    private sealed class OAuthBearerHandler(string providerId) : DelegatingHandler
    {
        private readonly string _providerId = providerId;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var tokens = await OAuthManager.Instance.GetValidTokensAsync(_providerId, ct).ConfigureAwait(false);

            request.Headers.Remove("x-api-key");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            // Claude-Code identity headers (authentic traffic shape; reduces 4xx on the OAuth path).
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.1.85 (external, cli)");
            request.Headers.TryAddWithoutValidation("x-app", "cli");
            request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");

            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// IChatClient decorator that guarantees the Claude-Code identity is the FIRST system message. On a
/// subscription OAuth token, Anthropic validates that the system prompt begins with the exact identity
/// string (as its own block) for every non-Haiku model - missing/concatenated => HTTP 400. This injects
/// it as a separate leading system message, leaving the caller's real system prompt(s) after it.
/// </summary>
internal sealed class ClaudeCodeSpoofChatClient(IChatClient inner) : IChatClient
{
    private readonly IChatClient _inner = inner;

    private static List<ChatMessage> WithIdentity(IEnumerable<ChatMessage> messages)
    {
        var list = new List<ChatMessage>(messages);
        // If the first system message already starts with the identity, do nothing (idempotent).
        var firstSystem = list.FirstOrDefault(m => m.Role == ChatRole.System);
        if (firstSystem?.Text is { } t && t.StartsWith(AnthropicOAuthChatClientFactory.ClaudeCodeIdentity, StringComparison.Ordinal))
            return list;

        // Insert the identity as a NEW first system block (its own message), ahead of everything.
        list.Insert(0, new ChatMessage(ChatRole.System, AnthropicOAuthChatClientFactory.ClaudeCodeIdentity));
        return list;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetResponseAsync(WithIdentity(messages), options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(WithIdentity(messages), options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
