using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuxSwarm.Utils.Teams;

/// <summary>
/// The persisted snapshot of a live (or resumable) team: roster, coordination policy, and
/// activity timestamps. Written to <c>{TeamsDirectory}/{slug}/team.json</c> so a relaunch can
/// list the team as resumable. Purely additive runtime state - no existing path reads it.
/// </summary>
public sealed class TeamState
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("lead")]
    public string Lead { get; set; } = string.Empty;

    [JsonPropertyName("members")]
    public List<string> Members { get; set; } = [];

    [JsonPropertyName("coordination")]
    public string Coordination { get; set; } = "fanout";

    /// <summary>Free-form lifecycle marker: "active", "idle", "closed".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastActive")]
    public DateTimeOffset LastActive { get; set; } = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Filesystem-safe slug for a team name. The giga: prefix (M6) becomes "giga-" on disk while
    /// the display name keeps its colon; any other path-hostile character is replaced with '-'.
    /// </summary>
    public static string Slug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "team";
        var s = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '-');
        s = s.Replace(':', '-');
        return string.IsNullOrWhiteSpace(s) ? "team" : s;
    }

    /// <summary>The install-dir root for a team's persisted state ({TeamsDirectory}/{slug}).</summary>
    public static string RootFor(string name)
        => Path.Combine(PlatformContext.TeamsDirectory, Slug(name));

    /// <summary>Persist this snapshot to {root}/team.json (best-effort, atomic via temp+move).</summary>
    public void Save()
    {
        try
        {
            var root = RootFor(Name);
            Directory.CreateDirectory(root);
            var tmp = Path.Combine(root, "team.json.tmp");
            var dst = Path.Combine(root, "team.json");
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, dst, overwrite: true);
        }
        catch { /* best-effort; an unwritable Teams dir must not crash a team launch */ }
    }

    /// <summary>Load a persisted team snapshot by name, or null when none exists/parse fails.</summary>
    public static TeamState? Load(string name)
    {
        try
        {
            var path = Path.Combine(RootFor(name), "team.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TeamState>(File.ReadAllText(path), JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Enumerate every persisted team snapshot under the install-dir Teams directory.</summary>
    public static List<TeamState> LoadAll()
    {
        var list = new List<TeamState>();
        try
        {
            var dir = PlatformContext.TeamsDirectory;
            if (!Directory.Exists(dir)) return list;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var path = Path.Combine(sub, "team.json");
                if (!File.Exists(path)) continue;
                try
                {
                    var st = JsonSerializer.Deserialize<TeamState>(File.ReadAllText(path), JsonOpts);
                    if (st is not null) list.Add(st);
                }
                catch { /* skip a corrupt team.json */ }
            }
        }
        catch { /* unreadable Teams dir -> empty */ }
        return list;
    }
}
