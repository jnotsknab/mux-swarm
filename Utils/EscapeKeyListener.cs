namespace MuxSwarm.Utils;

/// <summary>
/// Monitors for the Escape key on a background thread and cancels the
/// provided CancellationTokenSource when pressed. Dispose to stop listening.
/// Shared by both SingleAgentOrchestrator and MultiAgentOrchestrator.
/// </summary>
public sealed class EscapeKeyListener : IDisposable
{
    private readonly CancellationTokenSource _listenerCts;
    private int _disposed;

    private EscapeKeyListener(CancellationTokenSource listenerCts)
    {
        _listenerCts = listenerCts;
    }

    public static EscapeKeyListener Start(CancellationTokenSource targetCts, CancellationToken outerToken)
    {
        var listenerCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);

        _ = Task.Run(() =>
        {
            try
            {
                while (!listenerCts.Token.IsCancellationRequested)
                {
                    if (!Console.IsInputRedirected && Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Escape)
                        {
                            targetCts.Cancel();
                            break;
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        }, listenerCts.Token);

        return new EscapeKeyListener(listenerCts);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _listenerCts.Cancel();
            _listenerCts.Dispose();
        }
    }
}