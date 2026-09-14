namespace MuxSwarm.Engine.Tui;

/// <summary>
/// Typo-tolerant "did you mean" suggestions for unrecognized input at the top-level menu.
/// Layered cheap-first matching over the canonical <see cref="TuiCommands.All"/> catalog:
/// exact, then prefix, then in-order subsequence, then bounded Damerau-Levenshtein (optimal
/// string alignment) with a per-row early exit. The catalog is ~90 entries, so a full pass
/// costs microseconds - no index or cache is warranted.
/// </summary>
internal static class CommandSuggest
{
    /// <summary>Distinct base command tokens (multi-word catalog entries reduce to their head).</summary>
    private static readonly string[] BaseCommands = BuildBase();

    private static string[] BuildBase()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<string>();
        foreach (var e in TuiCommands.All)
        {
            var head = e.Cmd.Split(' ', 2)[0];
            if (seen.Add(head)) outp.Add(head);
        }
        return outp.ToArray();
    }

    /// <summary>
    /// Rank suggestions for an unrecognized input token. Accepts input with or without the
    /// leading slash ("agnet" and "/agnet" both suggest /agent); only the first whitespace
    /// token is considered. <paramref name="strict"/> caps the edit budget at one (used for
    /// bare slashless prose, where a loose match would misread chat intent - "hello" must NOT
    /// suggest /help). Returns up to <paramref name="max"/> commands, best first; empty when
    /// nothing is plausibly close.
    /// </summary>
    public static IReadOnlyList<string> Suggest(string input, int max = 3, bool strict = false)
    {
        string token = (input ?? "").Trim();
        if (token.Length == 0) return Array.Empty<string>();
        token = token.Split(' ', 2)[0].TrimStart('/').ToLowerInvariant();
        if (token.Length == 0) return Array.Empty<string>();

        int budget = strict ? 1 : (token.Length <= 3 ? 1 : 2);   // bounded, length-scaled edits
        var scored = new List<(int Tier, int Cost, int Len, string Cmd)>();
        foreach (var cmd in BaseCommands)
        {
            string body = cmd[1..].ToLowerInvariant();
            if (body == token) { scored.Add((0, 0, body.Length, cmd)); continue; }
            if (body.StartsWith(token, StringComparison.Ordinal)) { scored.Add((1, body.Length - token.Length, body.Length, cmd)); continue; }
            if (IsSubsequence(token, body)) { scored.Add((2, body.Length - token.Length, body.Length, cmd)); continue; }
            int d = BoundedOsa(token, body, budget);
            // Same-distance tiebreak: an adjacent-swap typo (hepl->help) is far likelier than a
            // substitution (hepl->heal), so transpositions rank ahead at equal edit distance.
            if (d >= 0) scored.Add((3, d * 2 - (IsOneTransposition(token, body) ? 1 : 0), body.Length, cmd));
        }
        return scored
            .OrderBy(s => s.Tier).ThenBy(s => s.Cost).ThenBy(s => s.Len)
            .ThenBy(s => s.Cmd, StringComparer.Ordinal)
            .Select(s => s.Cmd)
            .Take(Math.Max(1, max))
            .ToList();
    }

    /// <summary>True when the strings differ ONLY by one adjacent transposition (equal length,
    /// exactly two mismatched neighboring positions, crosswise equal).</summary>
    private static bool IsOneTransposition(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int first = -1;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (first < 0) { first = i; continue; }
            if (i != first + 1) return false;
            if (a[first] != b[i] || a[i] != b[first]) return false;
            // Remainder must match exactly.
            for (int j = i + 1; j < a.Length; j++) if (a[j] != b[j]) return false;
            return true;
        }
        return false;
    }

    /// <summary>All chars of <paramref name="needle"/> appear in order within <paramref name="hay"/>.
    /// Single-character needles are rejected - they match far too much to be a useful signal.</summary>
    private static bool IsSubsequence(string needle, string hay)
    {
        if (needle.Length < 2) return false;
        int i = 0;
        foreach (char c in hay)
            if (i < needle.Length && c == needle[i]) i++;
        return i == needle.Length;
    }

    /// <summary>
    /// Damerau-Levenshtein distance (optimal string alignment: substitutions, insertions,
    /// deletions, adjacent transpositions) bounded to <paramref name="maxDist"/>: returns the
    /// distance, or -1 as soon as the row minimum proves the bound is exceeded. Three rolling
    /// rows; O(a.Length * b.Length) worst case on strings that are at most ~15 chars here.
    /// </summary>
    private static int BoundedOsa(string a, string b, int maxDist)
    {
        if (Math.Abs(a.Length - b.Length) > maxDist) return -1;
        int[] prev2 = new int[b.Length + 1];
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > maxDist) return -1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length] <= maxDist ? prev[b.Length] : -1;
    }
}
