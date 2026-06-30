using System.Text;

namespace MuxSwarm.Utils.Memory;

/// <summary>
/// Selects a small, budgeted block of reflections to inject into an agent when deep mode is on.
/// Working agents NEVER query memory themselves - this is the only read path. Scoring is
/// recency * importance * relevance; entries below relevanceFloor are dropped; the assembled block
/// is hard-capped by injectTokenBudget (truncated, never overflowed).
///
/// Two surfaces:
///   * <see cref="BuildBlock"/> - the FULL relevant set, baked into a preamble at session/build time
///     (lead session start, and every sub-agent WrapTask).
///   * <see cref="BuildDelta"/> - only reflections NOT yet injected this session, surfaced as a light
///     per-turn system note so a long-lived lead session picks up freshly-gathered memory MID-SESSION
///     (the preamble is built once, so without this new memory would never reach an open session).
///
/// Relevance uses lexical token-overlap over the store; both surfaces obey injectTokenBudget. Best-
/// effort; never throws.
/// </summary>
public static class ReflectionInjector
{
    /// <summary>The latest user message / task, used as the relevance query. Set by the session
    /// just before a preamble is built. Null/empty = recency+importance only.</summary>
    public static string? CurrentQuery { get; set; }

    // Reflection ids already surfaced to the lead this session (full block + deltas), so a delta
    // only ever carries what is genuinely NEW. Reset when a new lead session starts.
    private static readonly HashSet<string> _injectedIds = new(StringComparer.Ordinal);
    private static readonly object _gate = new();

    /// <summary>Reset the per-session injected-id tracking (call when a fresh lead session starts).</summary>
    public static void ResetSession()
    {
        lock (_gate) _injectedIds.Clear();
    }

