using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Engine.Share;

/// <summary>
/// Host-side intake for keys typed by share guests the host allowed to type. Guest bytes are VT
/// key sequences; <see cref="Decode"/> turns them into an allowlisted set of editing edits (text,
/// Enter, Backspace, Delete, Left/Right, Home/End, Tab, Ctrl+A/E/K/U/W) - never host hotkeys,
/// history, clipboard or modal keys. Edits land in a bounded queue that ONLY the idle prompt drains
/// (see TuiDriver.Share.cs), so no picker or permission modal can ever see a guest key. The queue
/// only accepts input while a prompt is open, and every edit is re-checked against the guest's
/// current permission when taken, so a revoke also cancels anything still in flight.
/// </summary>
internal static class RemoteInput
{
    /// <summary>One queued guest edit: a key for the line editor, or a run of literal text.</summary>
    public readonly record struct Edit(int GuestId, string Name, ConsoleKeyInfo Key, string? Text);

    /// <summary>Largest guest input message accepted (chars); larger messages are dropped whole.</summary>
    public const int MaxMessageChars = 16 * 1024;

    private const int MaxQueued = 256;   // edits (one per text run/key), so memory stays bounded
    private static readonly ConcurrentQueue<Edit> Queue = new();
    private static readonly ConcurrentDictionary<int, string> Typists = new();
    private static int _queued;
    private static volatile bool _open;

    /// <summary>Number of guests currently allowed to type.</summary>
    public static int TypistCount => Typists.Count;

    /// <summary>True while an idle prompt is accepting guest input.</summary>
    public static bool IsOpen => _open;

    /// <summary>True when guest edits are waiting.</summary>
    public static bool HasPending => !Queue.IsEmpty;

    /// <summary>True when <paramref name="guestId"/> may type.</summary>
    public static bool CanType(int guestId) => Typists.ContainsKey(guestId);

    /// <summary>Allow a guest to type.</summary>
    public static void Grant(int guestId, string name) => Typists[guestId] = name;

    /// <summary>Stop a guest typing; false when it could not type.</summary>
    public static bool Revoke(int guestId) => Typists.TryRemove(guestId, out _);

    /// <summary>Stop every guest typing and drop queued edits. Returns how many were revoked.</summary>
    public static int RevokeAll()
    {
        int n = 0;
        foreach (var id in Typists.Keys) if (Typists.TryRemove(id, out _)) n++;
        Clear();
        return n;
    }

    /// <summary>The idle prompt opened: start accepting guest input (stale edits are dropped).</summary>
    public static void BeginPrompt() { Clear(); _open = true; }

    /// <summary>The idle prompt closed: stop accepting guest input and drop anything queued.</summary>
    public static void EndPrompt() { _open = false; Clear(); }

    /// <summary>Queue one guest input message. Dropped unless a prompt is open and the guest may type.</summary>
    public static void Post(int guestId, string vt)
    {
        if (!_open || vt.Length > MaxMessageChars || !Typists.TryGetValue(guestId, out var name)) return;
        foreach (var (key, text) in Decode(vt))
        {
            if (Interlocked.Increment(ref _queued) > MaxQueued) { Interlocked.Decrement(ref _queued); return; }
            Queue.Enqueue(new Edit(guestId, name, key, text));
        }
    }

    /// <summary>Take the next edit from a guest that may still type.</summary>
    public static bool TryTake(out Edit edit)
    {
        while (Queue.TryDequeue(out edit))
        {
            Interlocked.Decrement(ref _queued);
            if (Typists.ContainsKey(edit.GuestId)) return true;
        }
        return false;
    }

    /// <summary>Test seam: forget all grants and queued input.</summary>
    internal static void ResetForTest() { _open = false; Typists.Clear(); Clear(); }

