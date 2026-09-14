using System.Collections.Concurrent;
using System.Text;

namespace MuxSwarm.Engine.Tui;

/// <summary>
/// THE single input plane for both live TUI renderers: one dedicated thread is the ONLY caller of
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
    internal enum EventKind { Key, Wheel, Paste, Terminal, InferredPaste, Mouse }

    /// <summary>One typed input event. Key: a console key (including a classified bare/Alt ESC).
    /// Wheel: net wheel notches (positive = up/back into history). Paste: full pasted text with
    /// newlines normalized to '\n' (an explicit paste transaction, already reassembled).
    /// Mouse: a left-button press/drag/release report carrying the raw SGR button code plus 1-based
    /// cell coordinates; consumers classify it via the driver's MouseHandler (the semantic seam) and
    /// hit-test against the last presented frame. Wheel stays its own kind so burst coalescing is
    /// untouched.</summary>
    internal readonly record struct InputEvent(EventKind Kind, ConsoleKeyInfo Key, int WheelDir, string? PasteText,
        int MouseButton = 0, int MouseCol = 0, int MouseRow = 0, bool MouseRelease = false)
    {
        public static InputEvent OfKey(ConsoleKeyInfo k) => new(EventKind.Key, k, 0, null);
        public static InputEvent OfWheel(int dir) => new(EventKind.Wheel, default, dir, null);
        public static InputEvent OfPaste(string text) => new(EventKind.Paste, default, 0, text);
        internal static InputEvent OfInferredPaste(string text) => new(EventKind.InferredPaste, default, 0, text);
        internal static InputEvent OfTerminal(string packet) => new(EventKind.Terminal, default, 0, packet);
        /// <summary>A left-button press/drag/release report. <paramref name="button"/> is the RAW SGR
        /// code (motion/modifier flags preserved so MouseHandler.Classify sees them); col/row are
        /// 1-based terminal cells; <paramref name="release"/> mirrors the SGR final byte (m).</summary>
        internal static InputEvent OfMouse(int button, int col, int row, bool release)
            => new(EventKind.Mouse, default, 0, null, button, col, row, release);
    }

    private static readonly object _currentGate = new();
    private static ConsoleInputPump? _current;

    /// <summary>The running live-TUI pump, or null outside either docked renderer. Consumers
    /// use this to choose shared capture vs. the non-TUI/legacy input fallback.</summary>
    internal static ConsoleInputPump? Current { get { lock (_currentGate) return _current; } }

    private static volatile bool _promptActive;

    /// <summary>PROMPT OWNERSHIP: true for the whole lifetime of TuiDriver.ReadLine. The
    /// orchestrators' mid-turn EscapeKeyListener outlives its turn (its using-scope spans the
    /// whole goal iteration, INCLUDING the idle prompt that follows), so without this flag the
    /// listener and the prompt loop both block on the same queue and events round-robin between
    /// them - the listener steals typed chars it does not act on into its replay queue, which
    /// only drains at the NEXT prompt entry. Symptom: choppy/dropped typing at the post-turn
    /// prompt, with the missing chars "flushed" into the input box after the next agent reply.
    /// While set, the listener stands down entirely; a take it wins in the transition race is
    /// pushed back to the front lane instead of consumed.</summary>
    internal static bool PromptActive
    {
        get => _promptActive;
        set => _promptActive = value;
    }

    private static volatile bool _modalActive;

    /// <summary>MODAL OWNERSHIP (v0.12.4 in-frame prompt modal): true while TuiDriver.RunPromptModal
    /// is consuming the pump. The ask_user tool wrapper calls EscapeKeyListener.Pause() around its
    /// whole prompt, which flips IsInputSuspended - the pump's stand-down signal for yielding raw
    /// stdin to a legacy Spectre prompt. The in-frame modal is NOT a raw-stdin reader (it feeds off
    /// this pump), so standing down would deadlock it: modal blocks on TryTake, pump refuses to
    /// read until the suspension lifts, and the suspension lifts only when the modal returns.
    /// While set, the pump keeps reading THROUGH the suspension; the EscapeKeyListener still
    /// stands down via PromptActive, so the modal remains the sole consumer.</summary>
    internal static bool ModalActive
    {
        get => _modalActive;
        set => _modalActive = value;
    }

    /// <summary>True after Dispose: lets a bounded-wait consumer distinguish "timeout, keep
    /// polling" from "pump torn down" (the blocking-forever take used to encode this).</summary>
    internal bool Disposed => Volatile.Read(ref _disposed) == 1;

    private readonly BlockingCollection<InputEvent> _queue = new(new ConcurrentQueue<InputEvent>());
    private readonly Queue<InputEvent> _front = new();   // PushFront replay (replay wins FIFO)
    private readonly object _frontGate = new();
    private readonly SgrInputAssembler _asm;
    private readonly UnframedPasteBuffer _unframed = new();
    internal static Func<bool>? IsComposing;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly bool _originalCtrlC;
    private readonly bool _captureMouse;

    private ConsoleInputPump(bool mouseTracking, bool bracketedPaste, bool captureMouse = true)
    {
        try { _originalCtrlC = Console.TreatControlCAsInput; } catch { }
        _captureMouse = captureMouse;
        _asm = new SgrInputAssembler(mouseTracking, bracketedPaste);
        _thread = new Thread(PumpMain) { IsBackground = true, Name = "ConsoleInputPump" };
    }

    /// <summary>Test-only: an UNSTARTED pump (no reader thread, no console access) so the
    /// queue/front-lane ordering contract is testable without a real stdin.</summary>
    internal static ConsoleInputPump CreateUnstartedForTest(bool mouseTracking = true, bool bracketedPaste = true)
        => new(mouseTracking, bracketedPaste);

    /// <summary>Start THE pump (idempotent: an existing pump is returned). One per process;
    /// both renderers start it at activation and stop it at teardown. Native mouse capture is
    /// disabled for inline presentation and retained for frame presentation.</summary>
    internal static ConsoleInputPump Start(bool mouseTracking, bool bracketedPaste, bool captureMouse = true)
    {
        lock (_currentGate)
        {
            if (_current is { _disposed: 0 }) return _current;
            _current = new ConsoleInputPump(mouseTracking, bracketedPaste, captureMouse);
            if (OperatingSystem.IsWindows()) Win32ConsoleInput.EnableInput(mouse: _current._captureMouse, virtualTerminal: true);
            _current._thread.Start();
            return _current;
        }
    }

    /// <summary>Number of classified events ready to take, used only for repaint coalescing.
    /// Includes the PushFront replay lane; queue occupancy never defines paste boundaries.</summary>
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
        bool prevCtrlC = _originalCtrlC;
        try { Console.TreatControlCAsInput = true; } catch { }

        bool nativeOwned = Win32ConsoleInput.Active;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Prompts own stdin exclusively: stand down exactly like the listener does.
                // EXCEPT while the in-frame prompt modal is consuming the pump (see ModalActive):
                // it is the prompt, and starving it here deadlocks ask_user on the frame engine.
                if (EscapeKeyListener.IsInputSuspended && !_modalActive)
                {
                    if (nativeOwned) { Win32ConsoleInput.DisableMouse(); nativeOwned = false; }
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
                    if (EscapeKeyListener.IsInputSuspended && !_modalActive) { Thread.Sleep(20); continue; }
                    if ((!nativeOwned || !Win32ConsoleInput.Active) && OperatingSystem.IsWindows())
                        nativeOwned = Win32ConsoleInput.EnableInput(mouse: _captureMouse, virtualTerminal: true);
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
            if (nativeOwned) Win32ConsoleInput.DisableMouse();
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

        FeedRawKey(key);
    }

    /// <summary>Windows slice: mouse arrives as console INPUT RECORDS that Console.ReadKey would
    /// discard, so read the Win32 record queue. Mouse records classify to typed Wheel/Mouse events
    /// (never keys) - the adapter already synthesizes SGR-style codes with 1-based coords, so the
    /// consumer-side dispatch is shared with the VT path; keydowns flow through the same assembler
    /// as the Unix path. Hover moves are consumed inside the adapter and never surface.</summary>
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
            FeedRawKey(wev.Key);
            return;
        }
        if (wev.IsMouse)
        {
            int dir = MouseSgrParser.WheelDirection(wev.Button);
            if (dir != 0) ClassifyUnframed(InputEvent.OfWheel(dir));
            else ClassifyUnframed(InputEvent.OfMouse(wev.Button, wev.X, wev.Y, wev.Release));
        }
    }

    /// <summary>How long the raw stream must be idle before a pending ESC sequence gives up
    /// waiting for more bytes and emits a bare Esc. Recognized protocol prefixes never use this timeout. Long enough to
    /// bridge chunked delivery (a ConPTY/SSH paste chunk boundary inside the ESC[200~ opener),
    /// short enough that a human Esc press still feels instant.</summary>
    private const int EscClassifyWindowMs = 50;

    /// <summary>Classification-window flush. The old behavior flushed on the FIRST empty poll,
    /// which tore any sequence whose bytes arrived in chunks - a paste chunk boundary inside the
    /// ESC[200~ opener leaked "[200~" into the editor as literal text.</summary>
    private void ClassifyUnframed(InputEvent ev)
    {
        if (IsComposing?.Invoke() != true)
        {
            foreach (var pending in _unframed.Flush(long.MaxValue)) Enqueue(pending);
            Enqueue(ev); return;
        }
        foreach (var classified in _unframed.Feed(ev, Environment.TickCount64)) Enqueue(classified);
    }

    /// <summary>Feed a captured key through framing and compatibility classification, in producer order.</summary>
    internal void FeedRawKey(ConsoleKeyInfo key)
    {
        foreach (var ev in _asm.Feed(key)) ClassifyUnframed(ev);
    }

    /// <summary>Flush pending producer classification/recovery only when its corresponding deadline expires.</summary>
    internal void FlushAssemblerTimeout()
    {
        foreach (var e in _unframed.Flush(Environment.TickCount64)) Enqueue(e);
        if (!_asm.PendingExpired(EscClassifyWindowMs)) return;
        foreach (var e in _asm.FlushTimeout()) ClassifyUnframed(e);
    }

    internal void Enqueue(InputEvent ev)
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
/// bare Esc); <see cref="FlushTimeout"/> emits a bare Esc when the
/// classification window (~50ms of stream idle, gated by the pump via <see cref="PendingExpired"/>) expires. A torn report is DROPPED in
/// the machine — it can never surface as key events, at any split point.
/// </summary>
internal sealed class SgrInputAssembler
{
    private enum State { Ground, Esc, EscBracket, PasteOpen, PasteBody, PasteClose, MouseBody, MouseBodyDiscard, Osc, OscEscape, OscDiscard, OscDiscardEscape, ModeReply, ModeDiscard, CsiKey, Ss3Key }

