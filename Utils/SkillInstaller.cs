using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MuxSwarm.Utils;

/// <summary>
/// Backs the /installskill command: installs an Agent Skill (the SKILL.md-per-directory format
/// standardized at agentskills.io) into the live skills directory (PlatformContext.SkillsDirectory).
///
/// Sources: a curated registry of reputable public GitHub repos (official + community), OR an
/// explicit target -- a full GitHub tree URL, an "owner/repo" (search its skills path), or an
/// "owner/repo/path/to/skill" shorthand. Fetch is RECURSIVE (via the GitHub git-trees API) so a
/// skill's scripts/, references/, and assets/ subtrees come down too -- not just the flat SKILL.md.
///
/// After copy, a non-destructive normalizer aligns the skill to mux conventions: it guarantees the
/// frontmatter `name` matches the install directory (the spec's hard rule) and stamps a provenance
/// block into `metadata` (source repo, upstream URL, license, installedAt). All other frontmatter --
/// including the Claude Code superset fields -- is preserved verbatim.
///
/// Network-resilient: every failure returns a message, never throws to the caller.
/// </summary>
public static class SkillInstaller
{
    /// <summary>A curated skill source: a GitHub repo + the repo-relative path under which skill
    /// directories live. Ordered by trust (official first) -- first match wins in a name search.</summary>
    public sealed record SkillSource(string Label, string Repo, string Path, string Branch, string Tier);

    // Curated registry. Verified public repos that host SKILL.md-format skills. Kept deliberately
    // small + high-signal; a user can always point /installskill at any other owner/repo or URL.
    //
    // Branch here is the KNOWN default at time of writing but is NOT trusted blindly -- the installer
    // resolves each repo's live default_branch before fetching (ComposioHQ is on `master`, not `main`).
    // Paths are the VERIFIED skills roots (empty string = skills live at the repo root, dir-per-skill).
    public static readonly SkillSource[] CuratedSources =
    {
        new("Anthropic (official)",   "anthropics/skills",               "skills",                         "main",   "official"),
        new("obra/superpowers",       "obra/superpowers",                "skills",                         "main",   "reputable"),
        new("dotnet (Microsoft)",     "dotnet/skills",                   "plugins",                        "main",   "official"),
        new("Vercel Labs (agent)",    "vercel-labs/agent-skills",        "skills",                         "main",   "reputable"),
        new("Vercel Labs (cli)",      "vercel-labs/skills",              "skills",                         "main",   "reputable"),
        new("tech-leads-club",        "tech-leads-club/agent-skills",    "packages/skills-catalog/skills", "main",   "reputable"),
        new("Composio",               "ComposioHQ/awesome-claude-skills","",                               "master", "community"),
        new("OpenAI (curated)",       "openai/skills",                   "skills/.curated",                "main",   "official"),
        new("OpenAI (experimental)",  "openai/skills",                   "skills/.experimental",           "main",   "experimental"),
    };

    // Safety caps: a well-formed skill is a handful of small text files. These bound a hostile or
    // accidentally-huge skill dir so an install can't exhaust disk or hang.
    private const int MaxFiles = 60;
    private const long MaxTotalBytes = 8L * 1024 * 1024;   // 8 MB total
    private const long MaxSingleFileBytes = 4L * 1024 * 1024;

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mux-Swarm-SkillInstaller");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                 ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    // ---- GitHub DTOs ----
    private sealed record GhContentEntry(string name, string type, string? download_url, string path, string? git_url, string? sha);
    private sealed record GhTreeEntry(string path, string type, long size, string? sha);
    private sealed record GhTree(string sha, List<GhTreeEntry> tree, bool truncated);
    private sealed record GhRepo(string default_branch);

