using System.Text;

namespace MuxSwarm.Utils.Memory;

/// <summary>
/// Selects a small, budgeted block of reflections to inject into an agent when deep mode is on.
/// Working agents NEVER query memory themselves - this is the only read path. Scoring is
/// relevance * importance * recency; entries below relevanceFloor are dropped; the assembled block
/// is hard-capped by injectTokenBudget (truncated, never overflowed).
///
/// RELEVANCE is HYBRID: a semantic cosine term (from the dedicated ChromaDB reflection collection,
/// via <see cref="ReflectionSemanticIndex"/>) blended with a lexical token-overlap term. Semantic
/// recovers paraphrase/concept matches the lexical matcher misses ("build fails" ~ "compilation
/// error"); lexical preserves exact rare-identifier precision (g12.92, PR #59, a filename) that
/// dense embeddings blur. When Chroma is unavailable/times out, relevance falls back to lexical
/// only - deep mode never breaks.
///
/// Two surfaces x two cadences:
///   * <see cref="BuildBlock"/> (sync, lexical) - baked into a preamble at session/sub-agent start.
///     Instant, never blocks startup; the first mid-turn delta upgrades the injection to semantic.
///   * <see cref="BuildDeltaAsync"/> (async, semantic) - only reflections NOT yet injected this
///     session, ranked semantically, surfaced as a per-round-trip system note so a long-lived lead
///     session picks up freshly-gathered memory MID-SESSION at the (seconds-scale) tool-call cadence.
///
/// Both surfaces obey injectTokenBudget. Best-effort; never throws.
/// </summary>
public static class ReflectionInjector
{
    /// <summary>The latest user message / task, used as the relevance query. Set by the session
    /// just before a preamble is built. Null/empty = recency+importance only.</summary>
    public static string? CurrentQuery { get; set; }

    // Blend weights: relevance = SemanticWeight*cosine + LexicalWeight*lexical. Lexical is retained
    // (not zeroed) so exact-identifier queries never regress when semantic is weak/unavailable.
    private const double SemanticWeight = 0.65;
    private const double LexicalWeight = 0.35;

    // How many candidates the semantic oracle ranks (it only knows ids+distances; we map back to
    // full reflections locally). Generous so the local rerank has a rich pool.
    private const int SemanticTopK = 50;

    // Reflection ids already surfaced to the lead this session (full block + deltas), so a delta
    // only ever carries what is genuinely NEW. Reset when a new lead session starts.
    private static readonly HashSet<string> _injectedIds = new(StringComparer.Ordinal);
    private static readonly object _gate = new();

    /// <summary>Reset per-session state (injected-id tracking + semantic cache) on a fresh lead session.</summary>
    public static void ResetSession()
    {
        lock (_gate) _injectedIds.Clear();
        ReflectionSemanticIndex.ResetCache();
    }

    // ---- SYNC surface (lexical only): preamble-time, never blocks startup -----------------------

