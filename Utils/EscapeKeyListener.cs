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
    private static volatile bool _paused;


    private EscapeKeyListener(CancellationTokenSource listenerCts)
    {
        _listenerCts = listenerCts;
    }


    public static void Pause() => _paused = true;
    public static void Resume() => _paused = false;

    public static EscapeKeyListener Start(CancellationTokenSource targetCts, CancellationToken outerToken)
        => Start(targetCts, outerToken, onExpand: null);

    /// <summary>
    /// Start the listener with an optional <paramref name="onExpand"/> callback. While the
    /// agent is producing output, Esc cancels the turn (as before); Ctrl+E instead fires
    /// <paramref name="onExpand"/> WITHOUT cancelling - so the user can expand the latest tool
    /// result inline mid-stream. Any other key is ignored. The callback runs on the listener
    /// thread and must be self-serializing (the TUI driver guards with the console lock).
    /// </summary>
    public static EscapeKeyListener Start(CancellationTokenSource targetCts, CancellationToken outerToken, Action? onExpand)
    {
        var listenerCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);

        _ = Task.Run(() =>
        {
            try
            {
                while (!listenerCts.Token.IsCancellationRequested)
                {
                    if (_paused)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    if (!Console.IsInputRedirected && Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Escape)
                        {
                            targetCts.Cancel();
                            break;
                        }
                        // Ctrl+E: expand the latest tool result inline without cancelling the
                        // turn. Swallows the keystroke; the turn keeps streaming.
                        if (onExpand is not null
                            && key.Key == ConsoleKey.E
                            && (key.Modifiers & ConsoleModifiers.Control) != 0)
                        {
                            try { onExpand(); } catch { /* expansion is best-effort */ }
                            continue;
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