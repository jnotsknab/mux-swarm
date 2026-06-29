namespace MuxSwarm.Utils;

/// <summary>
/// Pure helper for the single-agent live token meter. The provider reports authoritative token
/// counts only via UsageContent frames; between frames the docked meter extrapolates FORWARD from
/// the last authoritative checkpoint so it stays responsive without freezing.
///
/// Design (matches Codex/Cline reconciliation): the live estimate is
///   sessionTokens (last authoritative total) + ceil(outputCharsSinceCheckpoint / charsPerToken)
/// minus cached (so the displayed figure is "live" context the way Claude-style meters show it).
/// On every authoritative UsageContent frame the caller snaps sessionTokens/cachedTokens to truth
/// AND resets the char accumulator to 0, so this estimate only ever bridges the small gap since the
/// last checkpoint -- never the whole turn. That is what eliminates the old "drops on turn end"
/// artifact: at a checkpoint liveTurnChars == 0, so Estimate() equals the authoritative display.
///
/// Only OUTPUT the model is actively generating (answer + reasoning text) feeds the char
/// accumulator. Tool-call args and tool-result text are deliberately excluded: they are folded into
/// the provider's InputTokenCount on the next iteration, so counting their chars here would
/// double-count and inflate the estimate.
/// </summary>
internal static class LiveTokenMeter
{
    internal const double DefaultCharsPerToken = 2.5;

    /// <summary>
    /// The displayed live estimate: forward-extrapolation from the last authoritative checkpoint,
    /// with cached tokens subtracted (never below zero). When <paramref name="liveTurnChars"/> is 0
    /// (i.e. right after a reconcile) this returns exactly the authoritative display value, so the
    /// live meter and the post-turn snap agree at every checkpoint.
    /// </summary>
    internal static uint Estimate(uint sessionTokens, long liveTurnChars, uint cachedTokens,
        double charsPerToken = DefaultCharsPerToken)
    {
        long safeChars = Math.Max(0, liveTurnChars);
        uint est = sessionTokens + (uint)Math.Ceiling(safeChars / charsPerToken);
        return est > cachedTokens ? est - cachedTokens : est;
    }
}
