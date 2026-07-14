using Microsoft.Extensions.AI;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

public class ReasoningEffortFallbackClientTests
{
    // Fake inner client: records the effort it was asked to send, and can be told to reject
    // the top tier (ExtraHigh) the way an endpoint would (a 400 naming the reasoning field).
    private sealed class FakeInner : IChatClient
    {
        private readonly bool _rejectExtraHigh;
        public List<ReasoningEffort?> Seen { get; } = new();

        public FakeInner(bool rejectExtraHigh) => _rejectExtraHigh = rejectExtraHigh;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var eff = options?.Reasoning?.Effort;
            Seen.Add(eff);
            if (_rejectExtraHigh && eff == ReasoningEffort.ExtraHigh)
                throw new InvalidOperationException("400 Bad Request: unsupported value \"xhigh\" for reasoning_effort");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var eff = options?.Reasoning?.Effort;
            Seen.Add(eff);
            if (_rejectExtraHigh && eff == ReasoningEffort.ExtraHigh)
                throw new InvalidOperationException("400 Bad Request: unsupported value \"xhigh\" for reasoning_effort");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatOptions TopTier() =>
        new() { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

    [Fact]
    public async Task ExtraHigh_PassesThrough_WhenEndpointAccepts()
    {
        var inner = new FakeInner(rejectExtraHigh: false);
        var client = new ReasoningEffortFallbackClient(inner, "model-accepts-xhigh");

        var resp = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, TopTier());

        Assert.Single(inner.Seen);
        Assert.Equal(ReasoningEffort.ExtraHigh, inner.Seen[0]);
        Assert.Equal("ok", resp.Text);
    }

    [Fact]
    public async Task ExtraHigh_DegradesToHigh_WhenEndpointRejects_AndNeverThrows()
    {
        var inner = new FakeInner(rejectExtraHigh: true);
        var client = new ReasoningEffortFallbackClient(inner, "model-rejects-xhigh-a");

        var resp = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, TopTier());

        // First attempt ExtraHigh (rejected) -> retried once at High -> succeeds, no throw.
        Assert.Equal(2, inner.Seen.Count);
        Assert.Equal(ReasoningEffort.ExtraHigh, inner.Seen[0]);
        Assert.Equal(ReasoningEffort.High, inner.Seen[1]);
        Assert.Equal("ok", resp.Text);
        Assert.True(ReasoningEffortFallbackClient.IsDowngraded("model-rejects-xhigh-a"));
    }

    [Fact]
    public async Task AfterDowngrade_SubsequentCalls_SkipTheDoomedTopTierAttempt()
    {
        var inner = new FakeInner(rejectExtraHigh: true);
        var client = new ReasoningEffortFallbackClient(inner, "model-rejects-xhigh-b");

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "1") }, TopTier());
        inner.Seen.Clear();
        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "2") }, TopTier());

        // Latched: the second call sends High up front, one attempt only.
        Assert.Single(inner.Seen);
        Assert.Equal(ReasoningEffort.High, inner.Seen[0]);
    }

    [Fact]
    public async Task Claude_IsDowngradedProactively_NoDoomedAttempt()
    {
        // Claude has no reasoning_effort param, so xhigh is always wasted. The client should send
        // High on the FIRST call without ever attempting ExtraHigh (no burned 400 round-trip).
        var inner = new FakeInner(rejectExtraHigh: true);
        var client = new ReasoningEffortFallbackClient(inner, "claude-opus-4-8");

        var resp = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, TopTier());

        Assert.Single(inner.Seen);
        Assert.Equal(ReasoningEffort.High, inner.Seen[0]);
        Assert.Equal("ok", resp.Text);
    }

    [Fact]
    public async Task NonTopTier_IsUntouched()
    {
        var inner = new FakeInner(rejectExtraHigh: true);
        var client = new ReasoningEffortFallbackClient(inner, "model-any");
        var opts = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium } };

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, opts);

        Assert.Single(inner.Seen);
        Assert.Equal(ReasoningEffort.Medium, inner.Seen[0]);
    }

    // Inner that throws a caller-supplied exception on the ExtraHigh attempt, else succeeds at High.
    private sealed class ThrowingInner : IChatClient
    {
        private readonly Func<Exception> _onTop;
        public List<ReasoningEffort?> Seen { get; } = new();
        public ThrowingInner(Func<Exception> onTop) => _onTop = onTop;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var eff = options?.Reasoning?.Effort;
            Seen.Add(eff);
            if (eff == ReasoningEffort.ExtraHigh) throw _onTop();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var eff = options?.Reasoning?.Effort;
            Seen.Add(eff);
            if (eff == ReasoningEffort.ExtraHigh) throw _onTop();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Generic400_WithoutReasoningWording_TriggersFallback()
    {
        // OpenRouter-style: a 400 that does NOT name the reasoning field should still fall back.
        var inner = new ThrowingInner(() =>
            new System.Net.Http.HttpRequestException("invalid value for parameter",
                null, System.Net.HttpStatusCode.BadRequest));
        var client = new ReasoningEffortFallbackClient(inner, "openrouter/some-model-x");

        var resp = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, TopTier());

        Assert.Equal(2, inner.Seen.Count);
        Assert.Equal(ReasoningEffort.ExtraHigh, inner.Seen[0]);
        Assert.Equal(ReasoningEffort.High, inner.Seen[1]);
        Assert.Equal("ok", resp.Text);
    }

    [Fact]
    public async Task Non400_ClientError_DoesNotTriggerFallback()
    {
        // A 401/5xx is a real failure, not a tier problem - it must propagate, not silently retry.
        var inner = new ThrowingInner(() =>
            new System.Net.Http.HttpRequestException("unauthorized",
                null, System.Net.HttpStatusCode.Unauthorized));
        var client = new ReasoningEffortFallbackClient(inner, "openrouter/other-model-y");

        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, TopTier()));
        Assert.Single(inner.Seen);
    }

    [Fact]
    public async Task Cancellation_DoesNotTriggerFallback()
    {
        var inner = new ThrowingInner(() => new OperationCanceledException());
        var client = new ReasoningEffortFallbackClient(inner, "openrouter/model-cancel-z");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, TopTier()));
        Assert.Single(inner.Seen);
    }
}
