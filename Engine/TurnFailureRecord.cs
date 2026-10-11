using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>
/// Makes a turn that did not complete durable in the persisted session. The agent framework only
/// commits a run's messages on success, so an interrupted, timed-out, or failed turn would
/// otherwise be persisted as the PRE-turn history, as if it never happened.
/// </summary>
internal static class TurnFailureRecord
{
    /// <summary>Number of messages in the session's in-memory history (0 for a fresh session).</summary>
    internal static int Count(AgentSession session) =>
        session.TryGetInMemoryChatHistory(out var history) && history is not null ? history.Count : 0;

    /// <summary>Appends the user goal plus an assistant note to the session's in-memory history.
    /// <paramref name="preTurnCount"/> is <see cref="Count"/> at turn start: if the history grew since,
    /// the framework already committed this turn's run (goal included), so only the note is appended.</summary>
    internal static void Append(AgentSession session, string goal, string assistantNote, int preTurnCount)
    {
        if (!session.TryGetInMemoryChatHistory(out var history) || history is null)
            history = new List<ChatMessage>();
        if (history.Count <= preTurnCount)
            history.Add(new ChatMessage(ChatRole.User, goal));
        history.Add(new ChatMessage(ChatRole.Assistant, assistantNote));
        session.SetInMemoryChatHistory(history);
    }

    /// <summary>
    /// The assistant-side record of a turn that did not complete: why, any partial text, and which
    /// tools ran (their side effects are real even though the turn's messages were not committed).
    /// </summary>
    internal static string Note(string reason, string partialResponse, IReadOnlyList<string> toolNames)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(partialResponse))
            sb.Append(partialResponse.TrimEnd()).Append("\r\n\r\n");
        sb.Append("[turn ended: ").Append(reason).Append(']');
        if (toolNames.Count > 0)
        {
            sb.Append("\r\n[").Append(toolNames.Count).Append(" tool call(s) ran before it ended; their side effects ")
              .Append("(files, commits, jobs) may exist even though this turn's messages were not saved: ")
              .Append(string.Join(", ", toolNames.GroupBy(n => n, StringComparer.Ordinal)
                  .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)))
              .Append(']');
        }
        return sb.ToString();
    }
}
