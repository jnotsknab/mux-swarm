using System.Text;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// The v0.12.0 Agent View model + renderer (Milestone M1). A keyboard-navigable, inline
/// session-list dashboard over the running parallel sub-agents, toggled with backslash
/// (the Claude-Code Agent View model). Intentionally minimal: a clear read on what is
/// running and the ability to FOREGROUND (attach) one agent's buffered stream - not a
/// multi-column cockpit. Collapsed one-line status rows for the rest; idle rows auto-hide.
///
/// This type is PURE: it holds the snapshot + selection state and produces markup rows, but
/// performs no console I/O and no foregrounding itself (the driver wires Enter to its existing
/// sub-agent expand machinery). That keeps the whole model headless-testable through the same
/// in-memory fake terminal the rest of the TUI uses.
///
/// Not thread-safe by itself - the driver serializes access through the console lock, exactly
/// like the live region it renders into.
/// </summary>
internal sealed class AgentView
{
    /// <summary>One dashboard row: a running (or just-finished) sub-agent and its live status.</summary>
    internal readonly record struct AgentRow(string Agent, string Status, string Tint, DateTime LastActive);

    // Idle rows auto-hide after this much inactivity (Claude-Code parity ~30s), unless selected.
    internal static readonly TimeSpan IdleHideAfter = TimeSpan.FromSeconds(30);
    // The list caps at this many visible rows with a "+N more" scroll hint (CC parity ~5).
    internal const int MaxVisible = 5;

    private List<AgentRow> _rows = new();
    private string? _selectedAgent;   // tracked by NAME so selection survives snapshot updates
    private bool _open;

    /// <summary>True while the dashboard is foregrounded (drawn into the live region).</summary>
    public bool IsOpen => _open;

    /// <summary>Open the dashboard. Idempotent.</summary>
    public void Open() => _open = true;

    /// <summary>Close the dashboard. Idempotent.</summary>
    public void Close() => _open = false;

    /// <summary>
    /// Replace the active-agent snapshot from the live registry. Each incoming entry is
    /// (agent, status, tint); the <c>LastActive</c> timestamp is preserved for an agent whose
    /// status is unchanged and stamped to <paramref name="now"/> when the agent is new or its
    /// status moved (so the idle-hide clock measures real inactivity, not snapshot churn). The
    /// selection is preserved by name; if the selected agent vanished it falls back to the first
    /// row on the next navigation/query.
    /// </summary>
    public void SetRows(IReadOnlyList<(string Agent, string Status, string Tint)> snapshot, DateTime now)
    {
        var next = new List<AgentRow>(snapshot.Count);
        foreach (var (agent, status, tint) in snapshot)
        {
            DateTime last = now;
            for (int i = 0; i < _rows.Count; i++)
                if (string.Equals(_rows[i].Agent, agent, StringComparison.Ordinal))
                {
                    // Unchanged status keeps its original activity time; a status change is activity.
                    last = string.Equals(_rows[i].Status, status, StringComparison.Ordinal) ? _rows[i].LastActive : now;
                    break;
                }
            next.Add(new AgentRow(agent, status, tint, last));
        }
        _rows = next;
        _selectedAgent ??= _rows.Count > 0 ? _rows[0].Agent : null;
    }

    /// <summary>Total agents in the current snapshot (visible + idle-hidden).</summary>
    public int Count => _rows.Count;

    /// <summary>
    /// The rows currently shown: snapshot rows that are still active (within
    /// <see cref="IdleHideAfter"/> of <paramref name="now"/>) OR the selected row (so a focused
    /// agent never hides under you), capped to <see cref="MaxVisible"/>. The out param reports how
    /// many active rows were elided past the cap, for the "+N more" hint.
    /// </summary>
    public List<AgentRow> VisibleRows(DateTime now, out int overflow)
    {
        var live = new List<AgentRow>(_rows.Count);
        foreach (var r in _rows)
        {
            bool active = (now - r.LastActive) < IdleHideAfter;
            bool selected = string.Equals(r.Agent, _selectedAgent, StringComparison.Ordinal);
            if (active || selected) live.Add(r);
        }
        overflow = 0;
        if (live.Count > MaxVisible)
        {
            overflow = live.Count - MaxVisible;
            live = live.GetRange(0, MaxVisible);
        }
        return live;
    }

