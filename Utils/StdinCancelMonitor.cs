namespace MuxSwarm.Utils;

/// <summary>
/// Background stdin reader for piped/stdio mode. Reads lines continuously,
/// routes regular input to a thread-safe queue, and fires a cancellation
/// signal when __CANCEL__ is received.
///
/// Replaces all direct Console.ReadLine() calls in orchestrators and agents
/// when running in stdio mode.
/// </summary>
public sealed class StdinCancelMonitor : IDisposable
{
    private readonly Thread _readerThread;
    private readonly BlockingQueue _lineQueue = new();
    private CancellationTokenSource? _activeTurnCts;
    private readonly object _ctsLock = new();
    private volatile bool _disposed;

    public static StdinCancelMonitor? Instance { get; private set; }

    private volatile bool _paused;

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    /// <summary>
    /// Start the monitor. Call once at app startup when --stdio is active.
    /// </summary>
    public static StdinCancelMonitor Start(bool startPaused = false)
    {
        var monitor = new StdinCancelMonitor(startPaused);
        Instance = monitor;
        return monitor;
    }

    private StdinCancelMonitor(bool startPaused = false)
    {
        // The ACP adapter starts the monitor PAUSED: it needs Instance to exist for
        // SetActiveTurnCts / FireCancel (so session/cancel can abort a turn), but the ACP
        // transport owns stdin itself, so the background ReadLoop must NOT also consume it.
        _paused = startPaused;
        _readerThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "StdinCancelMonitor"
        };
        _readerThread.Start();
    }

    /// <summary>
    /// Register the current turn's CTS so __CANCEL__ can fire it.
    /// Call at the start of each agent turn or orchestrator iteration.
    /// </summary>
    public void SetActiveTurnCts(CancellationTokenSource cts)
    {
        lock (_ctsLock)
        {
            _activeTurnCts = cts;
        }
    }

    /// <summary>
    /// Clear the turn CTS (call when the turn ends normally).
    /// </summary>
    public void ClearActiveTurnCts()
    {
        lock (_ctsLock)
        {
            _activeTurnCts = null;
        }
    }

    /// <summary>
    /// The cancellation token of the turn currently registered via <see cref="SetActiveTurnCts"/>,
    /// or <see cref="CancellationToken.None"/> when no turn is active. Delegated sub-agent work
    /// must observe THIS token (not just the outer app/session token) so that pressing Esc - which
    /// cancels the per-turn CTS - also tears down in-flight children. Without it, a lead blocked on
    /// Task.WhenAll(children) would wedge: Esc cancels the lead's turn token, the children keep
    /// running on the un-cancelled outer token, and input is dead until the app is restarted.
    /// </summary>
    public CancellationToken ActiveTurnToken
    {
        get { lock (_ctsLock) { return _activeTurnCts?.Token ?? CancellationToken.None; } }
    }


    /// <summary>
    /// Read the next line from stdin. Blocks until input is available.
    /// Use this instead of Console.ReadLine() everywhere.
    /// </summary>
    public string? ReadLine(CancellationToken cancellationToken = default)
    {
        return _lineQueue.Take(cancellationToken);
    }

    /// <summary>
    /// Try to read a line without blocking. Returns null if nothing queued.
    /// </summary>
    public string? TryReadLine()
    {
        return _lineQueue.TryTake();
    }

    /// <summary>
    /// Programmatically fire a cancel signal (used by ServeMode when
    /// __CANCEL__ arrives over WebSocket instead of stdin).
    /// </summary>
    public void FireCancel()
    {
        lock (_ctsLock)
        {
            if (_activeTurnCts is { IsCancellationRequested: false })
            {
                _activeTurnCts.Cancel();
                MuxConsole.WriteInfo("Cancelled by client.");
            }
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (!_disposed)
            {
                if (_paused)
                {
                    Thread.Sleep(50);
                    continue;
                }

                var line = Console.ReadLine();

                // EOF — stdin closed (container shutting down)
                if (line == null)
                {
                    _lineQueue.Add(null);
                    break;
                }

                if (line.Trim() == "__CANCEL__")
                {
                    lock (_ctsLock)
                    {
                        if (_activeTurnCts is { IsCancellationRequested: false })
                        {
                            _activeTurnCts.Cancel();
                            MuxConsole.WriteInfo("Cancelled by client.");
                        }
                    }
                    continue; // Don't queue __CANCEL__ as user input
                }

                _lineQueue.Add(line);
            }
        }
        catch (Exception)
        {
            // stdin closed or thread aborted
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Instance = null;
    }

    /// <summary>
    /// Simple thread-safe blocking queue for string lines.
    /// </summary>
    private sealed class BlockingQueue
    {
        private readonly Queue<string?> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);

        public void Add(string? item)
        {
            lock (_queue)
            {
                _queue.Enqueue(item);
            }
            _signal.Release();
        }

        public string? Take(CancellationToken ct = default)
        {
            _signal.Wait(ct);
            lock (_queue)
            {
                return _queue.Dequeue();
            }
        }

        public string? TryTake()
        {
            if (!_signal.Wait(0))
                return null;
            lock (_queue)
            {
                return _queue.Dequeue();
            }
        }
    }
}