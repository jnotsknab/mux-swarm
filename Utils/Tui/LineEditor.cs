using System.Text;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Result of feeding one key to the <see cref="LineEditor"/>.
/// </summary>
internal enum LineEditSignal
{
    /// <summary>Buffer/cursor changed; caller should repaint the input row.</summary>
    Continue,
    /// <summary>Enter pressed; <see cref="LineEditor.Buffer"/> is the submitted line.</summary>
    Submit,
    /// <summary>Ctrl-C / Esc cancel; caller should abort the current input.</summary>
    Cancel,
    /// <summary>Ctrl-D on an empty buffer; treat as EOF / exit.</summary>
    Eof,
    /// <summary>Shift+Tab pressed; caller should cycle the active mode (e.g. reasoning effort).</summary>
    ModeCycle,
    /// <summary>Tab pressed; caller should accept the top autocomplete candidate (palette/skill/session/@file).</summary>
    Complete,
    /// <summary>Normal-mode request to enter transcript NAV (scrollback browsing) mode.</summary>
    NavEnter,
    /// <summary>Vim mode (Insert &lt;-&gt; Normal) changed; caller should repaint the footer badge.</summary>
    ModeChanged,
    /// <summary>Key had no effect (e.g. unmapped control); no repaint needed.</summary>
    Ignored
}

/// <summary>Result of feeding one key while the reverse-incremental-history search (Ctrl+R)
/// is active.</summary>
internal enum ReverseSearchSignal
{
    /// <summary>Query/match changed (or a no-op key was swallowed); repaint the search row.</summary>
    Redraw,
    /// <summary>Esc: load the current match into the buffer and leave search at the prompt.</summary>
    Accept,
    /// <summary>Enter: load the current match into the buffer and submit it.</summary>
    AcceptAndSubmit,
    /// <summary>Ctrl+C / Ctrl+G: leave search and restore the pre-search buffer untouched.</summary>
    Cancel
}

/// <summary>Vim-style editing mode for the input buffer.</summary>

/// <summary>
/// A pure, headless line editor for the live-region TUI input box. It owns the edit
/// buffer, cursor position, and command history, and turns <see cref="ConsoleKeyInfo"/>
/// keystrokes into <see cref="LineEditSignal"/>s. It performs NO console I/O, so the full
/// editing state machine (insertion, deletion, cursor motion, history recall, as-you-type
/// slash detection) is unit-testable without a terminal. The raw-mode read loop and
/// painting live in the renderer driver; this is the model it manipulates.
/// </summary>
internal sealed class LineEditor
{
    private readonly StringBuilder _buf = new();
    private int _cursor;                       // index into _buf (0.._buf.Length)
    private readonly List<string> _history = new();
    private int _historyIndex;                 // == _history.Count means "current/new line"
    private string _stash = "";                // in-progress line stashed while browsing history

    // History-recall tracking. RecalledFromHistory is true while the buffer still holds an
    // unedited line pulled from history (or the restored in-progress stash) - the driver uses it
    // to keep Up/Down browsing history through a recalled slash command instead of letting the
    // command palette capture the arrows (the "can't go back further once you hit a slash cmd"
    // bug). Comparing against the snapshot means ANY edit clears it automatically (cursor-only
    // motion keeps it set), with no need to touch every mutation site.
    private bool _historyActive;
    private string _historyBuf = "";

    // Reverse-incremental search (Ctrl+R) state. _searchStash is the buffer captured on entry so
    // a cancel restores it verbatim; _searchPos is the history index of the current match (-1 none).
    private bool _searching;
    private readonly StringBuilder _search = new();
    private int _searchPos = -1;
    private string _searchStash = "";

    // Vim modal state. Default Insert preserves the prior (modeless) behavior exactly; the
    // user opts into Normal mode with Esc, and a "pending operator" (e.g. the first 'd' of
    // 'dd'/'dw' or the first 'g' of 'gg') is tracked across two keystrokes.

    /// <summary>Current edit buffer contents.</summary>
    public string Buffer => _buf.ToString();

    /// <summary>Cursor index within the buffer (0-based, may equal length).</summary>
    public int Cursor => _cursor;

    /// <summary>True when the buffer is empty.</summary>
    public bool IsEmpty => _buf.Length == 0;