    private State _state = State.Ground;
    private readonly StringBuilder _acc = new(32);     // paste text or mouse body
    private int _openMatched;                          // "[200~" / "[201~" match progress
    private readonly Func<long> _clock;
    private long _lastFeedTick;
    private const int PasteRecoveryMs = 2000;

    private static readonly char[] PasteOpenTail = { '[', '2', '0', '0', '~' };
    private static readonly char[] PasteCloseTail = { '[', '2', '0', '1', '~' };
    private static readonly ConsoleKeyInfo EscKey = new('\u001b', ConsoleKey.Escape, false, false, false);

    internal SgrInputAssembler(bool mouseTracking, bool bracketedPaste, Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _lastFeedTick = _clock();
        // Negotiation controls what we ask the terminal to send, not whether a received
        // delimiter is valid. Always recognize in-flight paste frames across live toggles,
        // and always classify ESC[< as a mouse report: a stray report arriving while
        // tracking is off (terminal race, dirty prior state) is classified-and-dropped,
        // which is strictly safer than letting its bytes fall through as keys.
    }

    internal bool HasPending => _state != State.Ground;

    /// <summary>True when a sequence is pending AND the stream has been idle for at least
    /// <paramref name="idleMs"/> since the last fed key. Recognized protocol prefixes and paste bodies/closers do not
    /// expire: only the end marker or explicit Ctrl+C ends that transaction.</summary>
    internal bool PendingExpired(int idleMs)
        => HasPending && _clock() - _lastFeedTick >=
            (_state is State.EscBracket or State.PasteOpen or State.PasteBody or State.PasteClose
                or State.CsiKey or State.Ss3Key or State.Osc or State.OscEscape or State.OscDiscard
                or State.OscDiscardEscape or State.ModeReply ? PasteRecoveryMs : idleMs);

