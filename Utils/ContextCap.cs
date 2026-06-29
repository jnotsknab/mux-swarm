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
    public static async Task<bool> CheckFileAsync(
        string fileName,
        Func<string, IChatClient>? chatClientFactory = null,
        string? model = null,
        CancellationToken ct = default)
    {
        return await CheckFileAsync(fileName, chatClientFactory, model, quiet: false, ct);
    }

    /// <summary>
    /// Core cap check. Returns true iff a force-rewrite actually fired. <paramref name="quiet"/>
    /// suppresses the warn/no-model console lines (used by the background pulse, which must stay
    /// silent on idle ticks and only surface a status when a rewrite happens).
    /// </summary>
    public static async Task<bool> CheckFileAsync(
        string fileName,
        Func<string, IChatClient>? chatClientFactory,
        string? model,
        bool quiet,
        CancellationToken ct = default)
    {
        try
        {
            var limits = LimitsFor(fileName);
            if (limits is null) return false;
            var (limit, mode) = limits.Value;
            if (limit <= 0 || mode == "off") return false;

            var path = Path.Combine(ContextDir, fileName);
            if (!File.Exists(path)) return false;

            var content = await File.ReadAllTextAsync(path, ct);
            if (content.Length <= limit) return false;

            if (mode == "warn")
            {
                if (!quiet)
                    MuxConsole.WriteWarning(
                        $"[context-cap] {fileName} is {content.Length} chars, over the {limit} limit (mode=warn).");
                return false;
            }

            if (mode == "force")
            {
                if (chatClientFactory == null || string.IsNullOrWhiteSpace(model))
                {
                    if (!quiet)
                        MuxConsole.WriteWarning(
                            $"[context-cap] {fileName} is {content.Length} chars, over the {limit} limit " +
                            "(mode=force, but no model available this context - not rewritten).");
                    return false;
                }

                return await ForceRewriteAsync(path, fileName, content, limit, chatClientFactory(model), ct);
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
                MuxConsole.WriteMuted($"[context-cap] check skipped for {fileName}: {ex.Message}");
        }
        return false;
    }

    /// <summary>Back up then LLM-rewrite a context file under its cap. Falls back to warn on failure.</summary>
    private static async Task<bool> ForceRewriteAsync(
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
                return false;
            }

            await File.WriteAllTextAsync(path, rewritten, ct);
            MuxConsole.WriteSuccess(
                $"[context-cap] {fileName} rewritten from {content.Length} to {rewritten.Length} chars " +
                $"(<= {limit}). Backup at {Path.GetFileName(bak)}.");
            return true;
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[context-cap] force rewrite failed for {fileName}: {ex.Message}. Original kept.");
        }
        return false;
    }

    /// <summary>
    /// True when the background prune pulse should run: a positive pulse interval is configured AND
    /// at least one context file is in "force" mode (only force can rewrite; warn/off never do).
    /// </summary>
    public static bool ShouldPulse()
    {
        var cl = App.Config?.ContextLimits;
        if (cl == null || cl.PrunePulseSeconds <= 0) return false;
        bool brainForce = (cl.BrainMdCapMode ?? "off").Trim().ToLowerInvariant() == "force" && cl.BrainMdCharLimit > 0;
        bool memForce = (cl.MemoryMdCapMode ?? "off").Trim().ToLowerInvariant() == "force" && cl.MemoryMdCharLimit > 0;
        return brainForce || memForce;
    }

    /// <summary>
    /// One pulse tick: quietly check both capped context files and force-rewrite any over their
    /// limit. Returns true if any rewrite fired (the caller surfaces a status line only then).
    /// </summary>
    public static async Task<bool> PulseAsync(
        Func<string, IChatClient>? chatClientFactory, string? model, CancellationToken ct = default)
    {
        bool any = false;
        any |= await CheckFileAsync(BrainFile, chatClientFactory, model, quiet: true, ct);
        any |= await CheckFileAsync(MemoryFile, chatClientFactory, model, quiet: true, ct);
        return any;
    }

    /// <summary>
    /// Start the background prune pulse if configured. Fire-and-forget: a dedicated loop sleeps
    /// 30s, then every PrunePulseSeconds calls <see cref="PulseAsync"/>; on an actual rewrite it
    /// emits a single status line. No-op when <see cref="ShouldPulse"/> is false. Best-effort -
    /// never throws into the caller.
    /// </summary>
    public static void StartPulse(
        Func<string, IChatClient>? chatClientFactory, string? model, CancellationToken ct = default)
    {
        if (!ShouldPulse()) return;
        int seconds = App.Config!.ContextLimits!.PrunePulseSeconds;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        bool rewrote = await PulseAsync(chatClientFactory, model, ct);
                        if (rewrote)
                            MuxConsole.WriteMuted("[context-cap] background prune rewrote a context file.");
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* best-effort; keep pulsing */ }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, seconds)), ct);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch { /* never surface pulse-loop failures */ }
        }, ct);
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
