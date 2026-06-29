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
        => Start(targetCts, outerToken, onExpand: null, onView: null);

    public static EscapeKeyListener Start(CancellationTokenSource targetCts, CancellationToken outerToken, Action? onExpand)
        => Start(targetCts, outerToken, onExpand, onView: null);

    public static EscapeKeyListener Start(CancellationTokenSource targetCts, CancellationToken outerToken, Action? onExpand, Action? onView)
        => Start(targetCts, outerToken, onExpand, onView, onAgents: null);

    /// <summary>
    /// Start the listener with optional mid-stream affordances. While the agent is producing
    /// output: Esc cancels the turn (as before); Ctrl+E fires <paramref name="onExpand"/> to
    /// expand the latest tool result inline WITHOUT cancelling; Ctrl+G fires
    /// <paramref name="onView"/> to open the scrollback / vim view overlay at any time (so the
    /// user can scroll up and expand a specific tool block or sub-agent output mid-turn) - also
    /// without cancelling. Any other key is ignored. Callbacks run on the listener thread and
    /// must be self-serializing (the TUI driver guards with the console lock). <paramref
    /// name="onView"/> runs the overlay loop synchronously on THIS thread, so there is never a
    /// second concurrent key reader.
    /// </summary>
    public static EscapeKeyListener Start(CancellationTokenSource targetCts, CancellationToken outerToken, Action? onExpand, Action? onView, Action? onAgents)
    {
        var listenerCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);

        // Run the key poll on a DEDICATED thread, never the thread pool. During sub-agent
        // fan-out the pool is saturated by concurrent model streams + MCP subprocess calls, so a
        // pool-scheduled poll (Task.Run) is starved and the Esc keystroke is not read until the
        // batch finishes - it then "fires afterwards". A dedicated thread reads Esc immediately.
        // (Same starvation class as the ACP stdin-read fix.)
        var listenerThread = new Thread(() =>
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
                            // Scoped cancel: if the user foregrounded (expanded) a specific
                            // sub-agent via the backslash Agent View, Esc cancels ONLY that child
                            // and keeps listening (siblings + the lead turn continue). With no
                            // sub-agents / none foregrounded, Esc cancels the whole turn as before.
                            if (MuxConsole.TryCancelForegroundedSubAgent())
                                continue;
                            targetCts.Cancel();
                            break;
                        }
                        bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
                        // Ctrl+G: open the scrollback / vim view overlay mid-turn (scroll up,
                        // expand a specific tool block or sub-agent output) WITHOUT cancelling.
                        // Runs the overlay synchronously on this thread, so there is never a
                        // second concurrent key reader. Falls back to onExpand if no view handler.
                        if (ctrl && key.Key == ConsoleKey.G && (onView is not null || onExpand is not null))
                        {
                            try { if (onView is not null) onView(); else onExpand!(); }
                            catch { /* overlay is best-effort */ }
                            continue;
                        }
                        // Backslash: foreground the inline Agent View dashboard (v0.12.0 M1) -
                        // a keyboard-navigable session list over the running sub-agents - WITHOUT
                        // cancelling. Runs the dashboard loop synchronously on this thread, so there
                        // is never a second concurrent key reader (same contract as the Ctrl+G view).
                        if (key.KeyChar == '\\' && onAgents is not null)
                        {
                            try { onAgents(); } catch { /* dashboard is best-effort */ }
                            continue;
                        }
                        // Ctrl+E: expand the latest tool result inline without cancelling the turn.
                        // Swallows the keystroke; the turn keeps streaming.
                        if (ctrl && key.Key == ConsoleKey.E && onExpand is not null)
                        {
                            try { onExpand(); } catch { /* expansion is best-effort */ }
                            continue;
                        }
                        // Ctrl+L: clear resize/redraw artifacts and repaint mid-turn without
                        // cancelling. Routes through the driver (no-op outside the TUI).
                        if (ctrl && key.Key == ConsoleKey.L)
                        {
                            try { MuxConsole.TuiForceRedraw(); } catch { /* redraw is best-effort */ }
                            continue;
                        }
                        // Ctrl+T: toggle the team TaskBoard strip mid-turn (v0.12.0 M2) without
                        // cancelling. No-op when no team board is active.
                        if (ctrl && key.Key == ConsoleKey.T)
                        {
                            try { MuxConsole.TuiToggleTaskBoard(); } catch { /* strip is best-effort */ }
                            continue;
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        }) { IsBackground = true, Name = "EscapeKeyListener" };
        listenerThread.Start();

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