using System.Buffers.Text;
using System.Text.RegularExpressions;

namespace MuxSwarm.Engine.Share;

/// <summary>
/// A share join link: <c>http://host:port/s/&lt;room&gt;#&lt;secret&gt;</c>. The room id (16 random
/// bytes) only routes the request; the 32-byte secret lives in the URL FRAGMENT, which browsers
/// never send to a server, and is never transmitted by the terminal guest either - it is only
/// KDF input for the handshake. The same link opens in a browser or with <c>mux-swarm --join</c>.
/// </summary>
internal sealed partial class ShareLink
{
    /// <summary>Room id length in bytes.</summary>
    public const int RoomBytes = 16;

    /// <summary>Link secret (PSK) length in bytes.</summary>
    public const int SecretBytes = 32;

    /// <summary><c>scheme://authority</c> of the host (http or https).</summary>
    public Uri BaseUri { get; }

    /// <summary>Base64url room id (path segment).</summary>
    public string RoomId { get; }

    /// <summary>The link secret. Never logged or sent on the wire.</summary>
    public byte[] Secret { get; }

    public ShareLink(Uri baseUri, string roomId, byte[] secret)
    {
        BaseUri = baseUri;
        RoomId = roomId;
        Secret = secret;
    }

    /// <summary>host:port for display.</summary>
    public string Authority => BaseUri.Authority;

    /// <summary>The WebSocket endpoint for this room (ws/wss mirrors http/https).</summary>
    public Uri WebSocketUri => new($"{(BaseUri.Scheme == "https" ? "wss" : "ws")}://{BaseUri.Authority}/s/{RoomId}");

    /// <summary>Full shareable link including the fragment secret.</summary>
    public override string ToString() => $"{BaseUri.Scheme}://{BaseUri.Authority}/s/{RoomId}#{Base64Url.EncodeToString(Secret)}";

    /// <summary>New random room id.</summary>
    public static string NewRoomId() => Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(RoomBytes));

    [GeneratedRegex("^/s/([A-Za-z0-9_-]{22})/?$")]
    private static partial Regex PathPattern();

    /// <summary>Parse a pasted link (quotes and whitespace tolerated). Accepts http/https/ws/wss.</summary>
    public static bool TryParse(string? text, out ShareLink? link, out string error)
    {
        link = null;
        error = "";
        var t = (text ?? "").Trim().Trim('"', '\'').Trim();
        if (t.Length == 0) { error = "no link given"; return false; }
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) { error = "not a valid URL"; return false; }
        string scheme = uri.Scheme switch { "http" or "ws" => "http", "https" or "wss" => "https", _ => "" };
        if (scheme.Length == 0) { error = "link must start with http:// or https://"; return false; }
        var m = PathPattern().Match(uri.AbsolutePath);
        if (!m.Success) { error = "not a Mux share link (expected /s/<room>)"; return false; }
        string room = m.Groups[1].Value;
        string frag = uri.Fragment.TrimStart('#');
        byte[] secret;
        try { secret = Base64Url.DecodeFromChars(frag); }
        catch (FormatException) { error = "link secret is malformed"; return false; }
        if (secret.Length != SecretBytes) { error = "link is missing its secret (the part after #)"; return false; }
        try { if (Base64Url.DecodeFromChars(room).Length != RoomBytes) { error = "room id is malformed"; return false; } }
        catch (FormatException) { error = "room id is malformed"; return false; }
        link = new ShareLink(new Uri($"{scheme}://{uri.Authority}"), room, secret);
        return true;
    }
}
