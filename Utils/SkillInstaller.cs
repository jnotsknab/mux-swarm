using System.Net.Http.Json;
using System.Text.Json;

namespace MuxSwarm.Utils;

/// <summary>
/// Backs the /installskill command: installs a skill into the live skills directory
/// (PlatformContext.SkillsDirectory) from a curated GitHub source by name, or from an arbitrary
/// GitHub repo path/URL. Uses the GitHub contents API to pull a skill directory's files (SKILL.md
/// + any siblings). Network-resilient: every failure returns a message, never throws to the caller.
/// </summary>
public static class SkillInstaller
{
    // Curated sources, in priority order, as (owner/repo, dir-path) for the GitHub contents API.
    public static readonly (string Label, string Repo, string Path)[] CuratedSources =
    {
        ("openai/skills (curated)",      "openai/skills",                    "skills/.curated"),
        ("openai/skills (experimental)", "openai/skills",                    "skills/.experimental"),
        ("VoltAgent",                    "VoltAgent/awesome-openclaw-skills", "skills"),
    };

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mux-Swarm-SkillInstaller");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                 ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    private sealed record GhEntry(string name, string type, string? download_url, string path);

    /// <summary>List the installable skill names across curated sources (deduped).</summary>
    public static async Task<List<string>> ListCuratedAsync(CancellationToken ct = default)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var http = NewClient();
        foreach (var (_, repo, path) in CuratedSources)
        {
            try
            {
                var url = $"https://api.github.com/repos/{repo}/contents/{path}";
                var entries = await http.GetFromJsonAsync<List<GhEntry>>(url, ct);
                if (entries is null) continue;
                foreach (var e in entries)
                    if (string.Equals(e.type, "dir", StringComparison.OrdinalIgnoreCase))
                        names.Add(e.name);
            }
            catch { /* source unreachable - skip */ }
        }
        return names.ToList();
    }

    /// <summary>
    /// Install a skill by name from the curated sources (first match wins). Returns a status string.
    /// </summary>
    public static async Task<string> InstallByNameAsync(string name, bool overwrite, CancellationToken ct = default)
    {
        using var http = NewClient();
        foreach (var (label, repo, path) in CuratedSources)
        {
            var dirUrl = $"https://api.github.com/repos/{repo}/contents/{path}/{name}";
            List<GhEntry>? entries;
            try { entries = await http.GetFromJsonAsync<List<GhEntry>>(dirUrl, ct); }
            catch { continue; }
            if (entries is null || entries.Count == 0) continue;

            return await InstallEntriesAsync(http, name, entries, overwrite, $"{label}", ct);
        }
        return $"Skill '{name}' not found in any curated source. Try /installskill (bare) to list, or pass a GitHub URL.";
    }

    /// <summary>
    /// Install from a GitHub URL pointing at a skill directory, e.g.
    /// https://github.com/owner/repo/tree/main/path/to/skill  (or a raw contents API URL).
    /// </summary>
    public static async Task<string> InstallFromUrlAsync(string url, bool overwrite, CancellationToken ct = default)
    {
        // Parse owner/repo + path from a /tree/ URL.
        // https://github.com/<owner>/<repo>/tree/<ref>/<path...>
        try
        {
            var u = new Uri(url);
            var segs = u.AbsolutePath.Trim('/').Split('/');
            if (segs.Length < 5 || !string.Equals(segs[2], "tree", StringComparison.OrdinalIgnoreCase))
                return "URL must look like https://github.com/<owner>/<repo>/tree/<ref>/<path-to-skill>.";
            string repo = $"{segs[0]}/{segs[1]}";
            string skillPath = string.Join('/', segs.Skip(4));
            string name = segs[^1];

            using var http = NewClient();
            var dirUrl = $"https://api.github.com/repos/{repo}/contents/{skillPath}";
            var entries = await http.GetFromJsonAsync<List<GhEntry>>(dirUrl, ct);
            if (entries is null || entries.Count == 0) return $"No files found at {url}.";
            return await InstallEntriesAsync(http, name, entries, overwrite, repo, ct);
        }
        catch (Exception ex) { return $"Failed to install from URL: {ex.Message}"; }
    }

    private static async Task<string> InstallEntriesAsync(
        HttpClient http, string name, List<GhEntry> entries, bool overwrite, string source, CancellationToken ct)
    {
        bool hasSkillMd = entries.Any(e =>
            string.Equals(e.name, "SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (!hasSkillMd)
            return $"'{name}' has no SKILL.md - not a valid skill directory.";

        string destRoot = PlatformContext.SkillsDirectory;
        string destDir = Path.Combine(destRoot, name);
        if (Directory.Exists(destDir) && !overwrite)
            return $"Skill '{name}' is already installed at {destDir}. Re-run with overwrite to replace it.";

        Directory.CreateDirectory(destDir);
        int files = 0;
        foreach (var e in entries)
        {
            if (!string.Equals(e.type, "file", StringComparison.OrdinalIgnoreCase) || e.download_url is null)
                continue; // skip nested dirs (skills are flat SKILL.md + assets)
            try
            {
                var bytes = await http.GetByteArrayAsync(e.download_url, ct);
                await File.WriteAllBytesAsync(Path.Combine(destDir, e.name), bytes, ct);
                files++;
            }
            catch { /* skip a failed file but continue */ }
        }
        if (files == 0) return $"Downloaded no files for '{name}' (source unreachable?).";

        SkillLoader.LoadSkills();
        return $"Installed skill '{name}' ({files} file(s)) from {source} to {destDir}. Skills reloaded.";
    }
}
