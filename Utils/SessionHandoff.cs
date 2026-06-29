using System.Text;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Generates a structured, cold-resume session-handoff document on demand (/handoff), using the
/// ACTIVE session model. Optional steering instruction is threaded into the system prompt. The
/// caller owns where the result is written; this helper only produces the markdown text.
/// </summary>
public static class SessionHandoff
{
    /// <summary>
    /// Produce a handoff document from the live conversation. Returns null on failure (no model,
    /// empty response, or transport error) so the caller can surface a clean message.
    /// </summary>
    public static async Task<string?> GenerateAsync(
        IReadOnlyList<ChatMessage> history,
        IChatClient client,
        string? instruction = null,
        ChatOptions? chatOptions = null,
        CancellationToken ct = default)
    {
        if (client is null || history is null || history.Count == 0)
            return null;

        var transcript = new StringBuilder();
        foreach (var msg in history)
        {
            string role = msg.Role == ChatRole.User ? "User" : "Agent";
            transcript.AppendLine($"[{role}]: {msg.Text ?? string.Empty}");
        }

        var system = new StringBuilder();
        system.AppendLine("You are a technical handoff writer. Produce a DENSE, structured handoff");
        system.AppendLine("document from the conversation below so a fresh session can resume COLD with");
        system.AppendLine("no other context.");
        system.AppendLine();
        system.AppendLine("MUST include (use these ## sections, omit a section only if truly empty):");
        system.AppendLine("  ## Summary - what this session was about, current state");
        system.AppendLine("  ## Key Decisions - decisions made and the reasoning");
        system.AppendLine("  ## Artifacts - file paths, build outputs, hashes, commits, URLs");
        system.AppendLine("  ## State - what is done vs in-progress");
        system.AppendLine("  ## Next Steps - concrete, actionable, enough detail to resume");
        system.AppendLine("  ## Gotchas - watch-outs, blockers, open questions");
        system.AppendLine();
        system.AppendLine("Be specific: preserve exact paths, identifiers, model names, config keys, and");
        system.AppendLine("error messages verbatim. Drop pleasantries and reasoning chains. Output ONLY");
        system.AppendLine("the markdown document, no preamble.");

        if (!string.IsNullOrWhiteSpace(instruction))
        {
            system.AppendLine();
            system.AppendLine($"Additional steering from the user: {instruction.Trim()}");
        }

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, system.ToString()),
                new(ChatRole.User, transcript.ToString())
            };
            var response = await client.GetResponseAsync(messages, chatOptions, ct);
            var text = response.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
