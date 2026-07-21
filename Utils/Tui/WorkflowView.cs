using MuxSwarm.State;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// The /workflows live viewer model (v0.12.4). Follows the AgentView/JobView pattern: PURE -
/// holds selection state and produces markup rows from a registry snapshot, no console I/O,
/// fully unit-testable. Master/detail layout (mux-style, low density): the selected run
/// expands into a linked pair of panels - LEFT lists the workflow's phases (sections) with
/// done-fraction counters, RIGHT enumerates ONLY the selected phase's tasks with per-task
/// telemetry (agent, status, model, tool count, duration). Up/Down selects the run,
/// Left/Right selects the phase. The driver serializes access through the console lock.
/// </summary>
internal sealed class WorkflowView
{
    private List<WorkflowRun> _runs = new();
    private string? _selectedId;
    private int _phaseIdx;
    private bool _open;
    private const int MaxTaskRows = 12;
    private const int PhaseColWidth = 30;

    public bool IsOpen => _open;
    public void Open() => _open = true;
    public void Close() => _open = false;

    /// <summary>Replace the run snapshot. Selection preserved by id; falls back to the newest
    /// RUNNING run (the interesting one), then the newest run.</summary>
    public void SetRuns(IReadOnlyList<WorkflowRun> snapshot)
    {
        _runs = new List<WorkflowRun>(snapshot);
        if (_selectedId is null || !_runs.Any(r => string.Equals(r.Id, _selectedId, StringComparison.Ordinal)))
        {
            _selectedId = _runs.LastOrDefault(r => r.State == WorkflowRunState.Running)?.Id ?? _runs.LastOrDefault()?.Id;
            _phaseIdx = 0;
        }
    }

    public int Count => _runs.Count;

    public string? SelectedId()
    {
        if (_runs.Count == 0) return null;
        return _runs.Any(r => string.Equals(r.Id, _selectedId, StringComparison.Ordinal))
            ? _selectedId : _runs[^1].Id;
    }

    private WorkflowRun? SelectedRun()
    {
        var id = SelectedId();
        return id is null ? null : _runs.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
    }

    public void Move(int delta)
    {
        if (_runs.Count == 0) return;
        int idx = _runs.FindIndex(r => string.Equals(r.Id, SelectedId(), StringComparison.Ordinal));
        idx = Math.Clamp(idx + delta, 0, _runs.Count - 1);
        if (!string.Equals(_runs[idx].Id, _selectedId, StringComparison.Ordinal)) _phaseIdx = 0;
        _selectedId = _runs[idx].Id;
    }

    /// <summary>Move the phase (section) selection of the selected run, clamped.</summary>
    public void MovePhase(int delta)
    {
        var run = SelectedRun();
        if (run is null || run.Manifest.Sections.Count == 0) return;
        _phaseIdx = Math.Clamp(_phaseIdx + delta, 0, run.Manifest.Sections.Count - 1);
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

    private static string PhaseGlyph(WorkflowSection sec, int ordinal)
    {
        int done = sec.Tasks.Count(t => t.Status == "done");
        if (sec.Tasks.Count > 0 && done == sec.Tasks.Count) return $"[{TuiComponents.Ok}]\u2714[/]";
        if (sec.Tasks.Any(t => t.Status == "failed")) return $"[{TuiComponents.Err}]\u2717[/]";
        if (sec.Tasks.Any(t => t.Status == "running")) return $"[{TuiComponents.Ok}]\u25cf[/]";
        return $"[{TuiComponents.Dim}]{ordinal}[/]";
    }

    private static string Dur(int secs)
        => secs >= 60 ? $"{secs / 60}m{secs % 60:00}s" : $"{secs}s";

    /// <summary>Pad a markup cell to a fixed PLAIN-text width (markup tags are zero-width).</summary>
    private static string Pad(string markup, int width)
    {
        int w = TuiMarkup.MarkupWidth(markup);
        return w >= width ? markup : markup + new string(' ', width - w);
    }

    /// <summary>
    /// Render the dashboard: header, run rows (selection-highlighted), and for the selected
    /// run a linked master/detail pair - phases (left) and the selected phase's tasks with
    /// telemetry (right). Bounded rows; low density by construction (one phase expanded).
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
                          $"[{TuiComponents.Dim}]{Esc(r.Mode)} \u00b7 {durTxt}[/]";
            rows.Add(isSel ? $"  [{TuiComponents.Accent}]\u203a[/] {line}" : $"    {line}");
        }

