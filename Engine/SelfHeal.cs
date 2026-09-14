using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>
/// Drives /heal and /reflect: a self-examination pass over the CURRENT session (normal mode)
/// using the ACTIVE session model. It reviews recent turns for repeated errors, missed memory
/// write-backs, useful reflexes, and anti-patterns, then proposes concise BRAIN.md / MEMORY.md
/// entries and complete skills for the user to approve (MultiSelect) before anything is written.
///
/// Deep mode is a heavier variant the caller may route to a swarm; this helper exposes the
/// single-pass analysis + the apply step, which deep mode reuses after its own consolidation.
/// </summary>
public static class SelfHeal
{
    /// <summary>A BRAIN/MEMORY write-back or a complete SKILL proposal.</summary>
    /// <param name="Type">BRAIN, MEMORY, or SKILL.</param>
    /// <param name="Key">Short memory key or skill name.</param>
    /// <param name="Content">Concise one-line memory entry or skill description; never the skill body.</param>
    /// <param name="SkillBody">Complete Markdown body for SKILL; null for memory proposals.</param>
    public readonly record struct Proposal(string Type, string Key, string Content, string? SkillBody = null)
    {
        /// <summary>One-line label for the MultiSelect picker.</summary>
        public string Label => $"[{Type}] {Key}: {Content}";
    }

