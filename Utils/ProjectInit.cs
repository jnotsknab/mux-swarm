using System.Text;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Drives /init: scans the workspace (bounded recursive walk) and has the ACTIVE model author a
/// concise AGENTS.md describing the project, stack, layout, build/test commands, and - crucially -
/// a path map of the RELEVANT files an agent should read for context (markdown docs, existing
/// agent-instruction files like CLAUDE.md/.cursorrules, CI workflows, key manifests) plus any
/// standing instructions found in those files. The generated file is a MAP, not injected context:
/// the agent reads it (and follows its pointers) on demand. The caller owns the file write +
/// overwrite confirm.
/// </summary>
public static class ProjectInit
{
    private static readonly string[] ManifestFiles =
    {
        "package.json", "pyproject.toml", "requirements.txt", "setup.py", "go.mod",
        "Cargo.toml", "pom.xml", "build.gradle", "Gemfile", "composer.json", "CMakeLists.txt",
        "Makefile", "Dockerfile", "docker-compose.yml", "tsconfig.json", ".csproj", ".sln",
    };

    /// <summary>
    /// Known agent-instruction / editor-rule files whose CONTENT carries standing directives an
    /// agent must honor. Matched by exact name (case-insensitive) at any depth within the walk.
    /// </summary>
    private static readonly string[] InstructionFileNames =
    {
        "AGENTS.md", "CLAUDE.md", "GEMINI.md", ".cursorrules", ".windsurfrules", ".clinerules",
        "copilot-instructions.md", "CONVENTIONS.md", "CONTRIBUTING.md",
    };