    /// <summary>
    /// Build the FULL reflection block for <paramref name="agentName"/> (preamble-time injection),
    /// LEXICAL-only so it is instant and never blocks session start. Empty when deep mode is off,
    /// scope excludes this agent, or nothing clears the floor. Marks everything it includes as
    /// injected so a subsequent delta won't repeat it. The first mid-turn delta upgrades to semantic.
    /// </summary>
    public static string BuildBlock(string agentName, bool isLead)
    {
        try
        {
            var cfg = App.SwarmConfig?.ResolveReflection();
            if (cfg is null || !cfg.IsDeep) return string.Empty;
            if (!ScopeAllows(cfg, isLead)) return string.Empty;

            var scored = ScoredFor(agentName, isLead, cfg, null);
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

    /// <summary>Sync lexical delta (kept for callers/tests that cannot await). Prefer <see cref="BuildDeltaAsync"/>.</summary>
    public static string BuildDelta(string agentName, bool isLead)
    {
        try
        {
            var cfg = App.SwarmConfig?.ResolveReflection();
            if (cfg is null || !cfg.IsDeep || !isLead) return string.Empty;
            if (!ScopeAllows(cfg, isLead)) return string.Empty;

            List<(Reflection r, double score)> scored;
            lock (_gate)
                scored = ScoredFor(agentName, isLead, cfg, null)
                    .Where(x => !_injectedIds.Contains(x.r.Id))
                    .ToList();
            if (scored.Count == 0) return string.Empty;

            var picked = new List<Reflection>();
            string block = Assemble(scored, cfg.InjectTokenBudget, "[DEEP MEMORY - new since last turn]", picked);
            if (!string.IsNullOrEmpty(block))
                lock (_gate) foreach (var r in picked) _injectedIds.Add(r.Id);
            return block;
        }
        catch { return string.Empty; }
    }

    // ---- ASYNC surface (hybrid semantic): mid-turn delta at tool-call cadence -------------------

    /// <summary>
    /// Build a DELTA block ranked with HYBRID semantic+lexical relevance: only reflections not yet
    /// injected to the lead this session, scored + token-capped. Empty when nothing is new. Queries
    /// the ChromaDB reflection collection (generous timeout - the query overlaps the seconds-scale
    /// gap before the next tool call, so latency is effectively free) and falls back to lexical when
    /// Chroma is unavailable. Marks what it returns as injected. Lead/orchestrator scope only.
    /// </summary>
    public static async Task<string> BuildDeltaAsync(string agentName, bool isLead, CancellationToken ct = default)
    {
        try
        {
            var cfg = App.SwarmConfig?.ResolveReflection();
            if (cfg is null || !cfg.IsDeep || !isLead) return string.Empty;
            if (!ScopeAllows(cfg, isLead)) return string.Empty;

            var semantic = await ReflectionSemanticIndex.QueryAsync(
                CurrentQuery, SemanticTopK, cfg.InjectQueryTimeoutMs, ct);

            List<(Reflection r, double score)> scored;
            lock (_gate)
                scored = ScoredFor(agentName, isLead, cfg, semantic)
                    .Where(x => !_injectedIds.Contains(x.r.Id))
                    .ToList();
            if (scored.Count == 0) return string.Empty;

            var picked = new List<Reflection>();
            string block = Assemble(scored, cfg.InjectTokenBudget, "[DEEP MEMORY - new since last turn]", picked);
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

    /// <summary>The role-filtered, floor-passing, score-ordered candidates for an agent. When
    /// <paramref name="semantic"/> is non-null, relevance blends its cosine with lexical overlap;
    /// when null (sync path / Chroma down) relevance is lexical-only.</summary>
    private static List<(Reflection r, double score)> ScoredFor(
        string agentName, bool isLead, ReflectionConfig cfg, IReadOnlyDictionary<string, double>? semantic)
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
            .Select(r => (r, score: Score(r, query, now, semantic)))
            .Where(x => x.score >= cfg.RelevanceFloor)
            .OrderByDescending(x => x.score)
            .ToList();
    }

    /// <summary>
    /// Composite score = relevance * (0.5 + 0.5*importance) * recencyDecay. RELEVANCE is hybrid when
    /// a semantic map is supplied (SemanticWeight*cosine + LexicalWeight*lexical), else lexical-only;
    /// when the query is empty it defaults to 1 so recency + importance still rank entries.
    /// </summary>
    public static double Score(Reflection r, string query, DateTimeOffset now,
        IReadOnlyDictionary<string, double>? semantic = null)
    {
        double relevance;
        if (string.IsNullOrWhiteSpace(query))
        {
            relevance = 1.0;
        }
        else
        {
            double lexical = LexicalOverlap(query, r.Content);
            if (semantic is not null && semantic.TryGetValue(r.Id, out var cos))
                relevance = SemanticWeight * cos + LexicalWeight * lexical;
            else if (semantic is not null)
                // Semantic ran but this reflection was outside topK: it is not among the nearest, so
                // its semantic contribution is ~0; keep only the (down-weighted) lexical signal.
                relevance = LexicalWeight * lexical;
            else
                relevance = lexical;
        }

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
            var body = r.Content.Replace("\r\n", "\n").TrimEnd();
            var entry = "- " + body.Replace("\n", "\n  ");
            if (used + entry.Length + 2 > charBudget)
            {
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
