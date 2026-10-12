using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>
/// Decides where mid-turn compaction may stop a streaming single-agent turn
/// (<c>executionLimits.midTurnCompaction</c>). A usage frame over the threshold arms the cut; the stream
/// is then stopped right after the last outstanding tool result of that model call, so the tools the
/// model asked for have run and their results are captured, and the next (large) model call is never sent.
/// A model call that asked for no tools (its final answer) is never cut: it finishes, and compaction
/// runs before the next turn instead. Create one per streaming pass.
/// </summary>
internal sealed class MidTurnCompactionCut
{
    private readonly HashSet<string> _outstanding = new(StringComparer.Ordinal);
    private bool _armed;

    /// <summary>
    /// Feed the contents of one streamed update, in order. <paramref name="overThreshold"/> is whether the
    /// session is over the compaction threshold after this update (only read when it carries a usage frame).
    /// Returns true when the stream should stop after this update.
    /// </summary>
    internal bool Observe(IEnumerable<AIContent> contents, bool overThreshold)
    {
        bool sawResult = false;
        foreach (var content in contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    _outstanding.Add(call.CallId);
                    break;
                case FunctionResultContent result:
                    _outstanding.Remove(result.CallId);
                    sawResult = true;
                    break;
                case UsageContent when overThreshold:
                    _armed = true;
                    break;
            }
        }
        return _armed && sawResult && _outstanding.Count == 0;
    }
}
