using MuxSwarm.Engine.Extensions;

namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Docked extension panels (<c>placement</c> side / band / auto): drawn next to the transcript or above the
/// prompt on every paint, never blocking input. Keys go to the prompt until the user focuses a docked panel
/// with F6 (or a click); then keys and clicks drive it like the full-screen view and Esc hands focus back.
/// Side panes need the frame engine (a fixed screen to split); elsewhere they show as a band.
/// </summary>
internal sealed partial class TuiDriver
{
    /// <summary>Terminal columns from which <c>auto</c> docks beside the transcript (else a band).</summary>
    internal const int AutoSideColumns = 144;

    // Narrowest transcript a side pane may leave; widest pane share of the terminal.
    private const int MinTranscriptColumns = 60;
    private const double MaxSideShare = 0.45;

    private readonly Dictionary<(string, string), ExtensionPanelView> _dockViews = new();
    private (string Ext, string Id)? _dockFocus;   // the docked panel the keyboard is in, or null
    private int _sideCols;                          // side pane width this frame (0 = none)
    private List<(int Row, int Col, int Width, int Payload, string Ext, string Id)> _dockHits = new();
    private int _bandLine = -1;                     // live-band line index of the first band row
    private List<string> _sideRows = new();
    private int? _sideWidth, _bandHeight;           // user-dragged sizes (this session), else the panel's ask
    private bool _resizingDock;                     // a sash drag is in progress (the divider/rule shows it)
    private List<(int Col, int Width, string Ext, string Id)> _bandTabs = new(), _sideTabs = new();
    private (int Rows, string Ext, string Id) _bandBody;   // band body height + its panel (click-to-focus backdrop)
    private int _sashPressRow, _sashPressRoom;      // band drag origin

    /// <summary>Where <paramref name="p"/> shows at this terminal size: screen, side or band.</summary>
    internal string DockedPlacement(ExtensionPanels.Panel p)
    {
        if (p.Placement == "screen") return "screen";
        bool sideFits = _engineFrame && _term.Width >= MinTranscriptColumns + 30;
        if (p.Placement == "side") return sideFits ? "side" : "band";
        return p.Placement == "auto" && sideFits && _term.Width >= AutoSideColumns ? "side" : "band";
    }

    private ExtensionPanelView DockView(ExtensionPanels.Panel p)
    {
        if (!_dockViews.TryGetValue((p.Extension, p.Id), out var v)) _dockViews[(p.Extension, p.Id)] = v = new ExtensionPanelView(p);
        else if (v.Panel.Version != p.Version) v.Update(p);
        v.ShowFocus = _dockFocus == (p.Extension, p.Id);
        return v;
    }

    private List<ExtensionPanels.Panel> Docked(string where)
    {
        var all = ExtensionPanels.All();
        foreach (var gone in _dockViews.Keys.Where(k => !all.Any(p => (p.Extension, p.Id) == k)).ToList()) _dockViews.Remove(gone);
        if (_dockFocus is { } f && !all.Any(p => (p.Extension, p.Id) == f && DockedPlacement(p) != "screen")) _dockFocus = null;
        return all.Where(p => DockedPlacement(p) == where).ToList();
    }

    /// <summary>Side pane width for this frame (0 when no side panel is open). Sets the transcript width.</summary>
    private int SidePaneColumns()
    {
        var side = Docked("side");
        if (side.Count == 0) return 0;
        int want = _sideWidth ?? side.Max(p => p.Columns ?? 48);
        return Math.Clamp(want, Math.Min(MinSideColumns, SideCap()), SideCap());
    }

    private const int MinSideColumns = 24;
    private int SideCap() => Math.Max(0, Math.Min((int)(_term.Width * MaxSideShare), _term.Width - 1 - MinTranscriptColumns));

    /// <summary>Band body rows: the dragged height (up to half the screen), else the panel's ask (default 6, up to a quarter).</summary>
    private int BandRoom(ExtensionPanels.Panel p) => _bandHeight is { } dragged
        ? Math.Clamp(dragged, 1, Math.Max(1, Height / 2))
        : Math.Clamp(p.Rows ?? 6, 1, Math.Max(1, Height / 4));

