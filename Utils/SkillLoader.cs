namespace MuxSwarm.Utils;
using static Setup.Setup;

/// <summary>
/// Represents a single skill's metadata parsed from SKILL.md frontmatter.
/// </summary>
public class SkillManifestEntry
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Location { get; set; } = "";
    
    /// <summary>Optional: only load this skill for specific agent names.</summary>
    public List<string>? Agents { get; set; }
    
    /// <summary>Optional: required environment variables that must be set.</summary>
    public List<string>? RequiresEnv { get; set; }
    
    /// <summary>Optional: required binaries that must exist on PATH.</summary>
    public List<string>? RequiresBins { get; set; }
}

/// <summary>
/// Loads, filters, and serves skills from disk following the OpenClaw/Anthropic
/// AgentSkills convention. Each skill is a directory containing a SKILL.md file
/// with YAML-like frontmatter and markdown instructions.
/// 
/// Directory precedence (highest to lowest):
///   1. Per-agent skills:  {baseDir}/skills/agents/{agentName}/
///   2. Shared skills:     {baseDir}/skills/shared/
///   3. Bundled skills:    {baseDir}/skills/bundled/
///   4. Extra dirs from config
/// </summary>
public static class SkillLoader
{
    private static List<SkillManifestEntry> _allSkills = new();
    private static bool _loaded = false;

    /// <summary>
    /// Scan all skill directories and build the manifest.
    /// Call once at startup.
    /// </summary>
    /// TODO: Add boolean arg for docker use, some users may have docker installed but still dont want to use docker skills variant
    public static void LoadSkills(List<string>? extraDirs = null)
    {
        _allSkills.Clear();
        _loaded = true;
    
        var skillsRoot = Path.GetDirectoryName(PlatformContext.SkillsDirectory);
        if (skillsRoot == null || !Directory.Exists(skillsRoot))
        {
            MuxConsole.WriteWarning("[SKILLS] No skills directory found — skipping");
            return;
        }
        
        var bundledDir = Path.Combine(skillsRoot, "bundled");
        var bundledDockerDir = Path.Combine(skillsRoot, "bundled-docker");

        var dockerAvailable = Directory.Exists(bundledDockerDir) && IsBinaryAvailable("docker");
        
        var selectedBundledDir = dockerAvailable ? bundledDockerDir : bundledDir;

        if (App.Config.IsUsingDockerForExec)
            MuxConsole.WriteInfo("[SKILLS] Docker detected — using bundled-docker skills");
        else
            MuxConsole.WriteInfo("[SKILLS] Docker not detected — using bundled skills");

        var skillDirs = new List<string>
        {
            selectedBundledDir,
            Path.Combine(skillsRoot, "shared"),
        };

        // Add per-agent skill directories (we'll scan all, filter later)
        var agentsSkillRoot = Path.Combine(skillsRoot, "agents");
        if (Directory.Exists(agentsSkillRoot))
        {
            foreach (var agentDir in Directory.GetDirectories(agentsSkillRoot))
                skillDirs.Add(agentDir);
        }

        if (extraDirs != null)
        {
            // Extra dirs get lowest precedence — add them first so they get overridden
            skillDirs.InsertRange(0, extraDirs);
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Process in reverse so higher-precedence dirs override lower ones
        for (int i = skillDirs.Count - 1; i >= 0; i--)
        {
            var dir = skillDirs[i];
            if (!Directory.Exists(dir)) continue;

            foreach (var skillDir in Directory.GetDirectories(dir))
            {
                var skillMdPath = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillMdPath)) continue;

                var entry = ParseSkillManifest(skillMdPath, skillDir);
                if (entry == null) continue;

                // Check environment requirements
                if (!CheckRequirements(entry)) continue;

                // Higher precedence wins
                if (seenNames.Contains(entry.Name))
                    continue;

                seenNames.Add(entry.Name);
                _allSkills.Add(entry);
            }
        }