        var run = SelectedRun();
        if (run is not null)
        {
            var secs = run.Manifest.Sections;
            if (secs.Count == 0)
            {
                rows.Add($"      [{TuiComponents.Dim}](no manifest yet - the driver script declares phases as it starts)[/]");
            }
            else
            {
                int pi = Math.Clamp(_phaseIdx, 0, secs.Count - 1);
                var cur = secs[pi];

                // LEFT panel rows: phases with fraction counters.
                var left = new List<string> { $"[{TuiComponents.Muted}]Phases[/]" };
                for (int i = 0; i < secs.Count; i++)
                {
                    var s = secs[i];
                    int done = s.Tasks.Count(t => t.Status == "done");
                    string frac = s.Tasks.Count > 0 ? $"{done}/{s.Tasks.Count}" : "";
                    string mark = i == pi ? $"[{TuiComponents.Accent}]\u203a[/]" : " ";
                    left.Add($"{mark} {PhaseGlyph(s, i + 1)} [{(i == pi ? TuiComponents.Text : TuiComponents.Muted)}]{Esc(Trunc(s.Name, 16))}[/] [{TuiComponents.Dim}]{frac}[/]");
                }

                // RIGHT panel rows: the selected phase's tasks with telemetry.
                var right = new List<string> { $"[{TuiComponents.Muted}]{Esc(cur.Name)} \u00b7 {cur.Tasks.Count} task(s)[/]" };
                int shown = 0;
                int labelW = Math.Max(16, width - PhaseColWidth - 44);
                foreach (var t in cur.Tasks)
                {
                    if (shown++ >= MaxTaskRows) { right.Add($"[{TuiComponents.Dim}]+{cur.Tasks.Count - MaxTaskRows} more[/]"); break; }
                    var tele = new List<string>();
                    if (!string.IsNullOrEmpty(t.Model)) tele.Add(Esc(t.Model!));
                    if (t.Tools is { } tc) tele.Add($"{tc} tools");
                    if (t.Secs is { } sd) tele.Add(Dur(sd));
                    string teleTxt = tele.Count > 0 ? $" [{TuiComponents.Dim}]{string.Join(" \u00b7 ", tele)}[/]" : "";
                    string detail = t.Status == "failed" && !string.IsNullOrEmpty(t.Detail)
                        ? $" [{TuiComponents.Err}]{Esc(Trunc(t.Detail!, Math.Max(16, labelW)))}[/]" : "";
                    right.Add($"{TaskGlyph(t.Status)} [{TuiComponents.Agent}]{Esc(t.Agent)}[/] [{TuiComponents.Muted}]{Esc(Trunc(t.Label, labelW))}[/]{teleTxt}{detail}");
                }

                // Compose two columns joined with a vertical rule; pad by PLAIN width.
                int n = Math.Max(left.Count, right.Count);
                for (int i = 0; i < n; i++)
                {
                    string lc = i < left.Count ? left[i] : "";
                    string rc = i < right.Count ? right[i] : "";
                    rows.Add($"      {Pad(lc, PhaseColWidth)}[{TuiComponents.Border}]\u2502[/] {rc}");
                }
            }
            if (run.Error is not null)
                rows.Add($"      [{TuiComponents.Err}]\u2717 {Esc(Trunc(run.Error, Math.Max(20, width - 12)))}[/]");
        }
        rows.Add($"  [{TuiComponents.Dim}]\u2191\u2193 run \u00b7 \u2190\u2192 phase \u00b7 c cancel \u00b7 esc/q close[/]");
        return rows;
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");
    private static string Trunc(string s, int max) => TuiMarkup.TruncatePlain(s ?? "", max);
}
