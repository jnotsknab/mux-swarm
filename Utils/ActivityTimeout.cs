namespace MuxSwarm.Utils;

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
        try { _timer.Change(_timeout, Timeout.InfiniteTimeSpan); }
        catch { /* disposed race — harmless */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
            _cts.Dispose();
        }
    }
}