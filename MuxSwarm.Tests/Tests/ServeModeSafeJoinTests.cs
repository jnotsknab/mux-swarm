using System.Runtime.InteropServices;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Path-containment tests for <see cref="ServeMode.SafeJoin"/> (CWE-22 regression guard). The
/// load-bearing guarantee: a resolved path is accepted ONLY when it equals the root or sits under
/// root + a path-component separator - a bare string-prefix match (a sibling named
/// <c>&lt;root&gt;_escape</c>) must be rejected. Also guards the drive-root / UNC-root cases that a
/// naive TrimEndingDirectorySeparator + separator concat would falsely deny.
/// </summary>
public class ServeModeSafeJoinTests
{
    // A real, canonical temp directory to use as the sandbox root for the common cases.
    private static string Root() => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

    private static string Basename(string root) => Path.GetFileName(root);

    [Fact]
    public void AdvisoryExploit_SiblingPrefixEscape_IsDenied()
    {
        var root = Root();
        // ..\<basename>_escape resolves to a SIBLING that string-prefix-matches the root.
        Assert.Null(ServeMode.SafeJoin(root, ".." + Path.DirectorySeparatorChar + Basename(root) + "_escape"));
    }

    [Fact]
    public void SiblingPrefixEscape_ForwardSlashes_IsDenied()
    {
        var root = Root();
        Assert.Null(ServeMode.SafeJoin(root, "../" + Basename(root) + "_escape"));
    }

    [Fact]
    public void SiblingPrefixEscape_Deeper_IsDenied()
    {
        var root = Root();
        var sub = ".." + Path.DirectorySeparatorChar + Basename(root) + "_escape"
                  + Path.DirectorySeparatorChar + "a" + Path.DirectorySeparatorChar + "b.txt";
        Assert.Null(ServeMode.SafeJoin(root, sub));
    }

    [Fact]
    public void ClassicTraversal_IsDenied()
    {
        var root = Root();
        Assert.Null(ServeMode.SafeJoin(root, Path.Combine("..", "..", "some_outside_dir", "x.ini")));
    }

    [Fact]
    public void RootedAbsoluteSubpath_IsDenied()
    {
        var root = Root();
        // Path.Combine discards root when subpath is rooted; the elsewhere-resolved path must fail.
        var outside = OperatingSystem.IsWindows() ? @"C:\Windows" : "/etc";
        Assert.Null(ServeMode.SafeJoin(root, outside));
    }

    [Fact]
    public void NormalSubdir_IsAllowed()
    {
        var root = Root();
        Assert.Equal(Path.Combine(root, "uploads"), ServeMode.SafeJoin(root, "uploads"));
    }

    [Fact]
    public void NestedSubdir_IsAllowed()
    {
        var root = Root();
        var sub = Path.Combine("uploads", "2026", "f.txt");
        Assert.Equal(Path.Combine(root, sub), ServeMode.SafeJoin(root, sub));
    }

    [Fact]
    public void RootItself_Empty_IsAllowed()
    {
        var root = Root();
        Assert.Equal(root, Path.TrimEndingDirectorySeparator(ServeMode.SafeJoin(root, "")!));
    }

    [Fact]
    public void RootItself_Dot_IsAllowed()
    {
        var root = Root();
        Assert.Equal(root, Path.TrimEndingDirectorySeparator(ServeMode.SafeJoin(root, ".")!));
    }

    [Fact]
    public void InTreeDotDot_StaysInside_IsAllowed()
    {
        var root = Root();
        // uploads\..\logs canonicalizes to <root>\logs - still inside.
        Assert.Equal(Path.Combine(root, "logs"),
            ServeMode.SafeJoin(root, Path.Combine("uploads", "..", "logs")));
    }

    [Fact]
    public void RootWithTrailingSeparator_IsAllowed()
    {
        var root = Root();
        Assert.Equal(Path.Combine(root, "uploads"),
            ServeMode.SafeJoin(root + Path.DirectorySeparatorChar, "uploads"));
    }

    [Fact]
    public void DriveRoot_Subdir_IsAllowed()
    {
        if (!OperatingSystem.IsWindows()) return;   // drive-root semantics are Windows-only
        Assert.Equal(@"C:\sandbox", ServeMode.SafeJoin(@"C:\", "sandbox"));
    }

    [Fact]
    public void DriveRoot_Itself_IsAllowed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(@"C:\", ServeMode.SafeJoin(@"C:\", ""));
    }

    [Fact]
    public void UncShareRoot_Subdir_IsAllowed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(@"\\srv\share\Jb", ServeMode.SafeJoin(@"\\srv\share\", "Jb"));
    }
}
