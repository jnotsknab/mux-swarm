using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MuxSwarm.Engine.Share;

/// <summary>
/// Host side of live session sharing (<c>/share</c>). While active, a dedicated Kestrel listener
/// accepts guests on <c>/s/&lt;room&gt;</c>; every guest runs the PSK + ephemeral-ECDH handshake,
/// is approved by the host in an in-frame modal, and then receives the exact bytes the frame
/// renderer writes to the host terminal (teed from <see cref="Tui.ConsoleTuiTerminal"/>), sealed
/// with a per-connection AES-GCM channel. Nothing listens when not sharing. Process singleton.
/// </summary>
internal static class ShareHost
{
    /// <summary>Default listen port (web UI 6723, test instances 6724, telemetry 6725).</summary>
    public const int DefaultPort = 6726;

    /// <summary>Maximum simultaneously connected guests.</summary>
    public const int MaxGuests = 4;

    private const int MaxPendingHandshakes = 4;
    private const int MaxQueuedBytesPerGuest = 4 * 1024 * 1024;
    private const int MaxFailuresBeforeBan = 5;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BanDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(2);

    private static readonly object Gate = new();
    private static WebApplication? _app;
    private static ShareLink? _link;
    private static bool _lan;
    private static List<string> _addresses = [];
    private static readonly ConcurrentDictionary<int, Guest> Guests = new();
    private static readonly ConcurrentDictionary<string, (int Failures, DateTime Until)> Failures = new();
    private static readonly SemaphoreSlim ApprovalGate = new(1, 1);
    private static int _pending;
    private static int _nextId;
    private static volatile int _guestCount;
    private static readonly object GuestLock = new();   // admission check + count update are one step
    private static CancellationTokenSource _shareCts = new();   // cancelled by StopAsync: ends pending approvals
    private static (int W, int H) _lastSize;
    private static string? _secretText;   // base64url link secret, redacted from the guest stream

    /// <summary>True while the share listener is running.</summary>
    public static bool IsActive => _app is not null;

    /// <summary>True when at least one approved guest is connected (cheap hot-path check).</summary>
    public static bool HasGuests => _guestCount > 0;

    /// <summary>Host's answer to a join request.</summary>
    internal enum Approval { Deny, Watch, WatchAndType }

    /// <summary>Test/selftest seam: when set, replaces the host approval modal.</summary>
    internal static Func<string, Approval>? ApprovalOverride { get; set; }

    /// <summary>
    /// Test seam for the approval question itself (question, choices, cancellation) -> chosen index or null.
    /// Unlike <see cref="ApprovalOverride"/> it runs inside the gate and the cancellation plumbing, so tests
    /// can prove a pending approval ends when the guest leaves or the share stops.
    /// </summary>
    internal static Func<string, string[], CancellationToken, int?>? AskOverride { get; set; }

    /// <summary>Current join link, or null when not sharing.</summary>
    public static ShareLink? Link => _link;

    /// <summary>Addresses guests can use (primary first).</summary>
    public static IReadOnlyList<string> Addresses => _addresses;

    /// <summary>Footer chip text while sharing, or null.</summary>
    public static string? FooterLabel => _app is null ? null
        : _guestCount == 0 ? "sharing"
        : $"sharing \u00b7 {_guestCount} watching" + (RemoteInput.TypistCount > 0 ? $" \u00b7 {RemoteInput.TypistCount} typing" : "");

    /// <summary>Connected guests in join order, with whether each may type.</summary>
    public static IReadOnlyList<(int Id, string Name, string Address, bool CanType)> Participants
        => Guests.Values.Where(g => g.Accepted).OrderBy(g => g.Id)
            .Select(g => (g.Id, g.Name, g.Address, RemoteInput.CanType(g.Id))).ToList();

