namespace MuxSwarm.Engine;

/// <summary>
/// Captures the final result text of one daemon-fired run (e.g. an inbound webhook) so it can be
/// sent back to the caller. Flows with the run via <see cref="AsyncLocal{T}"/>; the orchestrators
/// report into it at their existing completion points (single-agent final answer, swarm/pswarm
/// signal_task_complete). Inert (a single null check) for every run that did not open a scope.
/// </summary>
internal static class RunResult
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Open a capture scope for the current async flow. Dispose to close it.</summary>
    public static Scope Begin()
    {
        var scope = new Scope();
        Current.Value = scope;
        return scope;
    }

    /// <summary>Record the latest result text for the enclosing scope (last write wins). No-op without one.</summary>
    public static void Report(string? text)
    {
        if (Current.Value is { } scope && !string.IsNullOrWhiteSpace(text)) scope.Text = text;
    }

    /// <summary>A capture scope; <see cref="Text"/> holds the last reported result.</summary>
    public sealed class Scope : IDisposable
    {
        /// <summary>Last reported result text, or null when the run reported none.</summary>
        public string? Text { get; internal set; }

        public void Dispose() { if (ReferenceEquals(Current.Value, this)) Current.Value = null; }
    }
}
