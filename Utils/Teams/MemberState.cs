using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuxSwarm.Utils.Teams;

/// <summary>
/// The persisted snapshot of one team member's working state: its activity, how many board tasks
/// it has claimed/completed, and the running token estimate of its warm (persistent) session.
/// Written to <c>{TeamsDirectory}/{slug}/members/{member}.json</c> so a relaunch can show what
/// each member was doing and so the Agent View / kanban can surface member progress. Purely
/// additive runtime state - no existing path reads it; absent files simply mean "no history yet".
/// </summary>
public sealed class MemberState
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-form marker: "idle", "running", "done".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";

    /// <summary>Context mode this member is running under: "persistent" or "fresh".</summary>
    [JsonPropertyName("context")]
    public string Context { get; set; } = "persistent";

    /// <summary>The task id this member is currently running, or null when idle.</summary>
    [JsonPropertyName("currentTask")]
    public string? CurrentTask { get; set; }

    /// <summary>Count of board tasks this member has finished this team's lifetime.</summary>
    [JsonPropertyName("completedTasks")]
    public int CompletedTasks { get; set; }

    /// <summary>Number of pickups since this member's warm session was last (re)seeded - resets to
    /// 0 on a context compaction. 0 for fresh-context members (always one-shot).</summary>
    [JsonPropertyName("tasksSinceCompaction")]
    public int TasksSinceCompaction { get; set; }

    /// <summary>Number of times this member's persistent session has been auto-compacted.</summary>
    [JsonPropertyName("compactions")]
    public int Compactions { get; set; }

    /// <summary>Last estimated token size of this member's warm session (post any compaction).</summary>
    [JsonPropertyName("sessionTokens")]
    public int SessionTokens { get; set; }

    [JsonPropertyName("lastActive")]
    public DateTimeOffset LastActive { get; set; } = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string MemberFileSafe(string member)
    {
        var s = (member ?? string.Empty).Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '-');
        return string.IsNullOrWhiteSpace(s) ? "member" : s;
    }

    /// <summary>The members directory for a team ({TeamsDirectory}/{slug}/members).</summary>
    public static string DirFor(string teamName)
        => Path.Combine(TeamState.RootFor(teamName), "members");

    /// <summary>The on-disk path for one member's state file.</summary>
    public static string PathFor(string teamName, string member)
        => Path.Combine(DirFor(teamName), MemberFileSafe(member) + ".json");

    /// <summary>Persist this snapshot (best-effort, atomic via temp+move).</summary>
    public void Save(string teamName)
    {
        try
        {
            var dir = DirFor(teamName);
            Directory.CreateDirectory(dir);
            var dst = PathFor(teamName, Name);
            var tmp = dst + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, dst, overwrite: true);
        }
        catch { /* best-effort; an unwritable members dir must not crash a member run */ }
    }

    /// <summary>Load a member's persisted state, or null when none exists/parse fails.</summary>
    public static MemberState? Load(string teamName, string member)
    {
        try
        {
            var path = PathFor(teamName, member);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<MemberState>(File.ReadAllText(path), JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Enumerate every persisted member snapshot for a team.</summary>
    public static List<MemberState> LoadAll(string teamName)
    {
        var list = new List<MemberState>();
        try
        {
            var dir = DirFor(teamName);
            if (!Directory.Exists(dir)) return list;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var st = JsonSerializer.Deserialize<MemberState>(File.ReadAllText(file), JsonOpts);
                    if (st is not null) list.Add(st);
                }
                catch { /* skip a corrupt member.json */ }
            }
        }
        catch { /* unreadable members dir -> empty */ }
        return list;
    }
}
