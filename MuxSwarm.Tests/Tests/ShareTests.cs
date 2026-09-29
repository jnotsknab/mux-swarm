using System.Net.WebSockets;
using System.Text;
using MuxSwarm.Engine.Share;

namespace MuxSwarm.Tests.Tests;

public class ShareCryptoTests
{
    private static (ShareChannel Host, ShareChannel Guest) Pair()
    {
        var psk = new byte[32]; Random.Shared.NextBytes(psk);
        using var h = ShareHandshake.NewKey();
        using var g = ShareHandshake.NewKey();
        var hPub = ShareHandshake.ExportPublic(h);
        var gPub = ShareHandshake.ExportPublic(g);
        var t = ShareHandshake.Transcript("room", new byte[32], gPub, "g", new byte[32], hPub);
        var s1 = ShareHandshake.Agree(h, gPub);
        var s2 = ShareHandshake.Agree(g, hPub);
        Assert.Equal(s1, s2);
        var (h2g, g2h) = ShareHandshake.SessionKeys(s1, psk, t);
        return (new ShareChannel(h2g, g2h), new ShareChannel(g2h, h2g));
    }

    [Fact]
    public void Channel_RoundTrips_InOrder()
    {
        var (host, guest) = Pair();
        for (int i = 0; i < 5; i++)
        {
            var sealedMsg = host.Seal(Encoding.UTF8.GetBytes("frame " + i));
            Assert.True(guest.TryOpen(sealedMsg, out var pt));
            Assert.Equal("frame " + i, Encoding.UTF8.GetString(pt));
        }
        Assert.True(host.TryOpen(guest.Seal([1, 2, 3]), out var back));
        Assert.Equal(new byte[] { 1, 2, 3 }, back);
    }

    [Fact]
    public void Channel_Rejects_Tamper_Replay_Reorder_And_Reflection()
    {
        var (host, guest) = Pair();
        var a = host.Seal([1]);
        var b = host.Seal([2]);
        Assert.False(guest.TryOpen(b, out _));                 // reordered
        var tampered = (byte[])a.Clone(); tampered[0] ^= 1;
        Assert.False(guest.TryOpen(tampered, out _));          // bit flip
        Assert.True(guest.TryOpen(a, out _));
        Assert.False(guest.TryOpen(a, out _));                 // replay
        Assert.False(host.TryOpen(host.Seal([9]), out _));     // reflected own frame (direction keys differ)
        Assert.False(guest.TryOpen(new byte[3], out _));       // shorter than a tag
    }

    [Fact]
    public void Proofs_Differ_By_Role_And_Psk()
    {
        var t = new byte[32];
        var k1 = ShareHandshake.AuthKey(new byte[32]);
        var k2 = ShareHandshake.AuthKey(Enumerable.Repeat((byte)1, 32).ToArray());
        Assert.False(ShareHandshake.Verify(ShareHandshake.Proof(k1, 'H', t), ShareHandshake.Proof(k1, 'G', t)));
        Assert.False(ShareHandshake.Verify(ShareHandshake.Proof(k1, 'H', t), ShareHandshake.Proof(k2, 'H', t)));
        Assert.True(ShareHandshake.Verify(ShareHandshake.Proof(k1, 'H', t), ShareHandshake.Proof(k1, 'H', t)));
        Assert.False(ShareHandshake.Verify(ShareHandshake.Proof(k1, 'H', t), null));
    }

    [Fact]
    public void Agree_Rejects_Malformed_Point()
    {
        using var k = ShareHandshake.NewKey();
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => ShareHandshake.Agree(k, new byte[65]));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => ShareHandshake.Agree(k, new byte[10]));
    }
}

