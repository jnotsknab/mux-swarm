using System.Text;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Drives /review: takes a working-tree diff and has the ACTIVE session model perform a read-only
/// code review (correctness, security, performance, tests, style, breaking changes), returning
/// prioritized findings. It NEVER edits - it proposes. Mirrors SystemDiagnostics/SessionHandoff.
/// </summary>
public static class CodeReview
{
    public static async Task<string> ReviewAsync(
        string diff,
        IChatClient client,
        ChatOptions? chatOptions,
        CancellationToken ct)
    {
        var system = new StringBuilder();
        system.AppendLine("You are a senior code reviewer. Review ONLY the unified diff below. Produce a");
        system.AppendLine("concise, PRIORITIZED set of findings. You are READ-ONLY: propose changes, never");
        system.AppendLine("rewrite files.");
        system.AppendLine();
        system.AppendLine("Cover, in priority order, only what the diff warrants:");
        system.AppendLine("  - Correctness & logic bugs");
        system.AppendLine("  - Security (injection, authz, secrets, unsafe input)");
        system.AppendLine("  - Performance (hot paths, allocations, N+1, blocking)");
        system.AppendLine("  - Tests (missing/weak coverage for the change)");
        system.AppendLine("  - Breaking changes / API or behavior compatibility");
        system.AppendLine("  - Error handling & edge cases");
        system.AppendLine("  - Style & readability (last)");
        system.AppendLine();
        system.AppendLine("Format: group findings by severity P0 (must fix) / P1 (should fix) / P2 (nice to");
        system.AppendLine("have). For each: file:line (from the diff), the issue in one line, and a concrete");
        system.AppendLine("fix. If the diff is clean, say so plainly. No filler, no restating the diff.");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system.ToString()),
            new(ChatRole.User, "## Diff to review\n\n```diff\n" + diff + "\n```")
        };

        var response = await client.GetResponseAsync(messages, chatOptions, ct);
        return response?.Text ?? string.Empty;
    }
}
