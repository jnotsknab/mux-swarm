namespace MuxSwarm.Utils;

public sealed class ThinkingIndicator : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _renderRaw;
    private readonly Action<int> _clearLine;
    private readonly object _consoleLock;
    private readonly Action<string>? _onStatusUpdate;
    private readonly Action? _onDispose;

    private volatile string _status = "thinking";
    private int _disposed;

    private int _maxLen;
    private volatile bool _hasRendered;

    private readonly ManualResetEventSlim _loopExited = new(true);
    private volatile bool _clearedExternally;

    private static readonly string[] Frames =
        ["⠀", "⠄", "⠆", "⠦", "⠶", "⠷", "⣷", "⣿", "⣷", "⠷", "⠶", "⠦", "⠆", "⠄", "⠀"];

    internal ThinkingIndicator(
        Action<string> renderRaw,
        Action<int> clearLine,
        object consoleLock,
        Action<string>? onStatusUpdate = null,
        Action? onDispose = null)
    {
        _renderRaw = renderRaw;
        _clearLine = clearLine;
        _consoleLock = consoleLock;
        _onStatusUpdate = onStatusUpdate;
        _onDispose = onDispose;
    }

    internal bool HasRendered => _hasRendered;
    internal int MaxLen => _maxLen;

    public void UpdateStatus(string status)
    {
        _status = status;
        _onStatusUpdate?.Invoke(status);
    }

    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch { return 120; }
    }

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

    internal void ClearNow_NoLock()
    {
        if (_maxLen > 0)
        {
            _clearLine(_maxLen);
            _clearedExternally = true;
        }
    }

    internal void CancelNoWait()
    {
        try { _cts.Cancel(); }
        catch { /* ignore */ }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _cts.Cancel(); }
            catch { /* ignore */ }

            _loopExited.Wait(TimeSpan.FromMilliseconds(500));

            _onDispose?.Invoke();

            _cts.Dispose();
            _loopExited.Dispose();
        }
    }

    public void Dispose() => Stop();
}