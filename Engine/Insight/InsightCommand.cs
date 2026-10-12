namespace MuxSwarm.Engine.Insight;

/// <summary><c>/insight [off|basic|full|max]</c>: show or set the insight level (persisted to config.json).</summary>
internal static class InsightCommand
{
    /// <summary>True for <c>/insight</c> and <c>/insight &lt;arg&gt;</c>.</summary>
    internal static bool Matches(string input) =>
        input.Equals("/insight", StringComparison.OrdinalIgnoreCase)
        || input.StartsWith("/insight ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bare: print the current level and the choices. With a level: apply it live and persist it.</summary>
    internal static void Run(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var current = InsightLevels.Current;
        if (parts.Length < 2)
        {
            MuxConsole.WriteInfo($"Insight: {InsightLevels.Name(current)}. Set with /insight off|basic|full|max.");
            MuxConsole.WriteMuted("basic = context line on each model call; full = + growth, tool share, compactions; " +
                                  "max = + context_map/context_drop tools (next session).");
            return;
        }
        if (!InsightLevels.TryParse(parts[1], out var level))
        {
            MuxConsole.WriteWarning($"Unknown insight level '{parts[1].Trim()}'. Use off, basic, full or max.");
            return;
        }
        App.Config.Insight.Level = InsightLevels.Name(level);
        try
        {
            Common.SaveConfig(App.Config);
            MuxConsole.WriteSuccess($"Insight set to {InsightLevels.Name(level)} (persisted to config.json).");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"Insight set to {InsightLevels.Name(level)} for this run, but saving failed: {ex.Message}");
        }
    }
}
