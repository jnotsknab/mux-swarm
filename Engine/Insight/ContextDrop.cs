using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine.Insight;

/// <summary>
/// /insight max: lets the lead agent see what fills its context (<c>context_map</c>) and ask for specific
/// blocks to be dropped (<c>context_drop</c>). Drops are queued and applied at the END of the turn, when the
/// session is idle (the session commits a turn's messages only when the turn finishes); each dropped block is
/// replaced by a short notice and the full session is saved to a recovery snapshot first. Copy-on-write like
/// <see cref="ContextPruner"/>: messages, tool-call ids and non-text content are never removed.
/// </summary>
internal sealed class ContextDrop
{
    /// <summary>Prefix of the notice that replaces a dropped block.</summary>
    internal const string NoticePrefix = "[dropped by agent:";
    /// <summary>Blocks shorter than this are not listed or dropped (the notice would save nothing).</summary>
    internal const int MinChars = 1000;
    /// <summary>
    /// The newest droppable blocks up to this many characters are protected (recent working context). Counted per
    /// block, newest first, so one huge message cannot shield everything older than it. The running turn is never
    /// listed at all: the session commits a turn only when it ends, so it is not in the history being mapped.
    /// </summary>
    internal const int ProtectedTailChars = 10000;
    private const int MapRows = 25;

    /// <summary>One droppable block: <c>m{Message}</c> is the id the agent passes back.</summary>
    internal sealed record Block(int Message, int Content, string Kind, string Tool, int Chars, string Preview)
    {
        internal string Id => $"m{Message}";
    }

    private readonly Func<IReadOnlyList<ChatMessage>?> _history;
    private readonly Dictionary<int, string> _pending = new();
    private readonly object _gate = new();

    /// <param name="history">Reads the session's committed history (null when unavailable).</param>
    internal ContextDrop(Func<IReadOnlyList<ChatMessage>?> history) => _history = history;

    /// <summary>Drops waiting for the end of the turn.</summary>
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Forget queued drops (the session was replaced, e.g. by compaction or /wipe).</summary>
    internal void Clear() { lock (_gate) _pending.Clear(); }

