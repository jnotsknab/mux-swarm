using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MuxSwarm.Utils;

/// <summary>
/// A live interactive single-agent / teams / stateless session that has been DETACHED (parked)
/// from the foreground via /detach and can be re-entered with /attach. The session's running
/// <see cref="ChatTask"/> (the ChatAgentAsync call) stays alive the whole time: on /detach the
/// async frame parks itself by awaiting <see cref="AttachAsync"/>, which preserves its entire live
/// closure (agent, session, in-memory history, token counters) with no disk round-trip or state
/// copy. The menu and the parked frame hand the single console reader back and forth through two
/// long-lived semaphores, so exactly one of them ever reads input at a time.
/// </summary>
public sealed class InteractiveSession
{
    public required string Id { get; init; }      // "sess1", "sess2", ...
    public required string Label { get; init; }   // agent name, "teams:<name>", "stateless"
    public required string Mode { get; init; }    // "agent" | "teams" | "stateless"
    public System.DateTime StartedAt { get; } = System.DateTime.UtcNow;

    // Display-only snapshot of the session's context size at park time (the parked frame is idle,
    // so this does not move while parked). Updated by the frame just before it parks.
    public uint Tokens;

    // "active" while foregrounded/running, "parked" while detached awaiting /attach.
    public volatile string Status = "active";

    // The running ChatAgentAsync task for this session. Stays referenced here so the menu can
    // re-await it after each attach and observe completion (quit/finish/error).
    public Task? ChatTask;

    // Rendezvous: the parked frame Releases _detach to tell the menu "I am parked"; the menu
    // Releases _attach to tell the parked frame "resume + take the console back". Initial count 0,
    // max 1 - a clean single-permit handoff with no field-swap races across detach/attach cycles.
    private readonly SemaphoreSlim _detach = new(0, 1);
    private readonly SemaphoreSlim _attach = new(0, 1);

    /// <summary>Called by the parked frame: announce the park, then block (async) until /attach.
    /// Preserves the whole ChatAgentAsync closure across the await.</summary>
    public async Task ParkAndAwaitAttachAsync(CancellationToken ct)
    {
        Status = "parked";
        _detach.Release();
        await _attach.WaitAsync(ct).ConfigureAwait(false);
        Status = "active";
    }

    /// <summary>Awaited by the menu (inside Task.WhenAny against <see cref="ChatTask"/>): completes
    /// when the frame parks. Exactly one such wait is outstanding at a time (the menu is
    /// single-threaded), so the single permit is consumed deterministically.</summary>
    public Task WaitForDetachAsync(CancellationToken ct = default) => _detach.WaitAsync(ct);

    /// <summary>Called by the menu on /attach: release the parked frame so it resumes and reclaims
    /// the console reader. The menu must stop reading input before calling this.</summary>
    public void ReleaseAttach() => _attach.Release();
}

/// <summary>
/// Process-wide registry of detached interactive sessions. Single-threaded cooperative use from
/// the REPL menu + the parked ChatAgentAsync frames, guarded by a lock for the snapshot reads the
/// TUI Agent View performs. Off-path byte-identical: nothing is registered unless the user actually
/// launches an interactive session, and a session that is never detached is removed on completion.
/// </summary>
public static class InteractiveSessionRegistry
{
    private static readonly object _gate = new();
    private static readonly List<InteractiveSession> _sessions = new();
    private static int _seq;

    /// <summary>Create + register a new active interactive session handle.</summary>
    public static InteractiveSession Create(string mode, string label)
    {
        lock (_gate)
        {
            var s = new InteractiveSession
            {
                Id = $"sess{++_seq}",
                Mode = mode,
                Label = label,
            };
            _sessions.Add(s);
            return s;
        }
    }

    public static void Remove(InteractiveSession s)
    {
        lock (_gate) { _sessions.Remove(s); }
    }

    /// <summary>Look up a parked session by its id (case-insensitive, with or without the "sess"
    /// prefix so "/attach 1" and "/attach sess1" both work).</summary>
    public static InteractiveSession? Find(string id)
    {
        var key = (id ?? string.Empty).Trim();
        if (key.Length == 0) return null;
        if (!key.StartsWith("sess", System.StringComparison.OrdinalIgnoreCase))
            key = "sess" + key;
        lock (_gate)
            return _sessions.FirstOrDefault(s =>
                string.Equals(s.Id, key, System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Snapshot of the currently PARKED sessions, in creation order (for menu listing +
    /// the \ Agent View). Active (foregrounded) sessions are excluded - only detached ones can be
    /// attached.</summary>
    public static IReadOnlyList<(string Id, string Label, string Mode, uint Tokens)> ListParked()
    {
        lock (_gate)
            return _sessions
                .Where(s => s.Status == "parked")
                .Select(s => (s.Id, s.Label, s.Mode, s.Tokens))
                .ToList();
    }

    /// <summary>True when at least one session is parked (cheap check for the Agent View / menu).</summary>
    public static bool AnyParked()
    {
        lock (_gate) { return _sessions.Any(s => s.Status == "parked"); }
    }
}
