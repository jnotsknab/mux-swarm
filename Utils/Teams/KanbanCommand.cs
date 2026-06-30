using System.Text;
using MuxSwarm.State;

namespace MuxSwarm.Utils.Teams;

/// <summary>
/// The <c>/kanban</c> in-session command for a live taskboard team: a user-editable view of the
/// shared <see cref="TaskBoard"/> rendered as status columns, with direct edits (add a task, set
/// its state / assignee / dependencies, mark it ready) that feed the peer self-claim engine. It is
/// the human front-end to the same board the members pull work from: marking a task ready (Pending,
/// past any start timer) makes it claimable by a member on the next scan under the team's pickup
/// policy.
///
/// This is a SESSION-native command (handled in the in-session meta-loop, like <c>/compact</c>),
/// not a top-level launcher - it only does anything inside an active <c>/teams &lt;name&gt;</c>
/// taskboard session. Off-team / non-taskboard it prints a hint and changes nothing.
///
/// The parsing + column-bucketing here are pure and unit-tested; <see cref="Run"/> is the thin TUI
/// shell that resolves the active board and renders/edits it.
/// </summary>
public static class KanbanCommand
{
    /// <summary>The five board columns, left-to-right, in workflow order.</summary>
    public static readonly TeamTaskStatus[] Columns =
    {
        TeamTaskStatus.Pending, TeamTaskStatus.Blocked, TeamTaskStatus.InProgress,
        TeamTaskStatus.Done, TeamTaskStatus.Failed,
    };

    public static string ColumnTitle(TeamTaskStatus s) => s switch
    {
        TeamTaskStatus.Pending => "TODO",
        TeamTaskStatus.Blocked => "BLOCKED",
        TeamTaskStatus.InProgress => "IN PROGRESS",
        TeamTaskStatus.Done => "DONE",
        TeamTaskStatus.Failed => "FAILED",
        _ => s.ToString().ToUpperInvariant(),
    };

    /// <summary>The Spectre markup color for a column/status (matches the Ctrl+T strip palette).</summary>
    public static string ColumnColor(TeamTaskStatus s) => s switch
    {
        TeamTaskStatus.Pending => "grey",
        TeamTaskStatus.Blocked => "yellow",
        TeamTaskStatus.InProgress => "cyan",
        TeamTaskStatus.Done => "green",
        TeamTaskStatus.Failed => "red",
        _ => "white",
    };

    /// <summary>A parsed /kanban subcommand.</summary>
    public enum Action { Show, Help, Add, Move, Assign, Block, Ready, Remove, Clear, Peer, Artifacts, Unknown }

    /// <summary>The result of parsing a /kanban command line: the action and its positional args.</summary>
    public readonly record struct Parsed(Action Action, string Arg1, string Arg2, string Rest);

    /// <summary>
    /// Parse a raw "/kanban ..." line into a structured action. Bare "/kanban" (and "/kanban init",
    /// which simply shows/initializes the live board) renders the board. Editing verbs:
    /// add &lt;subject&gt; | move &lt;id&gt; &lt;status&gt; | assign &lt;id&gt; &lt;member&gt; |
    /// block &lt;id&gt; &lt;deps&gt; | ready &lt;id&gt; | remove &lt;id&gt; | clear | peer &lt;on|off&gt;.
    /// </summary>
    public static Parsed Parse(string raw)
    {
        var line = (raw ?? string.Empty).Trim();
        // strip a leading /kanban
        if (line.StartsWith("/kanban", StringComparison.OrdinalIgnoreCase))
            line = line.Substring("/kanban".Length).Trim();

        if (line.Length == 0) return new Parsed(Action.Show, "", "", "");

        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        string a1 = parts.Length > 1 ? parts[1] : "";
        string a2 = parts.Length > 2 ? parts[2] : "";

        return verb switch
        {
            "init" or "show" or "board" or "view" => new Parsed(Action.Show, "", "", ""),
            "help" or "?" => new Parsed(Action.Help, "", "", ""),
            // add: everything after the verb is the subject
            "add" or "new" => new Parsed(Action.Add, "", "", line.Substring(parts[0].Length).Trim()),
            "move" or "status" => new Parsed(Action.Move, a1, a2, a2),
            "assign" => new Parsed(Action.Assign, a1, a2, a2),
            "block" or "dep" => new Parsed(Action.Block, a1, a2, a2),
            "ready" or "unblock" => new Parsed(Action.Ready, a1, "", ""),
            "remove" or "rm" or "del" => new Parsed(Action.Remove, a1, "", ""),
            "clear" => new Parsed(Action.Clear, "", "", ""),
            "peer" => new Parsed(Action.Peer, a1, "", ""),
            "artifacts" or "files" or "artifact" => new Parsed(Action.Artifacts, a1, "", a2),
            _ => new Parsed(Action.Unknown, verb, "", ""),
        };
    }