    /// <summary>
    /// Droppable blocks in <paramref name="history"/>: large tool results and assistant text outside the
    /// protected tail. Never the system prompt, user messages, errors, or anything already dropped/pruned.
    /// </summary>
    internal static List<Block> Blocks(IReadOnlyList<ChatMessage> history)
    {
        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .GroupBy(c => c.CallId ?? "").ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var blocks = new List<Block>();
        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            for (int ci = 0; ci < msg.Contents.Count; ci++)
            {
                string? text = null, kind = null, tool = "";
                if (msg.Role == ChatRole.Tool && msg.Contents[ci] is FunctionResultContent { Exception: null } r)
                {
                    // String, JSON string or content-part list (MCP): its text, never the CLR type name.
                    text = string.Concat(ContextPruner.ResultText(r).Select(p => p.Text));
                    if (text.Length == 0) text = null;
                    kind = "tool_result"; tool = calls.GetValueOrDefault(r.CallId ?? "", "?");
                }
                else if (msg.Role == ChatRole.Assistant && msg.Contents[ci] is TextContent t)
                {
                    text = t.Text; kind = "assistant";
                }
                if (text is null || text.Length < MinChars || IsNotice(text) || LooksLikeError(text)) continue;
                blocks.Add(new Block(i, ci, kind!, tool, text.Length, Preview(text)));
            }
        }
        // Protect the newest blocks (by their own size) up to the tail budget, so the agent keeps what it is
        // working with; older blocks stay droppable even when they share a message with a protected one.
        long recent = 0;
        int keep = blocks.Count;
        while (keep > 0 && recent < ProtectedTailChars) recent += blocks[--keep].Chars;
        return blocks.Take(keep).ToList();
    }

    /// <summary>The <c>context_map</c> text: the largest droppable blocks, oldest first within equal size.</summary>
    internal string Map()
    {
        var history = _history();
        if (history is null || history.Count == 0) return "Context is empty (nothing committed yet; drops apply to earlier turns).";
        var blocks = Blocks(history);
        var sb = new StringBuilder();
        sb.AppendLine($"{history.Count} messages, ~{ContextGauge.K((uint)(ContextPruner.EstimateCharacters(history) / 2.5))} tokens of text. " +
                      $"{blocks.Count} droppable blocks (>= {MinChars} chars; the newest ~{ProtectedTailChars / 2500}k tokens are protected). " +
                      "This turn is not listed yet; it is committed when the turn ends.");
        foreach (var b in blocks.OrderByDescending(b => b.Chars).ThenBy(b => b.Message).Take(MapRows))
            sb.AppendLine($"{b.Id} {b.Kind}{(b.Tool.Length > 0 ? " " + b.Tool : "")} ~{ContextGauge.K((uint)(b.Chars / 2.5))} tok \"{b.Preview}\"");
        lock (_gate)
            if (_pending.Count > 0) sb.AppendLine($"Pending drops (applied at end of turn): {string.Join(", ", _pending.Keys.Select(k => "m" + k))}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The <c>context_drop</c> text: queue valid ids, report the rest.</summary>
    internal string Drop(IReadOnlyList<string> ids, string reason)
    {
        var history = _history();
        if (history is null) return "[ERROR] No context to drop from yet.";
        var droppable = Blocks(history).Select(b => b.Message).ToHashSet();
        var queued = new List<string>(); var rejected = new List<string>();
        reason = string.IsNullOrWhiteSpace(reason) ? "no longer needed" : OneLine(reason, 120);
        lock (_gate)
            foreach (string raw in ids.Select(i => i.Trim()).Distinct())
            {
                if (raw.Length > 1 && raw[0] is 'm' or 'M' && int.TryParse(raw[1..], out int m) && droppable.Contains(m))
                { _pending[m] = reason; queued.Add(raw); }
                else rejected.Add(raw);
            }
        string result = queued.Count > 0
            ? $"Queued {queued.Count} block(s) to drop at the end of this turn: {string.Join(", ", queued)}. " +
              "They stay visible until then; a recovery snapshot is saved before anything is removed."
            : "Nothing queued.";
        if (rejected.Count > 0)
            result += $" Not droppable (unknown, too small, protected, or user/system content): {string.Join(", ", rejected)}. Call context_map for ids.";
        return result;
    }

    /// <summary>
    /// Copy of <paramref name="history"/> with the queued blocks replaced by notices pointing at
    /// <paramref name="recoveryPath"/>, plus how many blocks were dropped. Queued ids that are no longer
    /// droppable (history changed) are skipped. Clears the queue.
    /// </summary>
    internal (List<ChatMessage> Messages, int Dropped, long CharsSaved) Apply(IReadOnlyList<ChatMessage> history, string recoveryPath)
    {
        Dictionary<int, string> pending;
        lock (_gate) { pending = new(_pending); _pending.Clear(); }
        var output = history.ToList();
        int dropped = 0; long saved = 0;
        foreach (var block in Blocks(history).Where(b => pending.ContainsKey(b.Message)))
        {
            string notice = $"{NoticePrefix} {pending[block.Message]}; {block.Chars} chars; recover: search {Path.ChangeExtension(recoveryPath, ".txt")}]";
            var original = history[block.Message];
            var clone = original.Clone();
            clone.RawRepresentation = null;
            clone.Contents = original.Contents.ToList();
            clone.Contents[block.Content] = original.Contents[block.Content] switch
            {
                FunctionResultContent r => new FunctionResultContent(r.CallId, notice)
                    { AdditionalProperties = r.AdditionalProperties, Annotations = r.Annotations },
                TextContent t => new TextContent(notice) { AdditionalProperties = t.AdditionalProperties, Annotations = t.Annotations },
                var other => other,
            };
            output[block.Message] = clone;
            dropped++; saved += block.Chars - notice.Length;
        }
        return (output, dropped, saved);
    }

    /// <summary>The two agent tools bound to this instance.</summary>
    internal IEnumerable<AITool> Tools()
    {
        yield return AIFunctionFactory.Create(Map, "context_map",
            "List the largest blocks in your context (id, kind, tool, size, preview) so you can drop ones you no longer need. " +
            "Read-only. Use when /insight shows the context getting full.");
        yield return AIFunctionFactory.Create(
            ([Description("Block ids from context_map, e.g. [\"m12\", \"m30\"].")] string[] ids,
             [Description("Short reason, kept in the notice that replaces each block.")] string reason) => Drop(ids, reason),
            "context_drop",
            "Drop blocks you no longer need from your context (old tool output, superseded drafts). Applied at the end of " +
            "this turn: each block becomes a one-line notice and the full original is saved to a recovery file you can " +
            "search later. Cannot drop user or system messages or your most recent context.");
    }

    private static bool IsNotice(string text) =>
        text.StartsWith(NoticePrefix, StringComparison.Ordinal) || text.StartsWith(ContextPruner.NoticePrefix, StringComparison.Ordinal);

    private static bool LooksLikeError(string text)
    {
        string t = text.TrimStart();
        return t.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
    }

    private static string Preview(string text) => OneLine(text, 60).Replace("\"", "'");

    private static string OneLine(string text, int max)
    {
        string s = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max] + "...";
    }
}
