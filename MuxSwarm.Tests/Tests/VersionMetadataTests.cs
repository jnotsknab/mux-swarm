using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// The csproj version (Windows file properties, assembly metadata) must match App.Version (--version, splash,
/// /api/health, updater). They drifted for years (csproj read 0.9.5); this keeps them in lockstep.
/// </summary>
public class VersionMetadataTests
{
    [Fact]
    public void CsprojVersion_MatchesAppVersion()
    {
        var p = XDocument.Load(Path.Combine(FindRepositoryRoot(), "MuxSwarm.csproj")).Descendants("PropertyGroup").First();
        Assert.Equal(MuxSwarm.App.Version, (string?)p.Element("Version"));
        Assert.Equal(MuxSwarm.App.Version, (string?)p.Element("FileVersion"));
        Assert.Equal(MuxSwarm.App.Version + ".0", (string?)p.Element("AssemblyVersion"));
    }

    [Fact]
    public void ApplicationIcon_IsCommitted_AndHasTheStandardSizes()
    {
        string root = FindRepositoryRoot();
        var p = XDocument.Load(Path.Combine(root, "MuxSwarm.csproj")).Descendants("PropertyGroup").First();
        string ico = Path.Combine(root, ((string?)p.Element("ApplicationIcon"))!.Replace('\\', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(ico), ico);
        byte[] b = File.ReadAllBytes(ico);
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, b[..4]);                     // ICONDIR, type 1 = icon
        int count = BitConverter.ToUInt16(b, 4);
        var sizes = Enumerable.Range(0, count).Select(i => b[6 + i * 16] == 0 ? 256 : b[6 + i * 16]).ToHashSet();
        Assert.Superset(new HashSet<int> { 16, 32, 48, 256 }, sizes);
        Assert.True(File.Exists(Path.Combine(root, "Assets", "mux-logo.svg")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MuxSwarm.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the MuxSwarm repository root.");
    }
}
