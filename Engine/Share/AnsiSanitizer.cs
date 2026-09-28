using System.Text;

namespace MuxSwarm.Engine.Share;

/// <summary>
/// Guest-side allowlist filter for the host's terminal output stream. The host frame carries
/// model and tool output, so a hostile repo or prompt injection could smuggle escape sequences
/// that write the guest's clipboard (OSC 52), retitle or hyperlink (OSC), or - worst - make the
/// guest's terminal TYPE a reply (DSR/DA/DECRQM queries) that would then be forwarded back as
/// input. Only the vocabulary the frame renderer needs survives: printable text, CR/LF/BS/TAB,
/// SGR, cursor positioning/erase CSIs, and the private modes for cursor visibility, auto-wrap and
/// synchronized output. Everything else (OSC/DCS/APC/PM/SOS, queries, mouse/paste/alt-screen
/// toggles, bare ESC sequences, C1 controls) is dropped. Stateful across chunks, and only ever
/// emits complete sequences, so a split at any byte cannot leave the guest mid-sequence.
/// </summary>
internal sealed class AnsiSanitizer
{
    private enum State { Ground, Esc, EscIntermediate, Csi, CsiIgnore, Str, StrEsc }

    private const int MaxCsiParams = 64;
    private const string AllowedCsiFinals = "mHfABCDEFGJKXd";
    private static readonly string[] AllowedPrivateModes = ["25", "7", "2026"];

    private State _state = State.Ground;
    private readonly StringBuilder _csi = new();

    /// <summary>
    /// Host-supplied plain text (notices, reject/bye reasons) shown outside the frame stream: drop
    /// every C0/C1 control and ESC so it can carry no escape sequence at all. Tab becomes a space.
    /// </summary>
    public static string PlainText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\t') sb.Append(' ');
            else if (c >= 0x20 && c != 0x7F && (c < 0x80 || c > 0x9F)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Reset to ground state.</summary>
    public void Reset() { _state = State.Ground; _csi.Clear(); }

    /// <summary>Filter one chunk of host output.</summary>
    public string Sanitize(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            switch (_state)
            {
                case State.Ground:
                    if (c == '\u001b') _state = State.Esc;
                    else if (c < 0x20) { if (c is '\r' or '\n' or '\b' or '\t') sb.Append(c); }
                    else if (c == 0x7f || (c >= 0x80 && c <= 0x9f)) { /* DEL / C1 controls */ }
                    else sb.Append(c);
                    break;

                case State.Esc:
                    if (c == '[') { _csi.Clear(); _state = State.Csi; }
                    else if (c is ']' or 'P' or '_' or '^' or 'X') _state = State.Str;
                    else if (c >= 0x20 && c <= 0x2f) _state = State.EscIntermediate;
                    else if (c == '\u001b') _state = State.Esc;
                    else _state = State.Ground;   // ESC 7/8/c/=/> ... all dropped
                    break;

                case State.EscIntermediate:
                    if (c >= 0x20 && c <= 0x2f) break;
                    _state = c == '\u001b' ? State.Esc : State.Ground;
                    break;

                case State.Csi:
                    if (c >= 0x30 && c <= 0x3f)
                    {
                        if (_csi.Length >= MaxCsiParams) _state = State.CsiIgnore;
                        else _csi.Append(c);
                    }
                    else if (c >= 0x20 && c <= 0x2f) _state = State.CsiIgnore;   // intermediates: never allowed
                    else if (c >= 0x40 && c <= 0x7e)
                    {
                        if (Allowed(_csi.ToString(), c)) sb.Append("\u001b[").Append(_csi).Append(c);
                        _state = State.Ground;
                    }
                    else if (c == '\u001b') _state = State.Esc;
                    else if (c == 0x18 || c == 0x1a) _state = State.Ground;   // CAN/SUB abort
                    break;

                case State.CsiIgnore:
                    if (c >= 0x40 && c <= 0x7e) _state = State.Ground;
                    else if (c == '\u001b') _state = State.Esc;
                    else if (c == 0x18 || c == 0x1a) _state = State.Ground;
                    break;

                case State.Str:
                    if (c == '\u0007' || c == 0x18 || c == 0x1a) _state = State.Ground;
                    else if (c == '\u001b') _state = State.StrEsc;
                    break;

                case State.StrEsc:
                    _state = c == '\\' ? State.Ground : c == '\u001b' ? State.StrEsc : State.Str;
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool Allowed(string param, char final)
    {
        if (param.Length > 0 && param[0] == '?')
            return final is 'h' or 'l' && Array.IndexOf(AllowedPrivateModes, param[1..]) >= 0;
        if (AllowedCsiFinals.IndexOf(final) < 0) return false;
        foreach (char p in param)
            if (!(char.IsAsciiDigit(p) || p == ';' || p == ':')) return false;
        return true;
    }
}