    /// <summary>
    /// Analyze the conversation and return proposed write-backs. Returns an empty list on any
    /// failure (no model, empty response, transport error) so the caller degrades cleanly.
    /// </summary>
    public static async Task<List<Proposal>> AnalyzeAsync(
        IReadOnlyList<ChatMessage> history,
        IChatClient client,
        bool deep = false,
        string? instruction = null,
        ChatOptions? chatOptions = null,
        CancellationToken ct = default)
    {
        if (client is null || history is null || history.Count == 0)
            return new List<Proposal>();

        var transcript = new StringBuilder();
        foreach (var msg in history)
        {
            string role = msg.Role == ChatRole.User ? "User" : "Agent";
            transcript.AppendLine($"[{role}]: {msg.Text ?? string.Empty}");
        }

        var system = new StringBuilder();
        system.AppendLine("You are a self-improvement reviewer for an AI coding agent. Review the");
        system.AppendLine("session below and identify durable lessons worth persisting to the agent's");
        system.AppendLine("long-lived memory files.");
        system.AppendLine();
        system.AppendLine("Look for:");
        system.AppendLine("  - Repeated errors or mistakes the agent made (-> a REFLEX/anti-pattern)");
        system.AppendLine("  - Missed memory write-backs: durable facts/decisions never persisted");
        system.AppendLine("  - Useful patterns, reflexes, or gotchas worth remembering");
        system.AppendLine("  - Corrections the user made that should not need repeating");
        system.AppendLine();
        system.AppendLine("Route each finding to the right layer:");
        system.AppendLine("  BRAIN  = behavioral: how to act, reflexes, anti-patterns, conventions");
        system.AppendLine("  MEMORY = factual: durable facts about the user, project, or environment");
        system.AppendLine("  SKILL  = a reusable procedure demonstrated in this session, ready to use");
        system.AppendLine();
        system.AppendLine("Output only a valid JSON array, with no Markdown fences or surrounding prose:");
        system.AppendLine("""
            [
              {"type":"BRAIN","key":"short key","content":"concise one-line lesson","skillBody":null},
              {"type":"MEMORY","key":"short key","content":"concise one-line fact","skillBody":null},
              {"type":"SKILL","key":"skill-name","content":"one-line description","skillBody":"# Skill title\n\n## When to use\nDescribe the trigger.\n\n## Steps\n1. Explain the complete reusable procedure.\n\n## Verification\nExplain how to check the result.\n"}
            ]
            """);
        system.AppendLine("Keys and content must be nonempty, single-line strings. Keep content concise.");
        system.AppendLine("For SKILL, skillBody must contain complete, actionable Markdown instructions,");
        system.AppendLine("including appropriate steps, prerequisites, and verification. No TODOs or stubs.");
        system.AppendLine("Do not include YAML frontmatter in skillBody; the runtime supplies name/description.");
        system.AppendLine("Encode body newlines and quotes as JSON string escapes; preserve code indentation and pipes.");
        system.AppendLine("For BRAIN/MEMORY, omit skillBody or set it to null.");
        system.AppendLine("Propose only HIGH-VALUE durable items; skip noise, secrets, and obvious facts.");
        system.AppendLine("If nothing is worth persisting, output [].");

        if (deep)
        {
            system.AppendLine();
            system.AppendLine("DEEP MODE: consolidate across the whole history; dedupe aggressively and");
            system.AppendLine("prefer a few high-signal entries over many small ones.");
        }

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
            return ParseProposals(response.Text ?? string.Empty);
        }
        catch
        {
            return new List<Proposal>();
        }
    }

    /// <summary>Parse one JSON array, skipping invalid entries; malformed JSON yields no proposals.</summary>
    public static List<Proposal> ParseProposals(string text)
    {
        var results = new List<Proposal>();
        if (string.IsNullOrWhiteSpace(text)) return results;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return results;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? Field(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                var type = Field("type")?.Trim().ToUpperInvariant();
                var key = Field("key")?.Trim();
                var content = Field("content")?.Trim();
                if (type is not ("BRAIN" or "MEMORY" or "SKILL") || !IsSingleLine(key) || !IsSingleLine(content)) continue;
                var body = type == "SKILL" ? Field("skillBody") : null;
                if (type == "SKILL" && !HasSkillBody(body)) continue;
                results.Add(new Proposal(type, key!, content!, body));
            }
        }
        catch (JsonException) { /* Reject malformed model output; never guess at a partial proposal. */ }
        return results;
    }

    private static bool IsSingleLine(string? value)
        => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool HasSkillBody(string? body)
        => !string.IsNullOrWhiteSpace(body) && !body.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    /// <summary>
    /// Append accepted memory entries under a dated heading and save complete skills without overwriting.
    /// Invalid/incomplete skills are skipped. Respects the configured memory char-cap afterward.
    /// </summary>
    public static async Task ApplyAsync(
        IReadOnlyList<Proposal> accepted,
        Func<string, IChatClient>? chatClientFactory = null,
        string? model = null,
        CancellationToken ct = default)
    {
        if (accepted is null || accepted.Count == 0) return;

        var ctxDir = PlatformContext.ContextDirectory;
        Directory.CreateDirectory(ctxDir);
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");

        await AppendGroupAsync(
            Path.Combine(ctxDir, ContextCap.BrainFile),
            accepted.Where(p => p.Type == "BRAIN").ToList(),
            $"## Heal {stamp}", ct);

        await AppendGroupAsync(
            Path.Combine(ctxDir, ContextCap.MemoryFile),
            accepted.Where(p => p.Type == "MEMORY").ToList(),
            $"## Heal {stamp}", ct);

        // Save complete accepted skills under the same root as /installskill, then refresh the manifest.
        var skillProps = accepted.Where(p => p.Type == "SKILL").ToList();
        if (skillProps.Count > 0)
        {
            bool any = false;
            foreach (var p in skillProps)
                any |= SaveSkill(p.Key, p.Content, p.SkillBody);
            if (any)
            {
                try { SkillLoader.LoadSkills(); }
                catch (Exception ex) { MuxConsole.WriteWarning($"[heal] skill hot-reload failed: {ex.Message}"); }
            }
        }

        // Honor the char-cap on the files we just grew.
        await ContextCap.CheckFileAsync(ContextCap.BrainFile, chatClientFactory, model, ct);
        await ContextCap.CheckFileAsync(ContextCap.MemoryFile, chatClientFactory, model, ct);
    }

    /// <summary>Publish a complete SKILL.md with escaped metadata and the supplied Markdown body unchanged.</summary>
    private static bool SaveSkill(string rawName, string description, string? body)
    {
        // ApplyAsync can also be called directly; validate before creating any skill directory.
        if (!IsSingleLine(rawName) || !IsSingleLine(description) || !HasSkillBody(body))
        {
            MuxConsole.WriteWarning("[heal] incomplete skill proposal skipped; name, description and body are required.");
            return false;
        }
        try
        {
            var name = SanitizeSkillName(rawName);
            if (name.Length == 0) return false;
            var dir = Path.Combine(PlatformContext.SkillsDirectory, name);
            var skillMd = Path.Combine(dir, "SKILL.md");
            if (File.Exists(skillMd))
            {
                MuxConsole.WriteWarning($"[heal] skill '{name}' already exists - skipped.");
                return false;
            }
            // JSON string quoting is a YAML-compatible double-quoted scalar; body stays out of metadata.
            string document = $"---\nname: {name}\ndescription: {JsonSerializer.Serialize(description.Trim())}\n---\n\n{body}";
            Directory.CreateDirectory(dir);
            string temporary = Path.Combine(dir, $".heal-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Encoding.UTF8.GetBytes(document);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                // No-overwrite is enforced at publication, not just by the earlier existence check.
                File.Move(temporary, skillMd, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            MuxConsole.WriteSuccess($"[heal] saved skill '{name}' -> {skillMd}");
            return true;
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[heal] failed to save skill '{rawName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Lowercase kebab-case, filesystem-safe skill folder name.</summary>
    private static string SanitizeSkillName(string raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '_' or '-' && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private static async Task AppendGroupAsync(
        string path, List<Proposal> items, string header, CancellationToken ct)
    {
        if (items.Count == 0) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append("\r\n").Append(header).Append("\r\n");
            foreach (var p in items)
                sb.Append($"- **{p.Key}**: {p.Content}").Append("\r\n");

            string existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : string.Empty;
            var sep = existing.Length > 0 && !existing.EndsWith("\r\n") ? "\r\n" : string.Empty;
            await File.WriteAllTextAsync(path, existing + sep + sb.ToString(), ct);
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[heal] failed to write {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
