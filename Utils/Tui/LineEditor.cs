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
    /// <summary>Key had no effect (e.g. unmapped control); no repaint needed.</summary>
    Ignored
}

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

    /// <summary>Reset the buffer and cursor for a fresh prompt (history is retained).</summary>
    public void Reset()
    {
        _buf.Clear();
        _cursor = 0;
        _historyIndex = _history.Count;
        _stash = "";
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

    /// <summary>
    /// Feed a key to the editor and get the resulting signal. Printable characters insert
    /// at the cursor; recognized control keys edit/navigate; Enter submits.
    /// </summary>
    public LineEditSignal Feed(ConsoleKeyInfo key)
    {
        // Ctrl-C / Ctrl-D / Esc handling first.
        bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        if (ctrl && key.Key == ConsoleKey.C) return LineEditSignal.Cancel;
        if (key.Key == ConsoleKey.Escape) return LineEditSignal.Cancel;
        if (ctrl && key.Key == ConsoleKey.D) return IsEmpty ? LineEditSignal.Eof : LineEditSignal.Ignored;
        // Shift+Tab cycles the active mode (reasoning effort) without typing a slash command.
        if (shift && key.Key == ConsoleKey.Tab) return LineEditSignal.ModeCycle;

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                return LineEditSignal.Submit;

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
        }
        else LoadFromHistory();
        return LineEditSignal.Continue;
    }

    private void LoadFromHistory()
    {
        _buf.Clear();
        _buf.Append(_history[_historyIndex]);
        _cursor = _buf.Length;
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
