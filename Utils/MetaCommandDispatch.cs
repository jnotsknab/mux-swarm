using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Shared dispatch for the session-AGNOSTIC interactive meta commands that must behave
/// identically in every interactive loop - the single-agent first prompt + its meta-loop, the
/// /swarm and /pswarm goal loops. Handles /background (/bg), /daemon (/da), /kanban, and the
/// slash-anywhere REPL hand-off (a REPL-only command typed inside a session warns + checkpoints
/// it to run at the top-level menu). Commands that need single-agent session state (/compact,
/// /undo, /retry, /effort, ...) are NOT here - they stay in SingleAgentOrchestrator's own
/// meta-loop. Returns NotHandled for anything else so the caller treats it as a goal.
/// </summary>
internal static class MetaCommandDispatch
{
    public enum Result
    {
        /// <summary>Not a recognized meta command - caller should treat the line as a goal.</summary>
        NotHandled,
        /// <summary>Handled in place (or a declined REPL hand-off) - caller should re-prompt.</summary>
        Handled,
        /// <summary>A REPL-only command was confirmed; PendingReplCommand is set. Caller should
        /// end the session and return to the top-level menu, which dispatches it.</summary>
        QuitToMenu,
    }

    /// <summary>
    /// Try to handle <paramref name="input"/> as a session-agnostic meta command. The factory and
    /// model map are only needed by /background; when omitted they default to the app-wide
    /// chat-client factory and the configured agent models, so swarm callers can pass nothing.
    /// </summary>
    public static async Task<Result> TryHandleAsync(
        string? input,
        System.Func<string, IChatClient>? chatClientFactory = null,
        Dictionary<string, string>? agentModels = null,
        CancellationToken ct = default)
    {
        var line = (input ?? string.Empty).Trim();
        if (line.Length == 0 || line[0] != '/') return Result.NotHandled;

        string cmd = line.Split(' ', 2)[0].ToLowerInvariant();
        switch (cmd)
        {
            case "/background":
            case "/bg":
                await DetachedRunner.RunCommand(
                    line,
                    chatClientFactory ?? (m => App.CreateChatClient(m)),
                    agentModels ?? Common.LoadAgentModels(),
                    ct);
                return Result.Handled;

            case "/daemon":
            case "/da":
                MuxSwarm.State.DaemonCommand.Run(line);
                return Result.Handled;

            case "/kanban":
                MuxSwarm.Utils.Teams.KanbanCommand.Run(line);
                return Result.Handled;
        }

        if (Tui.TuiCommands.IsReplOnly(cmd))
        {
            // Slash-anywhere: a REPL-only command typed inside a live session does not work here.
            // Offer to end the session and run it at the top-level menu (the App menu consumes
            // SingleAgentOrchestrator.PendingReplCommand). Declining is a no-op (never sent to the
            // agent as text), so the user is not surprised by the slash text becoming a goal.
            bool end = MuxConsole.Confirm(
                $"'{cmd}' only runs at the main menu. End this session and run it there?", false);
            if (end)
            {
                SingleAgentOrchestrator.PendingReplCommand = line;
                return Result.QuitToMenu;
            }
            return Result.Handled;
        }

        return Result.NotHandled;
    }
}
