using System.Security.Cryptography;
using System.Text;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// PKCE (RFC 7636) code verifier/challenge generation. Ported verbatim from CLIProxyAPI's
/// internal/auth/claude/pkce.go: the verifier is 96 random bytes base64url-encoded WITHOUT padding
/// (=> 128 chars), and the S256 challenge is SHA256(ASCII(verifier)) base64url WITHOUT padding.
/// Hash the ASCII bytes of the verifier STRING (not the raw random bytes) - this is the common pitfall.
/// </summary>
internal static class Pkce
{
    internal readonly record struct Codes(string Verifier, string Challenge);

    /// <summary>Generate a verifier/challenge pair (S256).</summary>
    public static Codes Generate(int byteLength = 96)
    {
        string verifier = Base64UrlNoPad(RandomNumberGenerator.GetBytes(byteLength));
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        string challenge = Base64UrlNoPad(hash);
        return new Codes(verifier, challenge);
    }

    /// <summary>A random URL-safe state string for CSRF protection (32 bytes -> base64url-nopad).</summary>
    public static string NewState() => Base64UrlNoPad(RandomNumberGenerator.GetBytes(32));

    /// <summary>Base64url encode without padding (RFC 4648 sec 5): + -> -, / -> _, drop trailing =.</summary>
    public static string Base64UrlNoPad(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
