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
        int want = side.Max(p => p.Columns ?? 48);
        int cap = Math.Min((int)(_term.Width * MaxSideShare), _term.Width - 1 - MinTranscriptColumns);
        return Math.Max(0, Math.Min(want, cap));
    }

    // Header row of a docked area: a tab per panel (the focused/first one highlighted).
    private string DockTabs(List<ExtensionPanels.Panel> panels, (string, string) active, bool focused)
    {
        var parts = panels.Select(p =>
        {
            bool on = (p.Extension, p.Id) == active;
            string title = Spectre.Console.Markup.Escape(p.Title);
            return on ? $"[{(focused ? "black on " + TuiComponents.Accent : TuiComponents.Accent + " bold")}] {title} [/]" : $"[{TuiComponents.Muted}] {title} [/]";
        });
        string hint = focused ? "Esc back \u00b7 F6 next" : "F6 focus";
        return string.Join("", parts) + $" [{TuiComponents.Muted}]{hint}[/]";
    }

    private (string, string) ActiveDock(List<ExtensionPanels.Panel> panels) =>
        _dockFocus is { } f && panels.Any(p => (p.Extension, p.Id) == f) ? f : (panels[0].Extension, panels[0].Id);

    /// <summary>Side pane rows (markup, <paramref name="height"/> rows of <paramref name="width"/> incl. the left rule).</summary>
    private List<string> BuildSidePane(int width, int height)
    {
        var rows = new List<string>(height);
        var panels = Docked("side");
        if (panels.Count == 0 || width < 4) return rows;
        var active = ActiveDock(panels);
        var panel = panels.First(p => (p.Extension, p.Id) == active);
        string bar = $"[{TuiComponents.Muted}]\u2502[/]";
        int inner = width - 1;
        rows.Add(bar + TuiMarkup.TruncateMarkup(DockTabs(panels, active, _dockFocus == active), inner, ""));
        rows.Add(bar + $"[{TuiComponents.Muted}]{new string('\u2500', inner)}[/]");
        var hits = new List<(int Row, int Col, int Width, int Payload)>();
        var body = DockView(panel).RenderBody(inner, height - rows.Count, hits);
        int top = rows.Count;
        foreach (var b in body) rows.Add(bar + TuiMarkup.TruncateMarkup(b, inner, ""));
        foreach (var h in hits) _dockHits.Add((top + h.Row, 1 + h.Col, h.Width, h.Payload, panel.Extension, panel.Id));
        while (rows.Count < height) rows.Add(bar);
        return rows;
    }

    /// <summary>Band rows (markup) above the footer rule, or none. Records hit rows relative to the band.</summary>
    private List<string> BuildBand(int width)
    {
        _dockHits.RemoveAll(h => h.Row < 0);   // band hits are rebuilt every paint (inline paints too)
        var panels = Docked("band");
        if (panels.Count == 0) return [];
        var active = ActiveDock(panels);
        var panel = panels.First(p => (p.Extension, p.Id) == active);
        int room = Math.Clamp(panel.Rows ?? 6, 1, Math.Max(1, Height / 3));
        var rows = new List<string> { TuiMarkup.TruncateMarkup(DockTabs(panels, active, _dockFocus == active), width, "") };
        var hits = new List<(int Row, int Col, int Width, int Payload)>();
        rows.AddRange(DockView(panel).RenderBody(width, room, hits).Select(b => TuiMarkup.TruncateMarkup(b, width, "")));
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

    /// <summary>A click on a docked control (hit payload from <see cref="MouseTargetKind.DockedPanel"/>): focus + activate.</summary>
    private void ClickDocked(HitRegion hit, int localCol)
    {
        if (hit.Tag is not { } tag || tag.Split('\u001f') is not [var ext, var id] || ExtensionPanels.Get(ext, id) is not { } p) return;
        _dockFocus = (ext, id);
        var (index, row) = ExtensionPanelView.DecodeHit(hit.Payload);
        var o = DockView(p).Click(index, row);
        if (o.IsEvent) ExtensionPanels.SendEvent(p.Extension, p.Id, o.Key, o.Type!, o.Value);
        Repaint();
    }

    /// <summary>Register the docked hit regions for this frame: side rows map 1:1; band rows via the live-band mapper.</summary>
    private void AddDockHits(MouseHitMap hits, int sideLeft, Func<int, (bool Ok, int Top)> bandRow)
    {
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
