using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuxSwarm.State;

/// <summary>The kind of an inter-agent team message. Drives both routing semantics (Shutdown is a
/// graceful stop signal) and the m-log glyph in the Agent View.</summary>
public enum MsgType
{
    Info,
    Question,
    Answer,
    Handoff,
    Shutdown,
}

/// <summary>
/// One peer-to-peer message between team agents. Persisted as a single JSON file per message in the
/// recipient's inbox so the conversation survives a resume (mirrors the TaskBoard task-file model).
/// </summary>
public sealed class TeamMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public MsgType Type { get; set; } = MsgType.Info;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("sent")]
    public DateTimeOffset Sent { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True once the recipient has drained (read) this message; peeking does not set it.</summary>
    [JsonPropertyName("read")]
    public bool Read { get; set; }
}

/// <summary>
/// A team's inter-agent mailbox: per-agent file inboxes at <c>{teamRoot}/inboxes/{agent}/{id}.json</c>.
/// Mirrors the <see cref="TaskBoard"/> design - an in-process lock guarantees ordering + atomic
/// mutation within one runtime, and the on-disk JSON is the durable record that reloads intact after
/// a resume. The lead sends messages (peer-to-peer or broadcast) and reads its own inbox; a
/// <see cref="MsgType.Shutdown"/> message sets a cooperative shutdown flag the member run-loops poll
/// between claims, so a teammate stops gracefully rather than being hard-cancelled.
///
/// M4 scope: the LEAD is the active sender/reader; members are recipients (their inbox is delivered
/// to, the shutdown flag stops their loop). Member-initiated send/read is a later, flag-gated follow-up
/// (it needs per-member tool injection through the parallel-worker path).
/// </summary>
public sealed class Mailbox
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string _inboxesDir;
    private int _seq;

    // Cooperative shutdown flags keyed by agent name; set when a Shutdown message is delivered to
    // that agent, polled by the member run-loops so a teammate stops between claims (not mid-call).
    private readonly HashSet<string> _shutdown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The team this mailbox belongs to (display name).</summary>
    public string Team { get; }

    /// <summary>The on-disk inboxes root ({teamRoot}/inboxes).</summary>
    public string InboxesDirectory => _inboxesDir;

    private Mailbox(string team, string teamRoot)
    {
        Team = team;
        _inboxesDir = Path.Combine(teamRoot, "inboxes");
    }

    /// <summary>
    /// Open (or create) the mailbox rooted at <paramref name="teamRoot"/>, restoring any persisted
    /// messages from <c>{teamRoot}/inboxes/{agent}/*.json</c> so a resumed team keeps its history.
    /// Undelivered Shutdown messages re-arm the cooperative flag on load. Unparseable files are
    /// skipped rather than aborting the load.
    /// </summary>
    public static Mailbox Open(string team, string teamRoot)
    {
        var box = new Mailbox(team, teamRoot);
        Directory.CreateDirectory(box._inboxesDir);
        foreach (var agentDir in Directory.EnumerateDirectories(box._inboxesDir))
        {
            var agent = Path.GetFileName(agentDir);
            foreach (var file in Directory.EnumerateFiles(agentDir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<TeamMessage>(File.ReadAllText(file), JsonOpts);
                    if (m is null || string.IsNullOrEmpty(m.Id)) continue;
                    if (int.TryParse(m.Id.TrimStart('m', 'M', '#'), out var n) && n > box._seq)
                        box._seq = n;
                    if (m.Type == MsgType.Shutdown && !m.Read)
                        box._shutdown.Add(agent);
                }
                catch { /* skip unparseable message files */ }
            }
        }
        return box;
    }

    private static string Slug(string agent)
    {
        var s = (agent ?? string.Empty).Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "_" : s;
    }

    private string InboxDir(string agent) => Path.Combine(_inboxesDir, Slug(agent));

    /// <summary>
    /// Send <paramref name="body"/> from <paramref name="from"/> to <paramref name="to"/>. A
    /// <paramref name="to"/> of "all" or "*" broadcasts to every <paramref name="members"/> inbox
    /// (excluding the sender). Returns the number of inboxes delivered to. A Shutdown message arms
    /// the recipient's cooperative stop flag.
    /// </summary>
    public int Send(string from, string to, MsgType type, string body, IReadOnlyList<string> members)
    {
        var recipients = new List<string>();
        var dest = (to ?? string.Empty).Trim();
        if (dest.Equals("all", StringComparison.OrdinalIgnoreCase) || dest == "*")
            recipients.AddRange(members.Where(m => !string.Equals(m, from, StringComparison.OrdinalIgnoreCase)));
        else
            recipients.Add(dest);

        int delivered = 0;
        lock (_gate)
        {
            foreach (var r in recipients)
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                var msg = new TeamMessage
                {
                    Id = $"m{++_seq}",
                    From = from,
                    To = r,
                    Type = type,
                    Body = body ?? string.Empty,
                };
                var dir = InboxDir(r);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{msg.Id}.json"),
                    JsonSerializer.Serialize(msg, JsonOpts));
                if (type == MsgType.Shutdown) _shutdown.Add(r);
                delivered++;
            }
        }
        return delivered;
    }

    /// <summary>
    /// Read <paramref name="agent"/>'s inbox. When <paramref name="drain"/> is true the returned
    /// messages are marked Read (and the change persisted) so a subsequent read only returns new
    /// ones; when false it is a non-destructive peek. Ordered oldest-first.
    /// </summary>
    public IReadOnlyList<TeamMessage> ReadInbox(string agent, bool drain)
    {
        lock (_gate)
        {
            var dir = InboxDir(agent);
            if (!Directory.Exists(dir)) return Array.Empty<TeamMessage>();
            var msgs = new List<(string File, TeamMessage Msg)>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<TeamMessage>(File.ReadAllText(file), JsonOpts);
                    if (m is not null && !string.IsNullOrEmpty(m.Id)) msgs.Add((file, m));
                }
                catch { /* skip */ }
            }
            msgs.Sort((a, b) => a.Msg.Sent.CompareTo(b.Msg.Sent));
            var fresh = msgs.Where(x => !x.Msg.Read).Select(x => x.Msg).ToList();
            if (drain)
            {
                foreach (var (file, m) in msgs)
                    if (!m.Read)
                    {
                        m.Read = true;
                        try { File.WriteAllText(file, JsonSerializer.Serialize(m, JsonOpts)); } catch { }
                    }
            }
            return fresh;
        }
    }

    /// <summary>Full message history for one agent's inbox (read + unread), oldest-first. Used by the
    /// Agent View m-log so the user can audit cross-agent chatter without draining the inbox.</summary>
    public IReadOnlyList<TeamMessage> History(string agent)
    {
        lock (_gate)
        {
            var dir = InboxDir(agent);
            if (!Directory.Exists(dir)) return Array.Empty<TeamMessage>();
            var msgs = new List<TeamMessage>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<TeamMessage>(File.ReadAllText(file), JsonOpts);
                    if (m is not null && !string.IsNullOrEmpty(m.Id)) msgs.Add(m);
                }
                catch { /* skip */ }
            }
            msgs.Sort((a, b) => a.Sent.CompareTo(b.Sent));
            return msgs;
        }
    }

    /// <summary>True when a Shutdown message has been delivered to <paramref name="agent"/>. Polled by
    /// the member run-loops between claims for graceful stop.</summary>
    public bool IsShutdownRequested(string agent)
    {
        lock (_gate) { return _shutdown.Contains((agent ?? string.Empty).Trim()); }
    }

    /// <summary>Unread message count for one agent's inbox.</summary>
    public int UnreadCountFor(string agent)
    {
        lock (_gate)
        {
            var dir = InboxDir(agent);
            if (!Directory.Exists(dir)) return 0;
            int n = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<TeamMessage>(File.ReadAllText(file), JsonOpts);
                    if (m is not null && !m.Read) n++;
                }
                catch { /* skip */ }
            }
            return n;
        }
    }

    /// <summary>True when <paramref name="agent"/> has an unread message that warrants a turn to act
    /// on (a Question or Handoff). Info/Answer messages are FYI and do not by themselves wake an idle
    /// member - they are still delivered into the next task's brief. Shutdown is handled separately.</summary>
    public bool HasActionableUnread(string agent)
    {
        lock (_gate)
        {
            var dir = InboxDir(agent);
            if (!Directory.Exists(dir)) return false;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<TeamMessage>(File.ReadAllText(file), JsonOpts);
                    if (m is not null && !m.Read && (m.Type == MsgType.Question || m.Type == MsgType.Handoff))
                        return true;
                }
                catch { /* skip */ }
            }
            return false;
        }
    }

    /// <summary>Total unread messages across all inboxes (for a lightweight UI badge).</summary>
    public int UnreadCount()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_inboxesDir)) return 0;
            int n = 0;
            foreach (var dir in Directory.EnumerateDirectories(_inboxesDir))
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    try
                    {
                        var m = JsonSerializer.Deserialize<TeamMessage>(File.ReadAllText(file), JsonOpts);
                        if (m is not null && !m.Read) n++;
                    }
                    catch { /* skip */ }
                }
            return n;
        }
    }
}