    internal IEnumerable<ConsoleInputPump.InputEvent> Feed(ConsoleKeyInfo key)
    {
        _lastFeedTick = _clock();
        var outp = new List<ConsoleInputPump.InputEvent>(2);
        char c = key.KeyChar;
        if (_state is not (State.PasteBody or State.PasteClose)) key = VtKeyboard.Normalize(key);
        if (_state is State.EscBracket or State.PasteOpen or State.PasteBody or State.PasteClose
            && key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            _acc.Clear(); _state = State.Ground; _openMatched = 0;
            outp.Add(ConsoleInputPump.InputEvent.OfKey(key));
            return outp;
        }

        switch (_state)
        {
            case State.Ground:
                if (c == '\u001b') { _state = State.Esc; break; }
                outp.Add(ConsoleInputPump.InputEvent.OfKey(key));
                break;

            case State.Esc:
                if (c is 'v' or 'V') { outp.Add(ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo(c, ConsoleKey.V, c == 'V', true, false))); _state = State.Ground; break; }
                if (c == ']') { _acc.Clear(); _acc.Append("\x1b]"); _state = State.Osc; break; }
                if (c == '[') { _state = State.EscBracket; break; }
                if (c == 'O') { _acc.Clear(); _state = State.Ss3Key; break; }
                // Alt-chord (ESC+char) or a second ESC: emit the held ESC, reprocess this key.
                outp.Add(ConsoleInputPump.InputEvent.OfKey(EscKey));
                _state = State.Ground;
                outp.AddRange(Feed(key));
                break;

            case State.EscBracket:
                if (c == '?') { _acc.Clear(); _acc.Append("\x1b[?"); _state = State.ModeReply; break; }
                if (c == '<') { _state = State.MouseBody; _acc.Clear(); break; }
                if (c == '2') { _state = State.PasteOpen; _openMatched = 2; break; }   // "[2" matched
                _acc.Clear(); _state = State.CsiKey;
                outp.AddRange(Feed(key));
                break;

            case State.PasteOpen:
                if (c == PasteOpenTail[_openMatched])
                {
                    _openMatched++;
                    if (_openMatched == PasteOpenTail.Length) { _state = State.PasteBody; _acc.Clear(); }
                    break;
                }
                // Could be a keyboard CSI beginning with 2 (Insert/F9/...). Decode as one sequence.
                _acc.Clear();
                for (int i = 1; i < _openMatched; i++) _acc.Append(PasteOpenTail[i]);
                _state = State.CsiKey;
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

            case State.CsiKey:
            case State.Ss3Key:
                _acc.Append(c);
                if (c is >= '@' and <= '~')
                {
                    if (VtKeyboard.TryDecode(_acc.ToString(), out var decoded))
                        outp.Add(ConsoleInputPump.InputEvent.OfKey(decoded));
                    // Unknown control sequences are not draft text. Never leak a partial command.
                    _acc.Clear(); _state = State.Ground;
                }
                else if (_acc.Length > 64) { _acc.Clear(); _state = State.ModeDiscard; }
                break;

            case State.ModeReply:
                _acc.Append(c);
                if (c >= '@' && c <= '~') { outp.Add(ConsoleInputPump.InputEvent.OfTerminal(_acc.ToString())); _state = State.Ground; }
                else if (_acc.Length > 64) { _acc.Clear(); _state = State.ModeDiscard; }
                break;
            case State.ModeDiscard:
                if (c >= '@' && c <= '~') _state = State.Ground;
                break;
            case State.Osc:
                if (c == '\a') { _acc.Append(c); outp.Add(ConsoleInputPump.InputEvent.OfTerminal(_acc.ToString())); _state = State.Ground; }
                else if (c == '\x1b') _state = State.OscEscape;
                else if (_acc.Length >= TerminalPasteProtocol.MaxPacketChars) { _acc.Clear(); _state = State.OscDiscard; }
                else _acc.Append(c);
                break;
            case State.OscEscape:
                if (c == '\\') { _acc.Append("\x1b\\"); outp.Add(ConsoleInputPump.InputEvent.OfTerminal(_acc.ToString())); _state = State.Ground; }
                else { _acc.Append('\x1b').Append(c); _state = State.Osc; }
                break;
            case State.OscDiscard:
                if (c == '\a') _state = State.Ground;
                else if (c == '\x1b') _state = State.OscDiscardEscape;
                else if (c == '\x03') { _state = State.Ground; outp.Add(ConsoleInputPump.InputEvent.OfKey(key)); }
                break;
            case State.OscDiscardEscape:
                if (c == '\\') _state = State.Ground;
                else if (c is '[' or ']') { _state = State.Esc; outp.AddRange(Feed(key)); }
                else _state = State.OscDiscard;
                break;
            case State.MouseBody:
                if (c is 'M' or 'm')
                {
                    if (MouseSgrParser.TryParseBody(_acc.ToString(), out int button, out int mx, out int my))
                    {
                        int dir = MouseSgrParser.WheelDirection(button);
                        if (dir != 0) outp.Add(ConsoleInputPump.InputEvent.OfWheel(dir));
                        else if ((button & 0x03) == 0)
                            // Left-button press/drag/release (motion+modifier flags preserved in the
                            // raw code; the m final byte marks release). Middle/right/no-button
                            // reports stay swallowed - only left + wheel are modeled.
                            outp.Add(ConsoleInputPump.InputEvent.OfMouse(button, mx, my, c == 'm'));
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
    /// A bare ESC is emitted as a key; a recognized paste waits for its closing marker;
    /// a partial mouse report is DROPPED (torn - its bytes must never become keys).</summary>
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
            case State.PasteOpen:
                if (_clock() - _lastFeedTick < PasteRecoveryMs) break;
                // Abandoned control prefix is not executable text.
                _state = State.Ground; _acc.Clear();
                break;
            case State.PasteBody:
            case State.PasteClose:
                if (_clock() - _lastFeedTick < PasteRecoveryMs) break;
                if (_state == State.PasteClose)
                {
                    _acc.Append('\x1b');
                    for (int i = 0; i < _openMatched; i++) _acc.Append(PasteCloseTail[i]);
                }
                outp.Add(ConsoleInputPump.InputEvent.OfInferredPaste(NormalizePaste(_acc.ToString())));
                _acc.Clear(); _state = State.Ground;
                break;
            case State.CsiKey:
            case State.Ss3Key:
            case State.ModeReply:
            case State.ModeDiscard:
                _state = State.Ground; _acc.Clear();
                break;
            case State.Osc:
            case State.OscEscape:
                _state = State.OscDiscard; _acc.Clear();
                break;
            case State.OscDiscard:
            case State.OscDiscardEscape:
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
