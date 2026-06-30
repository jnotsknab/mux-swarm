using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.State;

namespace MuxSwarm.Utils.Teams;

/// <summary>
/// M-B: auto-decomposition of a high-level goal into a <c>blockedBy</c> task graph on the active
/// board, via ONE light-model call. Used both as the one-shot <c>task_decompose</c> tool and by the
/// opt-in background <see cref="DecomposeDispatcher"/> (<c>/taskgraph on|off</c>).
///
/// The model is asked for a strict JSON array of subtasks, each with a local integer index, a
/// subject, a description, an optional assignee, and a list of LOCAL indices it depends on. We then
/// create the tasks in dependency order, mapping local indices to the real board ids so the
/// resulting <c>blockedBy</c> edges reference actual tasks.
/// </summary>
public static class TaskDecomposer
{
    private sealed record SubtaskSpec(int Index, string Subject, string Description,
        string? Assignee, List<int> DependsOn);

    /// <summary>
    /// Decompose <paramref name="goal"/> into subtasks on <paramref name="board"/>. Returns a short
    /// human-readable summary (also suitable as a tool result). Never throws: on any failure it
    /// returns an explanatory string and leaves the board untouched.
    /// </summary>
    public static async Task<string> DecomposeAsync(
        TaskBoard board, string goal, IReadOnlyList<string> members,
        IChatClient? client, ChatOptions? options, int maxSubtasks, CancellationToken ct)
    {
        if (board is null) return "[decompose] No active board to populate.";
        if (string.IsNullOrWhiteSpace(goal)) return "[decompose] Empty goal; nothing to decompose.";
        if (client is null) return "[decompose] No decomposition model available (set decompose.model or a compaction/orchestrator model).";

        int cap = Math.Clamp(maxSubtasks <= 0 ? 12 : maxSubtasks, 1, 50);
        string roster = members.Count > 0 ? string.Join(", ", members) : "(no named members; leave assignee null)";

        var sys = new ChatMessage(ChatRole.System,
            $$"""
            You are a task-decomposition planner. Break the user's GOAL into at most {{cap}} concrete,
            independently-actionable subtasks and express their dependency graph.

            Output ONLY a JSON array (no prose, no markdown fence). Each element:
              {"index": <int, 1-based, unique>, "subject": "<short title>",
               "description": "<what to do>", "assignee": "<one of the members, or null>",
               "dependsOn": [<indices of subtasks that must finish first>]}

            Rules:
            - Indices are local to THIS array, 1-based, contiguous.
            - dependsOn may only reference indices that appear in the array; no cycles.
            - Prefer a shallow graph; only add a dependency when output of one is truly required by another.
            - Assignees must be chosen from: {{roster}}. Use null when unsure.
            """);
        var usr = new ChatMessage(ChatRole.User, "GOAL:\n" + goal);

        string raw;
        try
        {
            // Tool-less by construction: we pass no Tools in options, and clear any that slipped in.
            var safe = options;
            if (safe?.Tools is { Count: > 0 })
            {
                safe = safe.Clone();
                safe.Tools = null;
                safe.ToolMode = ChatToolMode.None;
            }
            var resp = await client.GetResponseAsync(new[] { sys, usr }, safe, ct);
            raw = resp.Text ?? string.Empty;
        }
        catch (OperationCanceledException) { return "[decompose] Cancelled."; }
        catch (Exception ex) { return $"[decompose] Model call failed: {ex.Message}"; }

        var specs = ParseSpecs(raw, cap);
        if (specs.Count == 0)
            return "[decompose] Model returned no usable subtasks (could not parse a JSON task array).";

        // Validate dependency indices + detect cycles before mutating the board.
        var byIndex = specs.ToDictionary(s => s.Index);
        foreach (var s in specs)
            s.DependsOn.RemoveAll(d => !byIndex.ContainsKey(d) || d == s.Index);
        if (HasCycle(specs, byIndex))
            return "[decompose] Rejected: the proposed dependency graph contains a cycle.";

        // Create in topological order so a dependency's real id exists before its dependent.
        var order = TopoOrder(specs, byIndex);
        var localToRealId = new Dictionary<int, string>();
        var created = new List<string>();
        var memberSet = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);

        foreach (var s in order)
        {
            var deps = s.DependsOn
                .Where(localToRealId.ContainsKey)
                .Select(d => localToRealId[d])
                .ToList();
            string? who = !string.IsNullOrWhiteSpace(s.Assignee) && memberSet.Contains(s.Assignee!.Trim())
                ? s.Assignee!.Trim() : null;
            var t = board.Create(s.Subject, s.Description, deps, who, null);
            localToRealId[s.Index] = t.Id;
            created.Add($"{t.Id} [{t.Status}] {(who ?? "-")}: {t.Subject}");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[decompose] Added {created.Count} subtask(s) for goal: {Truncate(goal, 80)}");
        foreach (var line in created) sb.AppendLine("  " + line);
        return sb.ToString().TrimEnd();
    }

