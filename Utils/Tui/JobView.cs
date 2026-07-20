using System.Text;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// The /background jobs dashboard model + renderer: a keyboard-navigable, inline list over the
/// detached background jobs (running AND finished), mirroring the backslash Agent View. Where the
/// Agent View only lists LIVE lanes - so a finished bg agent vanishes from it - this view keeps
/// finished jobs listed, which is exactly the friction /background created: a bg agent's output
/// would otherwise be unreachable once it completes.
///
/// Each row shows id, status, agent, duration, activity (running jobs show the live status;
/// finished show a tail preview), and the goal. Keys: arrows/j-k move, o/Enter reopens the selected
/// FINISHED job's transcript (committed as a retained expandable), c cancels a RUNNING job,
/// Esc/q closes.
///
/// PURE: holds the snapshot + selection and produces markup rows; performs no console I/O and no
/// reopen/cancel itself (the driver wires those), so the whole model is headless-testable.
/// Not thread-safe - the driver serializes access through the console lock.
/// </summary>
internal sealed class JobView
{
    /// <summary>One dashboard row: a detached background job and its live/final status.</summary>
    internal readonly record struct JobRow(string Id, string Agent, string Status, string Activity,
        string Goal, int DurationSeconds, bool Running, string Tint);

    private List<JobRow> _rows = new();
    private string? _selectedId;   // tracked by ID so selection survives snapshot updates
    private bool _open;

    /// <summary>True while the dashboard is foregrounded (drawn into the live region).</summary>
    public bool IsOpen => _open;

    /// <summary>Open the dashboard. Idempotent.</summary>
    public void Open() => _open = true;

    /// <summary>Close the dashboard. Idempotent.</summary>
    public void Close() => _open = false;

    /// <summary>Total jobs in the current snapshot.</summary>
    public int Count => _rows.Count;

    /// <summary>
    /// Replace the job snapshot from the live registry. Selection is preserved by id; if the
    /// selected job vanished it falls back to the first row on the next navigation/query.
    /// </summary>
    public void SetRows(IReadOnlyList<JobRow> snapshot)
    {
        _rows = new List<JobRow>(snapshot);
        _selectedId ??= _rows.Count > 0 ? _rows[0].Id : null;
        if (_selectedId is not null && !_rows.Any(r => string.Equals(r.Id, _selectedId, StringComparison.Ordinal)))
            _selectedId = _rows.Count > 0 ? _rows[0].Id : null;
    }

    /// <summary>All rows (jobs are a finite registry list; no idle-hide or cap needed).</summary>
    public List<JobRow> VisibleRows() => new(_rows);

    /// <summary>The job id currently selected (falls back to the first row, or null when empty).</summary>
    public string? SelectedId()
    {
        if (_rows.Count == 0) return null;
        foreach (var r in _rows)
            if (string.Equals(r.Id, _selectedId, StringComparison.Ordinal)) return r.Id;
        return _rows[0].Id;
    }

    /// <summary>The selected row, or null when empty.</summary>
    public JobRow? Selected()
    {
        string? id = SelectedId();
        if (id is null) return null;
        foreach (var r in _rows)
            if (string.Equals(r.Id, id, StringComparison.Ordinal)) return r;
        return null;
    }

    /// <summary>Move the selection by <paramref name="delta"/> rows, clamped at the ends (no wrap).</summary>
    public void Move(int delta)
    {
        if (_rows.Count == 0) return;
        int idx = 0;
        for (int i = 0; i < _rows.Count; i++)
            if (string.Equals(_rows[i].Id, _selectedId, StringComparison.Ordinal)) { idx = i; break; }
        idx = Math.Clamp(idx + delta, 0, _rows.Count - 1);
        _selectedId = _rows[idx].Id;
    }

    /// <summary>
    /// Render the dashboard as markup rows for the live region: a header, the selection-highlighted
    /// job list, and a one-line key hint. Pure given its inputs - unit-tested without a console.
    /// </summary>
    public List<string> RenderDashboard(int width, int frame)
    {
        var rows = new List<string>();
        int running = _rows.Count(r => r.Running);
        rows.Add($"  [{TuiComponents.Accent}]\u25b8 background jobs[/] [{TuiComponents.Dim}]\u00b7 {running} running \u00b7 {_rows.Count} total[/]");
        if (_rows.Count == 0)
        {
            rows.Add($"    [{TuiComponents.Dim}]no background jobs[/]");
        }
        else
        {
            string? sel = SelectedId();
            foreach (var r in _rows)
            {
                bool isSel = string.Equals(r.Id, sel, StringComparison.Ordinal);
                string goal = r.Goal.Length > 44 ? r.Goal[..43] + "\u2026" : r.Goal;
                string act = r.Activity.Length > 40 ? r.Activity[..39] + "\u2026" : r.Activity;
                string stat = r.Running
                    ? $"[{TuiComponents.Ok}]{Esc(r.Status)}[/]"
                    : $"[{TuiComponents.Dim}]{Esc(r.Status)}[/]";
                string line =
                    $"[{r.Tint}]{Esc(r.Id)}[/] {stat} [{TuiComponents.Agent}]{Esc(r.Agent)}[/] " +
                    $"[{TuiComponents.Dim}]{r.DurationSeconds}s \u00b7 {Esc(act)} \u00b7[/] [{TuiComponents.Dim}]{Esc(goal)}[/]";
                rows.Add(isSel ? $"  [{TuiComponents.Accent}]\u203a[/] {line}" : $"    {line}");
            }
        }
        rows.Add($"  [{TuiComponents.Dim}]\u2191\u2193 select \u00b7 o/enter reopen \u00b7 c cancel \u00b7 esc/q close[/]");
        return rows;
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s ?? "");
}
