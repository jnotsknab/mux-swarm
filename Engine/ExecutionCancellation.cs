namespace MuxSwarm.Engine;

/// <summary>Execution-local cancellation ancestry, independent of stdin transport and process-global turn state.
/// Async work inherits its launching owner; entering a child scope preserves its own cancellation for descendants.</summary>
internal static class ExecutionCancellation
{
    private sealed record Node(CancellationToken Token, Node? Parent);
    private static readonly AsyncLocal<Node?> Owner = new();
    internal static CancellationToken Current => Owner.Value?.Token ?? CancellationToken.None;

    /// <summary>Enter one owner for this async flow. Disposal restores the caller, not the children already spawned.</summary>
    internal static IDisposable Enter(CancellationToken token)
    {
        var previous = Owner.Value;
        Owner.Value = new Node(token, previous);
        return new Scope(previous);
    }

    /// <summary>Link a captured session/lifetime token, actual invocation token, and this flow's owner.
    /// Keep the returned source alive for the full child/job lifetime, not just until scheduling returns.</summary>
    internal static CancellationTokenSource Link(CancellationToken lifetime, CancellationToken invocation = default)
    {
        var tokens = new HashSet<CancellationToken> { lifetime, invocation };
        for (var node = Owner.Value; node is not null; node = node.Parent) tokens.Add(node.Token);
        tokens.Remove(CancellationToken.None);
        return tokens.Count == 0 ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(tokens.ToArray());
    }

    private sealed class Scope(Node? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Owner.Value = previous;
        }
    }
}
