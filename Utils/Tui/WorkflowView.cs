using MuxSwarm.State;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// The /workflows live viewer model (v0.12.4). Follows the AgentView/JobView pattern: PURE -
/// holds selection state and produces markup rows from a registry snapshot, no console I/O,
/// fully unit-testable. More comprehensive than the `\` Agent View: one PANEL per workflow
/// section with its task rows (agent + status + detail) nested inside, plus a recent-events
/// strip for the selected run. The driver serializes access through the console lock.
/// </summary>
internal sealed class WorkflowView
{
    private List<WorkflowRun> _runs = new();
    private string? _selectedId;
    private bool _open;
    private const int MaxTaskRowsPerSection = 6;
    private const int MaxRecent = 3;

    public bool IsOpen => _open;
    public void Open() => _open = true;
    public void Close() => _open = false;

    /// <summary>Replace the run snapshot. Selection preserved by id; falls back to the newest
    /// RUNNING run (the interesting one), then the newest run.</summary>
    public void SetRuns(IReadOnlyList<WorkflowRun> snapshot)
    {
        _runs = new List<WorkflowRun>(snapshot);
        if (_selectedId is null || !_runs.Any(r => string.Equals(r.Id, _selectedId, StringComparison.Ordinal)))
            _selectedId = _runs.LastOrDefault(r => r.State == WorkflowRunState.Running)?.Id ?? _runs.LastOrDefault()?.Id;
    }

    public int Count => _runs.Count;

    public string? SelectedId()
    {
        if (_runs.Count == 0) return null;
        return _runs.Any(r => string.Equals(r.Id, _selectedId, StringComparison.Ordinal))
            ? _selectedId : _runs[^1].Id;
    }

    public void Move(int delta)
    {
        if (_runs.Count == 0) return;
        int idx = _runs.FindIndex(r => string.Equals(r.Id, SelectedId(), StringComparison.Ordinal));
        idx = Math.Clamp(idx + delta, 0, _runs.Count - 1);
        _selectedId = _runs[idx].Id;
    }

    private static string StateMarkup(WorkflowRunState s) => s switch
    {
        WorkflowRunState.Running => $"[{TuiComponents.Ok}]running[/]",
        WorkflowRunState.Done => $"[{TuiComponents.Dim}]done[/]",
        WorkflowRunState.Failed => $"[{TuiComponents.Err}]failed[/]",
        _ => $"[{TuiComponents.Warn}]cancelled[/]",
    };

    private static string TaskGlyph(string status) => status switch
    {
        "running" => $"[{TuiComponents.Ok}]\u25cf[/]",
        "done" => $"[{TuiComponents.Dim}]\u2714[/]",
        "failed" => $"[{TuiComponents.Err}]\u2717[/]",
        _ => $"[{TuiComponents.Dim}]\u25cb[/]",   // pending
    };

    /// <summary>
    /// Render the dashboard: header, run list (selection-highlighted), then the SELECTED run
    /// expanded as section panels with nested task rows + a recent-events strip. Bounded rows.
    /// </summary>
    public List<string> RenderDashboard(int width)
    {
        var rows = new List<string>();
        int running = _runs.Count(r => r.State == WorkflowRunState.Running);
        rows.Add($"  [{TuiComponents.Accent}]\u25b8 workflows[/] [{TuiComponents.Dim}]\u00b7 {running} running \u00b7 {_runs.Count} total[/]");
        if (_runs.Count == 0)
        {
            rows.Add($"    [{TuiComponents.Dim}]no workflow runs \u00b7 /workflow to start one[/]");
            rows.Add($"  [{TuiComponents.Dim}]esc/q close[/]");
            return rows;
        }

        string? sel = SelectedId();
        foreach (var r in _runs)
        {
            bool isSel = string.Equals(r.Id, sel, StringComparison.Ordinal);
            var dur = (r.Finished ?? DateTimeOffset.UtcNow) - r.Started;
            string durTxt = dur.TotalHours >= 1 ? $"{(int)dur.TotalHours}h{dur.Minutes:00}m" : $"{(int)dur.TotalMinutes}m{dur.Seconds:00}s";
            string line = $"[{TuiComponents.Agent}]{Esc(r.Name)}[/] {StateMarkup(r.State)} " +
                          $"[{TuiComponents.Dim}]{Esc(r.Mode)} \u00b7 {durTxt} \u00b7 {Esc(r.Id)}[/]";
            rows.Add(isSel ? $"  [{TuiComponents.Accent}]\u203a[/] {line}" : $"    {line}");

            if (!isSel) continue;
            // Expanded: one panel per section, task rows nested.
            foreach (var sec in r.Manifest.Sections)
            {
                int done = sec.Tasks.Count(t => t.Status == "done");
                rows.Add($"      [{TuiComponents.Accent}]\u250c[/] [{TuiComponents.Text}]{Esc(sec.Name)}[/] [{TuiComponents.Dim}]{done}/{sec.Tasks.Count}[/]");
                int shown = 0;
                foreach (var t in sec.Tasks)
                {
                    if (shown++ >= MaxTaskRowsPerSection)
                    {
                        rows.Add($"      [{TuiComponents.Dim}]\u2502   +{sec.Tasks.Count - MaxTaskRowsPerSection} more[/]");
                        break;
                    }
                    string detail = string.IsNullOrEmpty(t.Detail) ? "" : $" [{TuiComponents.Dim}]\u00b7 {Esc(Trunc(t.Detail!, Math.Max(20, width - 46)))}[/]";
                    rows.Add($"      [{TuiComponents.Dim}]\u2502[/] {TaskGlyph(t.Status)} [{TuiComponents.Agent}]{Esc(t.Agent)}[/] [{TuiComponents.Muted}]{Esc(Trunc(t.Label, 40))}[/]{detail}");
                }
            }
            if (r.Manifest.Sections.Count == 0)
                rows.Add($"      [{TuiComponents.Dim}]\u2502 (no manifest yet - the driver script declares sections as it starts)[/]");
            if (r.Error is not null)
                rows.Add($"      [{TuiComponents.Err}]\u2717 {Esc(Trunc(r.Error, Math.Max(20, width - 12)))}[/]");
            // Recent-events strip (newest last), dimmed.
            var recent = r.RecentEvents.ToArray();
            foreach (var ev in recent.Skip(Math.Max(0, recent.Length - MaxRecent)))
                rows.Add($"      [{TuiComponents.Dim}]  {Esc(Trunc(ev, Math.Max(20, width - 10)))}[/]");
        }
        rows.Add($"  [{TuiComponents.Dim}]\u2191\u2193 select \u00b7 c cancel \u00b7 esc/q close[/]");
        return rows;
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");
    private static string Trunc(string s, int max) => TuiMarkup.TruncatePlain(s ?? "", max);
}
