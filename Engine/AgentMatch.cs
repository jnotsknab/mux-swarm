namespace MuxSwarm.Engine;

/// <summary>
/// Name matching for <c>/agent &lt;name&gt;</c> and its completion list. Case-insensitive, and a trailing
/// "agent" is optional on both sides ("code" = "CodeAgent"). Tiers, best first: exact, prefix, substring,
/// in-order letters ("cdag"), then a typo within a small edit distance ("coed"). Agent lists are short,
/// so every query scores every name directly.
/// </summary>
internal static class AgentMatch
{
    /// <summary>Score of <paramref name="name"/> for <paramref name="query"/> (lower is better), or -1 for no match.</summary>
    internal static int Score(string name, string query)
    {
        string n = Norm(name), q = Norm(query);
        if (q.Length == 0) return 0;
        if (n == q) return 0;
        if (n.StartsWith(q, StringComparison.Ordinal)) return 100 + n.Length - q.Length;
        int at = n.IndexOf(q, StringComparison.Ordinal);
        if (at >= 0) return 200 + at;
        if (Subsequence(n, q) is var span and >= 0) return 300 + span;
        // Typos: compare with and without the "agent" suffix ("muxagnet" is one swap from "muxagent").
        string fn = name.Trim().ToLowerInvariant(), fq = query.Trim().ToLowerInvariant();
        int d = Math.Min(Distance(n, q), Distance(fn, fq));
        return d <= Math.Max(1, Math.Min(q.Length, fq.Length) / 3) ? 400 + d : -1;
    }

    /// <summary>Names matching <paramref name="query"/>, best first (ties keep the given order).</summary>
    internal static List<string> Rank(string? query, IEnumerable<string> names) =>
        names.Select((n, i) => (n, i, s: Score(n, query ?? "")))
            .Where(t => t.s >= 0).OrderBy(t => t.s).ThenBy(t => t.i).Select(t => t.n).ToList();

    /// <summary>The best match for <paramref name="query"/>, or null when nothing is close.</summary>
    internal static string? Best(string query, IEnumerable<string> names) => Rank(query, names).FirstOrDefault();

    private static string Norm(string s)
    {
        s = s.Trim().ToLowerInvariant();
        return s.Length > 5 && s.EndsWith("agent", StringComparison.Ordinal) ? s[..^5] : s;
    }

    // Span of the shortest left-to-right embedding of q's letters in n, or -1.
    private static int Subsequence(string n, string q)
    {
        int qi = 0, first = -1;
        for (int i = 0; i < n.Length && qi < q.Length; i++)
            if (n[i] == q[qi]) { if (first < 0) first = i; if (++qi == q.Length) return i - first; }
        return -1;
    }

    // Optimal string alignment distance (insert, delete, substitute, swap adjacent letters).
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
