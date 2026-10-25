using System.Diagnostics;
using System.Text.RegularExpressions;
using MuxSwarm.Engine.Share;
using MuxSwarm.Engine.Tui;
using Spectre.Console;

namespace MuxSwarm.Engine.Proxy;

/// <summary>
/// Console surface for a headless (no-browser) <c>/login</c>. The sidecar prints a long OAuth URL that the
/// user must carry to a browser on another machine, so it has to survive copy/paste intact. Committing it to
/// the live TUI breaks that: the transcript hard-wraps it into indented rows next to the scrollbar column,
/// and a terminal selection picks up the line breaks, the indent and the scrollbar glyph.
///
/// While the login runs this hands the terminal back (the same suspend a blocking prompt uses) and writes
/// the sidecar's lines plainly on the main screen: the URL on its own unindented line, which the terminal
/// soft-wraps so a selection copies it whole, as a hyperlink where the terminal supports links, and with a
/// <c>c</c> key that copies it without any selection (OSC 52 to the user's terminal, plus the local clipboard
/// tool when not over SSH). <c>Esc</c> cancels the login. In stdio/serve mode lines keep going out as events.
/// </summary>
internal sealed class HeadlessLoginConsole : IDisposable
{
    // A URL runs to whitespace or a quote/bracket; sentence punctuation right after it is not part of it.
    private static readonly Regex UrlPattern = new(@"https?://[^\s""'<>]+?(?=[.,;:!?)\]]*(?:[\s""'<>]|$))", RegexOptions.Compiled);

    private readonly bool _raw;            // false = stdio/serve: lines go out as events, unchanged
    private readonly bool _links;          // wrap the URL in an OSC 8 hyperlink
    private readonly bool _localClipboard; // also copy via the OS clipboard tool (not over SSH)
    private readonly Action<string> _write;
    private readonly bool _resumeTui;
    private readonly object _gate = new();
    private readonly Action<string> _commit;
    private readonly Queue<string> _tail = new();   // last plain lines, re-shown after a failed login
    private IDisposable? _input;
    private string? _url;
    private bool _hinted;
    private bool _keepOutput;
    private int _disposed;

    private const int TailLines = 8;

    // How often the wait loop checks for a key press while the login child runs (and exits on its own).
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(50);

    // After an Esc, how long to wait for the rest of an escape sequence (arrow key, mouse report). The terminal
    // sends a sequence in one write, so its bytes arrive well within this; a lone Esc has nothing after it.
    private static readonly TimeSpan EscapeSequenceWindow = TimeSpan.FromMilliseconds(30);

    internal HeadlessLoginConsole(bool raw, bool links, bool localClipboard, Action<string> write, bool resumeTui = false,
        Action<string>? commit = null)
    {
        _raw = raw;
        _links = links;
        _localClipboard = localClipboard;
        _write = write;
        _resumeTui = resumeTui;
        _commit = commit ?? MuxConsole.WriteMuted;
    }

    /// <summary>The auth URL: the first URL the sidecar printed, or null before it has printed one.</summary>
    internal string? Url { get { lock (_gate) return _url; } }

    /// <summary>
    /// Take the console for a headless login: suspend the live TUI (no-op without it) and own stdin so the
    /// <c>c</c>/<c>Esc</c> keys reach <see cref="WaitAsync"/>. Under stdio/serve nothing is suspended and
    /// lines are relayed as events. Dispose to give the console back.
    /// </summary>
    public static HeadlessLoginConsole Begin()
    {
        if (MuxConsole.StdioMode)
            return new HeadlessLoginConsole(raw: false, links: false, localClipboard: false, write: _ => { });

        MuxConsole.TuiSuspendForPrompt();
        bool links = AnsiConsole.Profile.Capabilities.Links;
        var console = new HeadlessLoginConsole(raw: true, links, !CliProxyManager.LooksHeadless(),
            loginStr => { lock (MuxConsole.ConsoleLock) { Console.Out.Write(loginStr); Console.Out.Flush(); } },
            resumeTui: true);
        if (!Console.IsInputRedirected) console._input = EscapeKeyListener.SuspendInput();
        console._write(Ansi.AutoWrapOn);   // the URL must soft-wrap, never be clipped at the edge
        return console;
    }