    /// <summary>
    /// Build the FULL reflection block for <paramref name="agentName"/> (preamble-time injection),
    /// or empty when deep mode is off, scope excludes this agent, or nothing clears the floor. Marks
    /// everything it includes as injected so a subsequent <see cref="BuildDelta"/> won't repeat it.
    /// </summary>
    public static string BuildBlock(string agentName, bool isLead)
    {
        try
        {
            var cfg = App.SwarmConfig?.ResolveReflection();
            if (cfg is null || !cfg.IsDeep) return string.Empty;
            if (!ScopeAllows(cfg, isLead)) return string.Empty;

            var scored = ScoredFor(agentName, isLead, cfg);
            if (scored.Count == 0) return string.Empty;

            var picked = new List<Reflection>();
            string block = Assemble(scored, cfg.InjectTokenBudget,
                "[DEEP MEMORY - auto-injected reflections]", picked);
            if (isLead)
                lock (_gate) foreach (var r in picked) _injectedIds.Add(r.Id);
            return block;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Build a DELTA block: only reflections not yet injected to the lead this session, scored +
    /// token-capped exactly like the full block. Returns empty when there is nothing new. Intended
    /// to be prepended as a transient system note on the next turn of a long-lived lead session, so
    /// mid-session gathered memory reaches the agent at a useful point without rebuilding the
    /// preamble. Marks what it returns as injected. Lead/orchestrator scope only.
    /// </summary>
    public static string BuildDelta(string agentName, bool isLead)
    {
        try
        {
            var cfg = App.SwarmConfig?.ResolveReflection();
            if (cfg is null || !cfg.IsDeep || !isLead) return string.Empty;
            if (!ScopeAllows(cfg, isLead)) return string.Empty;

            List<(Reflection r, double score)> scored;
            lock (_gate)
                scored = ScoredFor(agentName, isLead, cfg)
                    .Where(x => !_injectedIds.Contains(x.r.Id))
                    .ToList();
            if (scored.Count == 0) return string.Empty;

            var picked = new List<Reflection>();
            string block = Assemble(scored, cfg.InjectTokenBudget,
                "[DEEP MEMORY - new since last turn]", picked);
            if (!string.IsNullOrEmpty(block))
                lock (_gate) foreach (var r in picked) _injectedIds.Add(r.Id);
            return block;
        }
        catch { return string.Empty; }
    }

    private static bool ScopeAllows(ReflectionConfig cfg, bool isLead)
    {
        bool scopeAll = string.Equals(cfg.Scope?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        return scopeAll || isLead;
    }

    /// <summary>The role-filtered, floor-passing, score-ordered candidates for an agent.</summary>
    private static List<(Reflection r, double score)> ScoredFor(string agentName, bool isLead, ReflectionConfig cfg)
    {
        var all = ReflectionStore.LoadAll();
        if (all.Count == 0) return new();

        var candidates = all.Where(r =>
            r.Role.Equals("shared", StringComparison.OrdinalIgnoreCase)
            || r.Role.Equals(agentName, StringComparison.OrdinalIgnoreCase)
            || (isLead && r.Role.Equals("lead", StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count == 0) return new();

        var query = CurrentQuery ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        return candidates
            .Select(r => (r, score: Score(r, query, now)))
            .Where(x => x.score >= cfg.RelevanceFloor)
            .OrderByDescending(x => x.score)
            .ToList();
    }

    /// <summary>
    /// Composite score = relevance * (0.5 + 0.5*importance) * recencyDecay. Relevance is lexical
    /// token-overlap with the query in [0,1]; when the query is empty it defaults to 1 so recency +
    /// importance still rank entries.
    /// </summary>
    public static double Score(Reflection r, string query, DateTimeOffset now)
    {
        double relevance = string.IsNullOrWhiteSpace(query) ? 1.0 : LexicalOverlap(query, r.Content);
        double importanceWeight = 0.5 + 0.5 * Math.Clamp(r.Importance, 0, 1);
        double ageDays = Math.Max(0, (now - r.Timestamp).TotalDays);
        double recency = Math.Exp(-ageDays / 14.0);   // ~2-week half-life-ish decay
        return relevance * importanceWeight * recency;
    }

    /// <summary>Token-overlap relevance in [0,1] (Jaccard-ish over lowercased word sets).</summary>
    public static double LexicalOverlap(string query, string content)
    {
        var q = Tokenize(query);
        var c = Tokenize(content);
        if (q.Count == 0 || c.Count == 0) return 0.0;
        int inter = q.Count(c.Contains);
        return (double)inter / q.Count;
    }

    private static HashSet<string> Tokenize(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tok in s.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?',
            '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
            if (tok.Length > 2) set.Add(tok);
        return set;
    }

    /// <summary>
    /// Assemble a token-budgeted block from scored reflections (highest score first). The HARD CAP
    /// is on total injected tokens (injectTokenBudget), NOT on per-reflection length or count - a
    /// reflection can be multi-line and is included whole until the budget is hit. Appends each
    /// included reflection to <paramref name="picked"/>. Returns "" if nothing fit.
    /// </summary>
    private static string Assemble(
        List<(Reflection r, double score)> ordered, int tokenBudget, string header, List<Reflection> picked)
    {
        // ~2.5 chars/token (matches Common.EstimateTokenCount); convert the token cap to a char cap.
        int charBudget = Math.Max(200, (int)(tokenBudget * 2.5));
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine("Background context distilled from this and prior sessions. Treat as hints; do NOT");
        sb.AppendLine("query memory tools yourself. Flag any reflection that conflicts with what you observe.");
        int used = sb.Length;
        foreach (var (r, _) in ordered)
        {
            // Render the reflection whole (may be multi-line); only the TOTAL block is capped.
            var body = r.Content.Replace("\r\n", "\n").TrimEnd();
            var entry = "- " + body.Replace("\n", "\n  ");
            if (used + entry.Length + 2 > charBudget)
            {
                // If nothing has been added yet, include this one (truncated) so the block is never
                // empty just because the top reflection is long; otherwise stop at the budget.
                if (picked.Count == 0)
                {
                    int room = Math.Max(0, charBudget - used - 16);
                    if (room > 40)
                    {
                        sb.AppendLine(entry.Length > room ? entry[..room] + " ..." : entry);
                        picked.Add(r);
                        r.AccessCount++;
                    }
                }
                break;
            }
            sb.AppendLine(entry);
            used += entry.Length + 2;
            picked.Add(r);
            r.AccessCount++;
        }
        sb.AppendLine("[END DEEP MEMORY]");
        return picked.Count == 0 ? string.Empty : sb.ToString();
    }
}
