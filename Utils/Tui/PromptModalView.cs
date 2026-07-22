namespace MuxSwarm.Utils.Tui;

/// <summary>Outcome of an in-frame prompt modal run. <see cref="Cancelled"/> when the user
/// backed out (Esc); otherwise <see cref="Text"/> (Text kind), <see cref="Index"/> (Select
/// kind) or <see cref="Checked"/> (MultiSelect kind) carries the answer.</summary>
internal sealed record PromptModalResult(bool Cancelled, string? Text, int Index, IReadOnlyList<int>? Checked);

/// <summary>
/// The IN-FRAME prompt modal (v0.12.4): the frame engine's replacement for the alt-screen
/// flip that blocking Spectre prompts (ask_user / confirm / select / text) used to cause.
/// Follows the AgentView/JobView/WorkflowView pattern: PURE - holds prompt state and
/// produces markup rows from it, no console I/O, fully unit-testable. The driver renders it
/// inside the live band (transcript stays visible ABOVE it, footer stays pinned BELOW it)
/// and feeds it keys from the single input plane, so the alternate screen is never left.
/// The question body gets a SCROLLABLE viewport (tail-anchored: the actionable ask sits at
/// the end) so a plan longer than the screen is navigable with PgUp/PgDn/wheel instead of
/// being lost to a buffer flip.
/// </summary>
internal sealed class PromptModalView
{
    internal enum Kind { Select, MultiSelect, Text }

    private Kind _kind;
    private string _question = "";
    private List<string> _choices = new();
    private int _sel;
    private readonly HashSet<int> _checked = new();
    private int _scroll;                       // body rows scrolled UP from the tail
    private readonly System.Text.StringBuilder _input = new();
    private string? _default;
    private bool _secret;
    private bool _open;

    private const int MaxChoiceRows = 10;

    public bool IsOpen => _open;
    public int Sel => _sel;
    public string InputText => _input.ToString();
    public IReadOnlyCollection<int> CheckedIndices => _checked;

    public void Open(Kind kind, string question, IReadOnlyList<string>? choices, string? defaultValue, bool secret, int initialSel = 0)
    {
        _kind = kind;
        _question = question ?? "";
        _choices = choices is null ? new List<string>() : new List<string>(choices);
        _sel = Math.Clamp(initialSel, 0, Math.Max(0, _choices.Count - 1));
        _checked.Clear();
        _scroll = 0;
        _input.Clear();
        _default = defaultValue;
        _secret = secret;
        _open = true;
    }

    public void Close() => _open = false;

    public void MoveSel(int delta)
    {
        if (_choices.Count == 0) return;
        _sel = Math.Clamp(_sel + delta, 0, _choices.Count - 1);
    }

    public void ToggleChecked()
    {
        if (_choices.Count == 0) return;
        if (!_checked.Remove(_sel)) _checked.Add(_sel);
    }

    /// <summary>Scroll the question-body viewport. Positive = up (towards the start of the
    /// question), negative = down (towards the ask). Clamped at render time.</summary>
    public void ScrollBody(int delta) => _scroll = Math.Max(0, _scroll + delta);

    public void InputAppend(char c) { if (!char.IsControl(c)) _input.Append(c); }
    public void InputAppend(string s)
    {
        foreach (var c in s ?? "") InputAppend(c == '\n' || c == '\r' || c == '\t' ? ' ' : c);
    }
    public void InputBackspace() { if (_input.Length > 0) _input.Length -= 1; }

