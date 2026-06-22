namespace MuxSwarm.Utils.Tui;

/// <summary>Visual-selection mode for the nav cursor.</summary>
internal enum NavSelect { None, Char, Line }

/// <summary>
/// Pure, headless model for the alt-screen NAV cursor (v0.11.0 Workstream G). Holds the flat
/// list of DISPLAY lines (already markup, one screen row each), a 2D cursor (Row, Col) and an
/// optional visual-selection anchor. All movement, clamping and selected-text extraction is
/// computed here with NO console I/O, so the full cursor + selection behaviour is unit-testable.
///
/// Col is a CHARACTER index into the line's PLAIN text (markup stripped). The renderer is
/// responsible for translating (Row, Col) + selection back onto the styled display line; this
/// model only reasons about plain text so selection/yank semantics are unambiguous.
///
/// The model is deliberately decoupled from the transcript Entry list: callers feed it the
/// already-built display lines and, in parallel, a per-line owning-entry map for expand/collapse.
/// </summary>
internal sealed class NavCursorModel
{
    private List<string> _plain;          // per display line: plain text (markup stripped)
    private List<string> _markup;         // per display line: original styled markup (for color render)

    public NavCursorModel(IReadOnlyList<string> displayMarkupLines)
    {
        Load(displayMarkupLines);
    }

    /// <summary>Replace the display lines (e.g. after a card expand/collapse rebuild), clamping
    /// the cursor and any active selection anchor back into range.</summary>
    public void Load(IReadOnlyList<string> displayMarkupLines)
    {
        _markup = (displayMarkupLines ?? (IReadOnlyList<string>)System.Array.Empty<string>()).ToList();
        if (_markup.Count == 0) _markup.Add("");
        _plain = _markup.Select(TuiMarkup.Plain).ToList();
        ClampCursor();
        if (Select != NavSelect.None) ClampAnchor();
    }

    public int LineCount => _plain.Count;
    public int Row { get; private set; }
    public int Col { get; private set; }

    public NavSelect Select { get; private set; } = NavSelect.None;
    public int AnchorRow { get; private set; }
    public int AnchorCol { get; private set; }

    /// <summary>Plain text of a display line (markup stripped) - basis for column/selection math.</summary>
    public string PlainLine(int row) => _plain[Math.Clamp(row, 0, _plain.Count - 1)];

    /// <summary>Original styled markup of a display line - rendered with color in the NAV view.</summary>
    public string DisplayLine(int row) => _markup[Math.Clamp(row, 0, _markup.Count - 1)];

    private int CurLen => _plain[Row].Length;

    private void ClampCursor()
    {
        if (Row < 0) Row = 0;
        if (Row >= _plain.Count) Row = _plain.Count - 1;
        if (Col < 0) Col = 0;
        // Allow Col == length so the cursor can sit just past the last char (vim 'append' spot),
        // but never beyond.
        if (Col > _plain[Row].Length) Col = _plain[Row].Length;
    }

    private void ClampAnchor()
    {
        if (AnchorRow < 0) AnchorRow = 0;
        if (AnchorRow >= _plain.Count) AnchorRow = _plain.Count - 1;
        if (AnchorCol < 0) AnchorCol = 0;
        if (AnchorCol > _plain[AnchorRow].Length) AnchorCol = _plain[AnchorRow].Length;
    }

    // --- vertical movement: keep a desired column, clamp to each line's length ----------------
    private int _desiredCol;

    public void MoveUp(int n = 1)    { Row = Math.Max(0, Row - n);                  Col = Math.Min(_desiredCol, CurLen); }
    public void MoveDown(int n = 1)  { Row = Math.Min(_plain.Count - 1, Row + n);   Col = Math.Min(_desiredCol, CurLen); }

    // --- horizontal movement: update the desired column ---------------------------------------
    public void MoveLeft(int n = 1)  { Col = Math.Max(0, Col - n);            _desiredCol = Col; }
    public void MoveRight(int n = 1) { Col = Math.Min(CurLen, Col + n);       _desiredCol = Col; }

