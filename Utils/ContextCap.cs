using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Enforces the optional hard character limits on the shared context memory files
/// (BRAIN.md / MEMORY.md) configured under <see cref="ContextLimitsConfig"/>.
///
/// Per-file mode:
///   off   - never checked.
///   warn  - if the file exceeds its limit, print a console warning (startup + on write).
///   force - if the file exceeds its limit, back it up as &lt;name&gt;.bak then call a one-shot
///           LLM rewrite that condenses the content under the cap (preserving high-signal
///           facts). If the rewrite fails or does not get under the cap, fall back to warn.
///
/// All paths flow through <see cref="PlatformContext.ContextDirectory"/>. Limit == 0 means
/// the file is uncapped regardless of mode. Entirely additive: defaults are off so existing
/// installs are unaffected.
/// </summary>
public static class ContextCap
{
    public const string BrainFile = "BRAIN.md";
    public const string MemoryFile = "MEMORY.md";

    private static string ContextDir => PlatformContext.ContextDirectory;

    /// <summary>Resolve (limit, mode) for a context file name from config; null if unknown file.</summary>
    private static (int Limit, string Mode)? LimitsFor(string fileName)
    {
        var cl = App.Config?.ContextLimits;
        if (cl == null) return null;
        if (fileName.Equals(BrainFile, StringComparison.OrdinalIgnoreCase))
            return (cl.BrainMdCharLimit, (cl.BrainMdCapMode ?? "off").Trim().ToLowerInvariant());
        if (fileName.Equals(MemoryFile, StringComparison.OrdinalIgnoreCase))
            return (cl.MemoryMdCharLimit, (cl.MemoryMdCapMode ?? "off").Trim().ToLowerInvariant());
        return null;
    }

    /// <summary>
    /// Check a single context file against its cap and act per its mode. Safe to call on every
    /// relevant write and at startup. Best-effort: never throws. <paramref name="chatClient"/>
    /// and <paramref name="model"/> are only needed for "force" rewrites; if null, force
    /// degrades to warn.
    /// </summary>
    public static async Task CheckFileAsync(
        string fileName,
        Func<string, IChatClient>? chatClientFactory = null,
        string? model = null,
        CancellationToken ct = default)
    {
        try
        {
            var limits = LimitsFor(fileName);
            if (limits is null) return;
            var (limit, mode) = limits.Value;
            if (limit <= 0 || mode == "off") return;

            var path = Path.Combine(ContextDir, fileName);
            if (!File.Exists(path)) return;

            var content = await File.ReadAllTextAsync(path, ct);
            if (content.Length <= limit) return;

            if (mode == "warn")
            {
                MuxConsole.WriteWarning(
                    $"[context-cap] {fileName} is {content.Length} chars, over the {limit} limit (mode=warn).");
                return;
            }

            if (mode == "force")
            {
                if (chatClientFactory == null || string.IsNullOrWhiteSpace(model))
                {
                    MuxConsole.WriteWarning(
                        $"[context-cap] {fileName} is {content.Length} chars, over the {limit} limit " +
                        "(mode=force, but no model available this context - not rewritten).");
                    return;
                }

                await ForceRewriteAsync(path, fileName, content, limit, chatClientFactory(model), ct);
            }
        }
        catch (Exception ex)
        {
            MuxConsole.WriteMuted($"[context-cap] check skipped for {fileName}: {ex.Message}");
        }
    }

    /// <summary>Back up then LLM-rewrite a context file under its cap. Falls back to warn on failure.</summary>
    private static async Task ForceRewriteAsync(
        string path, string fileName, string content, int limit, IChatClient client, CancellationToken ct)
    {
        // Always preserve the original before touching it.
        var bak = path + ".bak";
        try { File.Copy(path, bak, overwrite: true); }
        catch (Exception ex) { MuxConsole.WriteMuted($"[context-cap] backup failed for {fileName}: {ex.Message}"); }

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    $"""
                    You are condensing a long-lived agent context file named {fileName} so it fits
                    under a hard limit of {limit} characters. Rewrite it to be STRICTLY shorter than
                    {limit} characters while preserving the maximum amount of durable, high-signal
                    information.

                    MUST preserve: operator identity/preferences, standing directives and rules,
                    environment/infrastructure facts (paths, services, ports, hosts), active projects
                    and their state, known incidents/constraints, and any explicit do/don't guidance.

                    Drop or merge: redundant phrasing, stale one-off notes, verbose explanations,
                    duplicated facts, and low-value commentary. Keep the existing Markdown section
                    structure where practical. Output ONLY the rewritten file content, no preamble.
                    """),
                new(ChatRole.User, content)
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: ct);
            var rewritten = response.Text ?? "";

            if (string.IsNullOrWhiteSpace(rewritten) || rewritten.Length > limit)
            {
                MuxConsole.WriteWarning(
                    $"[context-cap] force rewrite of {fileName} did not get under {limit} chars " +
                    $"(got {rewritten.Length}). Original kept; backup at {Path.GetFileName(bak)}.");
                return;
            }

            await File.WriteAllTextAsync(path, rewritten, ct);
            MuxConsole.WriteSuccess(
                $"[context-cap] {fileName} rewritten from {content.Length} to {rewritten.Length} chars " +
                $"(<= {limit}). Backup at {Path.GetFileName(bak)}.");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[context-cap] force rewrite failed for {fileName}: {ex.Message}. Original kept.");
        }
    }

    /// <summary>
    /// Pure helper: classify a file's length against a limit/mode. Returns the action that WOULD
    /// be taken ("none"|"warn"|"force"). Used for startup checks and unit tests without I/O.
    /// </summary>
    public static string ClassifyAction(int contentLength, int limit, string? mode)
    {
        mode = (mode ?? "off").Trim().ToLowerInvariant();
        if (limit <= 0 || mode == "off") return "none";
        if (contentLength <= limit) return "none";
        return mode == "force" ? "force" : "warn";
    }
}