    /// <summary>
    /// Render the modal: header, scrollable question body, then the choice list (selection
    /// window follows the chevron; MultiSelect adds check boxes) or the input row, then a
    /// kind-appropriate key hint. Bounded to the visible height so the footer below never
    /// leaves the screen; every row is clamped to the width so nothing soft-wraps.
    /// </summary>
    public List<string> Render(int width, int height)
    {
        var rows = new List<string>();
        int w = Math.Max(20, width);

        // Interactive rows below the body: choices (windowed) or the input row, + hint.
        int choiceRows = _kind == Kind.Text ? 1 : Math.Min(_choices.Count, MaxChoiceRows)
            + (_choices.Count > MaxChoiceRows ? 1 : 0);
        // Height budget: leave slack for the rule/footer band the driver renders beneath.
        int budget = height > 0 ? Math.Max(6, height - 6) : int.MaxValue / 2;
        int bodyMax = Math.Max(1, budget - choiceRows - 3);   // title + spacer + hint

        // Question body: markdown per line, wrapped, tail-anchored viewport.
        var body = new List<string>();
        foreach (var raw in _question.Replace("\r\n", "\n").Split('\n'))
            body.AddRange(TuiMarkup.WrapMarkup(TuiMarkdown.ToMarkup(raw), Math.Max(12, w - 4)));
        while (body.Count > 0 && string.IsNullOrWhiteSpace(TuiMarkup.Plain(body[^1]))) body.RemoveAt(body.Count - 1);

        int visN = Math.Min(body.Count, bodyMax);
        int maxScroll = Math.Max(0, body.Count - visN);
        if (_scroll > maxScroll) _scroll = maxScroll;
        int end = body.Count - _scroll;
        int startIdx = Math.Max(0, end - visN);

        string scrollHint = maxScroll > 0 ? $" [{TuiComponents.Dim}]\u00b7 pgup/pgdn scroll[/]" : "";
        rows.Add($"  [{TuiComponents.Accent}]\u25b8 input requested[/]{scrollHint}");
        if (startIdx > 0) rows.Add($"    [{TuiComponents.Dim}]\u2191 {startIdx} earlier line(s)[/]");
        for (int i = startIdx; i < end; i++) rows.Add("    " + body[i]);
        if (end < body.Count) rows.Add($"    [{TuiComponents.Dim}]\u2193 {body.Count - end} more line(s)[/]");
        rows.Add("");

        if (_kind == Kind.Text)
        {
            string shown = _secret ? new string('\u2022', _input.Length) : Esc(InputText);
            string hint = _input.Length == 0 && !string.IsNullOrEmpty(_default)
                ? $" [{TuiComponents.Dim}](default: {Esc(_default!)})[/]" : "";
            rows.Add($"  [{TuiComponents.Accent}]\u203a[/] {shown}[black on #E0E0E0] [/]{hint}");
            rows.Add($"  [{TuiComponents.Dim}]enter submit \u00b7 esc cancel[/]");
        }
        else
        {
            // Selection window follows the chevron, WorkflowView-style.
            int winN = Math.Min(_choices.Count, MaxChoiceRows);
            int off = Math.Clamp(_sel - winN + 1, 0, Math.Max(0, _choices.Count - winN));
            if (_sel < off) off = _sel;
            for (int i = off; i < Math.Min(off + winN, _choices.Count); i++)
            {
                bool isSel = i == _sel;
                string chev = isSel ? $"[{TuiComponents.Accent}]\u203a[/]" : " ";
                string box = _kind == Kind.MultiSelect
                    ? (_checked.Contains(i) ? $"[{TuiComponents.Ok}]\u25c9[/] " : $"[{TuiComponents.Dim}]\u25cb[/] ")
                    : "";
                rows.Add($"  {chev} {box}[{(isSel ? TuiComponents.Text : TuiComponents.Muted)}]{Esc(_choices[i])}[/]");
            }
            if (_choices.Count > winN)
                rows.Add($"    [{TuiComponents.Dim}]{off + 1}-{Math.Min(off + winN, _choices.Count)} of {_choices.Count}[/]");
            rows.Add(_kind == Kind.MultiSelect
                ? $"  [{TuiComponents.Dim}]\u2191\u2193 move \u00b7 space toggle \u00b7 enter accept \u00b7 esc cancel[/]"
                : $"  [{TuiComponents.Dim}]\u2191\u2193 move \u00b7 enter select \u00b7 esc cancel[/]");
        }

        for (int i = 0; i < rows.Count; i++)
            if (TuiMarkup.MarkupWidth(rows[i]) > w)
                rows[i] = TuiMarkup.TruncateMarkup(rows[i], w);
        return rows;
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");
}
