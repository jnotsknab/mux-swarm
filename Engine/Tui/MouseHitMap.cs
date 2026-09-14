namespace MuxSwarm.Engine.Tui;

/// <summary>Semantic identity of a clickable region in a presented frame. The payload field of a
/// <see cref="HitRegion"/> disambiguates instances (item index, entry sequence, lane id); mouse is
/// an ALIAS layer, so every kind resolves a click to the existing keyboard/command equivalent -
/// never to a mouse-only feature.</summary>
internal enum MouseTargetKind
{
    ScrollBarRail,
    ScrollBarThumb,
    TranscriptEntry,        // payload: transcript entry sequence
    PickerItem,             // payload: visible item index (the view maps it to the real index)
    PickerSearchBox,
    PromptModalOption,      // payload: option index
    AgentLane,              // payload: lane/worker index (activity strip + Agent View rows)
    ExpandedPanel,
    FooterBadge,            // payload: badge id (only badges with a keyboard/command alias register)
    ComposeArea,
    TaskBoardStrip,
    JobRow,
    WorkflowRow,
    NavRow,
}

/// <summary>One rectangular hit region in PHYSICAL terminal cells: <paramref name="Top"/> /
/// <paramref name="Left"/> are 1-based, the rectangle spans <paramref name="Height"/> rows and
/// <paramref name="Width"/> columns inclusive of its origin. <paramref name="Tag"/> carries an
/// optional string identity (agent-lane name) so consumers never index a separately-published
/// lookup that could tear against the map.</summary>
internal readonly record struct HitRegion(int Top, int Left, int Height, int Width, MouseTargetKind Kind, int Payload, string? Tag = null);

/// <summary>
/// Per-present mouse hit-testing registry: a flat list of semantic regions REBUILT during every
/// compose/render from the same geometry that painted the rows, so resize/scroll invalidation is
/// impossible by construction - there is no cached map to update, the freshest map always
/// corresponds to the frame actually on screen. Regions, not per-cell metadata: a frame is a few
/// dozen semantic rects, and a reverse-order containment scan (later <see cref="Add"/> = drawn
/// later = on top) is the honest match for Mux's flat row composition - no widget tree, no event
/// bubbling. The main-frame instance is built fresh per compose and swapped whole (readers never
/// see a half-built map); modal loops own private instances reused via <see cref="Begin"/> on
/// their single consumer thread.
/// </summary>
internal sealed class MouseHitMap
{
    private readonly List<HitRegion> _regions = new(32);
    private int _width;
    private int _height;

    /// <summary>Number of registered regions (test/diagnostic hook).</summary>
    public int Count => _regions.Count;

    /// <summary>Clear the registry and stamp the frame geometry hits are validated against.
    /// Coordinates outside 1..width / 1..height never hit.</summary>
    public void Begin(int width, int height)
    {
        _width = Math.Max(0, width);
        _height = Math.Max(0, height);
        _regions.Clear();
    }

    /// <summary>Register one region. Called by composers/views at the exact code points that
    /// already know the geometry, in paint order (topmost last). Degenerate rects are ignored.</summary>
    public void Add(in HitRegion r)
    {
        if (r.Height <= 0 || r.Width <= 0) return;
        _regions.Add(r);
    }