    // Header row of a docked area: a tab per panel (the focused/first one highlighted). Tab extents
    // (1-based col, width) go to <paramref name="tabs"/> so a click can switch panels. With a
    // <paramref name="rule"/> width the row is a rule (tabs set in it) that separates the band from the transcript.
    private string DockTabs(List<ExtensionPanels.Panel> panels, (string, string) active, bool focused,
        List<(int Col, int Width, string Ext, string Id)> tabs, int rule = 0)
    {
        tabs.Clear();
        string m = TuiComponents.Muted;
        var sb = new System.Text.StringBuilder(rule > 0 ? $"[{(_resizingDock ? TuiComponents.Accent : m)}]\u2500\u2500[/]" : " ");
        int col = rule > 0 ? 3 : 2;
        foreach (var p in panels)
        {
            bool on = (p.Extension, p.Id) == active;
            string title = Spectre.Console.Markup.Escape(p.Title);
            sb.Append(on ? $"[{(focused ? "black on " + TuiComponents.Accent : TuiComponents.Accent + " bold")}] {title} [/]" : $"[{m}] {title} [/]");
            int w = TuiMarkup.Width(p.Title) + 2;
            tabs.Add((col, w, p.Extension, p.Id));
            col += w;
        }
        string hint = focused ? "Esc back \u00b7 F6 next" : "F6 focus";
        sb.Append($" [{m}]{hint}[/] ");
        if (rule > 0)
            sb.Append($"[{(_resizingDock ? TuiComponents.Accent : m)}]{new string('\u2500', Math.Max(0, rule - (col - 1) - TuiMarkup.Width(hint) - 2))}[/]");
        return sb.ToString();
    }

    private (string, string) ActiveDock(List<ExtensionPanels.Panel> panels) =>
        _dockFocus is { } f && panels.Any(p => (p.Extension, p.Id) == f) ? f : (panels[0].Extension, panels[0].Id);

    /// <summary>
    /// Side pane rows (markup, <paramref name="height"/> rows of <paramref name="width"/>). The divider to its
    /// left is not part of the pane: it is the frame's gutter column, shared with the transcript scrollbar.
    /// </summary>
    private List<string> BuildSidePane(int width, int height)
    {
        var rows = new List<string>(height);
        var panels = Docked("side");
        if (panels.Count == 0 || width < 4) return rows;
        var active = ActiveDock(panels);
        var panel = panels.First(p => (p.Extension, p.Id) == active);
        int inner = width - 1;   // keep the screen's last column blank: a right margin, and safe from the row's EL clear
        rows.Add(TuiMarkup.TruncateMarkup(DockTabs(panels, active, _dockFocus == active, _sideTabs), inner, ""));
        rows.Add($"[{TuiComponents.Muted}]{new string('\u2500', inner)}[/]");
        var hits = new List<(int Row, int Col, int Width, int Payload)>();
        var view = DockView(panel);
        view.Compact = false;
        var body = view.RenderBody(inner, height - rows.Count, hits);
        int top = rows.Count;
        foreach (var b in body) rows.Add(TuiMarkup.TruncateMarkup(b, inner, ""));
        foreach (var h in hits) _dockHits.Add((top + h.Row, h.Col, h.Width, h.Payload, panel.Extension, panel.Id));
        while (rows.Count < height) rows.Add("");
        return rows;
    }

    /// <summary>Divider cell for a frame row when a side pane is open: the transcript scrollbar where it has
    /// a track, else a plain rule (accent while being dragged).</summary>
    private string DividerCell(int row) =>
        _frameScrollBar.Visible && row < _frameScrollBar.TrackRows && !_resizingDock
            ? _frameScrollBar.Cell(row)
            : TuiMarkup.ToAnsi($"[{RegionStyle.Color(_resizingDock ? "divider.activeFg" : "divider.fg")}]{RegionStyle.Glyph("divider.glyph")}[/]");

    /// <summary>Band rows (markup) above the footer rule, or none. Records hit rows relative to the band.</summary>
    private List<string> BuildBand(int width)
    {
        _dockHits.RemoveAll(h => h.Row < 0);   // band hits are rebuilt every paint (inline paints too)
        _bandTabs.Clear(); _bandBody = default;
        var panels = Docked("band");
        if (panels.Count == 0) return [];
        var active = ActiveDock(panels);
        var panel = panels.First(p => (p.Extension, p.Id) == active);
        var rows = new List<string> { TuiMarkup.TruncateMarkup(DockTabs(panels, active, _dockFocus == active, _bandTabs, width), width, "") };
        var hits = new List<(int Row, int Col, int Width, int Payload)>();
        var view = DockView(panel);
        view.Compact = true;   // a strip above the prompt: no blank spacer rows between items
        rows.AddRange(view.RenderBody(width, BandRoom(panel), hits).Select(b => TuiMarkup.TruncateMarkup(b, width, "")));
        _bandBody = (rows.Count - 1, panel.Extension, panel.Id);
        foreach (var h in hits) _dockHits.Add((-(1 + h.Row) - 1, h.Col, h.Width, h.Payload, panel.Extension, panel.Id));   // negative = band-relative
        return rows;
    }

