namespace MuxSwarm.Utils.Tui;

using System.Collections.Concurrent;
using System.Text;

/// <summary>
/// THE single input plane for the frame engine: one dedicated thread is the ONLY caller of
/// Console.ReadKey (and, on Windows, the Win32 record reader) in interactive TUI mode. Every
/// consumer — the idle prompt loop (TuiDriver.ReadLine), the mid-turn EscapeKeyListener, and the
/// modal overlays (NAV / Agent View) — reads typed events out of the pump's queue instead of
/// touching stdin. This eliminates the two-concurrent-readers race that tore SGR mouse reports
/// mid-sequence and leaked literal "&lt;64;…" / "&lt;[&lt;…" fragments into the input box (and
/// silently discarded typed keys mid-turn). A report can split at ANY byte; because the pump holds
/// an ESC in its reassembly state machine until the sequence classifies, no fragment of one ever
/// becomes a key event.
/// </summary>
internal sealed class ConsoleInputPump : IDisposable
{
    internal enum EventKind { Key, Wheel, Paste }

    /// <summary>One typed input event. Key: a console key (including a classified bare/Alt ESC).
    /// Wheel: net wheel notches (positive = up/back into history). Paste: full pasted text with
    /// newlines normalized to '\n' (bracketed paste or burst paste, already reassembled).</summary>
    internal readonly record struct InputEvent(EventKind Kind, ConsoleKeyInfo Key, int WheelDir, string? PasteText)
    {
        public static InputEvent OfKey(ConsoleKeyInfo k) => new(EventKind.Key, k, 0, null);
        public static InputEvent OfWheel(int dir) => new(EventKind.Wheel, default, dir, null);
        public static InputEvent OfPaste(string text) => new(EventKind.Paste, default, 0, text);
    }

    private static readonly object _currentGate = new();
    private static ConsoleInputPump? _current;

    /// <summary>The running pump, or null when the frame engine is not active. Consumers check
    /// this to choose the pump path vs. their legacy (non-frame) read path.</summary>
    internal static ConsoleInputPump? Current { get { lock (_currentGate) return _current; } }

    private readonly BlockingCollection<InputEvent> _queue = new(new ConcurrentQueue<InputEvent>());
    private readonly Queue<InputEvent> _front = new();   // PushFront replay (replay wins FIFO)
    private readonly object _frontGate = new();
    private readonly SgrInputAssembler _asm;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    private ConsoleInputPump(bool mouseTracking, bool bracketedPaste)
    {
        _asm = new SgrInputAssembler(mouseTracking, bracketedPaste);
        _thread = new Thread(PumpMain) { IsBackground = true, Name = "ConsoleInputPump" };
    }

    /// <summary>Start THE pump (idempotent: an existing pump is returned). One per process; the
    /// frame engine starts it at activation and stops it at teardown.</summary>
    internal static ConsoleInputPump Start(bool mouseTracking, bool bracketedPaste)
    {
        lock (_currentGate)
        {
            if (_current is { _disposed: 0 }) return _current;
            _current = new ConsoleInputPump(mouseTracking, bracketedPaste);
            _current._thread.Start();
            return _current;
        }
    }

    /// <summary>Number of events buffered and ready to take (burst-paste heuristic, repaint
    /// coalescing). Includes the PushFront replay lane.</summary>
    internal int PendingCount
    {
        get { lock (_frontGate) return _front.Count + _queue.Count; }
    }

    /// <summary>Take the next event, waiting up to <paramref name="timeoutMs"/> (0 = nonblocking,
    /// -1 = forever). Returns false on timeout or after Dispose.</summary>
    internal bool TryTake(out InputEvent ev, int timeoutMs)
    {
        lock (_frontGate)
        {
            if (_front.Count > 0) { ev = _front.Dequeue(); return true; }
        }
        try
        {
            if (timeoutMs < 0) return _queue.TryTake(out ev, Timeout.Infinite, _cts.Token);
            if (timeoutMs == 0) return _queue.TryTake(out ev);
            return _queue.TryTake(out ev, timeoutMs, _cts.Token);
        }
        catch (OperationCanceledException) { ev = default; return false; }
        catch (InvalidOperationException) { ev = default; return false; }   // CompleteAdding
    }

