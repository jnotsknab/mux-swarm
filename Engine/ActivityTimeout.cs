namespace MuxSwarm.Engine;

/// <summary>
/// Cancels a linked CancellationTokenSource if no activity (calls to Ping)
/// occurs within the configured timeout. Use as a deadman's switch for stalled streams.
/// </summary>
public sealed class ActivityTimeout : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Timer _timer;
    private readonly TimeSpan _timeout;
    private int _disposed;
    private volatile bool _suspended;

    private ActivityTimeout(CancellationTokenSource cts, Timer timer, TimeSpan timeout)
    {
        _cts = cts;
        _timer = timer;
        _timeout = timeout;
    }

    public static ActivityTimeout Start(TimeSpan timeout, CancellationToken innerToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(innerToken);

        var timer = new Timer(
            _ => { try { cts.Cancel(); } catch { /* already disposed */ } },
            null,
            timeout,
            Timeout.InfiniteTimeSpan);

        return new ActivityTimeout(cts, timer, timeout);
    }

    public CancellationToken Token => _cts.Token;

    public void Ping()
    {
        if (_suspended) return;
        try { _timer.Change(_timeout, Timeout.InfiniteTimeSpan); }
        catch { /* disposed race — harmless */ }
    }

    /// <summary>
    /// Stops the countdown until <see cref="Resume"/>, e.g. while a tool waits on the user (ask_user):
    /// a slow human is not a stalled stream. Pings are ignored while suspended so trailing stream
    /// updates cannot re-arm it early.
    /// </summary>
    public void Suspend()
    {
        _suspended = true;
        try { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch { /* disposed race — harmless */ }
    }

    /// <summary>Re-arms the full timeout. Safe to call when not suspended (acts like <see cref="Ping"/>).</summary>
    public void Resume()
    {
        _suspended = false;
        Ping();
    }

    /// <summary>True once the watchdog (or its linked parent token) cancelled the stream token. Safe after Dispose.</summary>
    public bool Expired => _cts.IsCancellationRequested;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
            _cts.Dispose();
        }
    }
}