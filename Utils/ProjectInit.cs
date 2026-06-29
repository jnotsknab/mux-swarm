using System.Text;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Drives /init: scans the workspace root (top-level entries + build/manifest files + README + git
/// remote) and has the ACTIVE model author a concise AGENTS.md describing the project, stack, key
/// dirs, and build/test commands. The caller owns the file write + overwrite confirm.
/// </summary>
public static class ProjectInit
{
    private static readonly string[] ManifestFiles =
    {
        "package.json", "pyproject.toml", "requirements.txt", "setup.py", "go.mod",
        "Cargo.toml", "pom.xml", "build.gradle", "Gemfile", "composer.json", "CMakeLists.txt",
        "Makefile", "Dockerfile", "docker-compose.yml", "tsconfig.json", ".csproj", ".sln",
    };

    /// <summary>Build a compact textual snapshot of the workspace for the model to summarize.</summary>
    public static string ScanWorkspace(string root)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"workspaceRoot: {root}");
        sb.AppendLine();

        sb.AppendLine("## Top-level entries");
        try
        {
            var dirs = Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith('.'))
                .OrderBy(n => n).Take(60).ToList();
            var files = Directory.GetFiles(root)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n).Take(80).ToList();
            sb.AppendLine("dirs: " + (dirs.Count > 0 ? string.Join(", ", dirs) : "(none)"));
            sb.AppendLine("files: " + (files.Count > 0 ? string.Join(", ", files) : "(none)"));
        }
        catch (Exception ex) { sb.AppendLine($"(scan error: {ex.Message})"); }
        sb.AppendLine();

        sb.AppendLine("## Detected manifests");
        try
        {
            var found = new List<string>();
            foreach (var f in Directory.GetFiles(root))
            {
                var name = Path.GetFileName(f);
                if (ManifestFiles.Any(m => name.EndsWith(m, StringComparison.OrdinalIgnoreCase)))
                    found.Add(name);
            }
            sb.AppendLine(found.Count > 0 ? string.Join(", ", found.Distinct()) : "(none detected)");
        }
        catch { sb.AppendLine("(none)"); }
        sb.AppendLine();

        // README excerpt
        try
        {
            var readme = Directory.GetFiles(root)
                .FirstOrDefault(f => Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase));
            if (readme is not null)
            {
                var text = File.ReadAllText(readme);
                sb.AppendLine("## README excerpt");
                sb.AppendLine(text.Length > 1500 ? text[..1500] + "..." : text);
                sb.AppendLine();
            }
        }
        catch { /* ignore */ }

        // git remote
        try
        {
            var gitConfig = Path.Combine(root, ".git", "config");
            if (File.Exists(gitConfig))
            {
                var url = File.ReadAllLines(gitConfig)
                    .FirstOrDefault(l => l.TrimStart().StartsWith("url =", StringComparison.OrdinalIgnoreCase));
                if (url is not null) sb.AppendLine("gitRemote: " + url.Split('=', 2)[^1].Trim());
            }
        }
        catch { /* ignore */ }

        return sb.ToString();
    }

    public static async Task<string?> GenerateAsync(
        string root,
        IChatClient client,
        ChatOptions? chatOptions,
        CancellationToken ct)
    {
        var snapshot = ScanWorkspace(root);

        var system = new StringBuilder();
        system.AppendLine("You are writing an AGENTS.md - a concise onboarding file that an AI coding agent");
        system.AppendLine("reads to work effectively in this project. Using ONLY the workspace scan below,");
        system.AppendLine("produce a SHORT, factual AGENTS.md with these ## sections (omit any you cannot");
        system.AppendLine("ground in the scan):");
        system.AppendLine("  ## Project - one-paragraph what-it-is");
        system.AppendLine("  ## Stack - languages, frameworks, key tools (from the manifests)");
        system.AppendLine("  ## Layout - the important top-level dirs and what they hold");
        system.AppendLine("  ## Build & Test - the concrete commands to build, run, and test");
        system.AppendLine("  ## Conventions - anything notable (only if evidenced)");
        system.AppendLine();
        system.AppendLine("Do NOT invent commands, frameworks, or dirs not implied by the scan. Prefer the");
        system.AppendLine("canonical command for the detected build system. Output ONLY the markdown, no preamble.");

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, system.ToString()),
                new(ChatRole.User, snapshot)
            };
            var response = await client.GetResponseAsync(messages, chatOptions, ct);
            var text = response?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }
}