    /// <summary>Directories never worth walking (deps, build output, VCS internals, caches).</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "out", "target", ".venv", "venv",
        "__pycache__", ".idea", ".vs", ".vscode", "packages", ".next", ".nuxt", "vendor",
        ".terraform", ".tox", ".mypy_cache", ".pytest_cache", "coverage", ".gradle",
    };

    private const int MaxWalkDepth = 4;
    private const int MaxMarkdownPaths = 80;
    private const int MaxManifestPaths = 40;
    private const int MaxInstructionExcerpts = 6;
    private const int InstructionExcerptChars = 2000;
    private const int ManifestExcerptChars = 1200;

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

        // Bounded recursive walk: collect markdown docs, instruction files, and manifests WITH
        // their relative paths, so the generated AGENTS.md can point the agent at the right files.
        var markdown = new List<string>();
        var instructionFiles = new List<string>();
        var manifests = new List<string>();
        var ciWorkflows = new List<string>();
        Walk(root, root, 0, markdown, instructionFiles, manifests, ciWorkflows);

        sb.AppendLine("## Agent-instruction files found (paths relative to root; content excerpts below)");
        sb.AppendLine(instructionFiles.Count > 0 ? string.Join("\n", instructionFiles.Take(20)) : "(none found)");
        sb.AppendLine();

        sb.AppendLine("## Markdown docs found (paths relative to root)");
        sb.AppendLine(markdown.Count > 0 ? string.Join("\n", markdown.Take(MaxMarkdownPaths)) : "(none found)");
        if (markdown.Count > MaxMarkdownPaths) sb.AppendLine($"(+{markdown.Count - MaxMarkdownPaths} more)");
        sb.AppendLine();

        sb.AppendLine("## Manifests / build files found (paths relative to root)");
        sb.AppendLine(manifests.Count > 0 ? string.Join("\n", manifests.Take(MaxManifestPaths)) : "(none detected)");
        sb.AppendLine();

        if (ciWorkflows.Count > 0)
        {
            sb.AppendLine("## CI workflows");
            sb.AppendLine(string.Join("\n", ciWorkflows.Take(15)));
            sb.AppendLine();
        }

        // Excerpts of existing instruction files: their directives must be surfaced (and folded
        // into the generated Instructions section) rather than silently shadowed.
        int excerpted = 0;
        foreach (var rel in instructionFiles)
        {
            if (excerpted >= MaxInstructionExcerpts) break;
            try
            {
                var full = Path.Combine(root, rel);
                var text = File.ReadAllText(full);
                sb.AppendLine($"## Instruction file excerpt: {rel}");
                sb.AppendLine(text.Length > InstructionExcerptChars
                    ? text[..InstructionExcerptChars] + "\n...(truncated)"
                    : text);
                sb.AppendLine();
                excerpted++;
            }
            catch { /* unreadable - path still listed above */ }
        }

        // Key manifest CONTENT (not just names): deps/scripts/frameworks ground the Stack and
        // Build & Test sections in facts instead of guesses.
        int manifestsRead = 0;
        foreach (var rel in manifests)
        {
            if (manifestsRead >= 4) break;
            var name = Path.GetFileName(rel);
            if (!name.Equals("package.json", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var full = Path.Combine(root, rel);
                var text = File.ReadAllText(full);
                sb.AppendLine($"## Manifest excerpt: {rel}");
                sb.AppendLine(text.Length > ManifestExcerptChars
                    ? text[..ManifestExcerptChars] + "\n...(truncated)"
                    : text);
                sb.AppendLine();
                manifestsRead++;
            }
            catch { /* ignore */ }
        }

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

    /// <summary>
    /// Bounded recursive walk collecting relative paths of markdown docs, agent-instruction files,
    /// manifests, and CI workflows. Depth-capped, skips dependency/build/VCS dirs, and never throws.
    /// </summary>
    private static void Walk(
        string root, string dir, int depth,
        List<string> markdown, List<string> instructionFiles, List<string> manifests, List<string> ciWorkflows)
    {
        if (depth > MaxWalkDepth) return;
        string[] files;
        string[] subdirs;
        try
        {
            files = Directory.GetFiles(dir);
            subdirs = Directory.GetDirectories(dir);
        }
        catch { return; }

        foreach (var f in files)
        {
            string name = Path.GetFileName(f);
            string rel = Path.GetRelativePath(root, f);

            if (InstructionFileNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                instructionFiles.Add(rel);
                continue; // an instruction file is listed once, in its own bucket
            }

            if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                if (markdown.Count < MaxMarkdownPaths * 2) markdown.Add(rel);
                continue;
            }

            if (ManifestFiles.Any(m => name.EndsWith(m, StringComparison.OrdinalIgnoreCase)))
            {
                if (manifests.Count < MaxManifestPaths * 2) manifests.Add(rel);
                continue;
            }

            // CI workflows (github actions / gitlab)
            if (rel.Replace('\\', '/').Contains(".github/workflows/", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".gitlab-ci.yml", StringComparison.OrdinalIgnoreCase))
            {
                ciWorkflows.Add(rel);
            }
        }

        foreach (var d in subdirs)
        {
            string dname = Path.GetFileName(d);
            if (string.IsNullOrEmpty(dname)) continue;
            // .github is skipped by the dot-prefix convention nowhere here; walk it explicitly for
            // workflows/copilot-instructions, but skip the heavyweight/no-signal dirs.
            if (SkipDirs.Contains(dname)) continue;
            if (dname.StartsWith('.') && !dname.Equals(".github", StringComparison.OrdinalIgnoreCase)
                                      && !dname.Equals(".cursor", StringComparison.OrdinalIgnoreCase))
                continue;
            Walk(root, d, depth + 1, markdown, instructionFiles, manifests, ciWorkflows);
        }
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
        system.AppendLine("  ## Stack - languages, frameworks, key tools (from the manifest CONTENTS)");
        system.AppendLine("  ## Layout - the important top-level dirs and what they hold");
        system.AppendLine("  ## Key Files - a bulleted PATH MAP of the files an agent should read for");
        system.AppendLine("     context, grouped: agent-instruction files (AGENTS.md/CLAUDE.md/.cursorrules");
        system.AppendLine("     etc.), important docs (markdown), key manifests, CI workflows. Use the");
        system.AppendLine("     relative paths from the scan verbatim; one line per file with a 3-8 word");
        system.AppendLine("     note on what it contains / when to read it.");
        system.AppendLine("  ## Build & Test - the concrete commands to build, run, and test");
        system.AppendLine("  ## Instructions - standing directives an agent must follow, FOLDED IN from any");
        system.AppendLine("     instruction-file excerpts in the scan (CLAUDE.md, .cursorrules, CONTRIBUTING");
        system.AppendLine("     etc.), deduplicated and attributed like '(from CLAUDE.md)'. If none: omit.");
        system.AppendLine("  ## Conventions - anything notable (only if evidenced)");
        system.AppendLine();
        system.AppendLine("Do NOT invent commands, frameworks, dirs, or paths not present in the scan. Prefer");
        system.AppendLine("the canonical command for the detected build system. Output ONLY the markdown, no");
        system.AppendLine("preamble.");

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
