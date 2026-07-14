using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Delegating chat client that lets the HIGHEST reasoning tier (<see cref="ReasoningEffort.ExtraHigh"/>)
/// be requested safely against ANY endpoint. ExtraHigh serializes to the wire value "xhigh" via the
/// Microsoft.Extensions.AI.OpenAI adapter, which some OpenAI-compatible endpoints (notably the CLIProxy
/// Claude path) reject with a 400. Rather than gate the user out of /ultra, /giga, or a Shift+Tab cycle
/// to the top tier, this client attempts ExtraHigh and, if the endpoint rejects the reasoning value,
/// transparently retries the SAME request one tier lower (<see cref="ReasoningEffort.High"/>) and latches
/// that downgrade per model id so subsequent round-trips skip the doomed attempt. Endpoints that accept
/// ExtraHigh are unaffected. It sits INSIDE the function-invocation middleware, so a downgrade retries a
/// single model call, never the whole turn.
/// </summary>
public sealed class ReasoningEffortFallbackClient : DelegatingChatClient
{
    // Model ids known (this process) to reject the top reasoning tier -> preemptively downgrade.
    private static readonly ConcurrentDictionary<string, byte> _downgraded = new(StringComparer.Ordinal);

    private readonly string _modelId;

    public ReasoningEffortFallbackClient(IChatClient inner, string modelId) : base(inner)
        => _modelId = modelId ?? string.Empty;

    /// <summary>True once this model has rejected ExtraHigh, so callers can label the effective tier.</summary>
    public static bool IsDowngraded(string modelId) =>
        !string.IsNullOrEmpty(modelId) && _downgraded.ContainsKey(modelId);

    // Only rewrite when the caller actually asked for the top tier. Returns the options to send now
    // plus whether a top-tier attempt is in flight (so a rejection can trigger the one-shot retry).
    private (ChatOptions? opts, bool attemptingTop) Prepare(ChatOptions? options)
    {
        if (options?.Reasoning?.Effort != ReasoningEffort.ExtraHigh)
            return (options, false);

        // Already known-bad for this model: send High up front, no attempt.
        if (_downgraded.ContainsKey(_modelId))
            return (Downgrade(options), false);

        return (options, true);
    }

    private static ChatOptions Downgrade(ChatOptions options)
    {
        var clone = options.Clone();
        clone.Reasoning = new ReasoningOptions
        {
            Effort = ReasoningEffort.High,
            Output = options.Reasoning?.Output
        };
        return clone;
    }

    // A reasoning-tier rejection is a provider 400 that names the reasoning/effort field. Match
    // defensively on message content (the OpenAI SDK surfaces it as ClientResultException/HttpRequestException
    // with the provider body) rather than a specific exception type, since the wire error shape varies.
    private static bool IsReasoningRejection(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message;
            if (string.IsNullOrEmpty(m)) continue;
            bool mentionsReasoning =
                m.Contains("xhigh", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("reasoning effort", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("reasoning.effort", StringComparison.OrdinalIgnoreCase);
            if (mentionsReasoning)
                return true;
        }
        return false;
    }

    private void MarkDowngraded()
    {
        if (_downgraded.TryAdd(_modelId, 1))
            MuxConsole.WriteMuted($"[reasoning: endpoint rejected extra_high for {_modelId}; using high]");
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (send, attemptingTop) = Prepare(options);
        try
        {
            return await base.GetResponseAsync(messages, send, cancellationToken);
        }
        catch (Exception ex) when (attemptingTop && IsReasoningRejection(ex))
        {
            MarkDowngraded();
            return await base.GetResponseAsync(messages, Downgrade(options!), cancellationToken);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (send, attemptingTop) = Prepare(options);

        // Buffer the stream start so a rejection that surfaces on the first pull can fall back
        // WITHOUT having yielded any partial content to the caller. Once the first update lands,
        // the request is committed and streams straight through.
        IAsyncEnumerator<ChatResponseUpdate>? e = null;
        bool haveFirst = false;
        try
        {
            e = base.GetStreamingResponseAsync(messages, send, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                haveFirst = await e.MoveNextAsync();
            }
            catch (Exception ex) when (attemptingTop && IsReasoningRejection(ex))
            {
                MarkDowngraded();
                if (e is not null) await e.DisposeAsync();
                e = base.GetStreamingResponseAsync(messages, Downgrade(options!), cancellationToken).GetAsyncEnumerator(cancellationToken);
                haveFirst = await e.MoveNextAsync();
            }

            while (haveFirst)
            {
                yield return e.Current;
                haveFirst = await e.MoveNextAsync();
            }
        }
        finally
        {
            if (e is not null) await e.DisposeAsync();
        }
    }
}