    /// <summary>Replay keys at the FRONT of the stream (FIFO preserved). Used for keys the
    /// mid-turn listener consumed but did not act on (typed chars during a turn), replacing the
    /// old EscapeKeyListener → ungetq bridge: same guarantee, one queue.</summary>
    internal void PushFront(IEnumerable<InputEvent> events)
    {
        lock (_frontGate)
            foreach (var e in events) _front.Enqueue(e);
    }

    // ---------------------------------------------------------------- pump thread

    private void PumpMain()
    {
        // The pump reads Ctrl+C as a key (the editor's Cancel signal) for its whole lifetime;
        // restored on Dispose. Spectre prompts during suspension get ^C as text instead of a
        // CancelKeyPress - accepted trade for a single owner (prompts have their own cancel keys).
        bool prevCtrlC = false;
        try { prevCtrlC = Console.TreatControlCAsInput; Console.TreatControlCAsInput = true; } catch { }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Prompts own stdin exclusively: stand down exactly like the listener does.
                if (EscapeKeyListener.IsInputSuspended)
                {
                    FlushAssemblerTimeout();
                    Thread.Sleep(20);
                    continue;
                }

                // Read UNDER the shared read gate so SuspendInput()'s barrier truly means "no
                // reader in flight" for the pump too (it is the only reader, but a prompt must
                // not race an in-flight poll). Re-check suspension inside the gate to close the
                // increment-after-check race, same contract as the listener.
                lock (EscapeKeyListener.ReadGate)
                {
                    if (EscapeKeyListener.IsInputSuspended) { Thread.Sleep(20); continue; }
                    if (Win32ConsoleInput.Active)
                        PumpWin32Slice();
                    else
                        PumpConsoleSlice();
                }
            }
        }
        catch (Exception) { /* the pump must never take the process down; consumers fall back */ }
        finally
        {
            try { Console.TreatControlCAsInput = prevCtrlC; } catch { }
        }
    }

    /// <summary>Unix / no-Win32-records slice: poll Console.KeyAvailable in ~10ms cadence, feed
    /// every key through the SGR assembler, and flush the assembler's ESC-classification window on
    /// idle so a bare Esc is emitted promptly.</summary>
    private void PumpConsoleSlice()
    {
        ConsoleKeyInfo key;
        try
        {
            if (!Console.KeyAvailable)
            {
                FlushAssemblerTimeout();
                Thread.Sleep(10);
                return;
            }
            key = Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException) { Thread.Sleep(50); return; }   // stdin redirected

        foreach (var e in _asm.Feed(key)) Enqueue(e);
    }

    /// <summary>Windows slice: mouse arrives as console INPUT RECORDS that Console.ReadKey would
    /// discard, so read the Win32 record queue. Mouse records classify to Wheel events (never
    /// keys); keydowns flow through the same assembler as the Unix path.</summary>
    private void PumpWin32Slice()
    {
        if (!Win32ConsoleInput.TryReadEvent(out var wev))
        {
            FlushAssemblerTimeout();
            Thread.Sleep(10);
            return;
        }
        if (wev.HasKey)
        {
            foreach (var e in _asm.Feed(wev.Key)) Enqueue(e);
            return;
        }
        if (wev.IsMouse)
        {
            int dir = MouseSgrParser.WheelDirection(wev.Button);
            if (dir != 0) Enqueue(InputEvent.OfWheel(dir));
        }
    }

    /// <summary>How long the raw stream must be idle before a pending ESC sequence gives up
    /// waiting for more bytes and classifies as-is (bare Esc / partial paste). Long enough to
    /// bridge chunked delivery (a ConPTY/SSH paste chunk boundary inside the ESC[200~ opener),
    /// short enough that a human Esc press still feels instant.</summary>
    private const int EscClassifyWindowMs = 50;

    /// <summary>Classification-window flush. The old behavior flushed on the FIRST empty poll,
    /// which tore any sequence whose bytes arrived in chunks - a paste chunk boundary inside the
    /// ESC[200~ opener leaked "[200~" into the editor as literal text.</summary>
    private void FlushAssemblerTimeout()
    {
        if (!_asm.PendingExpired(EscClassifyWindowMs)) return;
        foreach (var e in _asm.FlushTimeout()) Enqueue(e);
    }

    private void Enqueue(InputEvent ev)
    {
        try { _queue.Add(ev, _cts.Token); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_currentGate) if (ReferenceEquals(_current, this)) _current = null;
        try { _cts.Cancel(); } catch { }
        try { _queue.CompleteAdding(); } catch { }
        try { if (!_thread.Join(TimeSpan.FromSeconds(2))) { /* background thread; leave it */ } } catch { }
        try { _queue.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}

/// <summary>
/// Pure state machine reassembling the raw key stream into typed events. Fed one ConsoleKeyInfo at
/// a time; emits keys, wheel events, and whole-paste events. ESC is held in a pending state until
/// the following bytes classify the sequence (SGR mouse report / bracketed paste / Alt-chord /
/// bare Esc); <see cref="FlushTimeout"/> emits a bare Esc (or a partial paste) when the
/// classification window (~50ms of stream idle, gated by the pump via <see cref="PendingExpired"/>) expires. A torn report is DROPPED in
/// the machine — it can never surface as key events, at any split point.
/// </summary>
internal sealed class SgrInputAssembler
{
    private enum State { Ground, Esc, EscBracket, PasteOpen, PasteBody, PasteClose, MouseBody, MouseBodyDiscard }

    private readonly bool _mouseTracking;
    private readonly bool _bracketedPaste;
    private State _state = State.Ground;
    private readonly StringBuilder _acc = new(32);     // paste text or mouse body
    private int _openMatched;                          // "[200~" / "[201~" match progress
    private long _lastFeedTick = Environment.TickCount64;

    private static readonly char[] PasteOpenTail = { '[', '2', '0', '0', '~' };
    private static readonly char[] PasteCloseTail = { '[', '2', '0', '1', '~' };
    private static readonly ConsoleKeyInfo EscKey = new('\u001b', ConsoleKey.Escape, false, false, false);

    internal SgrInputAssembler(bool mouseTracking, bool bracketedPaste)
    {
        _mouseTracking = mouseTracking;
        _bracketedPaste = bracketedPaste;
    }

    internal bool HasPending => _state != State.Ground;

    /// <summary>True when a sequence is pending AND the stream has been idle for at least
    /// <paramref name="idleMs"/> since the last fed key - i.e. the classification window has
    /// expired and <see cref="FlushTimeout"/> may run without tearing an in-flight sequence.</summary>
    internal bool PendingExpired(int idleMs)
        => HasPending && Environment.TickCount64 - _lastFeedTick >= idleMs;

    internal IEnumerable<ConsoleInputPump.InputEvent> Feed(ConsoleKeyInfo key)
    {
        _lastFeedTick = Environment.TickCount64;
        var outp = new List<ConsoleInputPump.InputEvent>(2);
        char c = key.KeyChar;

        switch (_state)
        {
            case State.Ground:
                if (c == '\u001b') { _state = State.Esc; break; }
                outp.Add(ConsoleInputPump.InputEvent.OfKey(key));
                break;

            case State.Esc:
                if (c == '[') { _state = State.EscBracket; break; }
                // Alt-chord (ESC+char) or a second ESC: emit the held ESC, reprocess this key.
                outp.Add(ConsoleInputPump.InputEvent.OfKey(EscKey));
                _state = State.Ground;
                outp.AddRange(Feed(key));
                break;

            case State.EscBracket:
                if (c == '<' && _mouseTracking) { _state = State.MouseBody; _acc.Clear(); break; }
                if (c == '2' && _bracketedPaste) { _state = State.PasteOpen; _openMatched = 2; break; }   // "[2" matched
                // Not a sequence we own: emit ESC + '[' + this char.
                outp.Add(ConsoleInputPump.InputEvent.OfKey(EscKey));
                outp.Add(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('[', ConsoleKey.Oem4, false, false, false)));
                _state = State.Ground;
                outp.AddRange(Feed(key));
                break;

            case State.PasteOpen:
                if (c == PasteOpenTail[_openMatched])
                {
                    _openMatched++;
                    if (_openMatched == PasteOpenTail.Length) { _state = State.PasteBody; _acc.Clear(); }
                    break;
                }
                // False alarm: emit ESC + matched prefix + this char as literal keys.
                outp.Add(ConsoleInputPump.InputEvent.OfKey(EscKey));
                for (int i = 0; i < _openMatched; i++)
                    outp.Add(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo(PasteOpenTail[i], ConsoleKey.NoName, false, false, false)));
                _state = State.Ground;
                outp.AddRange(Feed(key));
                break;

            case State.PasteBody:
                if (c == '\u001b') { _state = State.PasteClose; _openMatched = 0; break; }
                AppendPasteChar(key);
                break;

            case State.PasteClose:
                if (c == PasteCloseTail[_openMatched])
                {
                    _openMatched++;
                    if (_openMatched == PasteCloseTail.Length)
                    {
                        outp.Add(ConsoleInputPump.InputEvent.OfPaste(NormalizePaste(_acc.ToString())));
                        _state = State.Ground;
                    }
                    break;
                }
                // ESC inside the paste was literal text: keep it + the partial close tail.
                _acc.Append('\u001b');
                for (int i = 0; i < _openMatched; i++) _acc.Append(PasteCloseTail[i]);
                _state = State.PasteBody;
                outp.AddRange(Feed(key));
                break;

            case State.MouseBody:
                if (c is 'M' or 'm')
                {
                    if (MouseSgrParser.TryParseBody(_acc.ToString(), out int button, out _, out _))
                    {
                        int dir = MouseSgrParser.WheelDirection(button);
                        if (dir != 0) outp.Add(ConsoleInputPump.InputEvent.OfWheel(dir));
                        // Non-wheel mouse events are swallowed by design (press/drag sinks unwired).
                    }
                    // Malformed body: drop the report; NEVER emit its bytes as keys.
                    _state = State.Ground;
                    break;
                }
                if (c == '\u001b')
                {
                    // Torn report (terminator lost): drop it, but reprocess the ESC as a new prefix.
                    _state = State.Esc;
                    break;
                }
                if (c != '\0') _acc.Append(c);
                if (_acc.Length > 32) _state = State.MouseBodyDiscard;   // runaway: swallow to terminator
                break;

            case State.MouseBodyDiscard:
                // Over-long/torn body: keep swallowing until the M/m terminator (or an ESC opening
                // a fresh sequence) so garbage bytes never surface as keys.
                if (c is 'M' or 'm') _state = State.Ground;
                else if (c == '\u001b') _state = State.Esc;
                break;
        }
        return outp;
    }

    /// <summary>The classification window expired (pump idle ~10ms with a pending sequence).
    /// A bare ESC is emitted as a key; a partial paste is flushed as a paste (never lose pasted
    /// text); a partial mouse report is DROPPED (torn - its bytes must never become keys).</summary>
    internal IEnumerable<ConsoleInputPump.InputEvent> FlushTimeout()
    {
        var outp = new List<ConsoleInputPump.InputEvent>(2);
        switch (_state)
        {
            case State.Esc:
                outp.Add(ConsoleInputPump.InputEvent.OfKey(EscKey));
                _state = State.Ground;
                break;
            case State.EscBracket:
                outp.Add(ConsoleInputPump.InputEvent.OfKey(EscKey));
                outp.Add(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('[', ConsoleKey.Oem4, false, false, false)));
                _state = State.Ground;
                break;
            case State.PasteOpen:
                outp.Add(ConsoleInputPump.InputEvent.OfKey(EscKey));
                for (int i = 0; i < _openMatched; i++)
                    outp.Add(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo(PasteOpenTail[i], ConsoleKey.NoName, false, false, false)));
                _state = State.Ground;
                break;
            case State.PasteBody:
                outp.Add(ConsoleInputPump.InputEvent.OfPaste(NormalizePaste(_acc.ToString())));
                _state = State.Ground;
                break;
            case State.PasteClose:
                _acc.Append('\u001b');
                for (int i = 0; i < _openMatched; i++) _acc.Append(PasteCloseTail[i]);
                outp.Add(ConsoleInputPump.InputEvent.OfPaste(NormalizePaste(_acc.ToString())));
                _state = State.Ground;
                break;
            case State.MouseBody:
            case State.MouseBodyDiscard:
                _state = State.Ground;   // torn report: drop, never leak
                break;
        }
        return outp;
    }

    private void AppendPasteChar(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n') { _acc.Append('\n'); return; }
        if (key.KeyChar != '\0') _acc.Append(key.KeyChar);
    }

    private static string NormalizePaste(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");
}
