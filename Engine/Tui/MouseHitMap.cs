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
/// <paramref name="Width"/> columns inclusive of its origin.</summary>
internal readonly record struct HitRegion(int Top, int Left, int Height, int Width, MouseTargetKind Kind, int Payload);

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
