using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Utils.Proxy;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Slice-1 coverage for the CLIProxyAPI on-demand provisioner: the pinned asset table (RID resolution,
/// URL shape, hash presence) and CliProxyManager's pure helpers (SHA256 verify gate, executable
/// location). These are offline/no-network unit tests; the live download+spawn smoke is a separate
/// gated integration test in a later slice.
/// </summary>
public class CliProxyAssetsTests
{
    [Fact]
    public void Artifacts_CoverAllSixMainstreamRids_WithDistinctHashes()
    {
        var rids = CliProxyAssets.Artifacts.Select(a => a.Rid).ToList();
        foreach (var expected in new[] { "win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64" })
            Assert.Contains(expected, rids);

        // Every artifact carries a full 64-hex-char SHA256 and they are all distinct.
        Assert.All(CliProxyAssets.Artifacts, a => Assert.Equal(64, a.Sha256.Length));
        var hashes = CliProxyAssets.Artifacts.Select(a => a.Sha256).ToList();
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public void Asset_Url_PointsAtPinnedGitHubRelease()
    {
        var a = CliProxyAssets.ForRid("win-x64");
        Assert.NotNull(a);
        Assert.Equal(
            $"https://github.com/router-for-me/CLIProxyAPI/releases/download/v{CliProxyAssets.Version}/{a!.FileName}",
            a.Url);
        Assert.True(a.IsZip);                       // windows ships .zip
        Assert.EndsWith(".zip", a.FileName);
    }

    [Fact]
    public void Asset_NonWindows_IsTarGz()
    {
        var a = CliProxyAssets.ForRid("linux-x64");
        Assert.NotNull(a);
        Assert.False(a!.IsZip);
        Assert.EndsWith(".tar.gz", a.FileName);
    }

    [Fact]
    public void ForRid_UnknownRid_ReturnsNull()
    {
        Assert.Null(CliProxyAssets.ForRid("solaris-sparc"));
    }

    [Fact]
    public void CurrentRid_ResolvesToAPinnedArtifact_OnSupportedHosts()
    {
        // The test host is win/linux/osx on x64/arm64, all of which are pinned.
        var rid = CliProxyAssets.CurrentRid();
        Assert.NotNull(rid);
        Assert.NotNull(CliProxyAssets.ForCurrent());
    }

    [Fact]
    public void ExecutableName_MatchesHostOs()
    {
        var name = CliProxyAssets.ExecutableName;
        if (OperatingSystem.IsWindows())
            Assert.Equal("cli-proxy-api.exe", name);
        else
            Assert.Equal("cli-proxy-api", name);
    }

    [Fact]
    public async Task VerifySha256Async_AcceptsCorrect_RejectsTampered()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cliproxy-shatest-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(tmp, new byte[] { 1, 2, 3, 4, 5 });
            // sha256 of bytes {1,2,3,4,5}
            const string correct = "74f81fe167d99b4cb41d6d0ccda82278caee9f3e2f25d5e5a3936ff3dcec60d0";
            await CliProxyManager.VerifySha256Async(tmp, correct, CancellationToken.None); // no throw

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CliProxyManager.VerifySha256Async(tmp, new string('0', 64), CancellationToken.None));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void LocateExecutable_FindsBinaryInNestedDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cliproxy-locate-{Guid.NewGuid():N}");
        try
        {
            var nested = Path.Combine(root, "inner");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(root, "README.md"), "x");
            var exe = Path.Combine(nested, CliProxyAssets.ExecutableName);
            File.WriteAllText(exe, "binary");

            var found = CliProxyManager.LocateExecutable(root);
            Assert.NotNull(found);
            Assert.Equal(Path.GetFileName(exe), Path.GetFileName(found!));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LocateExecutable_ReturnsNull_WhenAbsent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cliproxy-empty-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "notes.txt"), "nothing here");
            Assert.Null(CliProxyManager.LocateExecutable(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
