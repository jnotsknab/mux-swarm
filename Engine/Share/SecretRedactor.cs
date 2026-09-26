namespace MuxSwarm.Engine.Share;

/// <summary>
/// Masks the share-link secret out of the host's outgoing terminal stream so guests never see it,
/// even when the link is on the host's screen (the <c>/share</c> printout, <c>/share status</c>,
/// scrollback redraws). Scans the visible text between escape sequences as runs of base64url
/// characters; a run that is part of the secret (at least <see cref="MinFragment"/> chars, so a link
/// hard-wrapped across rows is caught on both sides) is replaced with same-width dots, keeping the
/// frame geometry intact. Stateless per write: the frame renderer emits each frame as one write.
/// </summary>
internal static class SecretRedactor
{
    /// <summary>Shortest secret fragment that is masked (shorter fragments carry negligible entropy).</summary>
    public const int MinFragment = 6;

    private const char Mask = '\u2022';

    /// <summary>Return <paramref name="s"/> with every visible fragment of <paramref name="secret"/> masked.</summary>
    public static string Redact(string s, string secret)
    {
        if (secret.Length < MinFragment || s.Length < MinFragment) return s;
        char[]? buf = null;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\u001b') { i = SkipEscape(s, i); continue; }
            if (!IsB64(c)) { i++; continue; }
            int start = i;
            while (i < s.Length && IsB64(s[i])) i++;
            if (i - start >= MinFragment) MaskRun(s, start, i - start, secret, ref buf);
        }
        return buf is null ? s : new string(buf);
    }

    // Mask the part of one visible base64url run that belongs to the secret.
    private static void MaskRun(string s, int start, int len, string secret, ref char[]? buf)
    {
        var run = s.AsSpan(start, len);
        int from, count;
        int at = run.IndexOf(secret.AsSpan(), StringComparison.Ordinal);
        if (at >= 0) { from = at; count = secret.Length; }                       // whole secret inside
        else if (secret.AsSpan().IndexOf(run, StringComparison.Ordinal) >= 0) { from = 0; count = len; }  // run is a fragment
        else
        {
            // Wrapped link: the run can end with the secret's head or start with its tail.
            int head = Overlap(run, secret.AsSpan(), headOfSecret: true);
            int tail = Overlap(run, secret.AsSpan(), headOfSecret: false);
            if (head < MinFragment && tail < MinFragment) return;
            if (head >= tail) { from = len - head; count = head; } else { from = 0; count = tail; }
        }
        buf ??= s.ToCharArray();
        for (int k = 0; k < count; k++) buf[start + from + k] = Mask;
    }

    // Longest n where run ends with secret[..n] (head) or starts with secret[^n..] (tail).
    private static int Overlap(ReadOnlySpan<char> run, ReadOnlySpan<char> secret, bool headOfSecret)
    {
        for (int n = Math.Min(run.Length, secret.Length) - 1; n >= MinFragment; n--)
        {
            bool ok = headOfSecret ? run[^n..].SequenceEqual(secret[..n]) : run[..n].SequenceEqual(secret[^n..]);
            if (ok) return n;
        }
        return 0;
    }

    private static bool IsB64(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    // Index just past the escape sequence starting at i (CSI, string sequences, or ESC + one char).
    private static int SkipEscape(string s, int i)
    {
        i++;
        if (i >= s.Length) return i;
        char k = s[i++];
        if (k == '[')
        {
            while (i < s.Length && !(s[i] >= '\u0040' && s[i] <= '\u007e')) i++;
            return Math.Min(s.Length, i + 1);
        }
        if (k is ']' or 'P' or '_' or '^' or 'X')
        {
            while (i < s.Length)
            {
                if (s[i] == '\u0007') return i + 1;
                if (s[i] == '\u001b' && i + 1 < s.Length && s[i + 1] == '\\') return i + 2;
                i++;
            }
        }
        return i;
    }
}