    /// <summary>Resolve a 1-based cell to the topmost containing region. <paramref name="localRow"/>
    /// and <paramref name="localCol"/> are 0-based offsets INTO the region (oh-my-pi's frame-local
    /// coordinate contract), so per-cell resolution stays available where a target needs it
    /// (thumb grab offset, compose click-to-position) without per-cell metadata.</summary>
    public bool TryHit(int row, int col, out HitRegion hit, out int localRow, out int localCol)
    {
        hit = default; localRow = localCol = 0;
        if (row < 1 || col < 1 || row > _height || col > _width) return false;
        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            var r = _regions[i];
            if (row < r.Top || row >= r.Top + r.Height) continue;
            if (col < r.Left || col >= r.Left + r.Width) continue;
            hit = r;
            localRow = row - r.Top;
            localCol = col - r.Left;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Minimal press-to-release click recognition for single-consumer modal loops, working directly on
/// pump <see cref="ConsoleInputPump.InputEvent"/> Mouse events: a left press captures the region
/// under the pointer, a release inside that same region is a CLICK (hermes capture semantics -
/// release elsewhere is a drag-cancel), motion never clicks. Modal loops own one tracker plus one
/// private <see cref="MouseHitMap"/> each, so modal clicks can never leak to the main frame's map.
/// </summary>
internal sealed class MouseClickTracker
{
    /// <summary>Two clicks on the SAME semantic target within this window make the second a
    /// double-click (the Enter alias). 450ms sits between the common 300-500ms defaults.</summary>
    internal const int DoubleClickWindowMs = 450;

    /// <summary>Horizontal slop (cells) within which two clicks on the same row count as the
    /// same SPOT for chaining - the terminal-cell analog of the OS double-click rectangle.</summary>
    internal const int ChainColSlop = 2;

    private readonly Func<long> _clock;
    private bool _captured;
    private HitRegion _capture;
    private int _chain;   // clicks so far in the active chain (0 = none; reset after a triple)
    private MouseTargetKind _lastKind;
    private int _lastPayload;
    private string? _lastTag;
    private int _lastRow, _lastCol;
    private long _lastTick;

    public MouseClickTracker(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>Feed one pump event. Returns true exactly when a click completed, with the clicked
    /// region, 0-based region-local coordinates of the RELEASE point, and the position of this
    /// click in its chain - 1 (single),
    /// 2 (double = the Enter alias), 3 (triple = the Apply/F4 alias where one exists). Chains on
    /// the same Kind+Payload+Tag with each release inside the window of the PREVIOUS one; a
    /// triple resets the chain (a quad is a fresh single).</summary>
    public bool Feed(in ConsoleInputPump.InputEvent ev, MouseHitMap map, out HitRegion clicked, out int localRow, out int localCol, out int clickCount)
    {
        clicked = default; localRow = localCol = 0; clickCount = 0;
        if (ev.Kind != ConsoleInputPump.EventKind.Mouse) return false;
        if (ev.MouseRelease)
        {
            if (!_captured) return false;
            _captured = false;
            if (ev.MouseRow < _capture.Top || ev.MouseRow >= _capture.Top + _capture.Height) return false;
            if (ev.MouseCol < _capture.Left || ev.MouseCol >= _capture.Left + _capture.Width) return false;
            clicked = _capture;
            localRow = ev.MouseRow - _capture.Top;
            localCol = ev.MouseCol - _capture.Left;
            long now = _clock();
            // Chain when the click is the SAME TARGET (Kind+Payload+Tag) *or* the SAME SPOT
            // (row + col slop) inside the window. Identity alone breaks when the prior click's
            // own action mutates the UI (a picker double-click advances the pane, so payloads
            // shift under the third click); position alone breaks when the action moves the
            // content (expanding a card shifts rows). The union is the OS convention (position
            // + time) hardened for a UI that repaints between clicks.
            bool sameTarget = clicked.Kind == _lastKind && clicked.Payload == _lastPayload
                && string.Equals(clicked.Tag, _lastTag, StringComparison.Ordinal);
            bool sameSpot = ev.MouseRow == _lastRow && Math.Abs(ev.MouseCol - _lastCol) <= ChainColSlop;
            bool chains = _chain > 0 && (sameTarget || sameSpot) && now - _lastTick <= DoubleClickWindowMs;
            _chain = chains ? _chain + 1 : 1;
            clickCount = _chain;
            if (_chain >= 3) _chain = 0;   // triple consumed: next click starts fresh
            _lastKind = clicked.Kind; _lastPayload = clicked.Payload; _lastTag = clicked.Tag;
            _lastRow = ev.MouseRow; _lastCol = ev.MouseCol; _lastTick = now;
            return true;
        }
        if ((ev.MouseButton & 0x20) != 0) return false;   // motion while held: not a new capture
        _captured = map.TryHit(ev.MouseRow, ev.MouseCol, out _capture, out _, out _);
        return false;
    }
}
