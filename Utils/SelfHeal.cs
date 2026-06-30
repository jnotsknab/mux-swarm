using System.Text;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Drives /heal and /reflect: a self-examination pass over the CURRENT session (normal mode)
/// using the ACTIVE session model. It reviews recent turns for repeated errors, missed memory
/// write-backs, useful reflexes, and anti-patterns, then proposes concise BRAIN.md / MEMORY.md
/// stub entries for the user to approve (MultiSelect) before anything is written.
///
/// Deep mode is a heavier variant the caller may route to a swarm; this helper exposes the
/// single-pass analysis + the apply step, which deep mode reuses after its own consolidation.
/// </summary>
public static class SelfHeal
{
    /// <summary>A proposed memory write-back. Type is "BRAIN" or "MEMORY".</summary>
    public readonly record struct Proposal(string Type, string Key, string Content)
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
        system.AppendLine();
        system.AppendLine("Output ONE proposal per line, EXACTLY in this pipe format, nothing else:");
        system.AppendLine("  BRAIN|<short key>|<concise one-line content>");
        system.AppendLine("  MEMORY|<short key>|<concise one-line content>");
        system.AppendLine("  SKILL|<skill-name>|<one-line what-it-does>   (ONLY if a reusable");
        system.AppendLine("        procedure emerged worth codifying as a standing skill)");
        system.AppendLine();
        system.AppendLine("Keep each proposal a single line. Propose only HIGH-VALUE, durable items");
        system.AppendLine("(skip transient noise, secrets, and anything already obvious). If there is");
        system.AppendLine("nothing worth persisting, output nothing.");

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

    /// <summary>Parse the pipe-delimited proposal lines from the model output.</summary>
    public static List<Proposal> ParseProposals(string text)
    {
        var results = new List<Proposal>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split('|');
            if (parts.Length < 3) continue;

            var type = parts[0].Trim().ToUpperInvariant();
            if (type != "BRAIN" && type != "MEMORY" && type != "SKILL") continue;

            var key = parts[1].Trim();
            var content = string.Join("|", parts.Skip(2)).Trim();
            if (key.Length == 0 || content.Length == 0) continue;

            results.Add(new Proposal(type, key, content));
        }
        return results;
    }

    /// <summary>
    /// Append the accepted proposals to BRAIN.md / MEMORY.md as concise stub entries under a dated
    /// heading. Best-effort: never throws. Respects the configured char-cap afterward.
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

        // SKILL proposals scaffold a new SKILL.md under the skills dir and hot-reload the manifest
        // (same path /installskill uses), so a reusable procedure becomes a standing skill without
        // a separate curator step.
        var skillProps = accepted.Where(p => p.Type == "SKILL").ToList();
        if (skillProps.Count > 0)
        {
            bool any = false;
            foreach (var p in skillProps)
                any |= ScaffoldSkill(p.Key, p.Content);
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

    /// <summary>
    /// Scaffold a new skill directory (<c>{SkillsDirectory}/{name}/SKILL.md</c>) following the
    /// AgentSkills frontmatter convention. The proposal content seeds the description + a body stub.
    /// Best-effort: returns false (and warns) on failure; never overwrites an existing skill.
    /// </summary>
    private static bool ScaffoldSkill(string rawName, string description)
    {
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
            Directory.CreateDirectory(dir);

            var desc = description.Replace("\r", " ").Replace("\n", " ").Trim();
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"name: {name}\n");
            sb.Append($"description: {desc}\n");
            sb.Append("---\n\n");
            sb.Append($"# {name}\n\n");
            sb.Append($"{desc}\n\n");
            sb.Append("## Steps\n\n");
            sb.Append("<!-- Seeded by /heal SelfHeal. Flesh out the reusable procedure here. -->\n");

            File.WriteAllText(skillMd, sb.ToString());
            MuxConsole.WriteSuccess($"[heal] scaffolded skill '{name}' -> {skillMd}");
            return true;
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[heal] failed to scaffold skill '{rawName}': {ex.Message}");
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