    /// <summary>Map a free-form status word to a <see cref="TeamTaskStatus"/>, or null if unknown.</summary>
    public static TeamTaskStatus? ParseStatus(string s)
    {
        switch ((s ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "todo": case "pending": return TeamTaskStatus.Pending;
            case "blocked": return TeamTaskStatus.Blocked;
            case "inprogress": case "in-progress": case "doing": case "wip": return TeamTaskStatus.InProgress;
            case "done": case "complete": case "completed": return TeamTaskStatus.Done;
            case "failed": case "fail": return TeamTaskStatus.Failed;
            default: return null;
        }
    }

    /// <summary>Bucket a board snapshot into the five columns (in <see cref="Columns"/> order).</summary>
    public static IReadOnlyList<(TeamTaskStatus Status, List<TeamTask> Tasks)> Bucket(IReadOnlyList<TeamTask> tasks)
    {
        var result = new List<(TeamTaskStatus, List<TeamTask>)>();
        foreach (var col in Columns)
            result.Add((col, tasks.Where(t => t.Status == col).OrderBy(t => t.Id).ToList()));
        return result;
    }

    /// <summary>Render the board as a column-bucketed plain-text board (used by Run + tests).</summary>
    public static string Render(string team, IReadOnlyList<TeamTask> tasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Kanban - {team}  ({tasks.Count} task(s))");
        foreach (var (status, list) in Bucket(tasks))
        {
            sb.AppendLine($"{ColumnTitle(status)} ({list.Count})");
            foreach (var t in list)
            {
                var who = t.Owner is not null ? $"@{t.Owner}" : t.Assignee is not null ? $"->{t.Assignee}" : "";
                var deps = t.BlockedBy.Count > 0 ? $" blockedBy=[{string.Join(",", t.BlockedBy)}]" : "";
                var files = t.Artifacts.Count > 0 ? $" \U0001F4CE{t.Artifacts.Count}" : "";
                sb.AppendLine($"  {t.Id} {who} {t.Subject}{deps}{files}".Replace("  ", " ").TrimEnd());
            }
        }
        return sb.ToString();
    }

    private static readonly string[] HelpLines =
    {
        "/kanban - editable team board (taskboard teams).",
        "  /kanban [init|show]          render the board as TODO|BLOCKED|IN PROGRESS|DONE|FAILED columns",
        "  /kanban add <subject>        add a TODO task",
        "  /kanban assign <id> <member> designate a member (auto-claimed by peer self-claim)",
        "  /kanban block <id> <ids>     set dependencies (comma/space separated)",
        "  /kanban ready <id>           clear blockers/owner -> claimable now",
        "  /kanban move <id> <status>   set status (todo|blocked|inprogress|done|failed)",
        "  /kanban remove <id> | clear  remove one task | the whole board",
        "  /kanban peer <on|off> [secs] toggle the peer self-claim engine that drains the board",
        "  /kanban artifacts <id> <p..> attach filepaths to a task (no paths = list)",
    };

    /// <summary>
    /// Execute a /kanban command against the live team board. Resolves the active board from
    /// <see cref="TeamController.ActiveBoard"/>; prints a hint and returns when no taskboard team is
    /// active. All output goes through MuxConsole so it is captured by the live renderer.
    /// </summary>
    public static void Run(string raw)
    {
        var board = TeamController.ActiveBoard;
        if (board is null)
        {
            MuxConsole.WriteWarning("/kanban needs an active taskboard team. Launch one with /teams <name> (coordination: taskboard).");
            return;
        }

        var p = Parse(raw);
        switch (p.Action)
        {
            case Action.Help:
                foreach (var line in HelpLines) MuxConsole.WriteMuted(line);
                return;

            case Action.Add:
                if (string.IsNullOrWhiteSpace(p.Rest)) { MuxConsole.WriteWarning("Usage: /kanban add <subject>"); return; }
                var created = board.Create(p.Rest, string.Empty);
                MuxConsole.WriteSuccess($"Added {created.Id} \"{created.Subject}\" (TODO).");
                break;

            case Action.Assign:
                if (p.Arg1.Length == 0 || p.Arg2.Length == 0) { MuxConsole.WriteWarning("Usage: /kanban assign <id> <member>"); return; }
                if (board.Reassign(p.Arg1, p.Arg2, out var ar)) MuxConsole.WriteSuccess($"{p.Arg1} {ar}.");
                else MuxConsole.WriteWarning($"[kanban] {ar}.");
                break;

            case Action.Block:
                if (p.Arg1.Length == 0) { MuxConsole.WriteWarning("Usage: /kanban block <id> <dep-ids>"); return; }
                ApplyBlock(board, p.Arg1, p.Rest);
                break;

            case Action.Ready:
                if (p.Arg1.Length == 0) { MuxConsole.WriteWarning("Usage: /kanban ready <id>"); return; }
                if (board.Unassign(p.Arg1, out var rr)) MuxConsole.WriteSuccess($"{p.Arg1} ready ({rr}).");
                else MuxConsole.WriteWarning($"[kanban] {rr}.");
                break;

            case Action.Move:
                var st = ParseStatus(p.Arg2);
                if (p.Arg1.Length == 0 || st is null) { MuxConsole.WriteWarning("Usage: /kanban move <id> <todo|blocked|inprogress|done|failed>"); return; }
                if (board.SetStatus(p.Arg1, st.Value, out var mr)) MuxConsole.WriteSuccess($"{p.Arg1} -> {st.Value} ({mr}).");
                else MuxConsole.WriteWarning($"[kanban] {mr}.");
                break;

            case Action.Remove:
                if (p.Arg1.Length == 0) { MuxConsole.WriteWarning("Usage: /kanban remove <id>"); return; }
                if (board.Remove(p.Arg1, out var dr)) MuxConsole.WriteSuccess($"{p.Arg1} {dr}.");
                else MuxConsole.WriteWarning($"[kanban] {dr}.");
                break;

            case Action.Clear:
                int n = board.RemoveAll();
                MuxConsole.WriteSuccess($"Cleared the board ({n} task(s) removed).");
                break;

            case Action.Peer:
                TogglePeer(p.Arg1);
                break;

            case Action.Artifacts:
                if (p.Arg1.Length == 0) { MuxConsole.WriteWarning("Usage: /kanban artifacts <id> <path...>  (no paths = list)"); return; }
                if (p.Rest.Trim().Length == 0)
                {
                    var cur = board.Get(p.Arg1);
                    if (cur is null) { MuxConsole.WriteWarning($"[kanban] no such task '{p.Arg1}'."); return; }
                    MuxConsole.WriteInfo(cur.Artifacts.Count == 0
                        ? $"{p.Arg1} has no artifacts."
                        : $"{p.Arg1} artifacts:\n" + string.Join("\n", cur.Artifacts.Select(a => $"  - {a}")));
                    return;
                }
                var addPaths = p.Rest.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (board.SetArtifacts(p.Arg1, add: addPaths, remove: null, set: null, out var aRes))
                    MuxConsole.WriteSuccess($"{p.Arg1} {aRes}.");
                else MuxConsole.WriteWarning($"[kanban] {aRes}.");
                break;

            case Action.Unknown:
                MuxConsole.WriteWarning($"Unknown /kanban action '{p.Arg1}'. Try /kanban help.");
                return;

            case Action.Show:
            default:
                break;
        }

        // Every path ends by rendering the (possibly mutated) board.
        MuxConsole.WritePanel($"Kanban - {board.Team}", Render(board.Team, board.Snapshot()));
    }

    private static void ApplyBlock(TaskBoard board, string id, string depsRaw)
    {
        var deps = (depsRaw ?? string.Empty).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        // Recreate dependency wiring by reopening + re-deriving: the board has no public "set deps",
        // so we model "block" as removing the task and recreating it with deps would lose history.
        // Instead, mark it Blocked when given deps that are not all Done; ready clears it. We honor
        // the simplest contract: if deps are given and any is unfinished, force Blocked.
        var existing = board.Get(id);
        if (existing is null) { MuxConsole.WriteWarning($"[kanban] no such task '{id}'."); return; }
        // Reject stale/typo'd dep ids loudly - an unknown dep must not silently pass gating.
        var unknown = board.UnknownDeps(deps);
        if (unknown.Count > 0)
        {
            MuxConsole.WriteWarning($"[kanban] unknown dependency id(s): {string.Join(", ", unknown)}. Nothing changed.");
            return;
        }
        bool anyOpen = deps.Any(d => board.Get(d) is { Status: not TeamTaskStatus.Done });
        if (deps.Length > 0 && anyOpen)
        {
            board.SetStatus(id, TeamTaskStatus.Blocked, out _);
            MuxConsole.WriteSuccess($"{id} marked BLOCKED (waiting on {string.Join(",", deps)}).");
        }
        else
        {
            board.Unassign(id, out _);
            MuxConsole.WriteSuccess($"{id} has no open blockers -> ready.");
        }
    }

    private static void TogglePeer(string arg)
    {
        var runner = TeamController.ActivePeerRunner;
        if (runner is null)
        {
            MuxConsole.WriteWarning("[kanban] No peer engine for this team (taskboard teams only).");
            return;
        }
        var on = (arg ?? string.Empty).Trim().ToLowerInvariant();
        if (on is "on" or "start" or "true")
        {
            runner.Start(runner.IntervalSeconds);
            MuxConsole.WriteSuccess($"Peer self-claim ON ({(runner.OpenPool ? "open" : "assigned")} pickup, every {runner.IntervalSeconds}s).");
        }
        else if (on is "off" or "stop" or "false")
        {
            runner.Stop();
            MuxConsole.WriteSuccess("Peer self-claim OFF.");
        }
        else
        {
            MuxConsole.WriteInfo($"Peer self-claim is {(runner.IsRunning ? "ON" : "OFF")}. Use /kanban peer on|off.");
        }
    }
}