    /// <summary>Relay one line of sidecar output (<paramref name="err"/> = it came from stderr).</summary>
    public void Line(string line, bool err)
    {
        if (!_raw)
        {
            if (err) MuxConsole.WriteMuted(line);
            else MuxConsole.WriteInfo(line);
            return;
        }

        string text = AnsiSanitizer.PlainText(TuiMarkup.StripAnsi(line));
        var match = UrlPattern.Match(text);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) { _commit(text); return; }   // a late line after the TUI is back
            if (!match.Success || _url is not null)
            {
                // Only the first URL is the auth link (verified for every provider); later ones print as text.
                _write(err ? Ansi.Dim + text + Ansi.Reset + "\r\n" : text + "\r\n");
                _tail.Enqueue(text);
                if (_tail.Count > TailLines) _tail.Dequeue();
                return;
            }

            _url = match.Value;
            string before = text[..match.Index].TrimEnd(), after = text[(match.Index + match.Length)..].Trim();
            if (before.Length > 0) _write(before + "\r\n");
            _write("\r\n" + Link(match.Value) + "\r\n\r\n");   // alone on its line, column 0, no indent
            if (after.Length > 0) _write(after + "\r\n");
            if (_hinted) return;
            _hinted = true;
            _write(Ansi.Dim + (_input is null
                ? "Open the link above in a browser on any machine; this continues when the login completes."
                : "c copy link \u00b7 Esc cancel \u00b7 open the link above in a browser on any machine; this continues when the login completes.")
                + Ansi.Reset + "\r\n");
        }
    }

    /// <summary>The URL as printed: an OSC 8 hyperlink around the URL text when the terminal supports links.</summary>
    internal string Link(string url)
        => _links ? $"\u001b]8;;{url}\u001b\\{url}\u001b]8;;\u001b\\" : url;

    /// <summary>Handle a key pressed while waiting. Returns true when it asks to cancel the login (Esc or Ctrl+C).</summary>
    internal bool HandleKey(ConsoleKeyInfo key)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (key.Key == ConsoleKey.Escape || key.KeyChar == '\u001b' || key.KeyChar == '\u0003' || (ctrl && key.Key == ConsoleKey.C))
            return true;
        if (char.ToLowerInvariant(key.KeyChar) != 'c' || ctrl) return false;
        string? url = Url;
        if (url is null) return false;
        bool local = _localClipboard && TuiClipboard.CopyViaShell(url);
        lock (_gate)
        {
            _write(TuiClipboard.Osc52(url));
            _write(Ansi.Dim + (local
                ? "Copied the login link."
                : "Sent the login link to your terminal's clipboard (OSC 52). If nothing was copied, select the link above.")
                + Ansi.Reset + "\r\n");
        }
        return false;
    }

    /// <summary>
    /// Wait for the login process to exit, serving the <c>c</c>/<c>Esc</c> keys meanwhile. Returns true when
    /// it exited and false when the user cancelled; <paramref name="ct"/> cancellation throws
    /// <see cref="OperationCanceledException"/>. The child is left running in both cases: its owner stops it.
    /// </summary>
    public async Task<bool> WaitAsync(Process proc, CancellationToken ct)
    {
        var exited = proc.WaitForExitAsync(CancellationToken.None);
        while (!exited.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            while (_input is not null && TryReadKey(out var key))
            {
                // A lone Esc cancels; an Esc that starts an escape sequence (arrow key, mouse report) does not.
                if (key.Key == ConsoleKey.Escape && FollowedByMore()) continue;
                if (HandleKey(key)) return false;
            }
            await Task.WhenAny(exited, Task.Delay(KeyPollInterval, ct)).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// The login failed on its own: re-show the sidecar's last lines in the transcript once the TUI is back,
    /// since the main-screen copy disappears when the frame returns and they usually carry the reason.
    /// </summary>
    public void KeepOutputOnClose() { lock (_gate) _keepOutput = true; }

    private static bool FollowedByMore()
    {
        Thread.Sleep(EscapeSequenceWindow);
        bool more = false;
        while (TryReadKey(out _)) more = true;   // drain the rest of the sequence
        return more;
    }

    private static bool TryReadKey(out ConsoleKeyInfo key)
    {
        key = default;
        try
        {
            if (!Console.KeyAvailable) return false;
            key = Console.ReadKey(intercept: true);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Give the console back: release stdin, resume the live TUI and, after a failed login, re-show its output.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _input?.Dispose();
        if (_resumeTui) MuxConsole.TuiResume();
        lock (_gate)
        {
            if (!_keepOutput) return;
            foreach (string line in _tail) _commit(line);
        }
    }
}
