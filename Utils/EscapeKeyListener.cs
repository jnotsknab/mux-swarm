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

    // Suspension is REFERENCE-COUNTED + ACKNOWLEDGED. `_suspendCount > 0` tells the poll loop to
    // stand down, but that alone is racy (an in-flight poll could still steal a key). `_readGate`
    // makes it exclusive: the listener holds it ONLY across its KeyAvailable+ReadKey critical
    // section, so a suspender that takes the same gate is guaranteed no listener read is in flight
    // AND none can start until it releases. This gives an interactive prompt true exclusive stdin
    // ownership, fixing dropped/laggy keys where the listener silently consumed the user's keystroke.
    private static int _suspendCount;
    private static readonly object _readGate = new();

    /// <summary>The exclusive-stdin gate, shared with the ConsoleInputPump (frame engine): the
    /// pump holds it across its KeyAvailable+ReadKey slice so <see cref="SuspendInput"/>'s
    /// barrier means "no reader in flight" regardless of which component owns the read loop.</summary>
    internal static object ReadGate => _readGate;

    /// <summary>Test/diagnostic probe: true while the background Esc listener is suspended from
    /// consuming stdin (a prompt owns input). Reference-counted, so nested suspensions are safe.</summary>
    internal static bool IsInputSuspended => Volatile.Read(ref _suspendCount) > 0;

    // ---- Shared input plane hardening (single-owner principle) ----

    /// <summary>Keys the listener read but does NOT act on (typed chars during a turn) are pushed
    /// here instead of discarded. The prompt input loop (<see cref="Tui.TuiDriver.ReadLine"/>) drains
    /// this FIRST on entry, so a key is never lost regardless of which reader grabbed it. This is the
    /// fix for "typing during/after a turn feels laggy and misses chars".</summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<ConsoleKeyInfo> ReplayQueue = new();

    /// <summary>Enqueue a key the listener consumed but does not act on, for the prompt loop to replay.</summary>
    internal static void ReplayKey(ConsoleKeyInfo key) => ReplayQueue.Enqueue(key);

    /// <summary>Move every queued replay key into <paramref name="sink"/> (in FIFO order). Called by
    /// the prompt input loop on entry so mid-turn typing is never dropped.</summary>
    internal static void DrainReplayTo(System.Collections.Generic.Queue<ConsoleKeyInfo> sink)
    {
        while (ReplayQueue.TryDequeue(out var k)) sink.Enqueue(k);
    }

    /// <summary>Optional hook: net wheel rows to scroll when the listener drains an SGR mouse report
    /// mid-turn (positive = back into history). Set by the TUI driver; null outside the frame engine.
    /// Routing the wheel here (instead of letting the report reach the editor) is what stops the
    /// <c>[&lt;64;…</c> escape-fragment leak AND lets the user scroll during streaming.</summary>
    internal static Action<int>? OnWheelScroll { get; set; }

    private EscapeKeyListener(CancellationTokenSource listenerCts)
    {
        _listenerCts = listenerCts;
    }

    /// <summary>Legacy volatile-style pause/resume kept for callers that only need best-effort
    /// quieting. Prefer <see cref="SuspendInput"/> for prompts that read stdin — it is acknowledged
    /// (guarantees no concurrent listener read) whereas these are advisory.</summary>
    public static void Pause() => Interlocked.Increment(ref _suspendCount);
    public static void Resume()
    {
        if (Volatile.Read(ref _suspendCount) > 0) Interlocked.Decrement(ref _suspendCount);
    }

    /// <summary>Acquire EXCLUSIVE, acknowledged stdin ownership for the lifetime of the returned
    /// scope: increments the suspend count AND takes the listener's read gate, so on return no
    /// listener key-read is in flight and none can start until dispose. Wrap every blocking
    /// interactive prompt in this so the background Esc listener cannot steal the user's keys.</summary>
    public static IDisposable SuspendInput() => new InputSuspension();

    private sealed class InputSuspension : IDisposable
    {
        private int _released;
        public InputSuspension()
        {
            Interlocked.Increment(ref _suspendCount);
            // Barrier: take + release the read gate so any in-flight ReadKey has completed and the
            // loop will observe _suspendCount before its next read.
            lock (_readGate) { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            if (Volatile.Read(ref _suspendCount) > 0) Interlocked.Decrement(ref _suspendCount);
        }
    }

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
                    if (Volatile.Read(ref _suspendCount) > 0)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    ConsoleKeyInfo key;
                    var pump = Tui.ConsoleInputPump.Current;
                    if (pump is not null)
                    {
                        // SINGLE INPUT PLANE (frame engine): the pump is the only stdin reader; this
                        // listener consumes typed events. Mouse reports are already reassembled
                        // upstream - wheel arrives as a Wheel event (scroll, never a cancel), and
                        // no report byte can ever be misread as an ESC or leak into the editor.
                        //
                        // PROMPT OWNERSHIP: this listener's using-scope in the orchestrators spans
                        // the whole goal iteration - including the IDLE PROMPT after the turn ends.
                        // Without standing down there, this loop and TuiDriver.ReadLine both block
                        // on the same queue and typed events round-robin between them: every char
                        // this loop stole was detoured through the replay queue and only surfaced
                        // at the NEXT prompt entry (choppy typing + chars "flushed" a turn late).
                        if (Tui.ConsoleInputPump.PromptActive) { Thread.Sleep(20); continue; }
                        if (!pump.TryTake(out var pev, 100)) continue;
                        if (Tui.ConsoleInputPump.PromptActive)
                        {
                            // Transition race: the prompt claimed ownership while we blocked in
                            // TryTake. Hand the event straight back at the FRONT of the stream
                            // (FIFO preserved) - it belongs to the prompt, not to us.
                            pump.PushFront(new[] { pev });
                            continue;
                        }
                        if (pev.Kind == Tui.ConsoleInputPump.EventKind.Wheel)
                        {
                            if (OnWheelScroll is not null)
                            {
                                try { OnWheelScroll(pev.WheelDir); } catch { /* scroll is best-effort */ }
                            }
                            continue;
                        }
                        if (pev.Kind == Tui.ConsoleInputPump.EventKind.Paste)
                        {
                            // Mid-turn paste: replay the text as keys so it lands at the next
                            // prompt, exactly like mid-turn typing.
                            foreach (char pc in pev.PasteText ?? string.Empty)
                                ReplayKey(new ConsoleKeyInfo(pc, ConsoleKey.NoName, false, false, false));
                            continue;
                        }
                        key = pev.Key;
                    }
                    else
                    {
                        // Legacy path (inline/classic): poll stdin directly under the gate so
                        // SuspendInput()'s barrier truly means "no read in flight".
                        lock (_readGate)
                        {
                            if (Volatile.Read(ref _suspendCount) > 0) { Thread.Sleep(50); continue; }
                            if (Console.IsInputRedirected || !Console.KeyAvailable) { Thread.Sleep(100); continue; }
                            key = Console.ReadKey(intercept: true);
                        }
                    }
                    {
                        // Esc cancels. A BARE 'q'/'Q' is an alias: some terminals/apps capture Esc,
                        // so q guarantees a working cancel. It mirrors Esc exactly - scoped cancel
                        // first, else the whole turn - and only when unmodified (Ctrl/Alt+Q ignored).
                        bool cancelKey = key.Key == ConsoleKey.Escape
                            || (key.Key == ConsoleKey.Q
                                && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0);
                        if (cancelKey)
                        {
                            // Scoped cancel: if the user foregrounded (expanded) a specific
                            // sub-agent via the backslash Agent View, Esc/q cancels ONLY that child
                            // and keeps listening (siblings + the lead turn continue). With no
                            // sub-agents / none foregrounded, Esc/q cancels the whole turn as before.
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
                        // Any OTHER key the listener read but does not act on (a typed char the user
                        // pressed mid-turn) must NOT be discarded - push it to the shared replay
                        // queue so the prompt input loop replays it on entry. This is the fix for
                        // "typing feels laggy and misses chars": previously these were dropped here.
                        ReplayKey(key);
                    }
                    // Poll throttle for the LEGACY path only: the pump path already paces itself
                    // in TryTake(100) and must drain mid-turn events (typed chars, wheel) promptly.
                    if (Tui.ConsoleInputPump.Current is null) Thread.Sleep(100);
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