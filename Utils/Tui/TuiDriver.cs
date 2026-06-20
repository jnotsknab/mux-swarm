using System.Text;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// The interactive live-region TUI driver (v0.11.0, Workstream G Option B). Owns a single
/// <see cref="LiveRegion"/> and a <see cref="LineEditor"/>, and is the only object that
/// talks to the real console for the TUI. It gives the renderer its Claude-Code feel:
/// a pinned footer (context meter + mode badges) and an input box at the bottom, with the
/// transcript flowing up into the terminal's NATIVE scrollback above it - no DECSTBM scroll
/// region and no alternate screen buffer, so scrollback survives and nothing is stranded.
///
/// Threading: all public methods must be called under MuxConsole.ConsoleLock (the existing
/// global serialization point), matching how every other MuxConsole writer behaves.
///
/// Teardown is guaranteed: <see cref="Shutdown"/> resets the scroll region (none),
/// re-shows the cursor, restores Ctrl-C handling, and clears the live region. It is wired
/// to AppDomain.ProcessExit / Console.CancelKeyPress by the installer.
/// </summary>
internal sealed class TuiDriver
{
    private readonly ITuiTerminal _term;
    private readonly LiveRegion _region;
    private readonly LineEditor _editor = new();

    // footer state
    private uint _tokens, _threshold;
    private bool _plan, _ultra, _psub;

    // streaming state - partial (un-newlined) tail shown live above the footer
    private readonly StringBuilder _streamTail = new();
    private bool _streaming;

    // input state - true only inside ReadLine's raw-mode loop
    private bool _inInput;
    private bool _shuttingDown;

    private static readonly (string Cmd, string Desc)[] PaletteEntries =
    {
        ("/plan", "Toggle plan mode (confirm before exec)"),
        ("/ultra", "Deep-reasoning mode (plan + max reasoning)"),
        ("/classic", "Switch to the classic line renderer"),
        ("/tui", "Switch to the live TUI renderer"),
        ("/verbose", "Toggle compact/full tool output"),
        ("/compact", "Compact current session context"),
        ("/tokens", "Show context/token usage"),
        ("/undo", "Undo the last exchange"),
        ("/retry", "Retry the last turn"),
        ("/resume", "Resume a previous session"),
        ("/swap", "Swap the active single-agent model"),
        ("/skills", "List available local skills"),
        ("/tools", "List available MCP tools"),
        ("/status", "Show system status"),
        ("/help", "Full command reference"),
        ("/qc", "Exit the agent loop"),
    };

    public TuiDriver(ITuiTerminal? term = null)
    {
        _term = term ?? new ConsoleTuiTerminal();
        _region = new LiveRegion(_term);
    }

    public int Width => Math.Max(20, _term.Width);

    /// <summary>Update the context meter / mode badges and repaint the live region.</summary>
    public void SetFooter(uint tokens, uint threshold, bool plan, bool ultra, bool psub)
    {
        _tokens = tokens; _threshold = threshold; _plan = plan; _ultra = ultra; _psub = psub;
        Repaint();
    }

    /// <summary>Commit finished transcript markup lines into native scrollback above the region.</summary>
    public void Commit(IReadOnlyList<string> markupLines)
    {
        if (markupLines.Count == 0) { Repaint(); return; }
        _region.CommitAbove(markupLines);
    }

    /// <summary>Commit a single markup line above the region.</summary>
    public void CommitLine(string markupLine) => Commit(new[] { markupLine });

    // --- streaming -----------------------------------------------------------

    public void BeginStream() { _streaming = true; _streamTail.Clear(); Repaint(); }

    /// <summary>
    /// Feed a chunk of streamed assistant text. Complete lines (split on '\n') are committed
    /// into scrollback; the trailing partial line is shown live just above the footer so the
    /// user watches it type in real time.
    /// </summary>
    public void StreamChunk(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _streamTail.Append(text);
        FlushCompleteStreamLines();
        Repaint();
    }

    public void EndStream()
    {
        if (_streamTail.Length > 0)
        {
            _region.CommitAbove(new[] { _streamTail.ToString() });
            _streamTail.Clear();
        }
        _streaming = false;
        Repaint();
    }

