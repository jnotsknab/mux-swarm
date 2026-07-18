namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Stateful parser for SGR mouse reports after a terminal/.NET input driver has stripped any
/// amount of the <c>ESC[&lt;</c> prefix. A report body is <c>b;x;yM</c> or <c>b;x;ym</c>.
/// </summary>
internal sealed class MouseSgrParser
{
    private enum ParseState { Idle, AfterEscape, AfterBracket, Body }

    private ParseState _state;
    private readonly int[] _numbers = new int[3];
    private int _numberIndex;
    private int _current;
    private bool _hasDigit;

    public bool InProgress => _state != ParseState.Idle;

    /// <summary>Characters that can survive as orphaned bytes of a torn mouse report. The greater-
    /// than sign is included for terminal/input-driver variants that expose it while splitting a
    /// report, even though canonical SGR reports use a less-than opener.</summary>
    public static bool IsFragmentChar(char ch)
        => ch == '\u001b' || ch == '[' || ch == '<' || ch == '>' || ch == ';'
            || ch == 'M' || ch == 'm' || (ch >= '0' && ch <= '9');

    public void Reset()
    {
        _state = ParseState.Idle;
        _numberIndex = 0;
        _current = 0;
        _hasDigit = false;
    }

    /// <summary>
    /// Consume one input character. Returns true when the character belongs to a possible mouse
    /// report and must not reach the line editor. A completed valid report is returned through
    /// <paramref name="mouseEvent"/>. Invalid partial reports are discarded and reset.
    /// </summary>
    /// <param name="allowStart">Allow an idle parser to accept a new prefix. The driver sets this
    /// only during the short post-mouse gate; an already-started parse continues after the gate.</param>
    public bool Feed(char ch, bool allowStart, out (int Button, int X, int Y, bool Release)? mouseEvent)
    {
        mouseEvent = null;

        switch (_state)
        {
            case ParseState.Idle:
                if (!allowStart) return false;
                if (ch == '') { Begin(ParseState.AfterEscape); return true; }
                if (ch == '[') { Begin(ParseState.AfterBracket); return true; }
                if (ch == '<') { Begin(ParseState.Body); return true; }
                return false;

            case ParseState.AfterEscape:
                if (ch == '[') { _state = ParseState.AfterBracket; return true; }
                if (ch == '') { Begin(ParseState.AfterEscape); return true; }
                if (ch == '<') { Begin(ParseState.Body); return true; }
                Reset();
                return false;

            case ParseState.AfterBracket:
                if (ch == '<') { _state = ParseState.Body; return true; }
                if (ch == '') { Begin(ParseState.AfterEscape); return true; }
                if (ch == '[') { Begin(ParseState.AfterBracket); return true; }
                Reset();
                return false;

            case ParseState.Body:
                // A fresh prefix can interrupt a torn body during a wheel burst. Restart instead
                // of discarding the opener; the following report can then complete normally.
                if (ch == '\u001b') { Begin(ParseState.AfterEscape); return true; }
                if (ch == '[') { Begin(ParseState.AfterBracket); return true; }
                if (ch == '<') { Begin(ParseState.Body); return true; }

                if (ch >= '0' && ch <= '9')
                {
                    _hasDigit = true;
                    _current = Math.Min(1_000_000, (_current * 10) + (ch - '0'));
                    return true;
                }

                if (ch == ';')
                {
                    if (!_hasDigit || _numberIndex >= 2) { Reset(); return true; }
                    _numbers[_numberIndex++] = _current;
                    _current = 0;
                    _hasDigit = false;
                    return true;
                }

                if (ch is 'M' or 'm')
                {
                    if (_hasDigit && _numberIndex == 2)
                    {
                        _numbers[2] = _current;
                        mouseEvent = (_numbers[0], _numbers[1], _numbers[2], ch == 'm');
                    }
                    Reset();
                    return true;
                }

                Reset();
                return false;

            default:
                Reset();
                return false;
        }
    }

    private void Begin(ParseState state)
    {
        Reset();
        _state = state;
    }
}
