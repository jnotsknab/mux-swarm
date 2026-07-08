using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuxSwarm.Utils.Memory;

/// <summary>
/// One distilled reflection: a durable, verbal lesson or importance-scored fact derived from the
/// live session. Carries provenance + role + importance so retrieval can score it and corrupt
/// entries stay traceable (OWASP ASI06 memory-poisoning mitigation).
/// </summary>
public sealed class Reflection
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    /// <summary>0..1 importance; higher = more durable / higher retrieval priority.</summary>
    [JsonPropertyName("importance")] public double Importance { get; set; } = 0.5;
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Role this reflection is scoped to: "lead" / "shared" / a specific sub-agent name.</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "shared";
    /// <summary>Where it came from (source turn / agent), for traceability.</summary>
    [JsonPropertyName("provenance")] public string Provenance { get; set; } = "";
    [JsonPropertyName("accessCount")] public int AccessCount { get; set; }
}

/// <summary>
/// Single-file, auto-pruned reflection store. All reflections live in ONE JSON document at
/// <c>Context/reflections.json</c>, rewritten atomically (temp sibling + File.Move) on every
/// mutation - so the deep-memory subsystem never explodes into thousands of tiny files. The store
/// is bounded (<see cref="MaxReflections"/> most-recent kept, oldest evicted) and deduplicated by
/// content, which suits the temporal nature of reflections. An in-memory cache backs reads so the
/// hot injection path does not hit disk every turn.
///
/// Filesystem is ALWAYS the primary store. The knowledge-graph + ChromaDB MCP servers are OPTIONAL
/// accelerators mirrored best-effort; when absent/failing the store degrades silently to
/// filesystem-only (deep mode stays active, no warning). All methods are best-effort, never throw.
/// </summary>
public static class ReflectionStore
{
    /// <summary>Default hard cap on retained reflections when not configured.</summary>
    public const int DefaultMaxReflections = 1000;

    /// <summary>Effective retained-reflection cap (reflectionAgent.maxReflections, floored at 10).</summary>
    public static int MaxReflections
    {
        get
        {
            try { return Math.Max(10, App.SwarmConfig?.ResolveReflection().MaxReflections ?? DefaultMaxReflections); }
            catch { return DefaultMaxReflections; }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly object IoLock = new();

    // In-memory authoritative copy (loaded lazily from disk once, then kept in sync on append).
    private static List<Reflection>? _cache;

    /// <summary>The single JSON file backing the store.</summary>
    public static string FilePath =>
        Path.Combine(PlatformContext.ContextDirectory, "reflections.json");

    /// <summary>Legacy per-file directory (migrated + removed on first load if present).</summary>
    private static string LegacyDir =>
        Path.Combine(PlatformContext.ContextDirectory, "Reflections");

    /// <summary>Status/display: the store file path (kept name-compatible with the old API).</summary>
    public static string Directory => FilePath;

    private static List<Reflection> Cache()
    {
        lock (IoLock)
        {
            if (_cache is not null) return _cache;
            _cache = LoadFromDisk();
            return _cache;
        }
    }

    private static List<Reflection> LoadFromDisk()
    {
        var list = new List<Reflection>();
        try
        {
            if (File.Exists(FilePath))
            {
                var arr = JsonSerializer.Deserialize<List<Reflection>>(File.ReadAllText(FilePath));
                if (arr is not null)
                    list = arr.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Content)).ToList();
            }
            else
            {
                // One-time migration from the legacy per-file directory.
                MigrateLegacy(list);
            }
        }
        catch { /* return whatever parsed */ }
        return list;
    }

