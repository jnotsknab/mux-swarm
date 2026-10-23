namespace MuxSwarm.Engine;

/// <summary>/logs: recent console notices (incl. the quiet startup lines) and runtime log lines, in a panel.</summary>
internal static class LogsCommand
{
    /// <summary>Rows for the /logs panel: this session's notices, then the newest runtime (telemetry) log lines.</summary>
    internal static List<string> Rows(int runtimeLimit = 150)
    {
        string Esc(string s) => Spectre.Console.Markup.Escape(s);
        string Color(string level) => level switch
        {
            "error" => Tui.RegionStyle.Color("notice.error"),
            "warn" or "warning" => Tui.RegionStyle.Color("notice.warn"),
            "ok" => Tui.RegionStyle.Color("notice.ok"),
            "muted" or "debug" => Theme.Active.Muted,
            _ => Tui.RegionStyle.Color("notice.info"),
        };
        var rows = new List<string> { $"[{Theme.Active.Step}]This session[/]" };
        var notices = MuxConsole.NoticeLog;
        if (notices.Count == 0) rows.Add($"  [{Theme.Active.Muted}]no notices yet[/]");
        foreach (var (level, text, at) in notices)
            rows.Add($"  [{Theme.Active.Muted}]{at:HH:mm:ss}[/] [{Color(level)}]{Esc(level.PadRight(5))}[/] {Esc(text)}");
        rows.Add("");
        rows.Add($"[{Theme.Active.Step}]Runtime log (newest first)[/]");
        IReadOnlyList<Telemetry.TraceStore.Rec> logs;
        try { logs = Telemetry.TraceStore.ListLogs(runtimeLimit, days: 2); }
        catch { logs = []; }
        if (logs.Count == 0) rows.Add($"  [{Theme.Active.Muted}]no runtime log lines[/]");
        foreach (var r in logs)
        {
            string level = (r.Level ?? "info").ToLowerInvariant();
            rows.Add($"  [{Theme.Active.Muted}]{r.TsUtc.ToLocalTime():MM-dd HH:mm:ss}[/] [{Color(level)}]{Esc(level.PadRight(5))}[/] {Esc((r.Text ?? "").ReplaceLineEndings(" "))}");
        }
        return rows;
    }

    /// <summary>Show the logs: a full-screen panel under the TUI, else an inline block.</summary>
    internal static void Run()
    {
        var rows = Rows();
        if (!MuxConsole.TryLongPanel("Logs", rows))
            MuxConsole.WritePanelMarkup("Logs", rows, string.Join("\n", rows.Select(Tui.TuiMarkup.Plain)));
    }
}
