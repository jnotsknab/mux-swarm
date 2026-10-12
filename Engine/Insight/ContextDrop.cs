using System.ComponentModel;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// One droppable block. <see cref="Id"/> is what the map shows: <c>m{Message}</c>, or <c>m{Message}.{Content}</c>
    /// when the message holds several contents (parallel tool results). Passing <c>m{Message}</c> drops every
    /// droppable block of that message.
    /// </summary>
    internal sealed record Block(int Message, int Content, string Kind, string Tool, int Chars, string Preview, bool Multi = false)
    {
        internal string Id => Multi ? $"m{Message}.{Content}" : $"m{Message}";
    }

    private readonly Func<IReadOnlyList<ChatMessage>?> _history;
    private readonly Dictionary<(int Message, int Content), string> _pending = new();
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
                    // String, JSON string or content-part list (MCP): its text, never the CLR type name. Other JSON
                    // (an MCP CallToolResult with structuredContent) counts as its raw text, like EstimateCharacters.
                    text = string.Concat(ContextPruner.ResultText(r).Select(p => p.Text));
                    if (text.Length == 0 && r.Result is JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json
                        && !(json.ValueKind == JsonValueKind.Object && json.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True))
                        text = json.GetRawText();
                    if (text.Length == 0) text = null;
                    kind = "tool_result"; tool = calls.GetValueOrDefault(r.CallId ?? "", "?");
                }
                else if (msg.Role == ChatRole.Assistant && msg.Contents[ci] is TextContent t)
                {
                    text = t.Text; kind = "assistant";
                }
                if (text is null || text.Length < MinChars || IsNotice(text) || LooksLikeError(text)) continue;
                blocks.Add(new Block(i, ci, kind!, tool, text.Length, Preview(text), msg.Contents.Count > 1));
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
            if (_pending.Count > 0) sb.AppendLine($"Pending drops (applied at end of turn): {string.Join(", ", _pending.Keys.Order().Select(k => $"m{k.Message}.{k.Content}"))}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The <c>context_drop</c> text: queue valid ids, report the rest. <c>mN</c> queues every droppable block of
    /// message N (all its parallel tool results); <c>mN.C</c> queues one block.
    /// </summary>
    internal string Drop(IReadOnlyList<string> ids, string reason)
    {
        var history = _history();
        if (history is null) return "[ERROR] No context to drop from yet.";
        var blocks = Blocks(history);
        var queued = new List<string>(); var rejected = new List<string>();
        int count = 0;
        reason = string.IsNullOrWhiteSpace(reason) ? "no longer needed" : OneLine(reason, 120);
        lock (_gate)
            foreach (string raw in ids.Select(i => i.Trim()).Distinct())
            {
                var matches = Match(blocks, raw);
                if (matches.Count == 0) { rejected.Add(raw); continue; }
                foreach (var b in matches)
                    if (_pending.TryAdd((b.Message, b.Content), reason)) count++;
                queued.Add(raw);
            }
        string result = queued.Count > 0
            ? $"Queued {count} block(s) to drop at the end of this turn: {string.Join(", ", queued)}. " +
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
        Dictionary<(int Message, int Content), string> pending;
        lock (_gate) { pending = new(_pending); _pending.Clear(); }
        var output = history.ToList();
        int dropped = 0; long saved = 0;
        foreach (var block in Blocks(history).Where(b => pending.ContainsKey((b.Message, b.Content))))
        {
            string notice = $"{NoticePrefix} {pending[(block.Message, block.Content)]}; {block.Chars} chars; recover: search {Path.ChangeExtension(recoveryPath, ".txt")}]";
            var original = history[block.Message];
            var clone = output[block.Message];
            if (ReferenceEquals(clone, original))   // first drop in this message; later ones edit the same clone
            {
                clone = original.Clone();
                clone.RawRepresentation = null;
                clone.Contents = original.Contents.ToList();
            }
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
            ([Description("Block ids from context_map, e.g. [\"m12\", \"m30.1\"]. \"m30\" drops every listed block of message 30.")] string[] ids,
             [Description("Short reason, kept in the notice that replaces each block.")] string reason) => Drop(ids, reason),
            "context_drop",
            "Drop blocks you no longer need from your context (old tool output, superseded drafts). Applied at the end of " +
            "this turn: each block becomes a one-line notice and the full original is saved to a recovery file you can " +
            "search later. Cannot drop user or system messages or your most recent context.");
    }

    /// <summary>Blocks an id names: <c>mN</c> = all of message N's droppable blocks, <c>mN.C</c> = that one block.</summary>
    private static List<Block> Match(List<Block> blocks, string id)
    {
        if (id.Length < 2 || id[0] is not ('m' or 'M')) return [];
        string[] parts = id[1..].Split('.');
        if (!int.TryParse(parts[0], out int m)) return [];
        if (parts.Length == 1) return blocks.Where(b => b.Message == m).ToList();
        return parts.Length == 2 && int.TryParse(parts[1], out int c)
            ? blocks.Where(b => b.Message == m && b.Content == c).ToList() : [];
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
