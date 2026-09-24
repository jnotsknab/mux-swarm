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

/// <summary>End-to-end over a real loopback listener: handshake, approval, frame delivery, and
/// the negative paths (wrong secret, wrong room, declined).</summary>
[Collection("ShareHost")]
public class ShareHostLoopbackTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        ShareHost.ApprovalOverride = null;
        await ShareHost.StopAsync();
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
        ShareHost.ApprovalOverride = n => { askedName = n; return true; };
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
        ShareHost.ApprovalOverride = _ => { asked = true; return true; };
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
        ShareHost.ApprovalOverride = _ => true;
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        var other = new ShareLink(link.BaseUri, ShareLink.NewRoomId(), link.Secret);
        var ex = await Assert.ThrowsAsync<ShareJoinException>(() => ShareGuestConnection.ConnectAsync(other, "x", CancellationToken.None));
        Assert.Contains("ended", ex.Message);
    }

    [Fact]
    public async Task Declined_Guest_Gets_Bye_And_Nothing_Else()
    {
        ShareHost.ApprovalOverride = _ => false;
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        using var guest = await ShareGuestConnection.ConnectAsync(link, "x", CancellationToken.None);
        var bye = await NextOfType(guest, ShareProtocol.Bye);
        Assert.Contains("declined", Encoding.UTF8.GetString(bye, 1, bye.Length - 1));
        Assert.False(ShareHost.HasGuests);
    }

    [Fact]
    public async Task Stop_Disconnects_Guests_And_Frees_Listener()
    {
        ShareHost.ApprovalOverride = _ => true;
        var link = await ShareHost.StartAsync(lan: false, port: 0);
        using var guest = await ShareGuestConnection.ConnectAsync(link, "x", CancellationToken.None);
        await NextOfType(guest, ShareProtocol.Accept);
        await ShareHost.StopAsync();
        Assert.False(ShareHost.IsActive);
        await Assert.ThrowsAsync<ShareJoinException>(() => ShareGuestConnection.ConnectAsync(link, "y", CancellationToken.None));
    }
}