    private static List<SubtaskSpec> ParseSpecs(string raw, int cap)
    {
        var result = new List<SubtaskSpec>();
        string json = ExtractJsonArray(raw);
        if (json.Length == 0) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (result.Count >= cap) break;
                if (el.ValueKind != JsonValueKind.Object) continue;

                int index = el.TryGetProperty("index", out var iEl) && iEl.TryGetInt32(out var iv) ? iv : result.Count + 1;
                string subject = el.TryGetProperty("subject", out var sEl) ? (sEl.GetString() ?? "") : "";
                string desc = el.TryGetProperty("description", out var dEl) ? (dEl.GetString() ?? "") : "";
                string? assignee = el.TryGetProperty("assignee", out var aEl) && aEl.ValueKind == JsonValueKind.String
                    ? aEl.GetString() : null;
                var deps = new List<int>();
                if (el.TryGetProperty("dependsOn", out var depEl) && depEl.ValueKind == JsonValueKind.Array)
                    foreach (var d in depEl.EnumerateArray())
                        if (d.TryGetInt32(out var dv)) deps.Add(dv);

                if (string.IsNullOrWhiteSpace(subject)) continue;
                result.Add(new SubtaskSpec(index, subject.Trim(), desc.Trim(), assignee, deps));
            }
        }
        catch { /* unparseable -> empty */ }

        return result;
    }

    // Pull the first balanced JSON array out of a model response (tolerates leading prose / fences).
    private static string ExtractJsonArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        int start = raw.IndexOf('[');
        if (start < 0) return string.Empty;
        int depth = 0; bool inStr = false; bool esc = false;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']') { depth--; if (depth == 0) return raw.Substring(start, i - start + 1); }
        }
        return string.Empty;
    }

    private static bool HasCycle(List<SubtaskSpec> specs, Dictionary<int, SubtaskSpec> byIndex)
    {
        var state = new Dictionary<int, int>(); // 0=unseen,1=in-stack,2=done
        bool Dfs(int idx)
        {
            state[idx] = 1;
            foreach (var d in byIndex[idx].DependsOn)
            {
                if (!byIndex.ContainsKey(d)) continue;
                int st = state.GetValueOrDefault(d, 0);
                if (st == 1) return true;
                if (st == 0 && Dfs(d)) return true;
            }
            state[idx] = 2;
            return false;
        }
        foreach (var s in specs)
            if (state.GetValueOrDefault(s.Index, 0) == 0 && Dfs(s.Index)) return true;
        return false;
    }

    private static List<SubtaskSpec> TopoOrder(List<SubtaskSpec> specs, Dictionary<int, SubtaskSpec> byIndex)
    {
        var ordered = new List<SubtaskSpec>();
        var visited = new HashSet<int>();
        void Visit(int idx)
        {
            if (!visited.Add(idx)) return;
            foreach (var d in byIndex[idx].DependsOn)
                if (byIndex.ContainsKey(d)) Visit(d);
            ordered.Add(byIndex[idx]);
        }
        foreach (var s in specs) Visit(s.Index);
        return ordered;
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s.Substring(0, n) + "...";
}

/// <summary>
/// M-B background tick dispatcher (<c>/taskgraph on</c>): a single non-blocking loop that, when a
/// new pending goal is parked on the board's decompose queue, expands it via
/// <see cref="TaskDecomposer.DecomposeAsync"/>. Mirrors <see cref="AutoRunner"/>'s lifecycle:
/// linked CTS off the session token, floor on the interval, clean Stop(). Activity-gated: it only
/// calls the model when the pending-goal queue is non-empty (zero LLM calls on an idle board).
/// </summary>
public sealed class DecomposeDispatcher
{
    private readonly TaskBoard _board;
    private readonly IReadOnlyList<string> _members;
    private readonly Func<IChatClient?> _clientFactory;
    private readonly Func<ChatOptions?> _optionsFactory;
    private readonly int _maxSubtasks;
    private readonly CancellationToken _sessionCt;
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public int IntervalSeconds { get; private set; } = 60;
    public bool IsRunning => _loop is { IsCompleted: false };

    public DecomposeDispatcher(TaskBoard board, IReadOnlyList<string> members,
        Func<IChatClient?> clientFactory, Func<ChatOptions?> optionsFactory,
        int maxSubtasks, CancellationToken sessionCt)
    {
        _board = board;
        _members = members;
        _clientFactory = clientFactory;
        _optionsFactory = optionsFactory;
        _maxSubtasks = maxSubtasks;
        _sessionCt = sessionCt;
    }

    /// <summary>Queue a goal for background expansion on the next tick.</summary>
    public void Enqueue(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return;
        lock (_gate) _pending.Enqueue(goal);
    }

    public void Start(int intervalSeconds)
    {
        IntervalSeconds = Math.Max(10, intervalSeconds);
        lock (_gate)
        {
            if (IsRunning) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCt);
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { /* already disposed */ }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Activity gate: drain whatever goals are queued; if none, do NO model work this tick.
                while (true)
                {
                    string? goal;
                    lock (_gate) goal = _pending.Count > 0 ? _pending.Dequeue() : null;
                    if (goal is null) break;

                    var client = _clientFactory();
                    var options = _optionsFactory();
                    var summary = await TaskDecomposer.DecomposeAsync(
                        _board, goal, _members, client, options, _maxSubtasks, ct);
                    MuxConsole.WriteMuted(summary);
                }

                try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* clean stop */ }
        catch (Exception ex) { MuxConsole.WriteWarning($"[taskgraph] Dispatcher stopped on error: {ex.Message}"); }
    }
}
