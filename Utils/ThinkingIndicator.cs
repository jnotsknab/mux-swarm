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
    private List<string>? _toolCalls;

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
        lock (_consoleLock)
        {
            _toolCalls = null;
        }
        _status = status;
        _onStatusUpdate?.Invoke(status);
    }

    public void UpdateStatus(IReadOnlyList<string> toolCalls)
    {
        lock (_consoleLock)
        {
            _toolCalls = new List<string>(toolCalls);
        }
        _onStatusUpdate?.Invoke($"[calling: {string.Join(", ", toolCalls)}]");
    }

    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch { return 120; }
    }

    private string BuildToolCallStatus(int budget)
    {
        // _consoleLock must be held by caller
        var calls = _toolCalls;
        if (calls == null || calls.Count == 0)
            return _status + "...";

        const string prefix = "[calling: ";
        const string suffix = "]...";
        const string separator = ", ";

        int overhead = prefix.Length + suffix.Length;
        int remaining = budget - overhead;

        if (remaining <= 0)
            return _status + "...";

        // Walk from tail, newest first, accumulate what fits
        var visible = new List<string>();
        int used = 0;

        for (int i = calls.Count - 1; i >= 0; i--)
        {
            int cost = calls[i].Length + (visible.Count > 0 ? separator.Length : 0);
            if (used + cost > remaining)
                break;
            visible.Add(calls[i]);
            used += cost;
        }

        visible.Reverse();
        return $"{prefix}{string.Join(separator, visible)}{suffix}";
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
                    string linePrefix = $"  {spinner} {agentName} ";

                    int maxWidth = SafeWindowWidth() - 1;
                    int budget = maxWidth - linePrefix.Length;

                    string statusPart;
                    lock (_consoleLock)
                    {
                        statusPart = _toolCalls != null
                            ? BuildToolCallStatus(budget)
                            : _status + "...";
                    }

                    string line = linePrefix + statusPart;

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