    /// <summary>
    /// Allow or stop typing for one guest (by #id or name) or every guest (<c>all</c>). Returns the
    /// names changed; empty when no guest matched. Guests pick their own names, so a name that
    /// matches more than one guest changes nobody and sets <paramref name="ambiguous"/>: use #id.
    /// </summary>
    public static IReadOnlyList<string> SetTyping(string who, bool allow, out bool ambiguous)
    {
        string w = who.Trim();
        bool all = w.Equals("all", StringComparison.OrdinalIgnoreCase);
        bool byId = w.StartsWith('#') || w.All(char.IsAsciiDigit);
        var matches = Guests.Values.Where(g => g.Accepted).OrderBy(g => g.Id)
            .Where(g => all || (byId ? g.Id.ToString() == w.TrimStart('#') : g.Name.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        ambiguous = !all && !byId && matches.Count > 1;
        var changed = new List<string>();
        if (ambiguous) return changed;
        foreach (var g in matches)
        {
            SetTyping(g, allow);
            changed.Add(g.Name);
        }
        if (changed.Count > 0) MuxConsole.TuiForceRedraw();   // footer chip
        return changed;
    }

    /// <summary>Host Ctrl+]: take typing away from every guest. Returns how many were revoked.</summary>
    public static int RevokeAllTyping()
    {
        int n = 0;
        foreach (var g in Guests.Values.Where(g => g.Accepted && RemoteInput.CanType(g.Id)))
        {
            SetTyping(g, false);
            n++;
        }
        RemoteInput.RevokeAll();
        if (n > 0) MuxConsole.TuiForceRedraw();
        return n;
    }

    /// <summary>Flash a status line on every connected guest.</summary>
    public static void NoticeAll(string text)
    {
        foreach (var g in Guests.Values.Where(g => g.Accepted)) g.Enqueue(new Outgoing(ShareProtocol.Notice, text, 0, 0));
    }

    private static void SetTyping(Guest g, bool allow)
    {
        if (allow) RemoteInput.Grant(g.Id, g.Name); else RemoteInput.Revoke(g.Id);
        g.Enqueue(new Outgoing(ShareProtocol.Control, allow ? "1" : "0", 0, 0));
    }

    /// <summary>
    /// Start sharing. <paramref name="lan"/> false binds loopback only (same machine); true binds
    /// all interfaces so LAN / Tailscale peers can join. Returns the join link.
    /// </summary>
    public static async Task<ShareLink> StartAsync(bool lan, int port = DefaultPort)
    {
        lock (Gate) { if (_app is not null) return _link!; }
        WebApplication app;
        try { app = await BuildAndStartAsync(lan, port); }
        catch (IOException) when (port != 0) { app = await BuildAndStartAsync(lan, 0); }   // port busy: take any

        int bound = new Uri(app.Urls.First()).Port;
        var addresses = lan ? LanAddresses() : [];
        if (addresses.Count == 0) addresses.Add("127.0.0.1");
        var link = new ShareLink(new Uri($"http://{FormatHost(addresses[0])}:{bound}"),
            ShareLink.NewRoomId(), RandomNumberGenerator.GetBytes(ShareLink.SecretBytes));
        lock (Gate)
        {
            _app = app;
            _link = link;
            _secretText = System.Buffers.Text.Base64Url.EncodeToString(link.Secret);
            _lan = lan;
            _addresses = addresses.Select(a => $"{FormatHost(a)}:{bound}").ToList();
            _lastSize = (0, 0);
            Failures.Clear();
            _shareCts = new CancellationTokenSource();
        }
        return link;
    }

    /// <summary>True when bound to all interfaces.</summary>
    public static bool IsLan => _lan;

    /// <summary>Stop sharing: disconnect every guest, close the listener, invalidate the link.</summary>
    public static async Task StopAsync(string reason = "host stopped sharing")
    {
        WebApplication? app;
        lock (Gate) { app = _app; _app = null; _link = null; _secretText = null; _addresses = []; }
        try { _shareCts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var g in Guests.Values) g.Close(reason);
        RemoteInput.RevokeAll();
        if (app is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await app.StopAsync(cts.Token); } catch { /* best-effort */ }
            try { await app.DisposeAsync(); } catch { /* best-effort */ }
        }
        Guests.Clear();
        _guestCount = 0;
    }

    /// <summary>Disconnect one guest by id or (case-insensitive) name. False when not found.</summary>
    public static bool Kick(string idOrName)
    {
        var g = Guests.Values.FirstOrDefault(x => x.Accepted &&
            (x.Id.ToString() == idOrName.Trim().TrimStart('#') || x.Name.Equals(idOrName.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (g is null) return false;
        g.Close("removed by host");
        return true;
    }

    /// <summary>
    /// Tee hook called for every string the TUI writes to the host terminal. Never blocks: output
    /// is queued per guest and a guest that falls too far behind is disconnected.
    /// </summary>
    internal static void OnTerminalWrite(string s, int width, int height)
    {
        if (_guestCount == 0 || s.Length == 0) return;
        // The join link may be on the host's screen (the /share printout, /share status). Guests
        // must never receive the secret, so mask it once here, before fan-out.
        if (_secretText is { } secret) s = SecretRedactor.Redact(s, secret);
        bool resized = (width, height) != _lastSize;
        _lastSize = (width, height);
        foreach (var g in Guests.Values)
        {
            if (!g.Accepted) continue;
            if (resized) g.Enqueue(new Outgoing(ShareProtocol.Size, null, width, height));
            g.Enqueue(new Outgoing(ShareProtocol.Output, s, 0, 0));
        }
    }

    // ------------------------------------------------------------------ listener

    private static async Task<WebApplication> BuildAndStartAsync(bool lan, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{(lan ? "0.0.0.0" : "127.0.0.1")}:{port}");
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxConcurrentConnections = 32;
            o.Limits.MaxConcurrentUpgradedConnections = MaxGuests + MaxPendingHandshakes;
            o.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Map("/s/{room}", HandleRoomAsync);
        await app.StartAsync();
        return app;
    }

    private static async Task HandleRoomAsync(HttpContext ctx, string room)
    {
        var link = _link;
        if (link is null || !FixedEquals(room, link.RoomId)) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.Headers.CacheControl = "no-store";
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(
                "This is a live Mux-Swarm session share.\n\n" +
                "Join from a terminal:  mux-swarm --join \"<paste the full link, including the part after #>\"\n");
            return;
        }

        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (IsBanned(ip)) { ctx.Response.StatusCode = 429; return; }
        if (Interlocked.Increment(ref _pending) > MaxPendingHandshakes || _guestCount >= MaxGuests)
        {
            Interlocked.Decrement(ref _pending);
            ctx.Response.StatusCode = 503;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        Guest? guest = null;
        try
        {
            guest = await HandshakeAsync(ws, link, ip, ctx.RequestAborted);
        }
        catch (Exception) { /* malformed / timed out: guest stays null */ }
        finally { Interlocked.Decrement(ref _pending); }

        if (guest is null)
        {
            await CloseQuietly(ws, WebSocketCloseStatus.PolicyViolation, "handshake failed");
            return;
        }
        await RunGuestAsync(guest, ctx.RequestAborted);
    }

    private static async Task<Guest?> HandshakeAsync(WebSocket ws, ShareLink link, string ip, CancellationToken aborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(HandshakeTimeout);

        var hello = await ShareProtocol.ReceiveAsync(ws, ShareProtocol.MaxHandshakeBytes, cts.Token);
        const int fixedLen = 1 + ShareHandshake.NonceBytes + ShareHandshake.PublicKeyBytes + 1;
        if (hello is null || hello.Length < fixedLen || hello[0] != ShareProtocol.Hello) return null;
        var gNonce = hello[1..33];
        var gPub = hello[33..98];
        int nameLen = hello[98];
        if (hello.Length != fixedLen + nameLen || nameLen > 128) return null;
        string rawName = Encoding.UTF8.GetString(hello, fixedLen, nameLen);

        using var key = ShareHandshake.NewKey();
        var hPub = ShareHandshake.ExportPublic(key);
        var hNonce = RandomNumberGenerator.GetBytes(ShareHandshake.NonceBytes);
        var transcript = ShareHandshake.Transcript(link.RoomId, gNonce, gPub, rawName, hNonce, hPub);
        var authKey = ShareHandshake.AuthKey(link.Secret);
        byte[] shared;
        try { shared = ShareHandshake.Agree(key, gPub); }
        catch (CryptographicException) { return null; }

        await ws.SendAsync(ShareProtocol.Msg(ShareProtocol.Welcome,
            [.. hNonce, .. hPub, .. ShareHandshake.Proof(authKey, 'H', transcript)]),
            WebSocketMessageType.Binary, true, cts.Token);

        var proof = await ShareProtocol.ReceiveAsync(ws, ShareProtocol.MaxHandshakeBytes, cts.Token);
        if (proof is null || proof.Length != 33 || proof[0] != ShareProtocol.Proof
            || !ShareHandshake.Verify(ShareHandshake.Proof(authKey, 'G', transcript), proof[1..]))
        {
            RecordFailure(ip);
            return null;
        }

        var (h2g, g2h) = ShareHandshake.SessionKeys(shared, link.Secret, transcript);
        return new Guest(Interlocked.Increment(ref _nextId), ShareProtocol.CleanName(rawName), ip, ws,
            new ShareChannel(sendKey: h2g, receiveKey: g2h));
    }

    private static async Task RunGuestAsync(Guest guest, CancellationToken aborted)
    {
        Guests[guest.Id] = guest;
        var sender = Task.Run(() => guest.SendLoopAsync(aborted));
        try
        {
            var approval = await ApproveAsync(guest, aborted);
            bool approved = approval != Approval.Deny;
            if (!approved || guest.Closed || !IsActive)
            {
                guest.Close(approved ? "disconnected" : "the host declined the request");
                await sender;
                return;
            }
            lock (GuestLock)
            {
                if (_guestCount >= MaxGuests) { guest.Close("the session is full"); }
                else
                {
                    var (w, h) = CurrentSize();
                    guest.Enqueue(new Outgoing(ShareProtocol.Accept, null, w, h));
                    guest.Accepted = true;
                    _guestCount = Guests.Values.Count(g => g.Accepted);
                }
            }
            if (!guest.Accepted) { await sender; return; }
            if (approval == Approval.WatchAndType) SetTyping(guest, true);
            MuxConsole.WriteInfo($"\u25cf {guest.Name} joined the share ({guest.Address})" +
                                 (approval == Approval.WatchAndType ? " and can type. Ctrl+] revokes." : ", view-only."));
            MuxConsole.TuiForceRedraw();   // full keyframe for the new guest (and a fresh footer)

            await guest.ReceiveLoopAsync(aborted);
        }
        catch (Exception) { /* connection error: fall through to cleanup */ }
        finally
        {
            guest.Close("disconnected");
            try { await sender; } catch { /* ignore */ }
            RemoteInput.Revoke(guest.Id);
            bool wasAccepted = guest.Accepted;
            lock (GuestLock)
            {
                Guests.TryRemove(guest.Id, out _);
                _guestCount = Guests.Values.Count(g => g.Accepted);
            }
            guest.Dispose();
            if (wasAccepted && IsActive)
            {
                MuxConsole.WriteMuted($"\u25cb {guest.Name} left the share.");
                MuxConsole.TuiForceRedraw();
            }
        }
    }

    /// <summary>
    /// Ask the host to approve a join. Ends as <see cref="Approval.Deny"/> when the guest disconnects
    /// (<paramref name="aborted"/>) or the share stops, so no stale prompt lingers and the approval gate
    /// is released promptly.
    /// </summary>
    private static async Task<Approval> ApproveAsync(Guest guest, CancellationToken aborted)
    {
        if (ApprovalOverride is { } ov) return ov(guest.Name);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted, _shareCts.Token);
        try { await ApprovalGate.WaitAsync(cts.Token); }
        catch (OperationCanceledException) { return Approval.Deny; }
        try
        {
            if (guest.Closed || cts.IsCancellationRequested) return Approval.Deny;
            string q = $"\"{guest.Name}\" ({guest.Address}) wants to join this session live. They will see everything " +
                       "on your screen. \"Watch + type\" also lets them type prompts the agent will act on " +
                       "(shell escapes and most / commands stay blocked; Ctrl+] revokes).";
            string[] choices = ["Deny", "Watch", "Watch + type"];
            int? answer = await Task.Run(() => AskOverride is { } ask
                ? ask(q, choices, cts.Token)
                : MuxConsole.TuiAskFromBackground(q, choices, 0, ApprovalTimeout, cts.Token));
            if (cts.IsCancellationRequested) return Approval.Deny;
            return answer switch { 1 => Approval.Watch, 2 => Approval.WatchAndType, _ => Approval.Deny };
        }
        finally { ApprovalGate.Release(); }
    }

    // ------------------------------------------------------------------ helpers

    private static (int W, int H) CurrentSize()
    {
        try { return (Math.Max(1, Console.BufferWidth), Math.Max(1, Console.WindowHeight)); }
        catch { return _lastSize == (0, 0) ? (80, 24) : _lastSize; }
    }

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static bool IsBanned(string ip)
        => Failures.TryGetValue(ip, out var f) && f.Failures >= MaxFailuresBeforeBan && DateTime.UtcNow < f.Until;

    private static void RecordFailure(string ip)
        => Failures.AddOrUpdate(ip, _ => (1, DateTime.UtcNow + BanDuration),
            (_, f) => (DateTime.UtcNow > f.Until ? 1 : f.Failures + 1, DateTime.UtcNow + BanDuration));

    private static async Task CloseQuietly(WebSocket ws, WebSocketCloseStatus status, string reason, bool outputOnly = false)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (ws.State != WebSocketState.Open) return;
            // A concurrent receive loop owns reads, so only send our close frame and let it observe the reply.
            if (outputOnly) await ws.CloseOutputAsync(status, reason, cts.Token);
            else await ws.CloseAsync(status, reason, cts.Token);
        }
        catch { /* ignore */ }
    }

    private static string FormatHost(string ip) => ip.Contains(':') ? $"[{ip}]" : ip;

    /// <summary>
    /// Reachable IPv4 addresses for an all-interfaces share: private LAN ranges first, then
    /// Tailscale/CGNAT (100.64.0.0/10), then anything else that is up and not loopback/link-local.
    /// </summary>
    internal static List<string> LanAddresses()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    var a = u.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                    var b = a.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    if (!found.Contains(a)) found.Add(a);
                }
            }
        }
        catch { /* interface enumeration unsupported: fall back to loopback */ }

        static int Rank(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)) return 0;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return 1;
            return 2;
        }
        return found.OrderBy(Rank).Select(a => a.ToString()).ToList();
    }

    // ------------------------------------------------------------------ guest

    private readonly record struct Outgoing(byte Type, string? Text, int W, int H);

    private sealed class Guest : IDisposable
    {
        public int Id { get; }
        public string Name { get; }
        public string Address { get; }
        public volatile bool Accepted;
        public bool Closed => _closed == 1;

        private readonly WebSocket _ws;
        private readonly ShareChannel _channel;
        private readonly Channel<Outgoing> _queue = Channel.CreateUnbounded<Outgoing>(
            new UnboundedChannelOptions { SingleReader = true });
        private long _queuedBytes;
        private int _closed;
        private string _closeReason = "disconnected";
        private long _lastResync;

        public Guest(int id, string name, string address, WebSocket ws, ShareChannel channel)
        {
            Id = id; Name = name; Address = address; _ws = ws; _channel = channel;
        }

        public void Enqueue(Outgoing o)
        {
            if (Closed) return;
            long n = Interlocked.Add(ref _queuedBytes, (o.Text?.Length ?? 0) * 2 + 8);
            if (n > MaxQueuedBytesPerGuest) { Close("connection too slow to keep up"); return; }
            _queue.Writer.TryWrite(o);
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _closeReason = reason;
            _queue.Writer.TryComplete();
        }

        public async Task SendLoopAsync(CancellationToken ct)
        {
            var batch = new StringBuilder();
            try
            {
                while (await _queue.Reader.WaitToReadAsync(ct))
                {
                    while (_queue.Reader.TryRead(out var o))
                    {
                        Interlocked.Add(ref _queuedBytes, -((o.Text?.Length ?? 0) * 2 + 8));
                        if (o.Type == ShareProtocol.Output)
                        {
                            batch.Append(o.Text);
                            if (batch.Length < 256 * 1024) continue;
                        }
                        await FlushAsync(batch, ct);
                        if (o.Type != ShareProtocol.Output)
                            await SendAsync(o.Type is ShareProtocol.Size or ShareProtocol.Accept
                                ? ShareProtocol.SizeMsg(o.Type, o.W, o.H)
                                : ShareProtocol.Msg(o.Type, o.Text ?? ""), ct);
                    }
                    await FlushAsync(batch, ct);
                }
                await SendAsync(ShareProtocol.Msg(ShareProtocol.Bye, _closeReason), ct);
            }
            catch { /* socket gone */ }
            await CloseQuietly(_ws, WebSocketCloseStatus.NormalClosure, "bye", outputOnly: true);
        }

        private async Task FlushAsync(StringBuilder batch, CancellationToken ct)
        {
            if (batch.Length == 0) return;
            var msg = ShareProtocol.Msg(ShareProtocol.Output, batch.ToString());
            batch.Clear();
            await SendAsync(msg, ct);
        }

        private Task SendAsync(byte[] plaintext, CancellationToken ct)
            => _ws.SendAsync(_channel.Seal(plaintext), WebSocketMessageType.Binary, true, ct);

        public async Task ReceiveLoopAsync(CancellationToken ct)
        {
            while (!Closed && _ws.State == WebSocketState.Open)
            {
                var sealedMsg = await ShareProtocol.ReceiveAsync(_ws, ShareProtocol.MaxGuestMessageBytes, ct);
                if (sealedMsg is null) return;
                if (!_channel.TryOpen(sealedMsg, out var m) || m.Length == 0) return;   // tampered: drop the guest
                switch (m[0])
                {
                    case ShareProtocol.GuestBye:
                        return;
                    case ShareProtocol.Input:
                        // Host-enforced: dropped unless this guest may type AND a prompt is open.
                        if (m.Length - 1 <= RemoteInput.MaxMessageChars * 4)
                            RemoteInput.Post(Id, Encoding.UTF8.GetString(m, 1, m.Length - 1));
                        break;
                    case ShareProtocol.Resync:
                        long now = Environment.TickCount64;
                        if (now - Interlocked.Read(ref _lastResync) >= 1000)
                        {
                            Interlocked.Exchange(ref _lastResync, now);
                            MuxConsole.TuiForceRedraw();
                        }
                        break;
                    default:
                        break;   // unknown / not-yet-permitted message types are ignored
                }
            }
        }

        public void Dispose() => _channel.Dispose();
    }
}
