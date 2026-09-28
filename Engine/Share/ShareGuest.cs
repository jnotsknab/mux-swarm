using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace MuxSwarm.Engine.Share;

/// <summary>
/// An authenticated, encrypted guest connection to a sharing host. Transport + crypto only (no
/// terminal I/O) so the terminal viewer, the self-test and unit tests share one implementation.
/// </summary>
internal sealed class ShareGuestConnection : IDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly ShareChannel _channel;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ShareGuestConnection(ClientWebSocket ws, ShareChannel channel)
    {
        _ws = ws;
        _channel = channel;
    }

    /// <summary>
    /// Connect and run the handshake. Throws <see cref="ShareJoinException"/> with a user-facing
    /// message when the host is unreachable, the room is gone, or the host cannot prove it holds
    /// the link secret (wrong/stale link or an impostor).
    /// </summary>
    public static async Task<ShareGuestConnection> ConnectAsync(ShareLink link, string name, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.Proxy = null;   // direct connect: HTTP(S)_PROXY would break same-network joins
        ws.Options.CollectHttpResponseDetails = true;   // surface 404/429/503 as specific user messages
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                try { await ws.ConnectAsync(link.WebSocketUri, cts.Token); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
                {
                    throw new ShareJoinException(ws.HttpStatusCode switch
                    {
                        System.Net.HttpStatusCode.NotFound => "that share has ended (or the link is wrong).",
                        System.Net.HttpStatusCode.ServiceUnavailable => "the session is full or busy - try again shortly.",
                        System.Net.HttpStatusCode.TooManyRequests => "too many failed attempts from this address - wait a minute.",
                        _ => $"could not reach {link.Authority} - check the address and that the host chose 'all interfaces' for remote joins.",
                    });
                }
            }

            string cleanName = ShareProtocol.CleanName(name);
            byte[] nameBytes = Encoding.UTF8.GetBytes(cleanName);
            using var key = ShareHandshake.NewKey();
            byte[] gPub = ShareHandshake.ExportPublic(key);
            byte[] gNonce = RandomNumberGenerator.GetBytes(ShareHandshake.NonceBytes);

            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.SendAsync(ShareProtocol.Msg(ShareProtocol.Hello, [.. gNonce, .. gPub, (byte)nameBytes.Length, .. nameBytes]),
                WebSocketMessageType.Binary, true, hs.Token);

            byte[]? welcome;
            try { welcome = await ShareProtocol.ReceiveAsync(ws, ShareProtocol.MaxHandshakeBytes, hs.Token); }
            catch (Exception ex) when (ex is WebSocketException or InvalidDataException) { welcome = null; }
            if (welcome is null || welcome.Length != 1 + 32 + 65 + 32 || welcome[0] != ShareProtocol.Welcome)
                throw new ShareJoinException("the host closed the connection during the handshake.");
            byte[] hNonce = welcome[1..33], hPub = welcome[33..98], macH = welcome[98..130];

            byte[] transcript = ShareHandshake.Transcript(link.RoomId, gNonce, gPub, cleanName, hNonce, hPub);
            byte[] authKey = ShareHandshake.AuthKey(link.Secret);
            if (!ShareHandshake.Verify(ShareHandshake.Proof(authKey, 'H', transcript), macH))
                throw new ShareJoinException("the host could not prove it owns this link (stale link or impostor) - refusing to connect.");

            byte[] shared;
            try { shared = ShareHandshake.Agree(key, hPub); }
            catch (CryptographicException) { throw new ShareJoinException("the host sent an invalid key."); }
            await ws.SendAsync(ShareProtocol.Msg(ShareProtocol.Proof, ShareHandshake.Proof(authKey, 'G', transcript)),
                WebSocketMessageType.Binary, true, hs.Token);

            var (h2g, g2h) = ShareHandshake.SessionKeys(shared, link.Secret, transcript);
            return new ShareGuestConnection(ws, new ShareChannel(sendKey: g2h, receiveKey: h2g));
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary>Receive and decrypt the next host message. Null when the connection closed.
    /// Throws <see cref="ShareJoinException"/> when a frame fails authentication.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        byte[]? sealedMsg;
        try { sealedMsg = await ShareProtocol.ReceiveAsync(_ws, ShareProtocol.MaxHostMessageBytes, ct); }
        catch (Exception ex) when (ex is WebSocketException or InvalidDataException) { return null; }
        if (sealedMsg is null) return null;
        if (!_channel.TryOpen(sealedMsg, out var m) || m.Length == 0)
            throw new ShareJoinException("received a corrupted or forged frame - disconnected for safety.");
        return m;
    }

    /// <summary>Encrypt and send one message to the host.</summary>
    public async Task SendAsync(byte[] plaintext, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(_channel.Seal(plaintext), WebSocketMessageType.Binary, true, ct); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Best-effort polite close.</summary>
    public async Task CloseAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendAsync(ShareProtocol.Msg(ShareProtocol.GuestBye, ""), cts.Token);
            if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _ws.Dispose();
        _channel.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>A join failure with a message meant for the user.</summary>
internal sealed class ShareJoinException(string message) : Exception(message);

/// <summary>
/// Terminal viewer for a shared session (<c>mux-swarm --join &lt;link&gt;</c> or <c>/join</c>).
/// Owns the local terminal for the duration: alternate screen, hidden cursor, sanitized host
/// output, and a local escape prefix <c>Ctrl+]</c> (then <c>q</c> leave, <c>r</c> redraw). The
/// terminal is always restored on every exit path.
/// </summary>
internal static class ShareGuest
{
    private const char EscapePrefix = '\u001d';   // Ctrl+]

    /// <summary>Key source abstraction: the TUI's shared input pump or raw console reads.</summary>
    internal delegate bool TryReadKey(out ConsoleKeyInfo key, int timeoutMs);

    /// <summary>Cold entry point for <c>--join</c>: no config, no setup, no provider needed.</summary>
    public static async Task<int> RunCliAsync(string? linkText)
    {
        if (!ShareLink.TryParse(linkText, out var link, out var err))
        {
            Console.Error.WriteLine($"mux-swarm --join: {err}");
            Console.Error.WriteLine("Usage: mux-swarm --join \"http://host:port/s/<room>#<secret>\"");
            return 2;
        }
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("mux-swarm --join needs an interactive terminal.");
            return 2;
        }
        bool prevCtrlC = false;
        try { prevCtrlC = Console.TreatControlCAsInput; Console.TreatControlCAsInput = true; } catch { }
        try
        {
            string? reason = await RunAsync(link!, ConsoleRead, Console.Out.Write);
            Console.Out.WriteLine($"Left the shared session: {AnsiSanitizer.PlainText(reason)}");
            return 0;
        }
        catch (ShareJoinException ex)
        {
            Console.Error.WriteLine($"Could not join: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Console.TreatControlCAsInput = prevCtrlC; } catch { }
        }
    }

    /// <summary>Bounded console key read for the viewer (the caller owns stdin).</summary>
    internal static bool ConsoleRead(out ConsoleKeyInfo key, int timeoutMs)
    {
        key = default;
        long end = Environment.TickCount64 + timeoutMs;
        do
        {
            try
            {
                if (Console.KeyAvailable) { key = Console.ReadKey(intercept: true); return true; }
            }
            catch (InvalidOperationException) { return false; }
            Thread.Sleep(15);
        } while (Environment.TickCount64 < end);
        return false;
    }

    /// <summary>
    /// Connect, wait for approval, and mirror the host until the user leaves or the host ends the
    /// share. <paramref name="write"/> receives raw terminal output (the caller owns the console).
    /// Returns the reason the session ended.
    /// </summary>
    public static async Task<string> RunAsync(ShareLink link, TryReadKey readKey, Action<string> write)
    {
        write($"Connecting to {link.Authority} ...\r\n");
        using var conn = await ShareGuestConnection.ConnectAsync(link, ShareProtocol.LocalName(), CancellationToken.None);
        write("Connected and verified (end-to-end encrypted). Waiting for the host to approve you ...\r\n");
        write("Press Ctrl+C to cancel.\r\n");

        using var cts = new CancellationTokenSource();
        var view = new GuestView(write);
        string reason = "disconnected";
        bool accepted = false;
        bool canType = false;   // host's word; the host enforces it independently

        var receiver = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var m = await conn.ReceiveAsync(cts.Token);
                    if (m is null) { reason = "the host closed the connection"; break; }
                    var payload = m.AsSpan(1);
                    switch (m[0])
                    {
                        case ShareProtocol.Accept:
                            var (w, h) = ShareProtocol.ReadSize(payload);
                            accepted = true;
                            view.Enter(w, h);
                            break;
                        case ShareProtocol.Reject:
                        case ShareProtocol.Bye:
                            reason = AnsiSanitizer.PlainText(Encoding.UTF8.GetString(payload));
                            cts.Cancel();
                            break;
                        case ShareProtocol.Size:
                            var (sw, sh) = ShareProtocol.ReadSize(payload);
                            if (view.SetHostSize(sw, sh)) await conn.SendAsync(ShareProtocol.Msg(ShareProtocol.Resync, ""), cts.Token);
                            break;
                        case ShareProtocol.Output:
                            view.Output(Encoding.UTF8.GetString(payload));
                            break;
                        case ShareProtocol.Notice:
                            view.Flash(AnsiSanitizer.PlainText(Encoding.UTF8.GetString(payload)));
                            break;
                        case ShareProtocol.Control:
                            canType = payload.Length > 0 && payload[0] == (byte)'1';
                            view.Flash(canType ? "The host let you type - Enter sends. Ctrl+] then q to leave."
                                               : "View-only now. Ctrl+] then q to leave.");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ShareJoinException ex) { reason = ex.Message; }
            catch (Exception ex) { reason = ex.Message; }
            finally { cts.Cancel(); }
        });

        bool prefix = false;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (view.PollResize()) await SafeSend(conn, ShareProtocol.Resync, cts.Token);
                if (!readKey(out var k, 100)) continue;
                bool ctrlBracket = k.KeyChar == EscapePrefix
                    || (k.Key == ConsoleKey.Oem6 && k.Modifiers.HasFlag(ConsoleModifiers.Control));
                bool ctrlC = k.KeyChar == '\u0003' || (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control));
                if (!accepted)
                {
                    if (ctrlC) { reason = "cancelled"; break; }
                    continue;
                }
                if (prefix)
                {
                    prefix = false;
                    view.HideMenu();
                    char c = char.ToLowerInvariant(k.KeyChar);
                    if (c == 'q' || ctrlC) { reason = "you left"; break; }
                    await SafeSend(conn, ShareProtocol.Resync, cts.Token);   // repaint under the menu row
                    continue;
                }
                if (ctrlBracket) { prefix = true; view.ShowMenu(); continue; }
                if (ctrlC) { view.Flash("Ctrl+C is not sent to the host. Ctrl+] then q to leave."); continue; }
                if (!canType)
                {
                    view.Flash("View-only session. Ctrl+] then q to leave, r to redraw.");
                    continue;
                }
                // Forward this key plus everything already buffered as ONE message: the host only
                // submits on a message that is exactly one Enter, so a paste can never submit itself.
                var vt = new StringBuilder(KeyToVt(k));
                bool menuNext = false, truncated = false;
                while (readKey(out var more, 0))
                {
                    if (more.KeyChar == EscapePrefix || (more.Key == ConsoleKey.Oem6 && more.Modifiers.HasFlag(ConsoleModifiers.Control)))
                    { menuNext = true; break; }
                    // Over the cap: drop the rest of this burst rather than split it - a split could
                    // leave a lone Enter as its own message, which the host would treat as a submit.
                    if (vt.Length >= RemoteInput.MaxMessageChars - 16) { truncated = true; continue; }
                    vt.Append(KeyToVt(more));
                }
                if (vt.Length > 0) await SafeSend(conn, ShareProtocol.Msg(ShareProtocol.Input, vt.ToString()), cts.Token);
                if (truncated) view.Flash("Paste too long - only the first part was sent.");
                if (menuNext) { prefix = true; view.ShowMenu(); }
            }
        }
        finally
        {
            cts.Cancel();
            try { await receiver; } catch { }
            await conn.CloseAsync();
            view.Leave();
        }
        return reason;
    }

    private static Task SafeSend(ShareGuestConnection conn, byte type, CancellationToken ct)
        => SafeSend(conn, ShareProtocol.Msg(type, ""), ct);

    private static async Task SafeSend(ShareGuestConnection conn, byte[] message, CancellationToken ct)
    {
        try { await conn.SendAsync(message, ct); } catch { /* connection closing */ }
    }

    /// <summary>
    /// Encode one local key as the VT bytes a terminal would send. Only keys the host accepts are
    /// encoded (text, Enter, Backspace, Tab, Delete, Left/Right, Home/End, Ctrl+A/E/K/U/W); the rest
    /// map to nothing. The host re-validates everything - this only keeps the wire honest.
    /// </summary>
    internal static string KeyToVt(ConsoleKeyInfo k)
    {
        bool ctrl = k.Modifiers.HasFlag(ConsoleModifiers.Control);
        switch (k.Key)
        {
            case ConsoleKey.Enter: return "\r";
            case ConsoleKey.Backspace: return "\u007f";
            case ConsoleKey.Tab: return k.Modifiers.HasFlag(ConsoleModifiers.Shift) ? "" : "\t";
            case ConsoleKey.Delete: return "\u001b[3~";
            case ConsoleKey.LeftArrow: return ctrl ? "\u001b[1;5D" : "\u001b[D";
            case ConsoleKey.RightArrow: return ctrl ? "\u001b[1;5C" : "\u001b[C";
            case ConsoleKey.Home: return "\u001b[H";
            case ConsoleKey.End: return "\u001b[F";
        }
        if (ctrl)
            return k.Key switch
            {
                ConsoleKey.A => "\u0001", ConsoleKey.E => "\u0005", ConsoleKey.K => "\u000b",
                ConsoleKey.U => "\u0015", ConsoleKey.W => "\u0017", _ => "",
            };
        char c = k.KeyChar;
        if (c == '\r' || c == '\n') return "\r";
        if (c is '\b' or '\u007f') return "\u007f";
        if (c == '\t') return "\t";
        return c != '\0' && !char.IsControl(c) ? c.ToString() : "";
    }

    /// <summary>Local terminal state for the viewer: alt screen, size gating, status overlays.</summary>
    /// <param name="write">Raw terminal output sink.</param>
    /// <param name="size">Local window size; defaults to the real console (tests pass a fixed size).</param>
    internal sealed class GuestView(Action<string> write, Func<(int W, int H)>? size = null)
    {
        private readonly object _lock = new();
        private readonly AnsiSanitizer _sanitizer = new();
        private bool _entered;
        private bool _tooSmall;
        private int _hostW, _hostH, _myW, _myH;

        private (int W, int H) MySize() => size is null ? ConsoleSize() : size();

        private static (int W, int H) ConsoleSize()
        {
            try { return (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight)); }
            catch { return (80, 24); }
        }

        public void Enter(int hostW, int hostH)
        {
            lock (_lock)
            {
                _entered = true;
                (_myW, _myH) = MySize();
                write("\u001b]0;Mux share (view-only) - Ctrl+] then q to leave\u0007");
                write("\u001b[?1049h\u001b[?25l\u001b[?7l\u001b[0m\u001b[2J\u001b[H");
                _hostW = hostW; _hostH = hostH;
                EvaluateFit();
            }
        }

        /// <summary>Apply a host resize; true when the guest needs a keyframe.</summary>
        public bool SetHostSize(int w, int h)
        {
            lock (_lock)
            {
                bool changed = (w, h) != (_hostW, _hostH);
                _hostW = w; _hostH = h;
                if (!_entered) return false;
                if (EvaluateFit()) return true;   // too-small -> fits (already cleared)
                if (!changed || _tooSmall) return false;
                // The host's new frame may be smaller than the last one: drop stale cells outside
                // it rather than relying on every host paint path to clear, then ask for a keyframe.
                write("\u001b[0m\u001b[2J\u001b[H");
                return true;
            }
        }

        /// <summary>Poll the local window size; true when a keyframe is needed.</summary>
        public bool PollResize()
        {
            lock (_lock)
            {
                if (!_entered) return false;
                var s = MySize();
                if (s == (_myW, _myH)) return false;
                (_myW, _myH) = s;
                write("\u001b[0m\u001b[2J\u001b[H");
                EvaluateFit();
                return !_tooSmall;
            }
        }

        private bool EvaluateFit()
        {
            bool wasSmall = _tooSmall;
            _tooSmall = _hostW > _myW || _hostH > _myH;
            if (_tooSmall)
            {
                write("\u001b[0m\u001b[2J\u001b[H");
                write($"\r\n  The host's terminal is {_hostW}x{_hostH}; yours is {_myW}x{_myH}.\r\n" +
                      "  Enlarge this window (or shrink the font) to watch.\r\n\r\n  Ctrl+] then q to leave.");
            }
            else if (wasSmall) write("\u001b[0m\u001b[2J\u001b[H");
            return wasSmall && !_tooSmall;
        }

        public void Output(string s)
        {
            string clean = _sanitizer.Sanitize(s);
            lock (_lock)
            {
                if (!_entered || _tooSmall || clean.Length == 0) return;
                write(clean);
            }
        }

        public void ShowMenu() => Bar("\u001b[7m Ctrl+] \u001b[0m  q leave  \u00b7  r redraw  \u00b7  any other key: back ");

        public void HideMenu() { }
        public void Flash(string text) => Bar("\u001b[7m " + text + " \u001b[0m");

        private void Bar(string text)
        {
            lock (_lock)
            {
                if (!_entered) return;
                write($"\u001b7\u001b[{_myH};1H\u001b[0m\u001b[2K{text}\u001b8");
            }
        }

        public void Leave()
        {
            lock (_lock)
            {
                if (!_entered) return;
                _entered = false;
                write("\u001b[0m\u001b[?7h\u001b[?25h\u001b[?1049l\u001b]0;\u0007");
            }
        }
    }
}
