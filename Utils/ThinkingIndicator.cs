namespace MuxSwarm.Utils;

public sealed class ThinkingIndicator : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _renderRaw;     // expects raw text; caller may include \r
    private readonly Action<int> _clearLine;
    private readonly object _consoleLock;

    private volatile string _status = "thinking";
    private int _disposed;

    private int _maxLen;                // max rendered length for safe clearing/padding
    private volatile bool _hasRendered; // whether we've actually drawn at least once

    /// <summary>
    /// Signals that the animation loop has fully exited and will no longer
    /// write to the console. Stop() blocks on this to ensure deterministic cleanup.
    /// Initialized as signaled so that no-op indicators (where Start() is never called)
    /// don't block in Stop(). Reset to unsignaled when Start() is called.
    /// </summary>
    private readonly ManualResetEventSlim _loopExited = new(true);

    /// <summary>
    /// When true, the animation loop must NOT perform its own final _clearLine
    /// because the caller has already cleared the line under the lock.
    /// This prevents the race where the loop's deferred clear erases new content.
    /// </summary>
    private volatile bool _clearedExternally;

    private static readonly string[] Frames =
        ["⠀","⠄","⠆","⠦","⠶","⠷","⣷","⣿","⣷","⠷","⠶","⠦","⠆","⠄","⠀"];

    internal ThinkingIndicator(Action<string> renderRaw, Action<int> clearLine, object consoleLock)
    {
        _renderRaw = renderRaw;
        _clearLine = clearLine;
        _consoleLock = consoleLock;
    }

    /// <summary>True once the indicator has drawn at least one frame.</summary>
    internal bool HasRendered => _hasRendered;

    /// <summary>Current max line length used for padding/clearing.</summary>
    internal int MaxLen => _maxLen;

    /// <summary>Update the status phrase to reflect what's actually happening.</summary>
    public void UpdateStatus(string status) => _status = status;
    
    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch { return 120; }
    }
    
    /// <summary>Starts the animation loop. Call once after construction.</summary>
    internal void Start(string agentName)
    {
        _loopExited.Reset();
        _ = Task.Run(async () =>
        {
            try
            {
                int frame = 0;

                while (!_cts.Token.IsCancellationRequested)
                {
                    string spinner = Frames[frame++ % Frames.Length];
                    string line = $"  {spinner} {agentName} {_status}...";

                    // Track max so we can always erase fully; pad to avoid leftover characters.
                    int maxWidth = SafeWindowWidth() - 1;
                    if (line.Length > maxWidth)
                        line = line[..(maxWidth - 3)] + "...";

                    int len = line.Length;
                    if (len > _maxLen) _maxLen = len;
                    
                    string padded = line.PadRight(_maxLen);

                    lock (_consoleLock)
                    {
                        if (!_cts.Token.IsCancellationRequested)
                        {
                            _hasRendered = true;
                            _renderRaw("\r" + padded);
                        }
                    }

                    try { await Task.Delay(80, _cts.Token); }
                    catch (OperationCanceledException) { break; }
                }

                // Only clear if nobody else already cleared for us.
                // When ClearNow_NoLock + Stop is called externally (e.g. BeginStreaming),
                // _clearedExternally is set so we skip this to avoid erasing new content.
                if (!_clearedExternally)
                {
                    lock (_consoleLock)
                    {
                        if (!_clearedExternally && _maxLen > 0)
                            _clearLine(_maxLen);
                    }
                }
            }
            finally
            {
                _loopExited.Set();
            }
        });
    }

    /// <summary>
    /// Clears the indicator line.
    /// Safe to call from anywhere; it locks internally.
    /// </summary>
    internal void ClearNow()
    {
        lock (_consoleLock)
        {
            if (_maxLen > 0)
            {
                _clearLine(_maxLen);
                _clearedExternally = true;
            }
        }
    }

    /// <summary>
    /// Clears the indicator line WITHOUT taking the lock.
    /// Use ONLY when the caller already holds the same console lock.
    /// </summary>
    internal void ClearNow_NoLock()
    {
        if (_maxLen > 0)
        {
            _clearLine(_maxLen);
            _clearedExternally = true;
        }
    }

    /// <summary>
    /// Cancel the animation without waiting for the loop to exit.
    /// Use when the caller already holds ConsoleLock (to avoid deadlock).
    /// The _clearedExternally flag must be set before calling this
    /// (via ClearNow_NoLock) so the loop's deferred clear is suppressed.
    /// </summary>
    internal void CancelNoWait()
    {
        try { _cts.Cancel(); }
        catch { /* ignore */ }
    }

    /// <summary>Stop the animation and wait for the loop to fully exit.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _cts.Cancel(); }
            catch { /* ignore */ }

            // Wait for the animation loop to finish so it can't write after we return.
            // Use a timeout to avoid hanging if the loop never started.
            _loopExited.Wait(TimeSpan.FromMilliseconds(500));

            _cts.Dispose();
            _loopExited.Dispose();
        }
    }

    public void Dispose() => Stop();
}