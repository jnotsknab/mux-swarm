using System.Runtime.InteropServices;

namespace MuxSwarm.Utils.Proxy;

/// <summary>
/// Pinned CLIProxyAPI (github.com/router-for-me/CLIProxyAPI, MIT) release metadata used by
/// <see cref="CliProxyManager"/> to download the correct per-OS/arch binary on first use of a
/// cliproxy-backed subscription provider. The proxy is NOT bundled in the repo or installer; it is
/// fetched from GitHub Releases on demand and SHA256-verified against the table below.
///
/// To bump the pinned version: update <see cref="Version"/> and replace every entry's asset name +
/// SHA256 from the new release's checksums. The <c>/proxy update</c> command re-resolves against this
/// same table at runtime.
/// </summary>
internal static class CliProxyAssets
{
    /// <summary>The pinned upstream release tag (without the leading 'v' it is added in the URL).</summary>
    public const string Version = "7.2.44";

    private const string ReleaseBase =
        "https://github.com/router-for-me/CLIProxyAPI/releases/download";

    /// <summary>A single resolved release artifact for one runtime identifier.</summary>
    public sealed record Asset(string Rid, string FileName, string Sha256, bool IsZip)
    {
        /// <summary>Full GitHub Releases download URL for this artifact at the pinned version.</summary>
        public string Url => $"{ReleaseBase}/v{Version}/{FileName}";
    }

    // Pinned v7.2.44 artifacts. SHA256 + sizes verified against the GitHub Releases API on 2026-06-28.
    private static readonly IReadOnlyList<Asset> All = new[]
    {
        new Asset("win-x64",    $"CLIProxyAPI_{Version}_windows_amd64.zip",   "36563f9f44f6791c146626d682f488a18bd052ee689e4abd878b7e4603001a07", IsZip: true),
        new Asset("win-arm64",  $"CLIProxyAPI_{Version}_windows_aarch64.zip", "98927fca02a0d05f2fea0454a173b11bc307bc7e27e9ca40e85800b075aaef2e", IsZip: true),
        new Asset("osx-x64",    $"CLIProxyAPI_{Version}_darwin_amd64.tar.gz", "c1b6cd4ea09fd18fdc14b6ff3fc4cac8ac9878abde73a37d588a1ab56c73ee1b", IsZip: false),
        new Asset("osx-arm64",  $"CLIProxyAPI_{Version}_darwin_aarch64.tar.gz","d4b00ebcd2fe6a9105b40306d37264ac4b9bdd54d107919321beb94bc062a702", IsZip: false),
        new Asset("linux-x64",  $"CLIProxyAPI_{Version}_linux_amd64.tar.gz",  "e927ba0b11846ddb576f69ff04935eb8b8058f92b4ab784ae5aeb57379bf027e", IsZip: false),
        new Asset("linux-arm64",$"CLIProxyAPI_{Version}_linux_aarch64.tar.gz","b29eab4d52dc3e5ba84aefcaa53165e665f84572c0037995827e293b278421b3", IsZip: false),
    };

    /// <summary>The runtime identifier (e.g. "win-x64") for the current process, or null if unsupported.</summary>
    public static string? CurrentRid()
    {
        string? os =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "osx" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? "linux" : null;
        if (os is null) return null;

        string? arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (arch is null) return null;

        return $"{os}-{arch}";
    }

    /// <summary>Resolve the pinned artifact for an explicit rid, or null if none matches.</summary>
    public static Asset? ForRid(string rid) =>
        All.FirstOrDefault(a => string.Equals(a.Rid, rid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolve the pinned artifact for the current runtime, or null if unsupported.</summary>
    public static Asset? ForCurrent()
    {
        var rid = CurrentRid();
        return rid is null ? null : ForRid(rid);
    }

    /// <summary>All pinned artifacts (for enumeration/tests).</summary>
    public static IReadOnlyList<Asset> Artifacts => All;

    /// <summary>The expected executable file name on the current OS once extracted.</summary>
    public static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cli-proxy-api.exe" : "cli-proxy-api";
}
