using System;
using MuxSwarm.Utils.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Coverage for the Windows UNC -> drive-letter mapper's pure parsing. The actual `net use` mapping is
/// Windows + NAS dependent and validated interactively; here we lock the UNC detection + share-root split.
/// </summary>
public class UncDriveMapperTests
{
    [Theory]
    [InlineData(@"\\banknas\Public\Jb\MuxSandboxV0.4.0", true)]
    [InlineData(@"//banknas/Public/Jb", true)]
    [InlineData(@"C:\Users\jnots\proj", false)]
    [InlineData(@"/home/dabsaint/proj", false)]
    [InlineData(@"relative\path", false)]
    public void IsUnc_DetectsUncPaths(string path, bool expected)
        => Assert.Equal(expected, UncDriveMapper.IsUnc(path));

    [Fact]
    public void SplitUnc_SeparatesShareRootAndSubpath()
    {
        var (root, sub) = UncDriveMapper.SplitUnc(@"\\banknas\Public\Jb\MuxSandboxV0.4.0");
        Assert.Equal(@"\\banknas\Public", root);
        Assert.Equal(@"Jb\MuxSandboxV0.4.0", sub);
    }

    [Fact]
    public void SplitUnc_ShareRootOnly_NoSubpath()
    {
        var (root, sub) = UncDriveMapper.SplitUnc(@"\\server\share");
        Assert.Equal(@"\\server\share", root);
        Assert.Equal("", sub);
    }

    [Fact]
    public void SplitUnc_Malformed_ReturnsNullRoot()
    {
        var (root, _) = UncDriveMapper.SplitUnc(@"\\onlyserver");
        Assert.Null(root);
    }

    [Fact]
    public void ToMountable_NonUncPath_PassesThroughUnchanged()
    {
        using var m = new UncDriveMapper();
        // A local path is never remapped on any OS.
        Assert.Equal(@"C:\Users\jnots\proj", m.ToMountable(@"C:\Users\jnots\proj"));
        Assert.Equal("/home/x/proj", m.ToMountable("/home/x/proj"));
    }
}
