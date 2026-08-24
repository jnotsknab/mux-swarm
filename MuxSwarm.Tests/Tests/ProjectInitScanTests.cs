using System;
using System.IO;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers the /init workspace scan (ProjectInit.ScanWorkspace): the bounded recursive walk must
/// surface RELATIVE PATHS of markdown docs, agent-instruction files (CLAUDE.md/.cursorrules etc.),
/// manifests, and CI workflows - plus instruction-file/manifest content excerpts - so the generated
/// AGENTS.md can act as a path map with instructions rather than a shallow top-level listing.
/// Uses a real temp-dir fixture; no model call involved.
/// </summary>
public class ProjectInitScanTests : IDisposable
{
    private readonly string _root;

    public ProjectInitScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "muxinit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // top level
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Demo project readme");
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "Always run tests before committing.");
        File.WriteAllText(Path.Combine(_root, "package.json"), "{ \"scripts\": { \"test\": \"jest\" } }");

        // nested docs + instruction files
        Directory.CreateDirectory(Path.Combine(_root, "docs", "guides"));
        File.WriteAllText(Path.Combine(_root, "docs", "guides", "architecture.md"), "# Arch");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", ".cursorrules"), "Use tabs.");

        // CI workflow under .github (dot-dir that must still be walked)
        Directory.CreateDirectory(Path.Combine(_root, ".github", "workflows"));
        File.WriteAllText(Path.Combine(_root, ".github", "workflows", "ci.yml"), "on: push");

        // noise that must be skipped
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "leftpad"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "leftpad", "SHOULD_NOT_APPEAR.md"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Scan_ListsNestedMarkdown_WithRelativePaths()
    {
        var scan = ProjectInit.ScanWorkspace(_root);
        Assert.Contains(Path.Combine("docs", "guides", "architecture.md"), scan);
    }

    [Fact]
    public void Scan_SurfacesInstructionFiles_AtAnyDepth_WithExcerpts()
    {
        var scan = ProjectInit.ScanWorkspace(_root);
        // listed as instruction files (root CLAUDE.md + nested .cursorrules)
        Assert.Contains("CLAUDE.md", scan);
        Assert.Contains(Path.Combine("src", ".cursorrules"), scan);
        // their CONTENT is excerpted so directives can be folded into the output
        Assert.Contains("Always run tests before committing.", scan);
        Assert.Contains("Use tabs.", scan);
    }

    [Fact]
    public void Scan_IncludesManifestContentExcerpt()
    {
        var scan = ProjectInit.ScanWorkspace(_root);
        Assert.Contains("package.json", scan);
        Assert.Contains("jest", scan); // content, not just the name
    }

    [Fact]
    public void Scan_FindsCiWorkflows_UnderDotGithub()
    {
        var scan = ProjectInit.ScanWorkspace(_root);
        Assert.Contains(Path.Combine(".github", "workflows", "ci.yml"), scan);
    }

    [Fact]
    public void Scan_SkipsDependencyDirs()
    {
        var scan = ProjectInit.ScanWorkspace(_root);
        Assert.DoesNotContain("SHOULD_NOT_APPEAR.md", scan);
    }

    [Fact]
    public void Scan_NeverThrows_OnMissingRoot()
    {
        var scan = ProjectInit.ScanWorkspace(Path.Combine(_root, "no-such-dir"));
        Assert.NotNull(scan); // degraded output, no exception
    }
}
