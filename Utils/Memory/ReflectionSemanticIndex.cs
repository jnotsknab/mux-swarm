using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace MuxSwarm.Utils.Memory;

/// <summary>
/// Semantic RANKER for the deep-memory injector. Reflections are mirrored into a dedicated ChromaDB
/// collection (<c>mux_reflections</c>) at gather time; this class queries that collection with the
/// current session context and returns a map of <c>reflection id -&gt; semantic relevance</c> in
/// [0,1]. Only ids + distances are ever pulled back - NEVER document text - because the reflection
/// PAYLOAD is always injected from the authoritative filesystem store; Chroma is used purely as the
/// similarity oracle that decides WHICH reflections rank highest.
///
/// Chroma runs the embedding model (MiniLM, 384-dim) server-side over stdio, so this stays fully
/// cross-platform with no in-process embedder and no new dependency. Best-effort: when the ChromaDB
/// MCP server is absent, times out, or errors, the query returns an EMPTY map and the injector
/// degrades to lexical scoring. Results are cached by query text so repeated mid-turn deltas within
/// a turn (same CurrentQuery) do not re-hit Chroma.
/// </summary>
public static class ReflectionSemanticIndex
{
    /// <summary>The dedicated reflection vector collection (kept in sync by the gatherer mirror).</summary>
    public const string Collection = "mux_reflections";

    private static readonly object _gate = new();
    private static string? _cachedQuery;
    private static IReadOnlyDictionary<string, double>? _cachedMap;

    /// <summary>True when the ChromaDB MCP accelerator is connected (semantic ranking available).</summary>
    public static bool Available() => App.McpClients.ContainsKey("ChromaDB");

    /// <summary>
    /// Query the reflection collection for <paramref name="query"/> and return id -&gt; relevance in
    /// [0,1] (cosine, derived from Chroma's L2^2 distance for MiniLM-normalized vectors). Empty map
    /// on any failure/timeout/absence. Cached by query text. Never throws.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, double>> QueryAsync(
        string? query, int topK, int timeoutMs, CancellationToken ct)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0) return Empty;

        lock (_gate)
            if (_cachedQuery == q && _cachedMap is not null) return _cachedMap;

        if (!App.McpClients.TryGetValue("ChromaDB", out var client)) return Empty;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Math.Max(250, timeoutMs));

            var res = await client.CallToolAsync("chroma_query_documents", new Dictionary<string, object?>
            {
                ["collection_name"] = Collection,
                ["query_texts"] = new[] { q },
                ["n_results"] = Math.Max(1, topK),
                ["include"] = new[] { "distances" },   // ids come back implicitly; never pull documents
            }!, cancellationToken: timeoutCts.Token);

            var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(b => b.Text));
            var map = Parse(text);

            lock (_gate) { _cachedQuery = q; _cachedMap = map; }
            return map;
        }
        catch { return Empty; }
    }

    /// <summary>Drop the cached query result (call when a fresh lead session starts).</summary>
    public static void ResetCache()
    {
        lock (_gate) { _cachedQuery = null; _cachedMap = null; }
    }

    // Parse Chroma's {"ids":[[...]], "distances":[[...]]} into id -> cosine relevance in [0,1].
    private static IReadOnlyDictionary<string, double> Parse(string json)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array) return map;
            if (idsEl.GetArrayLength() == 0) return map;
            var ids = idsEl[0];
            root.TryGetProperty("distances", out var distEl);
            JsonElement dists = distEl.ValueKind == JsonValueKind.Array && distEl.GetArrayLength() > 0
                ? distEl[0] : default;

            for (int i = 0; i < ids.GetArrayLength(); i++)
            {
                var id = ids[i].GetString();
                if (string.IsNullOrEmpty(id)) continue;
                double relevance = 1.0;
                if (dists.ValueKind == JsonValueKind.Array && i < dists.GetArrayLength()
                    && dists[i].TryGetDouble(out var d))
                {
                    // MiniLM vectors are L2-normalized => L2^2 = 2 - 2*cos => cos = 1 - d/2.
                    relevance = Math.Clamp(1.0 - d / 2.0, 0.0, 1.0);
                }
                map[id] = relevance;
            }
        }
        catch { /* unparseable -> empty */ }
        return map;
    }

    private static readonly IReadOnlyDictionary<string, double> Empty =
        new Dictionary<string, double>(0);
}