    /// <summary>F6 / Shift+F6: cycle keyboard focus through docked panels (then back to the prompt). True when handled.</summary>
    internal bool CycleDockFocus(bool back)
    {
        var docked = ExtensionPanels.All().Where(p => DockedPlacement(p) != "screen").Select(p => (p.Extension, p.Id)).ToList();
        if (docked.Count == 0) { if (_dockFocus is null) return false; _dockFocus = null; return true; }
        int i = _dockFocus is { } f ? docked.IndexOf(f) : -1;
        int next = back ? (i < 0 ? docked.Count - 1 : i - 1) : i + 1;
        _dockFocus = next >= 0 && next < docked.Count ? docked[next] : null;
        if (_dockFocus is { } nf && ExtensionPanels.Get(nf.Item1, nf.Item2) is { } np) DockView(np).FocusForEntry();
        return true;
    }

    /// <summary>True while a docked panel has the keyboard.</summary>
    internal bool DockFocused => _dockFocus is not null;

    /// <summary>
    /// Route a key while a docked panel has focus: Esc returns to the prompt, F6 cycles, everything else
    /// drives the panel (events go to the extension). Returns true (the key never reaches the prompt).
    /// </summary>
    internal bool HandleDockKey(ConsoleKeyInfo key)
    {
        if (_dockFocus is not { } f) return false;
        if (key.Key == ConsoleKey.F6) { CycleDockFocus(key.Modifiers.HasFlag(ConsoleModifiers.Shift)); return true; }
        if (ExtensionPanels.Get(f.Ext, f.Id) is not { } p) { _dockFocus = null; return true; }
        var view = DockView(p);
        var o = view.Handle(key, 5);
        if (o.Close) { _dockFocus = null; return true; }   // Esc in a docked panel = back to the prompt (the panel stays)
        if (o.IsEvent) ExtensionPanels.SendEvent(p.Extension, p.Id, o.Key, o.Type!, o.Value);
        return true;
    }

    /// <summary>
    /// Mid-turn key from the input listener: F6 cycles docked focus, and while a docked panel is focused
    /// it gets the key (Esc returns focus instead of cancelling the turn). True when consumed.
    /// </summary>
    internal bool TryDockKeyMidTurn(ConsoleKeyInfo key)
    {
        bool used = key.Key == ConsoleKey.F6 ? CycleDockFocus(key.Modifiers.HasFlag(ConsoleModifiers.Shift)) : HandleDockKey(key);
        if (used) Repaint();
        return used;
    }

    /// <summary>A paste while a docked panel has focus goes to its focused input.</summary>
    internal bool HandleDockPaste(string text)
    {
        if (_dockFocus is not { } f || ExtensionPanels.Get(f.Ext, f.Id) is not { } p) return false;
        DockView(p).Paste(text);
        return true;
    }

    /// <summary>
    /// A click on a docked panel (<see cref="MouseTargetKind.DockedPanel"/>, or its tab): focus that panel, and
    /// activate the control under the pointer. Payload -1 = the panel backdrop or a tab (focus only).
    /// </summary>
    private void ClickDocked(HitRegion hit)
    {
        if (hit.Tag is not { } tag || tag.Split('\u001f') is not [var ext, var id] || ExtensionPanels.Get(ext, id) is not { } p) return;
        bool entering = _dockFocus != (ext, id);
        _dockFocus = (ext, id);
        var view = DockView(p);
        if (hit.Payload < 0) { if (entering) view.FocusForEntry(); Repaint(); return; }
        var (index, row) = ExtensionPanelView.DecodeHit(hit.Payload);
        var o = view.Click(index, row);
        if (o.IsEvent) ExtensionPanels.SendEvent(p.Extension, p.Id, o.Key, o.Type!, o.Value);
        Repaint();
    }

    private static bool IsDockTarget(MouseTargetKind k) => k is MouseTargetKind.DockedPanel or MouseTargetKind.DockSash;

