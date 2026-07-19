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

    /// <summary>Optional probe: true when SGR mouse reporting is enabled in the current UI (the
    /// listener should interpret an ESC as a potential mouse-report prefix). Set by the TUI driver
    /// alongside <see cref="OnWheelScroll"/>; false elsewhere, so Esc keeps its plain meaning.</summary>
    internal static Func<bool>? IsMouseReportingActive { get; set; }

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
                    // Read the key UNDER the gate so SuspendInput()'s barrier truly means "no read in
                    // flight". Re-check the suspend count inside the gate to close the race where a
                    // suspender incremented after our outer check but before we acquired the gate.
                    ConsoleKeyInfo key;
                    lock (_readGate)
                    {
                        if (Volatile.Read(ref _suspendCount) > 0) { Thread.Sleep(50); continue; }
                        if (Console.IsInputRedirected || !Console.KeyAvailable) { Thread.Sleep(100); continue; }
                        key = Console.ReadKey(intercept: true);
                    }
                    {
                        // MOUSE REPORT GUARD (Unix path): an SGR wheel report starts with ESC, so
                        // without this check a wheel tick mid-turn would be misread as the user
                        // pressing Escape and CANCEL the turn, tearing the rest of the report into
                        // the editor as literal "[<64;…" chars (the reported leak). When mouse
                        // reporting is active, patiently probe for the [< prefix first: a report is
                        // drained whole + routed to scroll; only a BARE Esc cancels.
                        if (key.Key == ConsoleKey.Escape && (IsMouseReportingActive?.Invoke() ?? false)
                            && TryDrainMouseReport())
                        {
                            continue;   // consumed a mouse report (scrolled); keep listening
                        }
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
                        // Any OTHER key the listener read but does not act on (a typed char the user
                        // pressed mid-turn) must NOT be discarded - push it to the shared replay
                        // queue so the prompt input loop replays it on entry. This is the fix for
                        // "typing feels laggy and misses chars": previously these were dropped here.
                        ReplayKey(key);
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

    // Patiently read one key (~8ms total patience) from the console UNDER the read gate. Returns
    // false when nothing arrived in time (a fragmented report over SSH/WSL/Mac can split mid-body).
    private static bool TryReadKeyPatient(out ConsoleKeyInfo k)
    {
        for (int spin = 0; spin < 8; spin++)
        {
            lock (_readGate)
            {
                if (Volatile.Read(ref _suspendCount) == 0 && !Console.IsInputRedirected && Console.KeyAvailable)
                {
                    k = Console.ReadKey(intercept: true);
                    return true;
                }
            }
            Thread.Sleep(1);
        }
        k = default;
        return false;
    }

    /// <summary>Called right after an ESC was read while SGR mouse reporting is active. Probes for
    /// the <c>[&lt;</c> prefix (patiently), drains the whole report body up to its M/m terminator, and
    /// routes a wheel event to <see cref="OnWheelScroll"/>. Returns true when an ESC[&lt; report was
    /// consumed (the ESC must NOT be treated as a cancel); false when the prefix did not follow, in
    /// which case any probed bytes are pushed back to the replay queue and the ESC keeps its meaning.</summary>
    private static bool TryDrainMouseReport()
    {
        var probed = new List<ConsoleKeyInfo>(4);
        // Expect '[' then '<'.
        foreach (char expect in new[] { '[', '<' })
        {
            if (!TryReadKeyPatient(out var k) || k.KeyChar != expect)
            {
                // Not a mouse report: push the probed bytes back so nothing is lost, ESC stays ESC.
                foreach (var pb in probed) ReplayKey(pb);
                return false;
            }
            probed.Add(k);
        }

        // Drain the body up to M/m terminator.
        var body = new System.Text.StringBuilder(12);
        bool release = false;
        while (true)
        {
            if (!TryReadKeyPatient(out var k)) break;   // torn report: drop it, never leak to editor
            char c = k.KeyChar;
            if (c == 'M' || c == 'm') { release = c == 'm'; break; }
            if (c == '\u001b') { ReplayKey(k); return true; }   // stray ESC: stop; still consumed
            if (c == '\0') return true;
            body.Append(c);
            if (body.Length > 32) return true;   // runaway guard
        }

        if (Tui.MouseSgrParser.TryParseBody(body.ToString(), out int button, out _, out _))
        {
            int dir = Tui.MouseSgrParser.WheelDirection(button);
            if (dir != 0 && OnWheelScroll is not null)
            {
                try { OnWheelScroll(dir); } catch { /* scroll is best-effort */ }
            }
        }
        return true;   // consumed (wheel scrolled or non-wheel discarded) - never leaks to the editor
    }

}