    /// <summary>
    /// True when the buffer is an as-you-type slash command (starts with '/', no spaces yet),
    /// which the renderer uses to show the live command palette beneath the input.
    /// </summary>
    public bool IsSlashFilter => _buf.Length > 0 && _buf[0] == '/' && !Buffer.Contains(' ');

    /// <summary>The slash filter token (e.g. "/he" while typing "/help"), or null.</summary>
    public string? SlashFilter => IsSlashFilter ? Buffer : null;

    /// <summary>
    /// True when the buffer is a "/skill" or "/skills" command (optionally followed by a
    /// space + filter text), used to swap the slash palette for a live skills autocomplete.
    /// </summary>
    public bool IsSkillsFilter
    {
        get
        {
            if (_buf.Length == 0 || _buf[0] != '/') return false;
            var head = Buffer.Split(' ', 2)[0].ToLowerInvariant();
            return head is "/skill" or "/skills";
        }
    }

    /// <summary>The text after "/skill[s]" (the fuzzy filter), or "" when none yet.</summary>
    public string SkillsFilter
    {
        get
        {
            if (!IsSkillsFilter) return "";
            var parts = Buffer.Split(' ', 2);
            return parts.Length > 1 ? parts[1] : "";
        }
    }

    /// <summary>True when the buffer is a "/resume" command (optionally + space + filter).</summary>
    public bool IsResumeFilter
    {
        get
        {
            if (_buf.Length == 0 || _buf[0] != '/') return false;
            return Buffer.Split(' ', 2)[0].ToLowerInvariant() == "/resume";
        }
    }

    /// <summary>The text after "/resume" (the fuzzy filter), or "" when none yet.</summary>
    public string ResumeFilter
    {
        get
        {
            if (!IsResumeFilter) return "";
            var parts = Buffer.Split(' ', 2);
            return parts.Length > 1 ? parts[1] : "";
        }
    }

    /// <summary>True when the buffer is a "/tools" command (optionally + space + filter),
    /// used to swap the slash palette for a live, scrollable tools catalog.</summary>
    public bool IsToolsFilter
    {
        get
        {
            if (_buf.Length == 0 || _buf[0] != '/') return false;
            return Buffer.Split(' ', 2)[0].ToLowerInvariant() == "/tools";
        }
    }

    /// <summary>The text after "/tools" (the fuzzy filter), or "" when none yet.</summary>
    public string ToolsFilter
    {
        get
        {
            if (!IsToolsFilter) return "";
            var parts = Buffer.Split(' ', 2);
            return parts.Length > 1 ? parts[1] : "";
        }
    }

    // --- @-file reference (Claude-Code style fuzzy file picker) --------------

    /// <summary>Boundaries [start,end) of the whitespace-delimited token under the cursor.</summary>
    private (int Start, int End) CurrentToken()
    {
        int start = _cursor;
        while (start > 0 && _buf[start - 1] != ' ') start--;
        int end = _cursor;
        while (end < _buf.Length && _buf[end] != ' ') end++;
        return (start, end);
    }

    /// <summary>
    /// True when the current token begins with '@' (an inline file reference being typed),
    /// used to swap the live preview for a fuzzy file picker. Works mid-buffer, not just at
    /// the start, so "fix the bug in @Utils/Foo" is recognized.
    /// </summary>
    public bool IsAtFilter
    {
        get
        {
            var (start, end) = CurrentToken();
            return end > start && _buf[start] == '@';
        }
    }

    /// <summary>The text after '@' in the current token up to the cursor (the fuzzy filter).</summary>
    public string AtFilter
    {
        get
        {
            if (!IsAtFilter) return "";
            var (start, _) = CurrentToken();
            // Filter is what has been typed between '@' and the cursor. Guard the length: when the
            // token is a bare "@" and the cursor sits ON the '@' (e.g. after the vim-Normal clamp
            // pulls it back one column on Esc), _cursor - (start + 1) is negative - clamp to 0 so
            // ToString never throws "length must be non-negative" (crashed the app on Esc).
            int len = _cursor - (start + 1);
            if (len <= 0) return "";
            return _buf.ToString(start + 1, len);
        }
    }

