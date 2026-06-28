using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// Builds a native Claude <see cref="IChatClient"/> that talks DIRECTLY to api.anthropic.com using a
/// captured Max/Pro subscription OAuth bearer (no CLIProxyAPI, no x-api-key). Uses the tghamm
/// Anthropic.SDK for the HTTP/JSON/SSE plumbing via its NATIVE message API (MessageParameters +
/// GetClaudeMessageAsync / StreamClaudeMessageAsync) - deliberately NOT the SDK's own IChatClient
/// bridge, because that bridge references a Microsoft.Extensions.AI member (HostedMcpServerTool
/// .AuthorizationToken) that only exists in M.E.AI 10.3.0 and was removed in 10.4+, while Mux pins
/// M.E.AI 10.5.0 (required by Microsoft.Agents.AI 1.3.0) - using the bridge throws MissingMethodException
/// at runtime. We instead hand-roll a thin ChatMessage&lt;-&gt;MessageParameters adapter (text streaming),
/// so the M.E.AI version is irrelevant to the Anthropic path.
///
/// Auth: a delegating handler strips the SDK's x-api-key, injects a FRESH OAuth bearer per request
/// (from <see cref="OAuthManager"/>, auto-refreshing) and the Claude-Code identity headers. The system
/// prompt is forced to begin with the mandatory Claude-Code identity block (else Opus/Sonnet 400 on a
/// subscription token; Haiku exempt).
/// </summary>
internal static class AnthropicOAuthChatClientFactory
{
    public const string ClaudeCodeIdentity = "You are Claude Code, Anthropic's official CLI for Claude.";
    public const string DefaultBetaHeader = "claude-code-20250219,oauth-2025-04-20";

    public static IChatClient Create(string modelId, int toolIterations)
    {
        var handler = new OAuthBearerHandler(providerId: "claude")
        {
            InnerHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) },
        };
        var http = new HttpClient(handler);

        var anthropic = new AnthropicClient(apiKeys: new APIAuthentication("oauth-bearer-placeholder"), client: http)
        {
            AnthropicBetaVersion = DefaultBetaHeader,
            AnthropicVersion = "2023-06-01",
        };

        return new AnthropicNativeChatClient(anthropic, modelId);
    }

    /// <summary>
    /// Delegating handler: convert the SDK's x-api-key auth into the OAuth-subscription path. Removes
    /// x-api-key, sets Authorization: Bearer with a freshly-valid token, stamps Claude-Code identity
    /// headers. Refresh is transparent via <see cref="OAuthManager"/>.
    /// </summary>
    private sealed class OAuthBearerHandler(string providerId) : DelegatingHandler
    {
        private readonly string _providerId = providerId;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var tokens = await OAuthManager.Instance.GetValidTokensAsync(_providerId, ct).ConfigureAwait(false);
            request.Headers.Remove("x-api-key");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.1.85 (external, cli)");
            request.Headers.TryAddWithoutValidation("x-app", "cli");
            request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Hand-rolled <see cref="IChatClient"/> over the Anthropic.SDK native message API. Maps M.E.AI
/// ChatMessages to SDK Messages/SystemMessages, forces the Claude-Code identity as the first system
/// block, and streams text deltas back as ChatResponseUpdates. Text-only for now (tool/function calling
/// over the native API is a follow-up); this restores a usable Claude subscription chat path that is
/// independent of the M.E.AI version skew.
/// </summary>
internal sealed class AnthropicNativeChatClient(AnthropicClient client, string defaultModel) : IChatClient
{
    private readonly AnthropicClient _client = client;
    private readonly string _defaultModel = defaultModel;
    private const int DefaultMaxTokens = 8192;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var p = BuildParameters(messages, options, stream: false);
        var res = await _client.Messages.GetClaudeMessageAsync(p, cancellationToken).ConfigureAwait(false);
        string text = res.Message?.ToString() ?? string.Empty;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = p.Model,
            FinishReason = MapStop(res.StopReason),
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var p = BuildParameters(messages, options, stream: true);
        await foreach (var res in _client.Messages.StreamClaudeMessageAsync(p, cancellationToken).ConfigureAwait(false))
        {
            string? delta = res.Delta?.Text;
            if (!string.IsNullOrEmpty(delta))
                yield return new ChatResponseUpdate(ChatRole.Assistant, delta) { ModelId = p.Model };
        }
    }

    /// <summary>
    /// Build the SDK system blocks with the mandatory Claude-Code identity as the FIRST block (its own
    /// SystemMessage), followed by any caller system messages (deduped if they already lead with the
    /// identity). Extracted + internal for unit testing the spoof placement without a live client.
    /// </summary>
    internal static List<SystemMessage> BuildSystemBlocks(IEnumerable<ChatMessage> messages)
    {
        var system = new List<SystemMessage> { new(AnthropicOAuthChatClientFactory.ClaudeCodeIdentity) };
        foreach (var m in messages.Where(m => m.Role == ChatRole.System))
        {
            string t = m.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(t) && !t.StartsWith(AnthropicOAuthChatClientFactory.ClaudeCodeIdentity, StringComparison.Ordinal))
                system.Add(new SystemMessage(t));
        }
        return system;
    }

    private MessageParameters BuildParameters(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var list = messages.ToList();

        var system = BuildSystemBlocks(list);

        // Conversation: map user/assistant turns to SDK Messages (text content).
        var convo = new List<Message>();
        foreach (var m in list)
        {
            if (m.Role == ChatRole.System) continue;
            var role = m.Role == ChatRole.Assistant ? RoleType.Assistant : RoleType.User;
            string t = m.Text ?? string.Empty;
            if (t.Length == 0) continue;
            convo.Add(new Message(role, t));
        }
        if (convo.Count == 0)
            convo.Add(new Message(RoleType.User, "."));

        return new MessageParameters
        {
            Model = string.IsNullOrWhiteSpace(options?.ModelId) ? _defaultModel : options!.ModelId!,
            Messages = convo,
            System = system,
            MaxTokens = options?.MaxOutputTokens ?? DefaultMaxTokens,
            Temperature = options?.Temperature is { } tp ? (decimal)tp : null,
            Stream = stream,
        };
    }

    private static ChatFinishReason? MapStop(string? stop) => stop switch
    {
        "end_turn" or "stop_sequence" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "tool_use" => ChatFinishReason.ToolCalls,
        _ => null,
    };

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _client.Dispose();
}