    /// <summary>The agent name currently selected for foregrounding, resolved against the visible
    /// rows (falls back to the first visible row, or null when nothing is visible).</summary>
    public string? SelectedAgent(DateTime now)
    {
        var vis = VisibleRows(now, out _);
        if (vis.Count == 0) return null;
        foreach (var r in vis)
            if (string.Equals(r.Agent, _selectedAgent, StringComparison.Ordinal)) return r.Agent;
        return vis[0].Agent;
    }

    /// <summary>Move the selection by <paramref name="delta"/> rows within the visible list,
    /// clamped at the ends (no wrap). No-op when nothing is visible.</summary>
    public void Move(int delta, DateTime now)
    {
        var vis = VisibleRows(now, out _);
        if (vis.Count == 0) return;
        int idx = 0;
        for (int i = 0; i < vis.Count; i++)
            if (string.Equals(vis[i].Agent, _selectedAgent, StringComparison.Ordinal)) { idx = i; break; }
        idx = Math.Clamp(idx + delta, 0, vis.Count - 1);
        _selectedAgent = vis[idx].Agent;
    }

    /// <summary>
    /// Render the dashboard as markup rows for the live region: a header, the selection-
    /// highlighted agent list (idle-hidden + capped), an optional "+N more" hint, and a one-line
    /// key hint. Pure given its inputs - unit-tested without a console. <paramref name="frame"/>
    /// advances the shared spinner so the rows animate in step with the activity strip.
    /// </summary>
    public List<string> RenderDashboard(int width, DateTime now, int frame, string? foregrounded = null)
    {
        var rows = new List<string>();
        string spin = TuiComponents.SubAgentFrames[
            ((frame % TuiComponents.SubAgentFrames.Length) + TuiComponents.SubAgentFrames.Length) % TuiComponents.SubAgentFrames.Length];
        var vis = VisibleRows(now, out int overflow);
        string sel = SelectedAgent(now) ?? "";

        rows.Add($"  [{TuiComponents.Accent}]\u25b8 agents[/] [{TuiComponents.Dim}]\u00b7 {vis.Count} running[/]");
        if (vis.Count == 0)
        {
            rows.Add($"    [{TuiComponents.Dim}]no active agents[/]");
        }
        else
        {
            foreach (var r in vis)
            {
                string st = string.IsNullOrWhiteSpace(r.Status) ? "working" : r.Status.Trim();
                if (st.Length > 56) st = st[..55] + "\u2026";
                bool isSel = string.Equals(r.Agent, sel, StringComparison.Ordinal);
                bool isPinned = foregrounded is not null && string.Equals(r.Agent, foregrounded, StringComparison.Ordinal);
                // A small pin tag on the foregrounded agent so it is obvious which one the live
                // panel / Ctrl+E currently tracks - switching focus reads clearly.
                string pin = isPinned ? $" [{TuiComponents.Ok}]\u2605 foreground[/]" : "";
                rows.Add(isSel
                    ? $"  [{TuiComponents.Accent}]\u203a[/] [{r.Tint}]{spin}[/] [{TuiComponents.Text}]{Esc(r.Agent)}[/] [{TuiComponents.Dim}]\u00b7[/] [{TuiComponents.Text}]{Esc(st)}[/]{pin}"
                    : $"    [{r.Tint}]{spin}[/] [{TuiComponents.Agent}]{Esc(r.Agent)}[/] [{TuiComponents.Dim}]\u00b7[/] [{TuiComponents.Think} italic]{Esc(st)}[/]{pin}");
            }
        }
        if (overflow > 0)
            rows.Add($"    [{TuiComponents.Dim}]\u2193 +{overflow} more[/]");
        rows.Add($"  [{TuiComponents.Dim}]\u2191\u2193 select \u00b7 enter foreground \u00b7 esc close[/]");
        return rows;
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");
}
