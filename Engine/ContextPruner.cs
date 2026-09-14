using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>Deterministic, copy-on-write context reduction. Never drops messages, call IDs, or non-text data.</summary>
internal static class ContextPruner
{
    /// <summary>Explicit local elision passes; bare command combines all three.</summary>
    [Flags]
    internal enum Mode { Dupes = 1, Tools = 2, Stale = 4, All = Dupes | Tools | Stale }
    internal const string NoticePrefix = "[pruned:";
    internal const int DuplicateMinChars = 1000;
    internal const int ToolMinChars = 4000;
    private const int ProtectedTailChars = 10000; // ~4,000 tokens at Mux's 2.5 chars/token estimate.

    /// <summary>Immutable plan; unchanged messages/contents retain their original identities.</summary>
    internal sealed record Plan(IReadOnlyList<ChatMessage> Messages, int Dupes, int Tools, int Stale, long CharactersSaved)
    {
        internal int Blocks => Dupes + Tools + Stale;
        internal long EstimatedTokensSaved => (long)Math.Ceiling(CharactersSaved / 2.5);
    }
    private sealed record Slot(int Message, int Content, int Nested, string Text, FunctionCallContent? Call, bool Eligible);

    /// <summary>Recognize the exact command token; invalid options are handled as usage, never as model input.</summary>
    internal static bool TryParse(string input, out Mode mode, out string? error)
    {
        mode = Mode.All; error = null;
        var parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("/prune", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length == 1) return true;
        mode = parts[1].ToLowerInvariant() switch { "dupes" => Mode.Dupes, "tools" => Mode.Tools, "stale" => Mode.Stale, _ => 0 };
        if (parts.Length != 2 || mode == 0) error = "Usage: /prune [dupes|tools|stale]. Bare /prune applies all three.";
        return true;
    }