    /// <summary>
    /// Enumerate every skill path in a repo by walking its recursive git-tree and taking the parent
    /// dir of every <c>*/SKILL.md</c> blob under <paramref name="skillsPath"/> (empty = whole repo).
    /// Layout-agnostic: handles flat (anthropics), root-level (Composio), and deep nesting
    /// (dotnet plugins, tech-leads-club categories). Returns (skillName -> repo-relative dir path).
    /// </summary>
    private static async Task<Dictionary<string, string>> EnumerateSkillPathsAsync(
        HttpClient http, string repo, string branch, string skillsPath, CancellationToken ct)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var tree = await http.GetFromJsonAsync<GhTree>(
                $"https://api.github.com/repos/{repo}/git/trees/{branch}?recursive=1", ct);
            if (tree?.tree is null) return found;
            var prefix = string.IsNullOrEmpty(skillsPath) ? "" : skillsPath.TrimEnd('/') + "/";
            foreach (var t in tree.tree)
            {
                if (!string.Equals(t.type, "blob", StringComparison.OrdinalIgnoreCase)) continue;
                if (!t.path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase)
                    && !t.path.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
                if (prefix.Length > 0 && !t.path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var dir = t.path.Contains('/') ? t.path[..t.path.LastIndexOf('/')] : "";
                var leaf = dir.Length == 0 ? repo.Split('/')[^1] : dir[(dir.LastIndexOf('/') + 1)..];
                if (leaf.StartsWith(".")) continue; // skip dot-tier system dirs unless explicitly targeted
                found.TryAdd(leaf, dir);
            }
        }
        catch { /* source unreachable - skip */ }
        return found;
    }

    /// <summary>List the installable skill names across the curated sources (deduped, sorted).</summary>
    public static async Task<List<string>> ListCuratedAsync(CancellationToken ct = default)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var http = NewClient();
        foreach (var src in CuratedSources)
        {
            var branch = await ResolveBranchAsync(http, src.Repo, ct) ?? src.Branch;
            var skills = await EnumerateSkillPathsAsync(http, src.Repo, branch, src.Path, ct);
            foreach (var k in skills.Keys) names.Add(k);
        }
        return names.ToList();
    }

    /// <summary>List curated sources with their tier, for display.</summary>
    public static IEnumerable<string> SourceLabels() =>
        CuratedSources.Select(s => $"{s.Label} ({s.Repo}, {s.Tier})");

    /// <summary>
    /// Install a skill by name from the curated sources (first trusted match wins). Returns a status.
    /// </summary>
    public static async Task<string> InstallByNameAsync(string name, bool overwrite, CancellationToken ct = default)
    {
        using var http = NewClient();
        foreach (var src in CuratedSources)
        {
            var branch = await ResolveBranchAsync(http, src.Repo, ct) ?? src.Branch;
            var skills = await EnumerateSkillPathsAsync(http, src.Repo, branch, src.Path, ct);
            if (!skills.TryGetValue(name, out var dirPath)) continue;

            var upstream = $"https://github.com/{src.Repo}/tree/{branch}/{dirPath}".TrimEnd('/');
            return await FetchAndInstallAsync(http, src.Repo, branch, dirPath, name,
                overwrite, src.Label, upstream, ct);
        }
        return $"Skill '{name}' not found in any curated source. Try /installskill (bare) to list, " +
               $"or pass owner/repo, owner/repo/path/to/skill, or a GitHub tree URL.";
    }

    /// <summary>
    /// Install from an explicit target: a full GitHub tree URL, an "owner/repo" (searches its skills
    /// path for a single skill or lists if ambiguous), or "owner/repo/path/to/skill".
    /// </summary>
    public static async Task<string> InstallFromTargetAsync(string target, bool overwrite, CancellationToken ct = default)
    {
        try
        {
            string repo, path, name;
            string? branch = null;

            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // https://github.com/<owner>/<repo>/tree/<ref>/<path...>
                var u = new Uri(target);
                var segs = u.AbsolutePath.Trim('/').Split('/');
                if (segs.Length < 5 || !string.Equals(segs[2], "tree", StringComparison.OrdinalIgnoreCase))
                    return "URL must look like https://github.com/<owner>/<repo>/tree/<ref>/<path-to-skill>.";
                repo = $"{segs[0]}/{segs[1]}";
                branch = segs[3];
                path = string.Join('/', segs.Skip(4));
                name = segs[^1];
            }
            else
            {
                var segs = target.Trim('/').Split('/');
                if (segs.Length < 2)
                    return "Target must be a skill name, owner/repo, owner/repo/path/to/skill, or a GitHub tree URL.";
                repo = $"{segs[0]}/{segs[1]}";
                using var http0 = NewClient();
                branch = await ResolveBranchAsync(http0, repo, ct);

                if (segs.Length == 2)
                    return await InstallSingleFromRepoRootAsync(http0, repo, branch!, overwrite, ct);

                path = string.Join('/', segs.Skip(2));
                name = segs[^1];
            }

            using var http = NewClient();
            branch ??= await ResolveBranchAsync(http, repo, ct) ?? "main";
            var upstream = $"https://github.com/{repo}/tree/{branch}/{path}";
            return await FetchAndInstallAsync(http, repo, branch, path, name, overwrite, repo, upstream, ct);
        }
        catch (Exception ex) { return $"Failed to install from '{target}': {ex.Message}"; }
    }

    /// <summary>Back-compat shim for the old URL-only entry point.</summary>
    public static Task<string> InstallFromUrlAsync(string url, bool overwrite, CancellationToken ct = default)
        => InstallFromTargetAsync(url, overwrite, ct);

    // When given only owner/repo, look for skill dirs under common skills paths and install if there's
    // exactly one; otherwise report the choices.
    private static async Task<string> InstallSingleFromRepoRootAsync(
        HttpClient http, string repo, string branch, bool overwrite, CancellationToken ct)
    {
        foreach (var candidate in new[] { "skills", "packages/skills-catalog/skills", "." })
        {
            var url = $"https://api.github.com/repos/{repo}/contents/{candidate}?ref={branch}";
            List<GhContentEntry>? entries;
            try { entries = await http.GetFromJsonAsync<List<GhContentEntry>>(url, ct); }
            catch { continue; }
            if (entries is null) continue;

            var dirs = entries.Where(e => string.Equals(e.type, "dir", StringComparison.OrdinalIgnoreCase)).ToList();
            // A repo that IS a single skill (SKILL.md at the probed root).
            if (entries.Any(e => string.Equals(e.name, "SKILL.md", StringComparison.OrdinalIgnoreCase)))
            {
                var name = repo.Split('/')[^1];
                var basePath = candidate == "." ? "" : candidate;
                var upstream = $"https://github.com/{repo}/tree/{branch}/{basePath}".TrimEnd('/');
                return await FetchAndInstallAsync(http, repo, branch, basePath, name, overwrite, repo, upstream, ct);
            }
            if (dirs.Count == 1)
            {
                var d = dirs[0];
                var upstream = $"https://github.com/{repo}/tree/{branch}/{d.path}";
                return await FetchAndInstallAsync(http, repo, branch, d.path, d.name, overwrite, repo, upstream, ct);
            }
            if (dirs.Count > 1)
                return $"{repo} has {dirs.Count} skills under '{candidate}'. Install one with " +
                       $"/installskill {repo}/{candidate}/<name>. Available: " +
                       string.Join(", ", dirs.Take(30).Select(d => d.name));
        }
        return $"Could not find any SKILL.md skills in {repo}. Pass an explicit owner/repo/path/to/skill.";
    }

    private static async Task<string?> ResolveBranchAsync(HttpClient http, string repo, CancellationToken ct)
    {
        try
        {
            var r = await http.GetFromJsonAsync<GhRepo>($"https://api.github.com/repos/{repo}", ct);
            return r?.default_branch ?? "main";
        }
        catch { return "main"; }
    }

    /// <summary>
    /// Recursively fetch every file under a skill directory (via the repo's recursive git-tree,
    /// filtered to the skill's path prefix) and write it into the skills dir, then run the mux
    /// normalizer. Enforces the file-count/size caps and skips oversized blobs.
    /// </summary>
    private static async Task<string> FetchAndInstallAsync(
        HttpClient http, string repo, string branch, string skillPath, string name,
        bool overwrite, string source, string upstreamUrl, CancellationToken ct)
    {
        GhTree? tree;
        try
        {
            tree = await http.GetFromJsonAsync<GhTree>(
                $"https://api.github.com/repos/{repo}/git/trees/{branch}?recursive=1", ct);
        }
        catch (Exception ex) { return $"Could not read {repo} tree: {ex.Message}"; }
        if (tree?.tree is null) return $"No git tree for {repo}.";

        var prefix = string.IsNullOrEmpty(skillPath) ? "" : skillPath.TrimEnd('/') + "/";
        // Collect this skill's blobs as (repo-relative path, size) and re-root them under the skill dir.
        var files = new List<(string RelPath, long Size)>();
        bool hasSkillMd = false;
        foreach (var t in tree.tree)
        {
            if (!string.Equals(t.type, "blob", StringComparison.OrdinalIgnoreCase)) continue;
            if (prefix.Length > 0 && !t.path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = prefix.Length > 0 ? t.path[prefix.Length..] : t.path;
            // Only files directly under this skill dir (rel has no leading path back up). For a root
            // layout (prefix == "") take everything; the skill IS the repo root.
            files.Add((rel, t.size));
            if (string.Equals(rel, "SKILL.md", StringComparison.OrdinalIgnoreCase)) hasSkillMd = true;
        }

        if (!hasSkillMd)
            return $"'{name}' has no SKILL.md at {repo}/{skillPath} - not a valid skill directory.";

        var destRoot = PlatformContext.SkillsDirectory;
        var destDir = Path.Combine(destRoot, name);
        if (Directory.Exists(destDir) && !overwrite)
            return $"Skill '{name}' is already installed at {destDir}. Re-run with overwrite to replace it.";

        // Enforce caps.
        if (files.Count > MaxFiles)
            return $"'{name}' has {files.Count} files (cap {MaxFiles}); refusing to install. Report the source if this is legitimate.";
        long total = files.Sum(f => f.Size);
        if (total > MaxTotalBytes)
            return $"'{name}' totals {total / (1024 * 1024)} MB (cap {MaxTotalBytes / (1024 * 1024)} MB); refusing to install.";

        Directory.CreateDirectory(destDir);
        int written = 0;
        foreach (var (relPath, size) in files)
        {
            ct.ThrowIfCancellationRequested();
            if (size > MaxSingleFileBytes) continue; // skip an oversized blob, keep the rest
            var fetchPath = prefix.Length > 0 ? prefix + relPath : relPath;
            var rawUrl = $"https://raw.githubusercontent.com/{repo}/{branch}/{fetchPath}";
            try
            {
                var bytes = await http.GetByteArrayAsync(rawUrl, ct);
                var outPath = Path.Combine(destDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                await File.WriteAllBytesAsync(outPath, bytes, ct);
                written++;
            }
            catch { /* skip a failed file but continue */ }
        }
        if (written == 0) return $"Downloaded no files for '{name}' (source unreachable?).";

        // Align to mux conventions (non-destructive).
        var normNote = NormalizeInstalledSkill(destDir, name, repo, upstreamUrl, source);

        SkillLoader.LoadSkills();
        return $"Installed skill '{name}' ({written} file(s)) from {source} to {destDir}. {normNote} Skills reloaded.";
    }

    // ---- Normalizer: align an installed skill to mux conventions (non-destructive) ----

    /// <summary>
    /// Rewrite the installed SKILL.md so (1) the frontmatter `name` equals the install dir (the spec's
    /// hard rule), and (2) a `metadata` provenance block records where it came from. Every other line
    /// of frontmatter and the entire body are preserved byte-for-byte. Returns a short summary.
    /// </summary>
    internal static string NormalizeInstalledSkill(string destDir, string dirName, string repo, string upstreamUrl, string source)
    {
        var skillMd = Path.Combine(destDir, "SKILL.md");
        if (!File.Exists(skillMd)) return "(no SKILL.md to normalize).";
        try
        {
            var text = File.ReadAllText(skillMd);
            var (fm, body, hadFm) = SplitFrontmatter(text);
            var changes = new List<string>();

            // Detect license from a bundled LICENSE* file if the frontmatter lacks one.
            string? license = GetFrontmatterScalar(fm, "license");
            if (string.IsNullOrEmpty(license))
            {
                var lic = Directory.EnumerateFiles(destDir)
                    .FirstOrDefault(f => Path.GetFileName(f).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase));
                if (lic != null) license = "See " + Path.GetFileName(lic);
            }

            // (1) name == dir name.
            var curName = GetFrontmatterScalar(fm, "name");
            if (!string.Equals(curName, dirName, StringComparison.Ordinal))
            {
                fm = SetFrontmatterScalar(fm, "name", dirName);
                changes.Add(curName is null ? "added name" : "fixed name");
            }

            // (2) provenance under metadata (mux ignores unknown keys; safe additive).
            var stamp = new (string Key, string Val)[]
            {
                ("mux_source", source),
                ("mux_source_repo", repo),
                ("mux_source_url", upstreamUrl),
                ("mux_installed_at", DateTime.UtcNow.ToString("o")),
            }.Concat(string.IsNullOrEmpty(license) ? Array.Empty<(string,string)>() : new[] { ("mux_license", license!) });

            fm = UpsertMetadataBlock(fm, stamp);
            changes.Add("stamped provenance");

            var rebuilt = "---\n" + fm.TrimEnd('\n') + "\n---\n" + (hadFm ? body : "\n" + text);
            if (!hadFm) rebuilt = "---\n" + fm.TrimEnd('\n') + "\n---\n\n" + text;
            File.WriteAllText(skillMd, rebuilt);
            return "Normalized: " + string.Join(", ", changes) + ".";
        }
        catch (Exception ex) { return $"(normalize skipped: {ex.Message})"; }
    }

    private static (string Frontmatter, string Body, bool HadFrontmatter) SplitFrontmatter(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n")) return ("", normalized, false);
        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return ("", normalized, false);
        var fm = normalized[4..(end + 1)];
        var body = normalized[(end + 4)..];
        return (fm, body, true);
    }

    private static string? GetFrontmatterScalar(string fm, string key)
    {
        foreach (var line in fm.Split('\n'))
        {
            var t = line.TrimEnd();
            if (t.StartsWith(key + ":", StringComparison.Ordinal))
                return t[(key.Length + 1)..].Trim();
        }
        return null;
    }

    private static string SetFrontmatterScalar(string fm, string key, string value)
    {
        var lines = fm.Split('\n').ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd().StartsWith(key + ":", StringComparison.Ordinal) && !lines[i].StartsWith(" "))
            {
                lines[i] = $"{key}: {value}";
                return string.Join('\n', lines);
            }
        }
        lines.Insert(0, $"{key}: {value}");
        return string.Join('\n', lines);
    }

    // Append mux_* keys under a `metadata:` mapping. If metadata exists, add missing keys beneath it;
    // otherwise append a fresh metadata block. Existing user/author/version keys are left untouched.
    private static string UpsertMetadataBlock(string fm, IEnumerable<(string Key, string Val)> kvs)
    {
        var lines = fm.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        int metaIdx = lines.FindIndex(l => l.TrimEnd() == "metadata:" || l.TrimStart().StartsWith("metadata:"));
        var pairs = kvs.ToList();

        if (metaIdx < 0)
        {
            lines.Add("metadata:");
            foreach (var (k, v) in pairs) lines.Add($"  {k}: {Quote(v)}");
            return string.Join('\n', lines) + "\n";
        }

        // Find the extent of the metadata block (indented lines following it).
        int insertAt = metaIdx + 1;
        while (insertAt < lines.Count && (lines[insertAt].StartsWith(" ") || lines[insertAt].StartsWith("\t")))
            insertAt++;
        // Only add keys not already present in the block.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = metaIdx + 1; i < insertAt; i++)
        {
            var t = lines[i].Trim();
            var c = t.IndexOf(':');
            if (c > 0) existing.Add(t[..c].Trim());
        }
        var toAdd = pairs.Where(p => !existing.Contains(p.Key))
                         .Select(p => $"  {p.Key}: {Quote(p.Val)}").ToList();
        lines.InsertRange(insertAt, toAdd);
        return string.Join('\n', lines) + "\n";
    }

    private static string Quote(string v) =>
        v.IndexOfAny(new[] { ':', '#', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\\\"") + "\"" : v;
}
