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
    private int _taskIdx;
    private bool _taskExpanded;
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
            _taskIdx = 0;
            _taskExpanded = false;
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

    /// <summary>Cycle the run selection (Tab). WRAPS at the ends so repeated Tab loops
    /// through every run instead of pinning at the last one.</summary>
    public void Move(int delta)
    {
        if (_runs.Count == 0) return;
        int idx = _runs.FindIndex(r => string.Equals(r.Id, SelectedId(), StringComparison.Ordinal));
        idx = ((idx + delta) % _runs.Count + _runs.Count) % _runs.Count;
        if (!string.Equals(_runs[idx].Id, _selectedId, StringComparison.Ordinal)) { _phaseIdx = 0; _taskIdx = 0; _taskExpanded = false; }
        _selectedId = _runs[idx].Id;
    }

    /// <summary>Move the phase (section) selection of the selected run, clamped.</summary>
    public void MovePhase(int delta)
    {
        var run = SelectedRun();
        if (run is null || run.Manifest.Sections.Count == 0) return;
        int next = Math.Clamp(_phaseIdx + delta, 0, run.Manifest.Sections.Count - 1);
        if (next != _phaseIdx) { _taskIdx = 0; _taskExpanded = false; }
        _phaseIdx = next;
    }

    /// <summary>Move the task SELECTION within the selected phase (Up/Down); the visible
    /// window follows the selection. Collapses any expansion on move.</summary>
    public void MoveTask(int delta)
    {
        var run = SelectedRun();
        if (run is null || run.Manifest.Sections.Count == 0) return;
        var sec = run.Manifest.Sections[Math.Clamp(_phaseIdx, 0, run.Manifest.Sections.Count - 1)];
        if (sec.Tasks.Count == 0) return;
        _taskIdx = Math.Clamp(_taskIdx + delta, 0, sec.Tasks.Count - 1);
        _taskExpanded = false;
    }

    /// <summary>Toggle the selected task's expansion (full detail text, wrapped).</summary>
    public void ToggleTaskExpand() => _taskExpanded = !_taskExpanded;

    /// <summary>Select a run by 1-based ordinal (number keys). No-op out of range.</summary>
    public void SelectRunAt(int ordinal)
    {
        if (ordinal < 1 || ordinal > _runs.Count) return;
        var id = _runs[ordinal - 1].Id;
        if (!string.Equals(id, _selectedId, StringComparison.Ordinal)) { _phaseIdx = 0; _taskIdx = 0; _taskExpanded = false; }
        _selectedId = id;
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
    /// RESPONSIVE (same philosophy as the docked footer): below <see cref="StackWidth"/> the
    /// side-by-side panels STACK into a phase strip + full-width task rows, telemetry chips
    /// drop progressively as width shrinks, and every composed row is hard-clamped to the
    /// terminal width so the frame renderer never soft-wraps one into a jumbled multi-row
    /// mess. When <paramref name="height"/> is known the task window shrinks to fit.
    /// </summary>
    public List<string> RenderDashboard(int width, int height = 0)
    {
        var rows = new List<string>();
        int running = _runs.Count(r => r.State == WorkflowRunState.Running);
        rows.Add($"  [{TuiComponents.Accent}]\u25b8 workflows[/] [{TuiComponents.Dim}]\u00b7 {running} running \u00b7 {_runs.Count} total[/]");
        if (_runs.Count == 0)
        {
            rows.Add($"    [{TuiComponents.Dim}]no workflow runs \u00b7 /workflow to start one[/]");
            rows.Add($"  [{TuiComponents.Dim}]esc/q close[/]");
            return Clamp(rows, width);
        }

        bool stacked = width < StackWidth;
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
            _renderRunDir = run.RunDir;
            var secs = run.Manifest.Sections;
            if (secs.Count == 0)
            {
                rows.Add($"      [{TuiComponents.Dim}](no manifest yet - the driver script declares phases as it starts)[/]");
            }
            else
            {
                int pi = Math.Clamp(_phaseIdx, 0, secs.Count - 1);
                var cur = secs[pi];

                // Task window: bounded by MaxTaskRows, and further by the visible height when
                // known (header + run rows + phase chrome + hint reserved) so the dashboard
                // never composes more rows than the terminal can show.
                int maxTaskRows = MaxTaskRows;
                if (height > 0)
                {
                    int reserved = rows.Count + (stacked ? 2 : 1) + 3;   // phase strip/header + window hints + key hint
                    maxTaskRows = Math.Clamp(height - reserved, 3, MaxTaskRows);
                }

                int ti = Math.Clamp(_taskIdx, 0, Math.Max(0, cur.Tasks.Count - 1));
                int off = Math.Clamp(ti - maxTaskRows + 1, 0, Math.Max(0, cur.Tasks.Count - maxTaskRows));
                if (ti < off) off = ti;
                string winHint = cur.Tasks.Count > maxTaskRows
                    ? $" \u00b7 {off + 1}-{Math.Min(off + maxTaskRows, cur.Tasks.Count)} of {cur.Tasks.Count}"
                    : "";

                if (stacked)
                {
                    // STACKED: one phase strip (left/right arrows page it), then full-width
                    // task rows. The strip shows the selected phase plus compact neighbours.
                    int done = cur.Tasks.Count(t => t.Status == "done");
                    string frac = cur.Tasks.Count > 0 ? $"{done}/{cur.Tasks.Count}" : "";
                    string lArr = pi > 0 ? $"[{TuiComponents.Accent}]\u2039[/]" : $"[{TuiComponents.Dim}]\u2039[/]";
                    string rArr = pi < secs.Count - 1 ? $"[{TuiComponents.Accent}]\u203a[/]" : $"[{TuiComponents.Dim}]\u203a[/]";
                    rows.Add($"  {lArr} {PhaseGlyph(cur, pi + 1)} [{TuiComponents.Text}]{Esc(Trunc(cur.Name, Math.Max(8, width - 24)))}[/] " +
                             $"[{TuiComponents.Dim}]{frac} \u00b7 phase {pi + 1}/{secs.Count}[/] {rArr}");
                    if (winHint.Length > 0 || off > 0)
                        rows.Add($"  [{TuiComponents.Dim}]{(off > 0 ? $"\u2191 {off} more" : "")}{Esc(winHint)}[/]");
                    for (int k = off; k < Math.Min(off + maxTaskRows, cur.Tasks.Count); k++)
                        AppendTaskRow(rows, cur.Tasks[k], k == ti, width, stacked: true, indent: "  ");
                    if (off + maxTaskRows < cur.Tasks.Count)
                        rows.Add($"  [{TuiComponents.Dim}]\u2193 {cur.Tasks.Count - off - maxTaskRows} more[/]");
                }
                else
                {
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

                    // RIGHT panel rows: a selection-follow window over the selected phase's tasks
                    // with telemetry (tokens \u00b7 tools \u00b7 duration; running tasks tick live).
                    var right = new List<string> { $"[{TuiComponents.Muted}]{Esc(cur.Name)} \u00b7 {cur.Tasks.Count} task(s){Esc(winHint)}[/]" };
                    if (off > 0) right.Add($"[{TuiComponents.Dim}]\u2191 {off} more[/]");
                    for (int k = off; k < Math.Min(off + maxTaskRows, cur.Tasks.Count); k++)
                        AppendTaskRow(right, cur.Tasks[k], k == ti, width, stacked: false, indent: "");
                    if (off + maxTaskRows < cur.Tasks.Count)
                        right.Add($"[{TuiComponents.Dim}]\u2193 {cur.Tasks.Count - off - maxTaskRows} more[/]");

                    // Compose two columns joined with a vertical rule; pad by PLAIN width.
                    int n = Math.Max(left.Count, right.Count);
                    for (int i = 0; i < n; i++)
                    {
                        string lc = i < left.Count ? left[i] : "";
                        string rc = i < right.Count ? right[i] : "";
                        rows.Add($"      {Pad(lc, PhaseColWidth)}[{TuiComponents.Border}]\u2502[/] {rc}");
                    }
                }
            }
            if (run.Error is not null)
                rows.Add($"      [{TuiComponents.Err}]\u2717 {Esc(Trunc(run.Error, Math.Max(20, width - 12)))}[/]");
        }
        rows.Add(width < 60
            ? $"  [{TuiComponents.Dim}]\u2191\u2193 \u2190\u2192 \u21b5 \u21b9 \u00b7 esc close[/]"
            : $"  [{TuiComponents.Dim}]\u2191\u2193 tasks \u00b7 \u2190\u2192 phase \u00b7 enter expand \u00b7 tab run \u00b7 c cancel \u00b7 esc/q close[/]");
        return Clamp(rows, width);
    }

    /// <summary>Width threshold below which the master/detail panels STACK (phase strip on
    /// top, full-width task rows beneath). Side-by-side needs the phase column + telemetry
    /// budget; under ~96 cols the right panel starves and rows wrapped into garbage.</summary>
    private const int StackWidth = 96;

    /// <summary>Hard-clamp every composed row to the terminal width. The frame renderer
    /// soft-wraps overflowing rows, which is exactly the small-terminal jumble this viewer
    /// must never produce - truncation with an ellipsis is always the right degradation.</summary>
    private static List<string> Clamp(List<string> rows, int width)
    {
        if (width <= 0) return rows;
        for (int i = 0; i < rows.Count; i++)
            if (TuiMarkup.MarkupWidth(rows[i]) > width)
                rows[i] = TuiMarkup.TruncateMarkup(rows[i], width);
        return rows;
    }

    /// <summary>
    /// Append one task row (and its expansion, when selected+expanded): chevron, status
    /// glyph, agent, label, telemetry. Telemetry chips drop progressively as width shrinks
    /// (model first, then tool count, then tokens - duration survives longest, footer-style),
    /// and the label is sized to the space that remains so the chips that DO render never
    /// fall off the row end.
    /// </summary>
    private void AppendTaskRow(List<string> sink, WorkflowTask t, bool isTaskSel, int width, bool stacked, string indent)
    {
        var tele = new List<string>();
        if (!string.IsNullOrEmpty(t.Model) && width >= 100) tele.Add(Esc(t.Model!));
        if (t.Tokens is { } tok && width >= 52) tele.Add(tok >= 1000 ? $"{tok / 1000.0:0.0}k tok" : $"{tok} tok");
        if (t.Tools is { } tc && width >= 64) tele.Add($"{tc} tools");
        if (t.Secs is { } sd) tele.Add(Dur(sd));
        else if (t.Status == "running" && t.StartedAt is { } st)
            tele.Add(Dur((int)(DateTimeOffset.UtcNow - st).TotalSeconds));
        string teleTxt = tele.Count > 0 ? $" [{TuiComponents.Dim}]{string.Join(" \u00b7 ", tele)}[/]" : "";

        // Label budget: whatever the fixed parts leave. Fixed = indent + chevron/glyph(2) +
        // agent + spaces + telemetry; stacked mode has the full width, side-by-side is offset
        // by the phase column + rule.
        int fixedW = indent.Length + 2 + TuiMarkup.MarkupWidth($"[{TuiComponents.Agent}]{Esc(t.Agent)}[/]") + 2
                     + TuiMarkup.MarkupWidth(teleTxt);
        int avail = stacked ? width : width - PhaseColWidth - 8;
        int labelW = Math.Max(8, avail - fixedW);

        string detail = t.Status == "failed" && !string.IsNullOrEmpty(t.Detail) && !(_taskExpanded && isTaskSel)
            ? $" [{TuiComponents.Err}]{Esc(Trunc(t.Detail!, Math.Max(16, labelW)))}[/]" : "";
        string chev = isTaskSel ? $"[{TuiComponents.Accent}]\u203a[/]" : " ";
        sink.Add($"{indent}{chev}{TaskGlyph(t.Status)} [{TuiComponents.Agent}]{Esc(t.Agent)}[/] [{(isTaskSel ? TuiComponents.Text : TuiComponents.Muted)}]{Esc(Trunc(t.Label, labelW))}[/]{teleTxt}{detail}");
        if (_taskExpanded && isTaskSel)
        {
            // Expanded: LIVE TAIL of the task's output file (task_<id>.out in the run
            // dir, streamed by the driver script as the child produces it), falling
            // back to the journal detail when no file exists. Shows the LAST
            // ExpandTailLines wrapped lines so a running task reads like a live
            // transcript tail; refreshed every repaint (the modal repolls ~200ms).
            string body = ReadTaskOutput(t) ?? (string.IsNullOrEmpty(t.Detail) ? "(no output yet)" : t.Detail!);
            string colour = t.Status == "failed" ? TuiComponents.Err : TuiComponents.Dim;
            int wrapW = stacked ? Math.Max(20, width - indent.Length - 5)
                                : Math.Max(24, width - PhaseColWidth - 14);
            var wrapped = TuiMarkup.WrapPlain(body, wrapW);
            int skip = Math.Max(0, wrapped.Count - ExpandTailLines);
            if (skip > 0) sink.Add($"{indent}   [{TuiComponents.Dim}]\u2191 {skip} earlier line(s)[/]");
            foreach (var wl in wrapped.Skip(skip))
                sink.Add($"{indent}   [{colour}]{Esc(wl)}[/]");
        }
    }

    private const int ExpandTailLines = 14;
    private const int MaxOutReadBytes = 64 * 1024;

    // Set per-render so the expansion reader knows the selected run's directory.
    private string? _renderRunDir;

    /// <summary>Best-effort read of the task's live output file (task_&lt;id&gt;.out in the run
    /// dir, appended by the driver script as the child streams). Reads only the trailing
    /// window so huge transcripts stay cheap. Null when absent/unreadable.</summary>
    private string? ReadTaskOutput(WorkflowTask t)
    {
        if (_renderRunDir is null) return null;
        try
        {
            var p = Path.Combine(_renderRunDir, $"task_{t.Id}.out");
            if (!File.Exists(p)) return null;
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0) return null;
            long start = Math.Max(0, fs.Length - MaxOutReadBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var s = sr.ReadToEnd();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");
    private static string Trunc(string s, int max) => TuiMarkup.TruncatePlain(s ?? "", max);
}
