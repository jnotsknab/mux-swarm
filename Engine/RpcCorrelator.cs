using System.Collections.Concurrent;
using System.Text.Json;

namespace MuxSwarm.Engine;

/// <summary>
/// Matches JSON-RPC requests Mux sends with the responses that come back: each request gets a
/// fresh id and a task that completes with the response message. Shared by the ACP server and
/// extension processes. A timeout, cancellation or <see cref="CancelAll"/> completes the task
/// with <c>default</c> (an Undefined element), so callers never hang on a silent peer.
/// </summary>
internal sealed class RpcCorrelator
{
    private long _lastId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();

    /// <param name="firstId">Id of the first request (later ids increment from it).</param>
    internal RpcCorrelator(long firstId = 1) => _lastId = firstId - 1;

    /// <summary>Number of requests still waiting for a response.</summary>
    internal int PendingCount => _pending.Count;

    /// <summary>
    /// Register a new request. Returns its id (put it in the outgoing message) and a task that
    /// completes with the whole response message, or <c>default</c> after <paramref name="timeout"/>.
    /// </summary>
    internal (long Id, Task<JsonElement> Response) Begin(TimeSpan timeout, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _lastId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var registration = cts.Token.Register(() => Complete(id, default));
        tcs.Task.ContinueWith(_ => { registration.Dispose(); cts.Dispose(); }, TaskScheduler.Default);
        return (id, tcs.Task);
    }

    /// <summary>
    /// Route an incoming response message (an object with an <c>id</c> and no <c>method</c>) to
    /// its waiting request. False when it is not a response or the id is unknown/expired.
    /// </summary>
    internal bool TryComplete(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || message.TryGetProperty("method", out _)) return false;
        if (!message.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) return false;
        return Complete(id, message.Clone());
    }

    /// <summary>Complete request <paramref name="id"/> with <paramref name="message"/> (default = no answer).</summary>
    internal bool Complete(long id, JsonElement message) =>
        _pending.TryRemove(id, out var tcs) && tcs.TrySetResult(message);

    /// <summary>Complete every waiting request with <c>default</c> (peer went away).</summary>
    internal void CancelAll()
    {
        foreach (var id in _pending.Keys) Complete(id, default);
    }
}
