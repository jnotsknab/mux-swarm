namespace MuxSwarm.Utils;

/// <summary>
/// Free-form session tags persisted as a sidecar file (<see cref="TagFileName"/>) inside a
/// session directory. CRITICAL: the sidecar is intentionally NOT a *.json file - the
/// single-agent-vs-swarm resume detector counts <c>*.json</c> files in a session dir, so a
/// json sidecar would silently break resume classification. The ".muxtag" extension is
/// invisible to that heuristic.
///
/// Format: plain UTF-8, one tag per line (append-only). Greppable and trivially mergeable.
/// </summary>
public static class SessionTags
{
    public const string TagFileName = "tags.muxtag";

    private static string TagPath(string sessionDir) => Path.Combine(sessionDir, TagFileName);

    /// <summary>Append a free-form tag line to a session's sidecar. Best-effort; returns success.</summary>
    public static bool Append(string sessionDir, string tag)
    {
        if (string.IsNullOrWhiteSpace(sessionDir) || string.IsNullOrWhiteSpace(tag)) return false;
        try
        {
            Directory.CreateDirectory(sessionDir);
            // Normalize to a single line so the one-tag-per-line invariant holds.
            var line = tag.Replace("\r", " ").Replace("\n", " ").Trim();
            if (line.Length == 0) return false;
            File.AppendAllText(TagPath(sessionDir), line + "\n");
            return true;
        }
        catch { return false; }
    }

    /// <summary>All tags for a session dir, newest-appended last. Empty if none / on error.</summary>
    public static List<string> Read(string sessionDir)
    {
        try
        {
            var p = TagPath(sessionDir);
            if (!File.Exists(p)) return new List<string>();
            return File.ReadAllLines(p)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>True if a session dir has any tags.</summary>
    public static bool HasTags(string sessionDir) => Read(sessionDir).Count > 0;

    /// <summary>
    /// A compact single-line join of a session's tags for display / fuzzy-search, or null if
    /// none. Used to enrich the resume picker's preview string so tags are both shown and
    /// matchable without changing any tuple shapes.
    /// </summary>
    public static string? TagLabel(string sessionDir)
    {
        var tags = Read(sessionDir);
        if (tags.Count == 0) return null;
        return string.Join(", ", tags);
    }

    /// <summary>Read tags by session id (folder name) via the sessions root.</summary>
    public static string? TagLabelById(string sessionId)
    {
        try
        {
            var dir = Path.Combine(PlatformContext.SessionsDirectory, sessionId);
            return Directory.Exists(dir) ? TagLabel(dir) : null;
        }
        catch { return null; }
    }
}