public class ShareLinkTests
{
    [Fact]
    public void Link_RoundTrips_And_Keeps_Secret_In_Fragment()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var link = new ShareLink(new Uri("http://192.168.1.5:6726"), ShareLink.NewRoomId(), secret);
        string s = link.ToString();
        Assert.Contains("/s/" + link.RoomId + "#", s);
        Assert.True(ShareLink.TryParse($"  \"{s}\"  ", out var parsed, out _));
        Assert.Equal(secret, parsed!.Secret);
        Assert.Equal(link.RoomId, parsed.RoomId);
        Assert.Equal("ws://192.168.1.5:6726/s/" + link.RoomId, parsed.WebSocketUri.ToString());
        Assert.DoesNotContain("#", parsed.WebSocketUri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://h/s/AAAAAAAAAAAAAAAAAAAAAA#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("http://h:1/x/AAAAAAAAAAAAAAAAAAAAAA#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("http://h:1/s/AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("http://h:1/s/AAAAAAAAAAAAAAAAAAAAAA#short")]
    [InlineData("http://h:1/s/AAAA#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Link_Rejects_Bad_Input(string text)
        => Assert.False(ShareLink.TryParse(text, out _, out _));
}

public class AnsiSanitizerTests
{
    private static string S(string s) => new AnsiSanitizer().Sanitize(s);

    [Fact]
    public void Keeps_Renderer_Vocabulary()
    {
        string frame = "\u001b[?2026h\u001b[?25l\u001b[?7l\u001b[2J\u001b[H\u001b[3;1H\u001b[0m\u001b[38;2;1;2;3mhi\u001b[0K\u001b[?2026l";
        Assert.Equal(frame, S(frame));
        Assert.Equal("a\r\nb\tc", S("a\r\nb\tc"));
        Assert.Equal("\u2500\u25cf\U0001F600", S("\u2500\u25cf\U0001F600"));
    }

    [Theory]
    [InlineData("\u001b]52;c;ZXZpbA==\u0007")]          // OSC 52 clipboard write (BEL)
    [InlineData("\u001b]52;c;ZXZpbA==\u001b\\")]         // OSC 52 (ST)
    [InlineData("\u001b]0;pwned title\u0007")]           // title
    [InlineData("\u001bP$q m\u001b\\")]                  // DECRQSS query
    [InlineData("\u001b[6n")]                            // DSR cursor report
    [InlineData("\u001b[c")]                             // DA1
    [InlineData("\u001b[>c")]                            // DA2
    [InlineData("\u001b[18t")]                           // window size report
    [InlineData("\u001b[?1049h")]                        // alt screen (guest owns it)
    [InlineData("\u001b[?1000h\u001b[?1006h")]           // mouse tracking
    [InlineData("\u001b[?2004h")]                        // bracketed paste
    [InlineData("\u001b[?5522$p")]                       // DECRQM
    [InlineData("\u001b[1;50r")]                         // scroll region
    [InlineData("\u001b7\u001b8\u001bc")]                // save/restore/RIS
    [InlineData("\u0007\u0005\u007f")]                   // BEL/ENQ/DEL
    public void Drops_Dangerous_Sequences(string evil)
        => Assert.Equal("", S(evil));

    [Fact]
    public void Hyperlink_Keeps_Only_Its_Visible_Text()
        => Assert.Equal("x", S("\u001b]8;;http://evil\u001b\\x\u001b]8;;\u001b\\"));

    [Fact]
    public void C1_Csi_Byte_Is_Dropped_Leaving_Inert_Text()
        => Assert.Equal("6n", S("\u009b6n"));

    [Fact]
    public void Split_At_Every_Byte_Never_Leaks_A_Partial_Sequence()
    {
        string input = "ok\u001b]52;c;AAAA\u0007\u001b[31mred\u001b[6nend";
        for (int cut = 0; cut <= input.Length; cut++)
        {
            var s = new AnsiSanitizer();
            string got = s.Sanitize(input[..cut]) + s.Sanitize(input[cut..]);
            Assert.Equal("ok\u001b[31mredend", got);
        }
    }

    [Fact]
    public void Oversized_Csi_Is_Dropped_Not_Buffered()
        => Assert.Equal("x", S("\u001b[" + new string('1', 5000) + "mx"));
}

public class SecretRedactorTests
{
    private const string Secret = "q3Zk8VYb1mXw-Tn_4Lr0PdHs9EaJcUfGiKoMtByNe2A";   // 43-char base64url

    [Fact]
    public void Masks_Full_Link_Keeping_Width_And_Room()
    {
        string line = $"  mux-swarm --join \"http://10.0.0.5:6726/s/ROOMROOMROOMROOMROOMRO#{Secret}\"";
        string got = SecretRedactor.Redact(line, Secret);
        Assert.DoesNotContain(Secret[..SecretRedactor.MinFragment], got);
        Assert.Equal(line.Length, got.Length);
        Assert.Contains("/s/ROOMROOMROOMROOMROOMRO#" + new string('\u2022', Secret.Length) + "\"", got);
    }

    [Fact]
    public void Masks_Both_Halves_Of_A_Wrapped_Link_Across_Escapes()
    {
        string frame = "\u001b[?2026h\u001b[5;1H\u001b[0m\u001b[36m/s/ROOM#" + Secret[..20] + "\u001b[0K\u001b[6;1H\u001b[36m" + Secret[20..] + "\"\u001b[0m\u001b[?2026l";
        string got = SecretRedactor.Redact(frame, Secret);
        Assert.DoesNotContain(Secret[..10], got);
        Assert.DoesNotContain(Secret[^10..], got);
        Assert.Equal(frame.Length, got.Length);
        Assert.Contains("\u001b[5;1H\u001b[0m\u001b[36m", got);   // escapes untouched (digits are base64url chars)
    }

    [Fact]
    public void Leaves_Ordinary_Output_Untouched()
    {
        string s = "\u001b[38;2;10;20;30mBuilding MuxSwarm.csproj 1936/1936 passed\u001b[0m Secret-ish words";
        Assert.Same(s, SecretRedactor.Redact(s, Secret));
    }
}

[Collection("ConsoleState")]   // RemoteInput is process-global (shared with the loopback tests)
public class RemoteInputTests
{
    private static List<(ConsoleKeyInfo Key, string? Text)> D(string s) => RemoteInput.Decode(s);

    [Fact]
    public void Lone_Enter_Submits_But_Enter_In_A_Burst_Is_A_Newline()
    {
        Assert.Equal(ConsoleKey.Enter, Assert.Single(D("\r")).Key.Key);
        var burst = D("ls\r");
        Assert.Equal("ls\n", Assert.Single(burst).Text);
        Assert.DoesNotContain(burst, e => e.Key.Key == ConsoleKey.Enter);
    }

    [Fact]
    public void Editing_Keys_Decode_To_Editor_Keys()
    {
        var keys = D("a\u007f\u001b[D\u001b[1;5C\u001b[H\u001b[F\u001b[3~\t\u0001\u0005\u000b\u0015\u0017")
            .Where(e => e.Text is null).Select(e => e.Key).ToList();
        Assert.Equal(new[] { ConsoleKey.Backspace, ConsoleKey.LeftArrow, ConsoleKey.RightArrow, ConsoleKey.Home,
            ConsoleKey.End, ConsoleKey.Delete, ConsoleKey.Tab, ConsoleKey.A, ConsoleKey.E, ConsoleKey.K, ConsoleKey.U, ConsoleKey.W },
            keys.Select(k => k.Key));
        Assert.True(keys[1].Modifiers == 0 && keys[2].Modifiers == ConsoleModifiers.Control);
    }

    [Theory]
    [InlineData("\u001b[A")]                     // Up: host prompt history
    [InlineData("\u001b[B")]                     // Down
    [InlineData("\u001b[5~")]                    // PageUp
    [InlineData("\u001b[Z")]                     // Shift+Tab: effort cycle
    [InlineData("\u001bOS")]                     // F4: inferred-paste send
    [InlineData("\u001b")]                       // Esc: NAV
    [InlineData("\u0016")]                       // Ctrl+V: host clipboard
    [InlineData("\u0003")]                       // Ctrl+C
    [InlineData("\u0004")]                       // Ctrl+D
    [InlineData("\u0007")]                       // Ctrl+G
    [InlineData("\u0012")]                       // Ctrl+R: history search
    [InlineData("\u0014")]                       // Ctrl+T
    [InlineData("\u001d")]                       // Ctrl+]
    [InlineData("\u001b[<0;5;5M")]               // mouse
    [InlineData("\u001b[I")]                     // focus in
    [InlineData("\u001b]52;c;ZXZpbA==\u0007")]   // OSC
    [InlineData("\u001b[?2004;1$y")]             // mode reply
    [InlineData("\u001b[13;2u")]                 // kitty Shift+Enter
    [InlineData("\u001b[M")]                     // CSI Enter
    [InlineData("\u0085\u009b")]                 // C1
    public void Host_Hotkeys_And_Terminal_Noise_Are_Dropped(string vt) => Assert.Empty(D(vt));

    [Fact]
    public void Bracketed_Paste_Is_Literal_Text_And_Never_Submits()
    {
        var e = Assert.Single(D("\u001b[200~line1\r\nline2\u001b[31m\u001b[201~"));
        Assert.Equal("line1\nline2[31m", e.Text);
    }

    [Fact]
    public void Post_Requires_Grant_And_Open_Prompt_And_Revoke_Cancels_In_Flight()
    {
        RemoteInput.ResetForTest();
        RemoteInput.Post(7, "x");                        // no grant, no prompt
        RemoteInput.Grant(7, "g");
        RemoteInput.Post(7, "x");                        // prompt closed
        Assert.False(RemoteInput.HasPending);
        RemoteInput.BeginPrompt();
        RemoteInput.Post(7, "abc");
        RemoteInput.Post(8, "zzz");                      // other guest, no grant
        RemoteInput.Revoke(7);
        Assert.False(RemoteInput.TryTake(out _));        // queued before revoke: re-checked on take
        RemoteInput.Grant(7, "g");
        RemoteInput.Post(7, new string('a', RemoteInput.MaxMessageChars + 1));   // oversize: dropped whole
        Assert.False(RemoteInput.HasPending);
        RemoteInput.EndPrompt();
        RemoteInput.ResetForTest();
    }

    [Theory]
    [InlineData("explain this repo", true)]
    [InlineData("", false)]      // an empty submit quits the host's agent/swarm loop
    [InlineData("   ", false)]
    [InlineData("/", true)]
    [InlineData("/help", true)]
    [InlineData("/compact focus on tests", true)]
    [InlineData("/retry", true)]
    [InlineData("!rm -rf /", false)]
    [InlineData("  !whoami", false)]
    [InlineData("/exit", false)]
    [InlineData("/qc", false)]
    [InlineData("/share stop", false)]
    [InlineData("/join http://x", false)]
    [InlineData("/proxy update", false)]
    [InlineData("/config", false)]
    [InlineData("/model", false)]
    [InlineData("/paste", false)]
    [InlineData("/setup", false)]
    [InlineData("/some-future-command", false)]
    // F1 (v0.15.0 audit): invisible prefixes must not hide a shell escape or a blocked command.
    [InlineData("\u200B!whoami", false)]
    [InlineData("\u00AD!& whoami", false)]
    [InlineData("\uFEFF\u2060!id", false)]
    [InlineData("\u034F!id", false)]           // leading combining mark
    [InlineData("\u200B /config x y", false)]
    [InlineData("\u200B/daemon stop", false)]
    [InlineData("\u200B/help", true)]          // still an allowlisted command once visible
    [InlineData("family \U0001F468\u200D\U0001F469\u200D\U0001F467 photo", true)]  // ZWJ in ordinary text
    public void Policy_Allowlists_Guest_Lines(string line, bool allowed) => Assert.Equal(allowed, RemotePolicy.IsAllowed(line));

    [Fact]
    public void Guest_ZeroWidth_Bang_From_Wire_Is_Refused()
    {
        // Decode keeps Cf chars (unchanged behaviour); the policy must still refuse the line.
        string text = string.Concat(D("\u200B!& whoami").Select(e => e.Text));
        Assert.Contains("\u200B", text);
        Assert.False(RemotePolicy.IsAllowed(text));
        Assert.Equal("a shell command (!)", RemotePolicy.Describe(text));
    }

    [Fact]
    public void Guest_Key_Encoding_Round_Trips_Through_Host_Decode()
    {
        static ConsoleKeyInfo K(char c, ConsoleKey k, bool ctrl = false) => new(c, k, false, false, ctrl);
        Assert.Equal("h", ShareGuest.KeyToVt(K('h', ConsoleKey.H)));
        Assert.Equal("\r", ShareGuest.KeyToVt(K('\r', ConsoleKey.Enter)));
        Assert.Equal("", ShareGuest.KeyToVt(K('\u0016', ConsoleKey.V, ctrl: true)));   // Ctrl+V never sent
        Assert.Equal("", ShareGuest.KeyToVt(K('\0', ConsoleKey.UpArrow)));
        var keys = D(ShareGuest.KeyToVt(K('\0', ConsoleKey.LeftArrow)) + ShareGuest.KeyToVt(K('\u0017', ConsoleKey.W, ctrl: true)));
        Assert.Equal(new[] { ConsoleKey.LeftArrow, ConsoleKey.W }, keys.Select(k => k.Key.Key));
    }
}

/// <summary>Guest typing through the real idle prompt loop (no network): feed, submit, block, attribution.</summary>
[Collection("ConsoleState")]
public class RemotePromptTests
{
    private sealed class Terminal : MuxSwarm.Engine.Tui.ITuiTerminal
    {
        internal readonly StringBuilder Output = new();
        public int Width => 80;
        public int Height => 24;
        public void Write(string text) { lock (Output) Output.Append(text); }
        public void Flush() { }
        internal string Plain() { lock (Output) return System.Text.RegularExpressions.Regex.Replace(Output.ToString(), "\u001b\\[[0-9;?]*[A-Za-z]", ""); }
    }

    private static async Task<string?> Run(Action<MuxSwarm.Engine.Tui.ConsoleInputPump> drive, Terminal term)
    {
        RemoteInput.ResetForTest();
        RemoteInput.Grant(1, "alice");
        using var pump = MuxSwarm.Engine.Tui.ConsoleInputPump.CreateUnstartedForTest();
        var driver = new MuxSwarm.Engine.Tui.TuiDriver(term, frameEngine: false);
        var read = Task.Run(() => driver.ReadLineCore(pump));
        try
        {
            for (int i = 0; i < 300 && !RemoteInput.IsOpen; i++) await Task.Delay(10);
            drive(pump);
            return await read.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pump.Dispose();
            try { await read.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            driver.Shutdown();
            RemoteInput.ResetForTest();
        }
    }

    private static ConsoleKeyInfo K(char c, ConsoleKey k) => new(c, k, false, false, false);

    [Fact]
    public async Task Guest_Types_And_Submits_A_Prompt_With_Attribution()
    {
        var term = new Terminal();
        string? line = await Run(_ =>
        {
            RemoteInput.Post(1, "explain");
            RemoteInput.Post(1, " this");
            RemoteInput.Post(1, "\r");
        }, term);
        Assert.Equal("explain this", line);
        Assert.Contains("typed by alice", term.Plain());
    }

    [Fact]
    public async Task Guest_Shell_Escape_Is_Blocked_And_Host_Can_Still_Submit()
    {
        var term = new Terminal();
        string? line = await Run(pump =>
        {
            RemoteInput.Post(1, "!whoami");
            RemoteInput.Post(1, "\r");
            Thread.Sleep(300);
            foreach (char c in "ok") pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K(c, ConsoleKey.NoName)));
            pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K('\r', ConsoleKey.Enter)));
        }, term);
        Assert.Equal("ok", line);   // the blocked draft was cleared, never submitted
        Assert.Contains("Blocked a line typed by alice", term.Plain());
    }