    public void LineStart() { Col = 0;       _desiredCol = 0; }
    public void LineEnd()   { Col = CurLen;  _desiredCol = Col; }
    public void Top()       { Row = 0;                Col = Math.Min(_desiredCol, CurLen); }
    public void Bottom()    { Row = _plain.Count - 1; Col = Math.Min(_desiredCol, CurLen); }

    /// <summary>Place the cursor directly on a display-line index (clamped), keeping the desired
    /// column. Used to restore a saved cursor or re-anchor after an expand/collapse rebuild
    /// without the Top()-then-step "snap to top" jump.</summary>
    public void SeekRow(int row)
    {
        Row = Math.Clamp(row, 0, _plain.Count - 1);
        Col = Math.Min(_desiredCol, CurLen);
    }

    public void Page(int delta)
    {
        Row = Math.Clamp(Row + delta, 0, _plain.Count - 1);
        Col = Math.Min(_desiredCol, CurLen);
    }

    // --- selection ----------------------------------------------------------------------------

    /// <summary>Start (or restart) a visual selection of the given kind, anchored at the cursor.
    /// Toggling the same kind again clears the selection (vim-like).</summary>
    public void ToggleSelect(NavSelect kind)
    {
        if (kind == NavSelect.None) { Select = NavSelect.None; return; }
        if (Select == kind) { Select = NavSelect.None; return; }
        Select = kind;
        AnchorRow = Row; AnchorCol = Col;
    }

    public void ClearSelect() => Select = NavSelect.None;

    /// <summary>True if (row,col) lies within the active selection (for highlight rendering).
    /// Char selection is an inclusive character range across lines; Line selection covers whole
    /// lines between the anchor row and the cursor row.</summary>
    public bool InSelection(int row, int col)
    {
        if (Select == NavSelect.None) return false;
        var (loR, loC, hiR, hiC) = OrderedSpan();
        if (Select == NavSelect.Line) return row >= loR && row <= hiR;
        // Char: compare on a (row,col) ordering.
        if (row < loR || row > hiR) return false;
        if (row == loR && row == hiR) return col >= loC && col < hiC;
        if (row == loR) return col >= loC;
        if (row == hiR) return col < hiC;
        return true; // fully-covered middle line
    }

    /// <summary>Selection bounds normalised so (loR,loC) precedes (hiR,hiC). For Char the hi
    /// column is EXCLUSIVE-extended by one past the cursor so the char under the cursor is
    /// included in the yank (vim inclusive visual).</summary>
    private (int loR, int loC, int hiR, int hiC) OrderedSpan()
    {
        int aR = AnchorRow, aC = AnchorCol, bR = Row, bC = Col;
        bool anchorFirst = aR < bR || (aR == bR && aC <= bC);
        var (loR, loC, hiR, hiC) = anchorFirst ? (aR, aC, bR, bC) : (bR, bC, aR, aC);
        if (Select == NavSelect.Char) hiC = Math.Min(_plain[hiR].Length, hiC + 1); // inclusive
        return (loR, loC, hiR, hiC);
    }

    /// <summary>The plain text currently selected, ready for the clipboard. Empty when nothing is
    /// selected. Char selection joins partial lines; Line selection yields whole lines.</summary>
    public string SelectedText()
    {
        if (Select == NavSelect.None) return "";
        var (loR, loC, hiR, hiC) = OrderedSpan();
        if (Select == NavSelect.Line)
            return string.Join("\n", Enumerable.Range(loR, hiR - loR + 1).Select(r => _plain[r]));
        if (loR == hiR)
            return _plain[loR].Substring(loC, Math.Max(0, hiC - loC));
        var parts = new List<string> { _plain[loR].Substring(Math.Min(loC, _plain[loR].Length)) };
        for (int r = loR + 1; r < hiR; r++) parts.Add(_plain[r]);
        parts.Add(_plain[hiR].Substring(0, Math.Min(hiC, _plain[hiR].Length)));
        return string.Join("\n", parts);
    }
}
