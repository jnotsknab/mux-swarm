using System.Text.Json;

namespace MuxSwarm.Engine;

public static partial class MuxConsole
{
    /// <summary>
    /// Offer the alt-screen session HISTORY browser (/history), or - with
    /// <paramref name="resumePicker"/> - the bare-/resume fuzzy picker (Enter resumes, v previews).
    /// Returns false when no docked driver owns input (classic/stdio callers keep their prompts);
    /// <paramref name="resumeId"/> is non-null only for an explicit resume selection.
    /// </summary>
    internal static bool TryHistoryBrowser(
        IReadOnlyList<(string Id, string Preview)> sessions,
        Func<string, JsonElement?> loadSession,
        bool resumePicker,
        out string? resumeId)
    {
        resumeId = null;
        if (!ViaDriver || InputOverride != Console.In) return false;
        return _driver!.RunHistoryBrowser(
            new Tui.HistoryView(sessions, resumePicker), loadSession, out resumeId);
    }
}