    /// <summary>Replace the current whitespace-delimited token with <paramref name="replacement"/>.</summary>
    public void ReplaceCurrentToken(string replacement, bool addTrailingSpace = true)
    {
        var (start, end) = CurrentToken();
        _buf.Remove(start, end - start);
        string ins = replacement + (addTrailingSpace ? " " : "");
        _buf.Insert(start, ins);
        _cursor = start + ins.Length;
    }

    /// <summary>Replace the entire buffer (used to accept a slash/skill/session completion).</summary>
    /// <summary>Insert a literal text run at the cursor (used by bracketed paste). Embedded
    /// newlines are kept as \n so a multi-line paste becomes a multi-line compose buffer; the
    /// cursor advances to the end of the inserted text. Any history-recall state is cleared.</summary>
    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string norm = text.Replace("\r\n", "\n").Replace("\r", "\n");
        _buf.Insert(_cursor, norm);
        _cursor += norm.Length;
        _historyActive = false;
    }

    public void SetBuffer(string text, int? cursor = null)
    {
        _buf.Clear();
        _buf.Append(text ?? "");
        _cursor = cursor ?? _buf.Length;
        if (_cursor < 0) _cursor = 0;
        if (_cursor > _buf.Length) _cursor = _buf.Length;
    }

    /// <summary>Reset the buffer and cursor for a fresh prompt (history is retained).</summary>
    public void Reset()
    {
        _buf.Clear();
        _cursor = 0;
        _historyIndex = _history.Count;
        _stash = "";
        _historyActive = false;
        _historyBuf = "";
        _searching = false;
        _search.Clear();
        _searchPos = -1;
        _searchStash = "";
    }

    /// <summary>Append a submitted line to history (deduping immediate repeats, ignoring blanks).</summary>
    public void Remember(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_history.Count > 0 && _history[^1] == line) { _historyIndex = _history.Count; return; }
        _history.Add(line);
        _historyIndex = _history.Count;
    }

    /// <summary>Snapshot of the history (oldest first). Test/inspection hook.</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>True while the buffer holds an unedited line recalled from history. The driver
    /// gates the command-palette arrow intercept on this so history browsing is never trapped on
    /// a recalled slash command. Goes false the instant the buffer text is edited.</summary>
    public bool RecalledFromHistory => _historyActive && _buf.ToString() == _historyBuf;

    /// <summary>True while reverse-incremental history search (Ctrl+R) is active.</summary>
    public bool IsSearching => _searching;

    /// <summary>The current reverse-search query text (what the user has typed after Ctrl+R).</summary>
    public string SearchQuery => _search.ToString();

    /// <summary>The history line currently matched by the reverse search, or null if none.</summary>
    public string? SearchMatch
        => _searchPos >= 0 && _searchPos < _history.Count ? _history[_searchPos] : null;

    /// <summary>Enter reverse-incremental history search (Ctrl+R). Captures the current buffer so
    /// a cancel can restore it.</summary>
    public void BeginReverseSearch()
    {
        if (_history.Count == 0) return;
        _searching = true;
        _searchStash = _buf.ToString();
        _search.Clear();
        _searchPos = -1;
    }

    /// <summary>Feed one key while reverse search is active and return what the driver should do.
    /// Printable keys refine the query (re-search from the newest entry), Backspace trims it,
    /// Ctrl+R steps to the next older match, Enter accepts+submits, Esc accepts to the buffer,
    /// and Ctrl+C / Ctrl+G cancel (restoring the pre-search buffer).</summary>
    public ReverseSearchSignal SearchFeed(ConsoleKeyInfo key)
    {
        bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        if (ctrl && key.Key == ConsoleKey.R) { StepSearchOlder(); return ReverseSearchSignal.Redraw; }
        if (ctrl && (key.Key == ConsoleKey.G || key.Key == ConsoleKey.C))
        {
            _searching = false;
            SetBuffer(_searchStash);
            _search.Clear(); _searchPos = -1;
            return ReverseSearchSignal.Cancel;
        }
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                AcceptSearchMatch();
                return ReverseSearchSignal.AcceptAndSubmit;
            case ConsoleKey.Escape:
                AcceptSearchMatch();
                return ReverseSearchSignal.Accept;
            case ConsoleKey.Backspace:
                if (_search.Length > 0) { _search.Remove(_search.Length - 1, 1); RefreshSearch(); }
                return ReverseSearchSignal.Redraw;
        }
        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            _search.Append(key.KeyChar);
            RefreshSearch();
            return ReverseSearchSignal.Redraw;
        }
        return ReverseSearchSignal.Redraw;   // unmapped key: stay in search, no buffer change
    }

    /// <summary>Load the current match (or, if none, the pre-search buffer) and leave search.</summary>
    private void AcceptSearchMatch()
    {
        _searching = false;
        if (_searchPos >= 0 && _searchPos < _history.Count) SetBuffer(_history[_searchPos]);
        else SetBuffer(_searchStash);
        _search.Clear(); _searchPos = -1;
    }

    /// <summary>Re-run the search from the newest history entry after the query changed.</summary>
    private void RefreshSearch() => FindFrom(_history.Count - 1);

    /// <summary>Step to the next match strictly older than the current one (Ctrl+R again).
    /// Keeps the current match if there is no older one.</summary>
    private void StepSearchOlder()
    {
        int start = _searchPos >= 0 ? _searchPos - 1 : _history.Count - 1;
        if (start < 0) return;
        int saved = _searchPos;
        if (!FindFrom(start)) _searchPos = saved;
    }

    /// <summary>Find the newest history entry at or below <paramref name="start"/> containing the
    /// query (case-insensitive). Sets <c>_searchPos</c> to the match, or -1 when none / empty query.</summary>
    private bool FindFrom(int start)
    {
        string q = _search.ToString();
        if (q.Length == 0) { _searchPos = -1; return false; }
        for (int i = Math.Min(start, _history.Count - 1); i >= 0; i--)
            if (_history[i].Contains(q, StringComparison.OrdinalIgnoreCase)) { _searchPos = i; return true; }
        _searchPos = -1;
        return false;
    }

    /// <summary>
    /// Feed a key to the editor and get the resulting signal. Printable characters insert
    /// at the cursor; recognized control keys edit/navigate; Enter submits.
    /// </summary>
    public LineEditSignal Feed(ConsoleKeyInfo key)
    {
        // Ctrl-C / Ctrl-D handling first (mode-independent).
        bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        bool alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
        if (ctrl && key.Key == ConsoleKey.C) return LineEditSignal.Cancel;
        if (ctrl && key.Key == ConsoleKey.D)
            return IsEmpty ? LineEditSignal.Eof : LineEditSignal.Ignored;

        // Esc on an empty buffer opens the transcript NAV (view) overlay directly - the
        // most discoverable "scroll back through history" gesture (same as Ctrl+G). Quitting
        // the prompt is Ctrl+C. With text in the buffer Esc is inert: the editor has no vim
        // Normal mode anymore (stripped - the accidental sub-nav mode was never used and its
        // Esc flip made typed keys vanish into motions).
        if (key.Key == ConsoleKey.Escape)
            return IsEmpty ? LineEditSignal.NavEnter : LineEditSignal.Ignored;

        // Shift+Tab cycles the active mode (reasoning effort) without typing a slash command.
        if (shift && key.Key == ConsoleKey.Tab) return LineEditSignal.ModeCycle;
        // Plain Tab accepts the top autocomplete candidate; the driver resolves which list is
        // active (command palette / skill / session / @file) and applies the replacement.
        if (!shift && !ctrl && key.Key == ConsoleKey.Tab) return LineEditSignal.Complete;

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                // Alt+Enter inserts a literal newline (multiline compose) instead of submitting,
                // so users can write multi-line messages WITHOUT the \\ delimiter or delimiter-block
                // mode. Plain Enter still submits.
                if (alt)
                {
                    _buf.Insert(_cursor, '\n');
                    _cursor++;
                    return LineEditSignal.Continue;
                }
                return LineEditSignal.Submit;

            // Ctrl+J: newline alias (terminals that can\u0027t signal Alt+Enter still get multiline).
            case ConsoleKey.J when ctrl:
                _buf.Insert(_cursor, '\n');
                _cursor++;
                return LineEditSignal.Continue;

            case ConsoleKey.Backspace:
                if (_cursor > 0) { _buf.Remove(_cursor - 1, 1); _cursor--; return LineEditSignal.Continue; }
                return LineEditSignal.Ignored;

            case ConsoleKey.Delete:
                if (_cursor < _buf.Length) { _buf.Remove(_cursor, 1); return LineEditSignal.Continue; }
                return LineEditSignal.Ignored;

            case ConsoleKey.LeftArrow:
                if (ctrl) { _cursor = PrevWord(_cursor); return LineEditSignal.Continue; }
                if (_cursor > 0) { _cursor--; return LineEditSignal.Continue; }
                return LineEditSignal.Ignored;

            case ConsoleKey.RightArrow:
                if (ctrl) { _cursor = NextWord(_cursor); return LineEditSignal.Continue; }
                if (_cursor < _buf.Length) { _cursor++; return LineEditSignal.Continue; }
                return LineEditSignal.Ignored;

            case ConsoleKey.Home: _cursor = 0; return LineEditSignal.Continue;
            case ConsoleKey.End: _cursor = _buf.Length; return LineEditSignal.Continue;

            case ConsoleKey.UpArrow: return HistoryPrev();
            case ConsoleKey.DownArrow: return HistoryNext();

            case ConsoleKey.A when ctrl: _cursor = 0; return LineEditSignal.Continue;
            case ConsoleKey.E when ctrl: _cursor = _buf.Length; return LineEditSignal.Continue;
            case ConsoleKey.U when ctrl: // kill to start of line
                if (_cursor > 0) { _buf.Remove(0, _cursor); _cursor = 0; return LineEditSignal.Continue; }
                return LineEditSignal.Ignored;
            case ConsoleKey.K when ctrl: // kill to end of line
                if (_cursor < _buf.Length) { _buf.Remove(_cursor, _buf.Length - _cursor); return LineEditSignal.Continue; }
                return LineEditSignal.Ignored;
            case ConsoleKey.W when ctrl: // delete previous word
            {
                int p = PrevWord(_cursor);
                if (p < _cursor) { _buf.Remove(p, _cursor - p); _cursor = p; return LineEditSignal.Continue; }
                return LineEditSignal.Ignored;
            }
        }

        // Printable character insertion (ignore remaining control chars).
        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            _buf.Insert(_cursor, key.KeyChar);
            _cursor++;
            return LineEditSignal.Continue;
        }

        return LineEditSignal.Ignored;
    }

    /// <summary>Index of the end of the current/next word (for 'e' / dw end bound).</summary>
    private int EndOfWord(int from)
    {
        int i = from + 1;
        while (i < _buf.Length && _buf[i] == ' ') i++;
        while (i < _buf.Length - 1 && _buf[i + 1] != ' ') i++;
        return Math.Min(i, Math.Max(0, _buf.Length - 1));
    }

    private LineEditSignal HistoryPrev()
    {
        if (_history.Count == 0) return LineEditSignal.Ignored;
        if (_historyIndex == _history.Count) _stash = Buffer; // stash the in-progress line
        if (_historyIndex > 0)
        {
            _historyIndex--;
            LoadFromHistory();
            return LineEditSignal.Continue;
        }
        return LineEditSignal.Ignored;
    }

    private LineEditSignal HistoryNext()
    {
        if (_history.Count == 0 || _historyIndex >= _history.Count) return LineEditSignal.Ignored;
        _historyIndex++;
        if (_historyIndex == _history.Count)
        {
            _buf.Clear(); _buf.Append(_stash); _cursor = _buf.Length;
            _historyActive = true; _historyBuf = _buf.ToString();
        }
        else LoadFromHistory();
        return LineEditSignal.Continue;
    }

    private void LoadFromHistory()
    {
        _buf.Clear();
        _buf.Append(_history[_historyIndex]);
        _cursor = _buf.Length;
        _historyActive = true;
        _historyBuf = _buf.ToString();
    }

    private int PrevWord(int from)
    {
        int i = from;
        while (i > 0 && _buf[i - 1] == ' ') i--;
        while (i > 0 && _buf[i - 1] != ' ') i--;
        return i;
    }

    private int NextWord(int from)
    {
        int i = from;
        while (i < _buf.Length && _buf[i] == ' ') i++;
        while (i < _buf.Length && _buf[i] != ' ') i++;
        return i;
    }
}