        MuxConsole.WriteSuccess($"[SKILLS] Loaded {_allSkills.Count} skills from {skillDirs.Count(Directory.Exists)} directories");
    }

    /// <summary>
    /// Get skill metadata entries for dynamic discovery (e.g. list_skills tool).
    /// Filters by agent name if skills have agent restrictions.
    /// </summary>
    public static List<SkillManifestEntry> GetSkillMetadata(string? agentName = null)
    {
        return _allSkills
            .Where(s =>
                s.Agents == null ||
                s.Agents.Count == 0 ||
                (agentName != null && s.Agents.Any(a =>
                    a.Equals(agentName, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }
    
    /// <summary>
    /// Read the full content of a SKILL.md file by skill name.
    /// Returns the full markdown content (minus frontmatter) for injection into context.
    /// </summary>
    public static string? ReadSkill(string skillName)
    {
        var skill = _allSkills.FirstOrDefault(s =>
            s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skill == null)
            return null;

        var skillMdPath = Path.Combine(skill.Location, "SKILL.md");
        if (!File.Exists(skillMdPath))
            return null;

        var content = File.ReadAllText(skillMdPath);
        content = TokenInjector.InjectTokens(content);

        // Strip frontmatter if present (--- ... ---)
        if (content.StartsWith("---"))
        {
            var endIndex = content.IndexOf("---", 3);
            if (endIndex > 0)
            {
                content = content[(endIndex + 3)..].TrimStart('\r', '\n');
            }
        }

        return content;
    }

    private static SkillManifestEntry? ParseSkillManifest(string skillMdPath, string skillDir)
    {
        try
        {
            var lines = File.ReadAllLines(skillMdPath);
            var entry = new SkillManifestEntry
            {
                Location = skillDir,
                Name = Path.GetFileName(skillDir) // default name = folder name
            };

            // Parse simple YAML-like frontmatter between --- markers
            if (lines.Length > 0 && lines[0].Trim() == "---")
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line == "---") break;

                    var colonIdx = line.IndexOf(':');
                    if (colonIdx <= 0) continue;

                    var key = line[..colonIdx].Trim().ToLower();
                    var value = line[(colonIdx + 1)..].Trim();

                    switch (key)
                    {
                        case "name":
                            entry.Name = value;
                            break;
                        case "description":
                            entry.Description = value;
                            break;
                        case "agents":
                            entry.Agents = ParseListValue(value);
                            break;
                        case "requires_env":
                            entry.RequiresEnv = ParseListValue(value);
                            break;
                        case "requires_bins":
                            entry.RequiresBins = ParseListValue(value);
                            break;
                    }
                }
            }

            // If no description from frontmatter, try to extract from first paragraph
            if (string.IsNullOrEmpty(entry.Description))
            {
                var bodyStart = false;
                foreach (var line in lines)
                {
                    if (line.Trim() == "---")
                    {
                        if (bodyStart) break;
                        bodyStart = true;
                        continue;
                    }
                    if (bodyStart && !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                    {
                        entry.Description = line.Trim();
                        if (entry.Description.Length > 120)
                            entry.Description = entry.Description[..120] + "...";
                        break;
                    }
                }
            }

            return entry;
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"[SKILLS] Failed to parse {skillMdPath}: {ex.Message}");
            return null;
        }
    }

    private static List<string>? ParseListValue(string value)
    {
        // Handles both: "agents: [WebAgent, CodeAgent]" and "agents: WebAgent, CodeAgent"
        value = value.Trim('[', ']', ' ');
        if (string.IsNullOrEmpty(value)) return null;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
    }

    private static bool CheckRequirements(SkillManifestEntry entry)
    {
        if (entry.RequiresEnv != null)
        {
            foreach (var envVar in entry.RequiresEnv)
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
                {
                    MuxConsole.WriteMuted($"[SKILLS] Skipping '{entry.Name}': missing env var {envVar}");
                    return false;
                }
            }
        }

        if (entry.RequiresBins != null)
        {
            foreach (var bin in entry.RequiresBins)
            {
                if (!IsBinaryAvailable(bin))
                {
                    MuxConsole.WriteMuted($"[SKILLS] Skipping '{entry.Name}': binary not found: {bin}");
                    return false;
                }
            }
        }

        return true;
    }
    
}