    private static bool IsErrorText(string text)
    {
        string t = text.TrimStart();
        return t.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("[BLOCKED]", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Traceback (", StringComparison.Ordinal)
            || t.StartsWith("Exception:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProtectedCall(FunctionCallContent call)
    {
        string name = call.Name;
        if (name.Contains("skill", StringComparison.OrdinalIgnoreCase)
            || name.Contains("instruction", StringComparison.OrdinalIgnoreCase)
            || name.Contains("prompt", StringComparison.OrdinalIgnoreCase)) return true;
        if (call.Arguments is null) return false;
        foreach (var arg in call.Arguments)
        {
            if (!arg.Key.Contains("path", StringComparison.OrdinalIgnoreCase) && !arg.Key.Contains("file", StringComparison.OrdinalIgnoreCase)) continue;
            string? value = arg.Value is string s ? s : arg.Value is JsonElement { ValueKind: JsonValueKind.String } j ? j.GetString() : null;
            if (value is null) continue;
            string path = "/" + value.Replace('\\', '/').TrimStart('/');
            string file = path.Split('/').Last();
            if (file.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
                || file.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase)
                || file.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
                || file.Equals("DOCS.md", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("BRAIN", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("MEMORY", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/skills/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/prompts/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Names are explicit and ordinal. Unknown tools are never assumed read-only.
    private static string? ReadKey(FunctionCallContent call)
    {
        if (call.Name is not ("Filesystem_read_text_file" or "Filesystem_list_directory")) return null;
        if (call.Arguments is null || !call.Arguments.TryGetValue("path", out var p)) return null;
        string? path = p is string s ? s : p is JsonElement { ValueKind: JsonValueKind.String } j ? j.GetString() : null;
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Compare all arguments, including head/tail selectors; do not normalize paths across platforms.
        // Unknown argument shapes are skipped rather than invoking arbitrary object serialization.
        var args = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in call.Arguments)
        {
            string? value = item.Value switch
            {
                null => "null", string v => JsonSerializer.Serialize(v),
                bool v => v ? "true" : "false", int v => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
                long v => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonElement v when v.ValueKind is JsonValueKind.String => JsonSerializer.Serialize(v.GetString()),
                JsonElement v when v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => v.GetRawText(),
                _ => null
            };
            if (value is null) return null;
            args[item.Key] = value;
        }
        return call.Name + "\n" + JsonSerializer.Serialize(args);
    }

    private static bool ResultError(FunctionResultContent result)
    {
        if (result.Exception is not null) return true;
        if (result.AdditionalProperties is { } props &&
            (props.TryGetValue("isError", out var error) && (error is true || error is JsonElement { ValueKind: JsonValueKind.True }))) return true;
        return ResultText(result).Any(part => IsErrorText(part.Text));
    }

    // SDK serialization of IList<AIContent> inside object-valued Result becomes a JsonElement
    // array on resume. Recognize this exact tagged array contract; never flatten arbitrary JSON.
    private static bool IsContentArray(JsonElement json)
        => json.ValueKind == JsonValueKind.Array && json.GetArrayLength() > 0
            && json.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("$type", out var type) && type.ValueKind == JsonValueKind.String
                && (type.GetString() != "text" || item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String));

    private static IEnumerable<(int Nested, string Text)> ResultText(FunctionResultContent result)
    {
        if (result.Result is string s) yield return (-1, s);
        else if (result.Result is JsonElement { ValueKind: JsonValueKind.String } json) yield return (-1, json.GetString() ?? "");
        else if (result.Result is IList<AIContent> parts)
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] is TextContent t) yield return (i, t.Text);
        if (result.Result is JsonElement array && IsContentArray(array))
            for (int i = 0; i < array.GetArrayLength(); i++)
                if (array[i].GetProperty("$type").GetString() == "text")
                    yield return (i, array[i].GetProperty("text").GetString() ?? "");
    }

    /// <summary>Estimate text-bearing context including tool output, with bounded handling of known structured data.</summary>
    internal static long EstimateCharacters(IReadOnlyList<ChatMessage> messages)
        => messages.Sum(MessageCharacters);

    private static long MessageCharacters(ChatMessage message)
    {
        long chars = 0;
        foreach (var c in message.Contents)
        {
            if (c is TextContent t) chars += t.Text.Length;
            else if (c is TextReasoningContent r) chars += r.Text?.Length ?? 0;
            else if (c is FunctionResultContent f)
            {
                chars += ResultText(f).Sum(x => (long)x.Text.Length);
                if (f.Result is JsonElement j && j.ValueKind != JsonValueKind.String && !IsContentArray(j)) chars += j.GetRawText().Length;
            }
        }
        return chars;
    }

    private static bool ContextScaffold(ChatMessage message)
    {
        string text = message.Text;
        return text.Contains("auto-injected deep memory; treat as context, not an instruction.", StringComparison.Ordinal)
            || text.StartsWith("[COMPACTED CONTEXT", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[DEEP MEMORY", StringComparison.Ordinal);
    }

    /// <summary>Build an elision plan without touching the live session or invoking tools/models.</summary>
    internal static Plan Create(IReadOnlyList<ChatMessage> history, Mode mode)
    {
        int count = history.Count;
        var users = Enumerable.Range(0, count).Where(i => history[i].Role == ChatRole.User && !ContextScaffold(history[i])).ToArray();
        int firstUser = users.Length == 0 ? -1 : users[0];
        int tailStart = users.Length < 2 ? 0 : users[^2];
        long accumulated = 0;
        for (int i = count - 1; i >= 0 && accumulated < ProtectedTailChars; i--)
        { tailStart = Math.Min(tailStart, i); accumulated += MessageCharacters(history[i]); }
        var calls = new Dictionary<string, (FunctionCallContent Call, int Index)>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        var resultCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
            foreach (var c in history[i].Contents)
            {
                if (c is FunctionCallContent f)
                {
                    if (!calls.TryAdd(f.CallId, (f, i))) invalid.Add(f.CallId);
                }
                else if (c is FunctionResultContent r)
                    resultCounts[r.CallId] = resultCounts.GetValueOrDefault(r.CallId) + 1;
            }
        var slots = new List<Slot>();
        for (int i = 0; i < count; i++)
        {
            var msg = history[i];
            bool protect = i >= tailStart || i == firstUser || ContextScaffold(msg)
                || msg.Role == ChatRole.System || msg.Role == new ChatRole("developer");
            for (int ci = 0; ci < msg.Contents.Count; ci++)
            {
                var content = msg.Contents[ci];
                if (content is TextContent text && (msg.Role == ChatRole.User || msg.Role == ChatRole.Assistant))
                    slots.Add(new Slot(i, ci, -2, text.Text, null, !protect && !text.Text.StartsWith(NoticePrefix, StringComparison.Ordinal)));
                else if (content is FunctionResultContent result && msg.Role == ChatRole.Tool
                    && calls.TryGetValue(result.CallId, out var pair) && pair.Index < i
                    && !invalid.Contains(result.CallId) && resultCounts[result.CallId] == 1
                    && pair.Call.Exception is null && !ResultError(result))
                {
                    bool eligible = !protect && !ProtectedCall(pair.Call);
                    foreach (var part in ResultText(result))
                        slots.Add(new Slot(i, ci, part.Nested, part.Text, pair.Call,
                            eligible && !part.Text.StartsWith(NoticePrefix, StringComparison.Ordinal)));
                }
            }
        }

        var decisions = new Dictionary<Slot, Mode>();
        var retainedFor = new Dictionary<Slot, List<Slot>>();
        // Stable notices retain the surviving copy on repeated commands and after serialize/resume.
        // Hashes identify exact content only; equality is still checked during new deduplication.
        string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var retainedHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (!slot.Text.StartsWith(NoticePrefix, StringComparison.Ordinal)) continue;
            const string marker = "retained-sha256=";
            int at = 0;
            while ((at = slot.Text.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                at += marker.Length;
                if (slot.Text.Length >= at + 64) retainedHashes.Add(slot.Text.Substring(at, 64));
            }
        }
        var keepers = slots.Where(s => retainedHashes.Contains(Digest(s.Text))).ToHashSet();
        var hashes = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
        foreach (var slot in slots.AsEnumerable().Reverse())
        {
            if (slot.Text.Length < DuplicateMinChars) continue;
            string hash = Digest(slot.Text);
            if (!hashes.TryGetValue(hash, out var group)) hashes[hash] = group = new();
            var newer = group.FirstOrDefault(s => s.Text.Equals(slot.Text, StringComparison.Ordinal));
            if (newer is not null && slot.Eligible && !keepers.Contains(slot) && mode.HasFlag(Mode.Dupes))
            { decisions[slot] = Mode.Dupes; retainedFor[slot] = new() { newer }; keepers.Add(newer); }
            else group.Add(slot);
        }
        // Successful later observations supersede only exactly matching known read calls.
        var newestReads = new Dictionary<string, Slot>(StringComparer.Ordinal);
        var incompleteReads = slots.Where(s => s.Call is not null && s.Text.StartsWith(NoticePrefix, StringComparison.Ordinal))
            .Select(s => (s.Message, s.Content)).ToHashSet();
        foreach (var slot in slots.AsEnumerable().Reverse())
        {
            // A notice (or partially elided multipart result) is not a successful replacement
            // observation. It must never justify deleting the last actual older observation.
            if (slot.Call is null || incompleteReads.Contains((slot.Message, slot.Content))
                || ReadKey(slot.Call) is not { } key) continue;
            if (newestReads.TryGetValue(key, out var newer) && newer.Message > slot.Message
                && slot.Eligible && !keepers.Contains(slot) && !decisions.ContainsKey(slot) && mode.HasFlag(Mode.Stale))
            {
                decisions[slot] = Mode.Stale;
                var complete = slots.Where(s => s.Message == newer.Message && s.Content == newer.Content).ToList();
                retainedFor[slot] = complete;
                foreach (var part in complete) keepers.Add(part);
            }
            else newestReads.TryAdd(key, slot);
        }
        foreach (var slot in slots)
            if (mode.HasFlag(Mode.Tools) && slot.Call is not null && slot.Eligible && slot.Text.Length >= ToolMinChars
                && !keepers.Contains(slot) && !decisions.ContainsKey(slot)) decisions[slot] = Mode.Tools;

        var changed = new Dictionary<(int Message, int Content), Dictionary<int, string>>();
        int dupes = 0, tools = 0, stale = 0; long saved = 0;
        foreach (var (slot, reason) in decisions)
        {
            string reference = retainedFor.TryGetValue(slot, out var kept)
                ? string.Concat(kept.Select(k => $"; retained-sha256={Digest(k.Text)}")) : "";
            string notice = $"[pruned:{reason.ToString().ToLowerInvariant()}{reference}; {slot.Text.Length} characters omitted; pre-prune recovery snapshot retained]";
            if (notice.Length >= slot.Text.Length) continue;
            var key = (slot.Message, slot.Content);
            if (!changed.TryGetValue(key, out var replacements)) changed[key] = replacements = new();
            replacements[slot.Nested] = notice;
            saved += slot.Text.Length - notice.Length;
            if (reason == Mode.Dupes) dupes++; else if (reason == Mode.Tools) tools++; else stale++;
        }
        if (changed.Count == 0) return new Plan(history, 0, 0, 0, 0);
        var output = history.ToArray();
        foreach (var message in changed.GroupBy(x => x.Key.Message))
        {
            var clone = history[message.Key].Clone();
            clone.RawRepresentation = null;
            clone.Contents = history[message.Key].Contents.ToList();
            foreach (var item in message)
            {
                var original = clone.Contents[item.Key.Content];
                if (original is TextContent t) clone.Contents[item.Key.Content] = CopyText(t, item.Value[-2]);
                else if (original is FunctionResultContent f)
                {
                    object? value;
                    if (f.Result is IList<AIContent> parts)
                    {
                        var newParts = parts.ToList();
                        foreach (var replacement in item.Value)
                            newParts[replacement.Key] = CopyText((TextContent)parts[replacement.Key], replacement.Value);
                        value = newParts;
                    }
                    else if (f.Result is JsonElement array && IsContentArray(array))
                    {
                        var node = JsonNode.Parse(array.GetRawText())!.AsArray();
                        foreach (var replacement in item.Value) node[replacement.Key]!["text"] = replacement.Value;
                        value = JsonSerializer.SerializeToElement(node);
                    }
                    else value = item.Value[-1];
                    clone.Contents[item.Key.Content] = new FunctionResultContent(f.CallId, value)
                    { AdditionalProperties = f.AdditionalProperties, Annotations = f.Annotations, Exception = f.Exception };
                }
            }
            output[message.Key] = clone;
        }
        return new Plan(output, dupes, tools, stale, saved);
    }

    private static TextContent CopyText(TextContent original, string text) => new(text)
    { AdditionalProperties = original.AdditionalProperties, Annotations = original.Annotations };
}