    [Fact]
    public async Task Host_Submitting_A_Guest_Edited_Draft_Still_Applies_Policy()
    {
        var term = new Terminal();
        string? line = await Run(pump =>
        {
            RemoteInput.Post(1, "/exit");
            Thread.Sleep(300);
            pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K('\r', ConsoleKey.Enter)));   // host presses Enter
            Thread.Sleep(300);
            foreach (char c in "fine") pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K(c, ConsoleKey.NoName)));
            pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K('\r', ConsoleKey.Enter)));
        }, term);
        Assert.Equal("fine", line);
    }

    [Fact]
    public async Task Guest_Typed_Paste_Command_Never_Pastes_The_Host_Clipboard()
    {
        // `/paste` + Enter is a host clipboard shortcut; a guest-typed `/paste` must be refused by the
        // guest-draft check instead, even when the HOST presses Enter (Mux Bud #94 round 2).
        var term = new Terminal();
        string? line = await Run(pump =>
        {
            RemoteInput.Post(1, "/paste");
            Thread.Sleep(300);
            pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K('\r', ConsoleKey.Enter)));   // host Enter
            Thread.Sleep(300);
            foreach (char c in "ok") pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K(c, ConsoleKey.NoName)));
            pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K('\r', ConsoleKey.Enter)));
        }, term);
        Assert.Equal("ok", line);
        Assert.Contains("Blocked a line typed by alice", term.Plain());
    }

    [Fact]
    public async Task Guest_Enter_On_Empty_Prompt_Does_Not_Submit()
    {
        // An empty submit is "quit" to the agent/swarm loops (Mux Bud #94 P1): a guest's bare Enter
        // must be a no-op, so ReadLine keeps waiting until the host types.
        var term = new Terminal();
        string? line = await Run(pump =>
        {
            RemoteInput.Post(1, "\r");
            RemoteInput.Post(1, "   \r");
            Thread.Sleep(300);
            foreach (char c in "host") pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K(c, ConsoleKey.NoName)));
            pump.Enqueue(MuxSwarm.Engine.Tui.ConsoleInputPump.InputEvent.OfKey(K('\r', ConsoleKey.Enter)));
        }, term);
        Assert.Equal("host", line.Trim());
    }
}