    private static void MigrateLegacy(List<Reflection> into)
    {
        try
        {
            if (!System.IO.Directory.Exists(LegacyDir)) return;
            foreach (var path in System.IO.Directory.EnumerateFiles(LegacyDir, "*.json"))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<Reflection>(File.ReadAllText(path));
                    if (r is not null && !string.IsNullOrWhiteSpace(r.Content)) into.Add(r);
                }
                catch { /* skip corrupt */ }
            }
            if (into.Count > 0)
            {
                FlushNoLock(into);                       // write the consolidated file
                try { System.IO.Directory.Delete(LegacyDir, recursive: true); } catch { }
            }
        }
        catch { /* migration is best-effort */ }
    }

    /// <summary>Count of persisted reflections.</summary>
    public static int Count()
    {
        try { return Cache().Count; } catch { return 0; }
    }

    /// <summary>Timestamp of the most recently written reflection, or null when empty.</summary>
    public static DateTimeOffset? LastWrite()
    {
        try
        {
            var c = Cache();
            return c.Count == 0 ? null : c.Max(r => r.Timestamp);
        }
        catch { return null; }
    }

    /// <summary>A snapshot copy of every persisted reflection (newest tracked by Timestamp).</summary>
    public static List<Reflection> LoadAll()
    {
        try { lock (IoLock) return new List<Reflection>(Cache()); }
        catch { return new List<Reflection>(); }
    }

    /// <summary>
    /// Append a reflection: dedup by (trimmed) content, then atomically rewrite the single file,
    /// pruning to the <see cref="MaxReflections"/> most-recent. Returns the file path on success,
    /// or null on failure. The optional Chroma/KG mirror is done separately by <see cref="MirrorAsync"/>.
    /// </summary>
    public static string? Append(Reflection r)
    {
        if (r is null || string.IsNullOrWhiteSpace(r.Content)) return null;
        try
        {
            lock (IoLock)
            {
                var list = Cache();
                var key = r.Content.Trim();
                // Dedup: if the same content already exists, refresh it in place (bump timestamp +
                // importance to the higher of the two) instead of adding a near-duplicate.
                var dupe = list.FirstOrDefault(x =>
                    string.Equals(x.Content.Trim(), key, StringComparison.OrdinalIgnoreCase));
                if (dupe is not null)
                {
                    dupe.Timestamp = r.Timestamp;
                    dupe.Importance = Math.Max(dupe.Importance, r.Importance);
                    dupe.AccessCount += 1;
                }
                else
                {
                    list.Add(r);
                }

                // Prune oldest beyond the cap.
                if (list.Count > MaxReflections)
                {
                    list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                    list.RemoveRange(0, list.Count - MaxReflections);
                }

                FlushNoLock(list);
            }
            return FilePath;
        }
        catch { return null; }
    }

    private static void FlushNoLock(List<Reflection> list)
    {
        try
        {
            System.IO.Directory.CreateDirectory(PlatformContext.ContextDirectory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
            _cache = list;
        }
        catch { /* best-effort durability; in-memory cache stays authoritative this session */ }
    }

    /// <summary>
    /// Best-effort mirror of a reflection into the dedicated ChromaDB reflection collection for the
    /// injector's semantic ranking. This is the ONLY store the pipeline writes to; the agents'
    /// knowledge bases (knowledge graph, user_memory, etc.) are READ-ONLY to this subsystem and are
    /// owned by the agents themselves. Silently no-ops when Chroma is not connected or any call
    /// fails - the filesystem copy from <see cref="Append"/> remains authoritative. Never throws.
    /// </summary>
    public static async Task MirrorAsync(Reflection r, CancellationToken ct = default)
    {
        if (r is null || string.IsNullOrWhiteSpace(r.Content)) return;
        await TryChromaUpsertAsync(r, ct);
    }

    /// <summary>True when the ChromaDB MCP server is connected (accelerator available).</summary>
    public static bool ChromaAvailable() => App.McpClients.ContainsKey("ChromaDB");

    /// <summary>True when the Memory (knowledge-graph) MCP server is connected.</summary>
    public static bool KgAvailable() => App.McpClients.ContainsKey("Memory");

    private const string ChromaCollection = "mux_reflections";

    private static async Task TryChromaUpsertAsync(Reflection r, CancellationToken ct)
    {
        try
        {
            if (!App.McpClients.TryGetValue("ChromaDB", out var client)) return;
            try
            {
                await client.CallToolAsync("chroma_create_collection",
                    new Dictionary<string, object?> { ["collection_name"] = ChromaCollection }!, cancellationToken: ct);
            }
            catch { /* already exists / unsupported - ignore */ }

            await client.CallToolAsync("chroma_add_documents", new Dictionary<string, object?>
            {
                ["collection_name"] = ChromaCollection,
                ["documents"] = new[] { r.Content },
                ["ids"] = new[] { r.Id },
                ["metadatas"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = r.Role,
                        ["importance"] = r.Importance,
                        ["timestamp"] = r.Timestamp.ToUnixTimeSeconds(),
                        ["provenance"] = r.Provenance,
                    }
                }
            }!, cancellationToken: ct);
        }
        catch { /* silent degrade */ }
    }
}
