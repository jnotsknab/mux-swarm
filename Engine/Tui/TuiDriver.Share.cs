using MuxSwarm.Engine.Share;

namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Share guest typing at the idle prompt. Guest edits come from <see cref="RemoteInput"/>, never
/// from the input pump, and are applied straight to the line editor, bypassing every host hotkey
/// (clipboard paste, history, NAV, pickers, effort cycling). Any draft a guest touched is
/// guest-authored until it is cleared: on submit it must pass <see cref="RemotePolicy"/> and its
/// echo names the guests who typed it.
/// </summary>
internal sealed partial class TuiDriver
{
    private readonly SortedSet<string> _remoteAuthors = new(StringComparer.Ordinal);

    /// <summary>Host Ctrl+] (a raw GS byte from VT input, or Oem6 + Ctrl from native input).</summary>
    internal static bool IsRevokeKey(ConsoleKeyInfo key)
        => key.KeyChar == '\u001d' || (key.Key == ConsoleKey.Oem6 && key.Modifiers.HasFlag(ConsoleModifiers.Control));

    /// <summary>Apply queued guest edits to the draft. Returns a line to submit, or null.</summary>
    private string? DrainRemoteInput()
    {
        if (_remoteAuthors.Count > 0 && _editor.IsEmpty) _remoteAuthors.Clear();
        if (!RemoteInput.HasPending) return null;
        bool changed = false;
        while (RemoteInput.TryTake(out var e))
        {
            if (!_inInput || _editor.IsSearching || PastePending) continue;   // not composing: drop
            if (e.Text is { } text)
            {
                _editor.InsertPaste(text);   // literal text only - never the file/image paste path
                _remoteAuthors.Add(e.Name);
                changed = true;
                continue;
            }
            if (e.Key.Key == ConsoleKey.Enter)
            {
                _remoteAuthors.Add(e.Name);
                string line = _editor.Buffer;
                if (!RemoteSubmitAllowed(line)) { changed = true; continue; }
                return SubmitDraft(line);
            }
            var sig = _editor.Feed(e.Key);
            if (sig == LineEditSignal.Complete) AcceptCompletion();
            if (sig is LineEditSignal.Continue or LineEditSignal.Complete) { _remoteAuthors.Add(e.Name); changed = true; }
        }
        if (changed) { _paletteSel = -1; Repaint(); }
        return null;
    }

    /// <summary>False (and the draft is cleared, with notices) when a guest-authored line is refused.</summary>
    private bool RemoteSubmitAllowed(string line)
    {
        if (_remoteAuthors.Count == 0 || RemotePolicy.IsAllowed(line)) return true;
        string who = string.Join(", ", _remoteAuthors);
        string what = RemotePolicy.Describe(line);
        CommitLine($"  [{TuiComponents.Warn}]\u26a0 Blocked a line typed by {Spectre.Console.Markup.Escape(who)}: " +
                   $"{Spectre.Console.Markup.Escape(what)} is not allowed from a share guest.[/]");
        ShareHost.NoticeAll($"Blocked: {what} is not allowed from a guest.");
        _editor.SetBuffer("");
        _remoteAuthors.Clear();
        return false;
    }

    /// <summary>Finish a submit: history, viewport reset, echo (+ guest attribution). Returns the line.</summary>
    private string SubmitDraft(string line)
    {
        _editor.Remember(line);
        _inInput = false;
        _frameScroll = 0;   // submitting always returns the frame viewport to the live tail
        _userScrolled = false;
        _pendingGap = false;
        // Suppress the echo for bare commands that open a blocking interactive picker (e.g. /set,
        // /swap): the picker draws its own UI and the handler prints a confirmation.
        EchoDraft(line);
        if (_remoteAuthors.Count > 0)
        {
            CommitLine($"  [{TuiComponents.Muted}]\u21b3 typed by {Spectre.Console.Markup.Escape(string.Join(", ", _remoteAuthors))} (share guest)[/]");
            _remoteAuthors.Clear();
        }
        return line;
    }

    /// <summary>Host Ctrl+]: take typing away from every guest. False when not sharing.</summary>
    private bool TryRevokeGuestTyping(ConsoleKeyInfo key)
    {
        if (!IsRevokeKey(key) || !ShareHost.IsActive) return false;
        int n = ShareHost.RevokeAllTyping();
        CommitLine($"  [{TuiComponents.Muted}]{(n > 0 ? "Guest typing revoked." : "No guest can type.")} (/share control <name> on to allow again)[/]");
        return true;
    }
}