public class GuestViewTests
{
    [Fact]
    public void Host_Resize_Clears_Stale_Cells_And_Requests_Keyframe()
    {
        var sb = new StringBuilder();
        // Fixed local size: headless runs (CI, containers) have no real console window.
        var view = new ShareGuest.GuestView(s => sb.Append(s), () => (80, 24));
        view.Enter(20, 5);
        sb.Clear();
        Assert.True(view.SetHostSize(10, 3));          // host shrank: clear + keyframe
        Assert.Contains("\u001b[2J", sb.ToString());
        sb.Clear();
        Assert.False(view.SetHostSize(10, 3));         // same size: no-op
        Assert.Equal("", sb.ToString());
    }

    [Fact]
    public void Host_Text_Cannot_Carry_Escape_Sequences()
    {
        // Notices and reject/bye reasons bypass the frame sanitizer: OSC 52 (clipboard) and a DSR
        // query must not survive, and neither may C1 controls.
        string hostile = "hi\u001b]52;c;ZXZpbA==\u0007\u001b[6n\u009b0c\tthere\r\n";
        string clean = AnsiSanitizer.PlainText(hostile);
        Assert.DoesNotContain('\u001b', clean);
        Assert.DoesNotContain('\u0007', clean);
        Assert.DoesNotContain('\u009b', clean);
        Assert.DoesNotContain('\r', clean);
        Assert.Equal("hi]52;c;ZXZpbA==[6n0c there", clean);
        Assert.Equal("", AnsiSanitizer.PlainText(null));
    }
}

