using System.Collections.Concurrent;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Footer chips pushed by extension processes (<c>footer/set</c>). Each chip is cached plain text,
/// keyed <c>{extension}.{id}</c>; the footer reads the cache when it paints and never calls a process.
/// A chip disappears when its process exits or is stopped.
/// </summary>
internal static class ExtensionFooter
{
    /// <summary>Longest chip text in characters; longer text is cut.</summary>
    internal const int MaxChipChars = 40;

    private static readonly ConcurrentDictionary<string, string> Chips = new(StringComparer.Ordinal);
    // Insertion order of chip keys, so chips keep a stable left-to-right order.
    private static volatile string[] _order = [];
    private static readonly object Gate = new();

    /// <summary>
    /// Set (or, with null/empty <paramref name="text"/>, remove) chip <paramref name="id"/> of
    /// <paramref name="extension"/>. Returns true when the visible footer changed.
    /// </summary>
    internal static bool Set(string extension, string id, string? text)
    {
        if (!IsValidId(id)) return false;
        string key = extension + "." + id;
        string clean = Clean(text);
        lock (Gate)
        {
            if (clean.Length == 0)
            {
                if (!Chips.TryRemove(key, out _)) return false;
                _order = _order.Where(k => k != key).ToArray();
                return true;
            }
            if (Chips.TryGetValue(key, out var old) && old == clean) return false;
            Chips[key] = clean;
            if (!_order.Contains(key)) _order = [.. _order, key];
            return true;
        }
    }

    /// <summary>Remove every chip of <paramref name="extension"/>. Returns true when any was removed.</summary>
    internal static bool Clear(string extension)
    {
        lock (Gate)
        {
            var mine = _order.Where(k => k.StartsWith(extension + ".", StringComparison.Ordinal)).ToArray();
            foreach (var k in mine) Chips.TryRemove(k, out _);
            _order = _order.Except(mine).ToArray();
            return mine.Length > 0;
        }
    }

    /// <summary>Current chips as (<c>{extension}.{id}</c>, text), in the order they first appeared.</summary>
    internal static IReadOnlyList<(string Id, string Text)> Snapshot()
    {
        var order = _order;
        var list = new List<(string, string)>(order.Length);
        foreach (var k in order)
            if (Chips.TryGetValue(k, out var t)) list.Add((k, t));
        return list;
    }

    /// <summary>Chip ids: <c>a-z</c>, <c>0-9</c>, <c>-</c>, <c>_</c>, 1-32 chars.</summary>
    internal static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= 32 } && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    // One line of plain text: control characters become spaces, whitespace collapses, capped length.
    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var s = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= MaxChipChars ? s : s[..MaxChipChars];
    }
}