    private void FlushCompleteStreamLines()
    {
        string s = _streamTail.ToString();
        int nl;
        var commit = new List<string>();
        while ((nl = s.IndexOf('\n')) >= 0)
        {
            commit.Add(s[..nl].TrimEnd('\r'));
            s = s[(nl + 1)..];
        }
        if (commit.Count > 0)
        {
            _streamTail.Clear();
            _streamTail.Append(s);
            _region.CommitAbove(commit);
        }
    }

    // --- live-region composition (pure, testable) ----------------------------

    /// <summary>
    /// Build the live-region markup lines from current state: optional streaming tail, a
    /// thin rule, the footer, and (during input) the input row + slash palette. Pure given
    /// the inputs - unit-tested without a console.
    /// </summary>
    internal List<string> BuildLiveFrame(int width)
    {
        var lines = new List<string>();

        if (_streaming && _streamTail.Length > 0)
            lines.Add(_streamTail.ToString());

        int ruleW = Math.Clamp(width - 2, 8, 60);
        lines.Add($"[{TuiComponents.Border}]{new string('\u2500', ruleW)}[/]");
        lines.Add(TuiComponents.Footer(_tokens, _threshold, _plan, _ultra, _psub));

        if (_inInput)
        {
            lines.Add(TuiComponents.InputRow(_editor.Buffer));
            if (_editor.IsSlashFilter)
                lines.AddRange(TuiComponents.SlashPalette(_editor.SlashFilter, PaletteEntries));
        }
        return lines;
    }

    private void Repaint()
    {
        if (_shuttingDown) return;
        _region.SetLive(BuildLiveFrame(Width));
    }

    // --- input ---------------------------------------------------------------

    /// <summary>
    /// Read a line of input with a live-edited input box pinned at the bottom (raw-mode key
    /// loop). Returns the submitted text, or null on EOF/cancel (caller treats like Ctrl-C).
    /// The submitted line is echoed into scrollback so history reads naturally.
    /// </summary>
    public string? ReadLine()
    {
        _editor.Reset();
        _inInput = true;
        bool prevCtrlC;
        try { prevCtrlC = Console.TreatControlCAsInput; } catch { prevCtrlC = false; }
        try
        {
            try { Console.TreatControlCAsInput = true; } catch { /* ignore */ }
            Repaint();

            while (true)
            {
                ConsoleKeyInfo key;
                try { key = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException)
                {
                    // stdin not a real console (shouldn't happen in TUI) - fall back.
                    _inInput = false;
                    _region.Clear();
                    return Console.ReadLine();
                }

                var sig = _editor.Feed(key);
                switch (sig)
                {
                    case LineEditSignal.Submit:
                    {
                        string line = _editor.Buffer;
                        _editor.Remember(line);
                        _inInput = false;
                        // Erase input box, then echo the submitted line into scrollback.
                        _region.CommitAbove(new[] { $"  [{TuiComponents.Accent}]\u203a[/] [{TuiComponents.Text}]{Spectre.Console.Markup.Escape(line)}[/]" });
                        return line;
                    }
                    case LineEditSignal.Cancel:
                        _inInput = false;
                        _region.Clear();
                        return null;
                    case LineEditSignal.Eof:
                        _inInput = false;
                        _region.Clear();
                        return null;
                    case LineEditSignal.Continue:
                        Repaint();
                        break;
                    case LineEditSignal.Ignored:
                    default:
                        break;
                }
            }
        }
        finally
        {
            _inInput = false;
            try { Console.TreatControlCAsInput = prevCtrlC; } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Clear the live region and hand the terminal back cleanly (cursor shown, no residue).
    /// Called before any blocking external prompt, mode switch, or exit. Idempotent.
    /// </summary>
    public void Suspend() => _region.Clear();

    /// <summary>
    /// Full teardown for process exit / mode switch: clear the live region, show the cursor.
    /// Idempotent and exception-safe so it is safe on ProcessExit / CancelKeyPress.
    /// </summary>
    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try { _region.Clear(); } catch { /* ignore */ }
    }
}