/// <summary>End-to-end over a real loopback listener: handshake, approval, frame delivery, and
/// the negative paths (wrong secret, wrong room, declined).</summary>
// Serialized with the console-state tests: ShareHost is process-global and feeds the TUI footer
// chip and transcript, so running in parallel with TuiDriver tests races their assertions.
[Collection("ConsoleState")]
public class ShareHostLoopbackTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        ShareHost.ApprovalOverride = null;
        ShareHost.AskOverride = null;
        await ShareHost.StopAsync();
        RemoteInput.ResetForTest();
    }

    /// <summary>An approval ask that blocks until cancelled (or 10 s), recording what ended it.</summary>
    private static (TaskCompletionSource Entered, TaskCompletionSource<bool> Cancelled) BlockingAsk()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShareHost.AskOverride = (_, _, ct) =>
        {
            entered.TrySetResult();
            bool wasCancelled = ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            cancelled.TrySetResult(wasCancelled);
            return wasCancelled ? null : 1;
        };
        return (entered, cancelled);
    }

    [Fact]
    public async Task Pending_Approval_Ends_When_The_Share_Stops()
    {
        var (entered, cancelled) = BlockingAsk();
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        using var guest = await ShareGuestConnection.ConnectAsync(link, "x", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ShareHost.StopAsync();
        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));   // withdrawn, not answered
        Assert.False(ShareHost.HasGuests);
    }

    [Fact]
    public async Task Pending_Approval_Ends_When_The_Guest_Leaves()
    {
        var (entered, cancelled) = BlockingAsk();
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        var guest = await ShareGuestConnection.ConnectAsync(link, "x", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        guest.Dispose();   // drop the connection while the host is still being asked
        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(ShareHost.HasGuests);
    }

    private static async Task<byte[]> NextOfType(ShareGuestConnection c, byte type)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var m = await c.ReceiveAsync(cts.Token) ?? throw new Xunit.Sdk.XunitException("closed");
            if (m[0] == type) return m;
        }
    }

    [Fact]
    public async Task Approved_Guest_Receives_Teed_Output()
    {
        string? askedName = null;
        ShareHost.ApprovalOverride = n => { askedName = n; return ShareHost.Approval.Watch; };
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        using var guest = await ShareGuestConnection.ConnectAsync(link, "tester<\u001b>", CancellationToken.None);
        await NextOfType(guest, ShareProtocol.Accept);
        Assert.Equal("tester", askedName);   // control chars / markup stripped from peer-supplied name
        for (int i = 0; i < 50 && !ShareHost.HasGuests; i++) await Task.Delay(20);
        Assert.True(ShareHost.HasGuests);

        ShareHost.OnTerminalWrite("\u001b[1;1Hhello-frame", 100, 30);
        var sizeMsg = await NextOfType(guest, ShareProtocol.Size);
        Assert.Equal((100, 30), ShareProtocol.ReadSize(sizeMsg.AsSpan(1)));
        var output = await NextOfType(guest, ShareProtocol.Output);
        Assert.Contains("hello-frame", Encoding.UTF8.GetString(output, 1, output.Length - 1));
        await guest.CloseAsync();
    }

    [Fact]
    public async Task Wrong_Secret_Is_Refused_Before_Approval()
    {
        bool asked = false;
        ShareHost.ApprovalOverride = _ => { asked = true; return ShareHost.Approval.Watch; };
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        var forged = new ShareLink(link.BaseUri, link.RoomId, new byte[32]);
        var ex = await Assert.ThrowsAsync<ShareJoinException>(() => ShareGuestConnection.ConnectAsync(forged, "x", CancellationToken.None));
        Assert.Contains("prove", ex.Message);
        Assert.False(asked);
        Assert.False(ShareHost.HasGuests);
    }

    [Fact]
    public async Task Wrong_Room_Is_404()
    {
        ShareHost.ApprovalOverride = _ => ShareHost.Approval.Watch;
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        var other = new ShareLink(link.BaseUri, ShareLink.NewRoomId(), link.Secret);
        var ex = await Assert.ThrowsAsync<ShareJoinException>(() => ShareGuestConnection.ConnectAsync(other, "x", CancellationToken.None));
        Assert.Contains("ended", ex.Message);
    }

    [Fact]
    public async Task Declined_Guest_Gets_Bye_And_Nothing_Else()
    {
        ShareHost.ApprovalOverride = _ => ShareHost.Approval.Deny;
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        using var guest = await ShareGuestConnection.ConnectAsync(link, "x", CancellationToken.None);
        var bye = await NextOfType(guest, ShareProtocol.Bye);
        Assert.Contains("declined", Encoding.UTF8.GetString(bye, 1, bye.Length - 1));
        Assert.False(ShareHost.HasGuests);
    }

    [Fact]
    public async Task Link_Secret_Never_Reaches_Guests()
    {
        ShareHost.ApprovalOverride = _ => ShareHost.Approval.Watch;
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        using var guest = await ShareGuestConnection.ConnectAsync(link, "x", CancellationToken.None);
        await NextOfType(guest, ShareProtocol.Accept);
        for (int i = 0; i < 50 && !ShareHost.HasGuests; i++) await Task.Delay(20);
        string secret = System.Buffers.Text.Base64Url.EncodeToString(link.Secret);
        ShareHost.OnTerminalWrite("\u001b[2;1Hjoin: " + link, 120, 30);
        var output = await NextOfType(guest, ShareProtocol.Output);
        string text = Encoding.UTF8.GetString(output, 1, output.Length - 1);
        Assert.Contains("/s/" + link.RoomId + "#", text);
        Assert.DoesNotContain(secret[..SecretRedactor.MinFragment], text);
        await guest.CloseAsync();
    }

    [Fact]
    public async Task Typing_Is_Host_Enforced_Granted_And_Revoked()
    {
        RemoteInput.ResetForTest();
        ShareHost.ApprovalOverride = _ => ShareHost.Approval.WatchAndType;
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        using var guest = await ShareGuestConnection.ConnectAsync(link, "typist", CancellationToken.None);
        await NextOfType(guest, ShareProtocol.Accept);
        var ctl = await NextOfType(guest, ShareProtocol.Control);
        Assert.Equal((byte)'1', ctl[1]);
        for (int i = 0; i < 50 && RemoteInput.TypistCount == 0; i++) await Task.Delay(20);

        // No prompt open: input is dropped even though the guest may type.
        await guest.SendAsync(ShareProtocol.Msg(ShareProtocol.Input, "early"), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(RemoteInput.HasPending);

        RemoteInput.BeginPrompt();
        try
        {
            await guest.SendAsync(ShareProtocol.Msg(ShareProtocol.Input, "hi"), CancellationToken.None);
            for (int i = 0; i < 50 && !RemoteInput.HasPending; i++) await Task.Delay(20);
            Assert.True(RemoteInput.TryTake(out var e));
            Assert.Equal("hi", e.Text);
            Assert.Equal("typist", e.Name);

            Assert.Equal(1, ShareHost.RevokeAllTyping());
            var off = await NextOfType(guest, ShareProtocol.Control);
            Assert.Equal((byte)'0', off[1]);
            await guest.SendAsync(ShareProtocol.Msg(ShareProtocol.Input, "after"), CancellationToken.None);
            await Task.Delay(150);
            Assert.False(RemoteInput.TryTake(out _));   // revoked: host drops it
        }
        finally { RemoteInput.EndPrompt(); }
        await guest.CloseAsync();
    }

    [Fact]
    public async Task Watch_Only_Guest_Input_Is_Dropped()
    {
        RemoteInput.ResetForTest();
        ShareHost.ApprovalOverride = _ => ShareHost.Approval.Watch;
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        using var guest = await ShareGuestConnection.ConnectAsync(link, "viewer", CancellationToken.None);
        await NextOfType(guest, ShareProtocol.Accept);
        RemoteInput.BeginPrompt();
        try
        {
            await guest.SendAsync(ShareProtocol.Msg(ShareProtocol.Input, "rm -rf\r"), CancellationToken.None);
            await Task.Delay(150);
            Assert.False(RemoteInput.HasPending);
        }
        finally { RemoteInput.EndPrompt(); }
        await guest.CloseAsync();
    }

    [Fact]
    public async Task Stop_Disconnects_Guests_And_Frees_Listener()
    {
        ShareHost.ApprovalOverride = _ => ShareHost.Approval.Watch;
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        using var guest = await ShareGuestConnection.ConnectAsync(link, "x", CancellationToken.None);
        await NextOfType(guest, ShareProtocol.Accept);
        await ShareHost.StopAsync();
        Assert.False(ShareHost.IsActive);
        await Assert.ThrowsAsync<ShareJoinException>(() => ShareGuestConnection.ConnectAsync(link, "y", CancellationToken.None));
    }
}