    /// <summary>A press outside every docked panel hands the keyboard back to the prompt (the click-away
    /// equivalent of Esc). Scrollbar presses keep focus where it is.</summary>
    private void UnfocusDockOnPress(MouseTargetKind? kind)
    {
        if (_dockFocus is null || kind is { } k && (IsDockTarget(k) || k is MouseTargetKind.ScrollBarRail or MouseTargetKind.ScrollBarThumb)) return;
        _dockFocus = null;
        Repaint();
    }

    /// <summary>Start a sash drag (payload 0 = side divider, 1 = band rule).</summary>
    private void BeginSash(HitRegion hit, int row)
    {
        _resizingDock = true;
        _sashPressRow = row;
        _sashPressRoom = _bandBody.Rows;   // the rows actually shown (content may be shorter than the room)
        Repaint();
    }

    /// <summary>Sash drag: the side divider follows the pointer column; the band rule follows the pointer row
    /// (up = taller). Sizes hold for the session and are clamped so the transcript keeps its minimum.</summary>
    private bool DragSash(HitRegion hit, int row, int col)
    {
        if (hit.Payload == 0)
        {
            int want = Math.Clamp(_term.Width - col, Math.Min(MinSideColumns, SideCap()), SideCap());
            if (want == _sideWidth) return false;
            _sideWidth = want;
            return true;
        }
        int rows = Math.Clamp(_sashPressRoom + (_sashPressRow - row), 1, Math.Max(1, Height / 2));
        if (rows == _bandHeight) return false;
        _bandHeight = rows;
        return true;
    }

    private void EndSash() { _resizingDock = false; Repaint(); }

    /// <summary>
    /// Register the docked hit regions for this frame: side rows map 1:1 (the pane starts at <paramref name="sideLeft"/>,
    /// the divider sits just left of it); band rows via the live-band mapper (row 0 = the band's rule row).
    /// Order matters (later = on top): backdrop, then sash, then tabs and controls.
    /// </summary>
    private void AddDockHits(MouseHitMap hits, int sideLeft, int height, Func<int, (bool Ok, int Top)> bandRow)
    {
        if (_sideCols > 0 && Docked("side") is { Count: > 0 } side)
        {
            var (ext, id) = ActiveDock(side);
            hits.Add(new HitRegion(1, sideLeft, height, _sideCols, MouseTargetKind.DockedPanel, -1, ext + "\u001f" + id));
            // The divider (where the scrollbar has no track) and the pane's blank first column are the sash.
            int track = _frameScrollBar.Visible ? _frameScrollBar.TrackRows : 0;
            hits.Add(new HitRegion(1, sideLeft, height, 1, MouseTargetKind.DockSash, 0));
            if (height > track) hits.Add(new HitRegion(1 + track, sideLeft - 1, height - track, 1, MouseTargetKind.DockSash, 0));
            foreach (var t in _sideTabs) hits.Add(new HitRegion(1, sideLeft + t.Col - 1, 1, t.Width, MouseTargetKind.DockedPanel, -1, t.Ext + "\u001f" + t.Id));
        }
        if (_bandBody.Ext is not null && bandRow(-1) is (true, var ruleTop))
        {
            if (_bandBody.Rows > 0 && bandRow(0) is (true, var bodyTop))
                hits.Add(new HitRegion(bodyTop, 1, _bandBody.Rows, Width, MouseTargetKind.DockedPanel, -1, _bandBody.Ext + "\u001f" + _bandBody.Id));
            hits.Add(new HitRegion(ruleTop, 1, 1, Width, MouseTargetKind.DockSash, 1));
            foreach (var t in _bandTabs) hits.Add(new HitRegion(ruleTop, t.Col, 1, t.Width, MouseTargetKind.DockedPanel, -1, t.Ext + "\u001f" + t.Id));
        }
        foreach (var h in _dockHits)
        {
            string tag = h.Ext + "\u001f" + h.Id;
            if (h.Row >= 0)
                hits.Add(new HitRegion(h.Row + 1, sideLeft + h.Col - 1, 1, h.Width, MouseTargetKind.DockedPanel, h.Payload, tag));
            else if (bandRow(-h.Row - 2) is (true, var top))
                hits.Add(new HitRegion(top, h.Col, 1, h.Width, MouseTargetKind.DockedPanel, h.Payload, tag));
        }
    }
}
