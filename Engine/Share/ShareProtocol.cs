using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

namespace MuxSwarm.Engine.Share;

/// <summary>
/// Wire vocabulary for live session sharing. Handshake messages (Hello/Welcome/Proof) travel in
/// the clear - they carry only nonces, ephemeral public keys, MACs and the guest's display name.
/// Every message after the handshake is one AES-GCM sealed WebSocket binary frame whose plaintext
/// starts with a one-byte type tag.
/// </summary>
internal static class ShareProtocol
{
    // --- handshake (plaintext) ---
    public const byte Hello = 0x01;     // G->H: nonce[32] pub[65] nameLen[1] name
    public const byte Welcome = 0x02;   // H->G: nonce[32] pub[65] macH[32]
    public const byte Proof = 0x03;     // G->H: macG[32]

    // --- host -> guest (sealed) ---
    public const byte Accept = 0x10;    // w[u16] h[u16]
    public const byte Reject = 0x11;    // utf8 reason
    public const byte Output = 0x12;    // utf8 terminal output (sanitized by the guest)
    public const byte Size = 0x13;      // w[u16] h[u16]
    public const byte Notice = 0x14;    // utf8 status text
    public const byte Control = 0x15;   // utf8 "1" = the guest may type, "0" = view-only
    public const byte Bye = 0x1F;       // utf8 reason

    // --- guest -> host (sealed) ---
    public const byte Input = 0x21;     // utf8 VT key bytes (host drops it unless the guest may type)
    public const byte Resync = 0x23;    // request a full repaint
    public const byte GuestBye = 0x2F;

    /// <summary>Largest handshake message accepted.</summary>
    public const int MaxHandshakeBytes = 512;

    /// <summary>Largest guest->host sealed message accepted.</summary>
    public const int MaxGuestMessageBytes = 64 * 1024;

    /// <summary>Largest host->guest sealed message accepted.</summary>
    public const int MaxHostMessageBytes = 4 * 1024 * 1024;

    /// <summary>Max guest display-name length.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Restrict a peer-supplied display name to a safe, short, printable subset.</summary>
    public static string CleanName(string? raw)
    {
        var sb = new StringBuilder();
        foreach (char c in raw ?? "")
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' or ' ') sb.Append(c);
            if (sb.Length >= MaxNameLength) break;
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "guest" : s;
    }

    /// <summary>Default display name for this machine's guest.</summary>
    public static string LocalName() => CleanName($"{Environment.UserName}@{Environment.MachineName}");

    /// <summary>Build a typed message: [type][payload].</summary>
    public static byte[] Msg(byte type, ReadOnlySpan<byte> payload)
    {
        var m = new byte[payload.Length + 1];
        m[0] = type;
        payload.CopyTo(m.AsSpan(1));
        return m;
    }

    /// <summary>Typed message with a UTF-8 text payload.</summary>
    public static byte[] Msg(byte type, string text) => Msg(type, Encoding.UTF8.GetBytes(text));

    /// <summary>Typed message carrying a width/height pair.</summary>
    public static byte[] SizeMsg(byte type, int w, int h)
    {
        Span<byte> p = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)Math.Clamp(w, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(p[2..], (ushort)Math.Clamp(h, 0, ushort.MaxValue));
        return Msg(type, p);
    }

    /// <summary>Read a width/height pair payload.</summary>
    public static (int W, int H) ReadSize(ReadOnlySpan<byte> payload)
        => payload.Length < 4 ? (0, 0)
            : (BinaryPrimitives.ReadUInt16BigEndian(payload), BinaryPrimitives.ReadUInt16BigEndian(payload[2..]));

    /// <summary>
    /// Receive one whole binary WebSocket message, bounded by <paramref name="maxBytes"/>. Returns
    /// null on a close frame. Throws <see cref="InvalidDataException"/> for text frames or oversize
    /// messages so a peer can never make the reader buffer unbounded data.
    /// </summary>
    public static async Task<byte[]?> ReceiveAsync(WebSocket ws, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(maxBytes, 64 * 1024) + 1];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            if (r.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("unexpected text frame");
            if (ms.Length + r.Count > maxBytes) throw new InvalidDataException("message too large");
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return ms.ToArray();
        }
    }
}
