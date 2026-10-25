using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine.Memory;

/// <summary>
/// The mid-turn steers (Ctrl+N) spliced into the current turn, each with the message it followed in the request.
/// The agent framework commits the turn's request and reply messages, but not a note a delegating client appended,
/// so the steer reached the model yet was missing from the saved session (after /resume the model saw replies to a
/// note that was never there). <see cref="RestoreInto"/> puts each note back right after the message it followed
/// (or at the end when that message is not in the history), as a user message. The committed history holds the
/// framework's own copies, so the anchor is matched by its tool call ids, else its message id, else role + text.
/// </summary>
internal sealed class SteerLog
{
    private readonly object _gate = new();
    private readonly List<(ChatMessage? After, ChatMessage Note)> _notes = new();

    /// <summary>Record a steer appended to <paramref name="request"/> (the list before the note was added).</summary>
    internal void Record(IReadOnlyList<ChatMessage> request, ChatMessage note)
    {
        lock (_gate) _notes.Add((request.Count > 0 ? request[^1] : null, note));
    }

    /// <summary>True when no steer was recorded since the last <see cref="Take"/>.</summary>
    internal bool IsEmpty { get { lock (_gate) return _notes.Count == 0; } }

    /// <summary>Remove and return the recorded notes, oldest first.</summary>
    internal List<(ChatMessage? After, ChatMessage Note)> Take()
    {
        lock (_gate) { var all = _notes.ToList(); _notes.Clear(); return all; }
    }

    /// <summary>
    /// Insert <paramref name="notes"/> into <paramref name="history"/> after the message each one followed (matched by
    /// reference); a note whose anchor is missing (e.g. history rebuilt by compaction) is appended at the end. Notes
    /// already present (same instance) are skipped. Returns how many were inserted.
    /// </summary>
    internal static int RestoreInto(List<ChatMessage> history, IEnumerable<(ChatMessage? After, ChatMessage Note)> notes)
    {
        int added = 0;
        foreach (var (after, note) in notes)
        {
            if (history.Any(m => ReferenceEquals(m, note))) continue;
            int at = after is null ? -1 : history.FindLastIndex(m => SameMessage(m, after));
            // Keep notes that followed the same message in their original order.
            int pos = at < 0 ? history.Count : at + 1;
            while (at >= 0 && pos < history.Count && notes.Any(n => ReferenceEquals(n.Note, history[pos]))) pos++;
            history.Insert(pos, note);
            added++;
        }
        return added;
    }

    // The same logical message: same instance, or same tool call ids, else same message id, else same role and text.
    private static bool SameMessage(ChatMessage a, ChatMessage b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Role != b.Role) return false;
        string ia = CallIds(a), ib = CallIds(b);
        if (ia.Length > 0 || ib.Length > 0) return ia == ib;
        if (a.MessageId is { Length: > 0 } && b.MessageId is { Length: > 0 }) return a.MessageId == b.MessageId;
        return a.Text == b.Text;
    }

    private static string CallIds(ChatMessage m) => string.Join(",", m.Contents.Select(c => c switch
    {
        FunctionCallContent call => "call:" + call.CallId,
        FunctionResultContent result => "result:" + result.CallId,
        _ => null,
    }).Where(id => id is not null));
}