    private static void Clear()
    {
        while (Queue.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
    }

    private static readonly ConsoleKeyInfo EnterKey = new('\r', ConsoleKey.Enter, false, false, false);

    /// <summary>
    /// Decode one guest message into allowlisted edits. Only a message that is exactly one Enter
    /// ("\r") yields a submitting Enter; an Enter inside any larger message (fast typing, a paste)
    /// becomes a newline in the draft, so a burst can never submit on its own. Bracketed-paste
    /// bodies become literal text. Every other control byte or escape sequence is dropped.
    /// </summary>
    public static List<(ConsoleKeyInfo Key, string? Text)> Decode(string vt)
    {
        var outp = new List<(ConsoleKeyInfo, string?)>();
        if (vt == "\r") { outp.Add((EnterKey, null)); return outp; }
        var text = new StringBuilder();
        void FlushText() { if (text.Length > 0) { outp.Add((default, text.ToString())); text.Clear(); } }
        void AddKey(ConsoleKeyInfo k) { FlushText(); outp.Add((k, null)); }

        int i = 0;
        while (i < vt.Length)
        {
            char c = vt[i];
            if (c == '\u001b')
            {
                int end = SequenceEnd(vt, i);
                string seq = vt[(i + 1)..end];
                i = end;
                if (seq == "[200~")
                {
                    int close = vt.IndexOf("\u001b[201~", i, StringComparison.Ordinal);
                    text.Append(CleanPaste(close < 0 ? vt[i..] : vt[i..close]));
                    i = close < 0 ? vt.Length : close + 6;
                }
                else if (seq.Length >= 2 && seq[0] is '[' or 'O' && seq[1] is not ('<' or '?' or '>')
                         && VtKeyboard.TryDecode(seq[1..], out var k) && IsAllowedKey(k))
                    AddKey(k);
                continue;
            }
            i++;
            switch (c)
            {
                case '\r' or '\n': text.Append('\n'); break;
                case '\t': AddKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)); break;
                case '\u007f' or '\b': AddKey(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false)); break;
                case '\u0001': AddKey(Ctrl(c, ConsoleKey.A)); break;
                case '\u0005': AddKey(Ctrl(c, ConsoleKey.E)); break;
                case '\u000b': AddKey(Ctrl(c, ConsoleKey.K)); break;
                case '\u0015': AddKey(Ctrl(c, ConsoleKey.U)); break;
                case '\u0017': AddKey(Ctrl(c, ConsoleKey.W)); break;
                default: if (!char.IsControl(c)) text.Append(c); break;   // drops C0, DEL, C1
            }
        }
        FlushText();
        return outp;
    }

    private static ConsoleKeyInfo Ctrl(char c, ConsoleKey key) => new(c, key, false, false, true);

    // CSI/SS3 keys a guest may send: cursor movement and Delete only (Up/Down would browse the host's
    // prompt history; Enter/Tab/F-keys via CSI are never a guest submit or hotkey).
    private static bool IsAllowedKey(ConsoleKeyInfo k)
        => k.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.Delete
           && (k.Modifiers == 0 || (k.Modifiers == ConsoleModifiers.Control && k.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow));

    private static string CleanPaste(string body)
    {
        var sb = new StringBuilder(body.Length);
        foreach (char c in body.Replace("\r\n", "\n").Replace('\r', '\n'))
            if (c is '\n' or '\t' || !char.IsControl(c)) sb.Append(c);
        return sb.ToString();
    }

    // Index just past the escape sequence starting at i (CSI, SS3, string sequences, or ESC + one char).
    private static int SequenceEnd(string s, int i)
    {
        if (i + 1 >= s.Length) return s.Length;
        char n = s[i + 1];
        if (n == 'O') return Math.Min(s.Length, i + 3);
        if (n == '[')
        {
            int j = i + 2;
            while (j < s.Length && !(s[j] >= '\u0040' && s[j] <= '\u007e')) j++;
            return Math.Min(s.Length, j + 1);
        }
        if (n is ']' or 'P' or '_' or '^' or 'X')
        {
            for (int j = i + 2; j < s.Length; j++)
            {
                if (s[j] == '\u0007') return j + 1;
                if (s[j] == '\u001b' && j + 1 < s.Length && s[j + 1] == '\\') return j + 2;
            }
            return s.Length;
        }
        return i + 2;
    }
}

/// <summary>
/// What a line a guest typed (or edited) may do when submitted. Allowlist, so new commands are
/// blocked by default: plain prompts and a few read-only / conversation commands only. Shell
/// escapes (<c>!cmd</c>), sharing, lifecycle, config and clipboard commands are always refused.
/// </summary>
internal static class RemotePolicy
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/help", "/shortcuts", "/keys", "/status", "/tokens", "/context", "/cost", "/diff",
        "/compact", "/retry", "/undo", "/redo",
    };

    /// <summary>
    /// True when a guest-authored line may be submitted. Empty/whitespace lines are refused: the
    /// agent and swarm loops treat an empty submit as quit.
    /// </summary>
    public static bool IsAllowed(string? line)
    {
        var t = Visible(line);
        if (t.Length == 0) return false;
        if (t[0] == '!') return false;
        if (t[0] != '/') return true;
        if (t is "/" or "/?") return true;
        return AllowedCommands.Contains(t.Split(' ', 2)[0]);
    }

    /// <summary>Short label for a refused line (for the host's notice).</summary>
    public static string Describe(string? line)
    {
        var t = Visible(line);
        if (t.Length == 0) return "an empty line (ends the host's session)";
        if (t.StartsWith('!')) return "a shell command (!)";
        var token = t.Split(' ', '\n')[0];
        return token.Length > 40 ? token[..40] + "..." : token;
    }

    /// <summary>
    /// The line as a reader sees it: format / zero-width characters (Unicode Cf) removed, then any
    /// leading combining marks (Mn/Me) with no base character, then trimmed. The policy classifies
    /// this, so an invisible prefix can never make a dispatcher see a different first character
    /// than the policy did (v0.15.0 audit F1). Guest text itself is not modified.
    /// </summary>
    private static string Visible(string? line)
    {
        var src = line ?? "";
        var sb = new StringBuilder(src.Length);
        foreach (char c in src)
            if (char.GetUnicodeCategory(c) != UnicodeCategory.Format) sb.Append(c);
        var t = sb.ToString().Trim();
        int i = 0;
        while (i < t.Length && char.GetUnicodeCategory(t[i]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) i++;
        return t[i..].TrimStart();
    }
}
