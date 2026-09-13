namespace MuxSwarm.Engine.Tui;

/// <summary>Physical geometry for the passive transcript scrollbar; offset zero is the live tail.</summary>
internal readonly record struct FrameScrollBar(int TrackRows, int Top, int Length)
{
    /// <summary>True when retained content extends beyond the transcript viewport.</summary>
    public bool Visible => Length > 0;

    /// <summary>Compute a proportional thumb with a two-cell minimum and exact endpoint placement.</summary>
    public static FrameScrollBar Create(int scroll, int totalRows, int viewportRows)
    {
        int track = Math.Max(0, viewportRows);
        if (track == 0 || totalRows <= track) return new(track, 0, 0);
        int maximum = Math.Max(1, track - 1); // Overflow must leave travel, except in a one-row pane.
        int length = Math.Clamp((int)Math.Ceiling((double)track * track / totalRows),
            Math.Min(2, maximum), maximum);
        int maxScroll = totalRows - track;
        double fraction = (double)Math.Clamp(scroll, 0, maxScroll) / maxScroll;
        int top = (int)Math.Round((1 - fraction) * (track - length));
        return new(track, top, length);
    }

    /// <summary>One independent, reset-delimited rail cell, or a blank outside the transcript.</summary>
    public string Cell(int row)
    {
        if (!Visible || row < 0 || row >= TrackRows) return " ";
        bool thumb = row >= Top && row < Top + Length;
        return TuiMarkup.ToAnsi(thumb
            ? $"[{TuiComponents.Accent}]█[/]"
            : $"[{TuiComponents.Muted}]│[/]");
    }
}
