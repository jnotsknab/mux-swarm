using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MuxSwarm.Engine.Share;

/// <summary>
/// Key agreement and authentication for live session sharing (<c>/share</c> / <c>--join</c>).
/// Shape: Noise NNpsk0-style handshake built only from BCL primitives (ECDH P-256, HKDF-SHA256,
/// HMAC-SHA256, AES-256-GCM) so it runs identically on every supported RID and maps 1:1 onto
/// WebCrypto for the browser guest. Both sides prove knowledge of the link secret (PSK) before
/// the host shows any UI; the per-connection ephemeral ECDH gives forward secrecy, so a link
/// leaked after the session cannot decrypt recorded traffic.
/// </summary>
internal static class ShareHandshake
{
    /// <summary>Protocol label bound into every derived key and MAC.</summary>
    public const string Proto = "mux-share/1";

    /// <summary>Handshake nonce length (bytes).</summary>
    public const int NonceBytes = 32;

    /// <summary>Uncompressed P-256 public point length (0x04 || X || Y).</summary>
    public const int PublicKeyBytes = 65;

    /// <summary>Create a fresh ephemeral P-256 key pair.</summary>
    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Export the public half as an uncompressed point (WebCrypto "raw" format).</summary>
    public static byte[] ExportPublic(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        var raw = new byte[PublicKeyBytes];
        raw[0] = 0x04;
        q.X!.CopyTo(raw, 1);
        q.Y!.CopyTo(raw, 33);
        return raw;
    }

    /// <summary>ECDH with the peer's raw public point. Throws <see cref="CryptographicException"/>
    /// for a malformed or off-curve point (the platform import validates the point).</summary>
    public static byte[] Agree(ECDiffieHellman mine, byte[] peerPublic)
    {
        if (peerPublic is not { Length: PublicKeyBytes } || peerPublic[0] != 0x04)
            throw new CryptographicException("invalid peer public key");
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = peerPublic[1..33], Y = peerPublic[33..65] },
        });
        return mine.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>Hash of every handshake field, length-prefixed so no two field layouts collide.</summary>
    public static byte[] Transcript(string roomId, byte[] guestNonce, byte[] guestPub, string guestName,
        byte[] hostNonce, byte[] hostPub)
    {
        using var ms = new MemoryStream();
        void Put(ReadOnlySpan<byte> field)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)field.Length);
            ms.Write(len);
            ms.Write(field);
        }
        Put(Encoding.UTF8.GetBytes(Proto));
        Put(Encoding.UTF8.GetBytes(roomId));
        Put(guestNonce);
        Put(guestPub);
        Put(Encoding.UTF8.GetBytes(guestName));
        Put(hostNonce);
        Put(hostPub);
        return SHA256.HashData(ms.ToArray());
    }

    /// <summary>MAC key derived from the link secret; used only for the mutual proofs.</summary>
    public static byte[] AuthKey(byte[] psk)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, psk, 32, salt: null, info: Encoding.UTF8.GetBytes(Proto + "/auth"));

    /// <summary>Proof MAC for <paramref name="role"/> ('H' host, 'G' guest) over the transcript.</summary>
    public static byte[] Proof(byte[] authKey, char role, byte[] transcript)
    {
        var data = new byte[transcript.Length + 1];
        data[0] = (byte)role;
        transcript.CopyTo(data, 1);
        return HMACSHA256.HashData(authKey, data);
    }

    /// <summary>Constant-time proof check.</summary>
    public static bool Verify(byte[] expected, byte[]? actual)
        => actual is not null && actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, actual);

    /// <summary>Per-direction AES-256 session keys: ECDH output as IKM, PSK as salt, transcript as info.</summary>
    public static (byte[] HostToGuest, byte[] GuestToHost) SessionKeys(byte[] shared, byte[] psk, byte[] transcript)
    {
        byte[] Info(string dir) => [.. Encoding.UTF8.GetBytes(Proto + "/" + dir + "/"), .. transcript];
        return (HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, psk, Info("h2g")),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, psk, Info("g2h")));
    }
}

/// <summary>
/// One direction-keyed AES-256-GCM channel. Nonces are implicit per-direction counters (the
/// WebSocket is ordered), so a dropped, reordered, replayed or forged frame fails authentication
/// and the connection is torn down. Not thread-safe: one sender and one receiver per side.
/// </summary>
internal sealed class ShareChannel : IDisposable
{
    /// <summary>AES-GCM tag size (the only size Apple CryptoKit supports).</summary>
    public const int TagBytes = 16;

    private readonly AesGcm _tx;
    private readonly AesGcm _rx;
    private ulong _txCounter;
    private ulong _rxCounter;

    public ShareChannel(byte[] sendKey, byte[] receiveKey)
    {
        _tx = new AesGcm(sendKey, TagBytes);
        _rx = new AesGcm(receiveKey, TagBytes);
    }

    private static byte[] Nonce(ulong counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    /// <summary>Encrypt one message: ciphertext || tag.</summary>
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[plaintext.Length + TagBytes];
        _tx.Encrypt(Nonce(_txCounter++), plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length));
        return output;
    }

    /// <summary>Decrypt the next expected message; false on any authentication failure.</summary>
    public bool TryOpen(ReadOnlySpan<byte> sealedMessage, out byte[] plaintext)
    {
        plaintext = [];
        if (sealedMessage.Length < TagBytes) return false;
        var pt = new byte[sealedMessage.Length - TagBytes];
        try
        {
            _rx.Decrypt(Nonce(_rxCounter), sealedMessage[..pt.Length], sealedMessage[pt.Length..], pt);
        }
        catch (CryptographicException) { return false; }
        _rxCounter++;
        plaintext = pt;
        return true;
    }

    public void Dispose()
    {
        _tx.Dispose();
        _rx.Dispose();
    }
}
