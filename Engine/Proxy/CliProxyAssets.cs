using System.Runtime.InteropServices;
using System.Text.Json;

namespace MuxSwarm.Engine.Proxy;

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

    // ─── Latest-release resolution (used by `/proxy update`) ───

    private const string LatestReleaseApi =
        "https://api.github.com/repos/router-for-me/CLIProxyAPI/releases/latest";

    /// <summary>A release artifact resolved at RUNTIME (the latest upstream release) rather than the
    /// compile-time pin. Its SHA256 comes from the release's own checksums.txt, so the download is
    /// still integrity-verified end to end.</summary>
    public sealed record ResolvedRelease(string Version, string FileName, string Sha256, bool IsZip)
    {
        /// <summary>Full GitHub Releases download URL for this artifact at its resolved version.</summary>
        public string Url => $"{ReleaseBase}/v{Version}/{FileName}";
    }

    /// <summary>
    /// Compose the upstream asset file name for a runtime identifier at an arbitrary version,
    /// mirroring the pinned table's naming scheme. Null when the rid is unsupported.
    /// </summary>
    public static string? FileNameFor(string rid, string version) => rid.ToLowerInvariant() switch
    {
        "win-x64"     => $"CLIProxyAPI_{version}_windows_amd64.zip",
        "win-arm64"   => $"CLIProxyAPI_{version}_windows_aarch64.zip",
        "osx-x64"     => $"CLIProxyAPI_{version}_darwin_amd64.tar.gz",
        "osx-arm64"   => $"CLIProxyAPI_{version}_darwin_aarch64.tar.gz",
        "linux-x64"   => $"CLIProxyAPI_{version}_linux_amd64.tar.gz",
        "linux-arm64" => $"CLIProxyAPI_{version}_linux_aarch64.tar.gz",
        _ => null,
    };

    /// <summary>
    /// Parse a GitHub-release checksums.txt (whitespace-separated "&lt;sha256&gt; &lt;filename&gt;" pairs,
    /// one per line) into a filename -&gt; sha256 map. Tolerant of blank lines and extra whitespace.
    /// </summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            // Pairs are (hash, filename); a 64-hex first token keeps us aligned if a stray line sneaks in.
            if (tokens[i].Length == 64) map[tokens[i + 1]] = tokens[i];
            else i -= 1; // resync: treat the next token as a potential hash
        }
        return map;
    }

    /// <summary>
    /// Resolve the LATEST upstream release for the current runtime: queries the GitHub Releases API
    /// for the tag, then fetches the release's checksums.txt for the artifact's SHA256. Throws on
    /// network failure, an unsupported platform, or a checksums.txt without this artifact.
    /// </summary>
    public static async Task<ResolvedRelease> ResolveLatestAsync(CancellationToken ct = default)
    {
        string rid = CurrentRid()
            ?? throw new PlatformNotSupportedException("Unsupported OS/architecture for CLIProxyAPI.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("mux-swarm-cliproxy-fetch");

        string json = await http.GetStringAsync(LatestReleaseApi, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        string tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (tag.Length == 0)
            throw new InvalidOperationException("GitHub latest-release response had no tag_name.");
        string version = tag.TrimStart('v', 'V');

        string fileName = FileNameFor(rid, version)
            ?? throw new PlatformNotSupportedException($"No CLIProxyAPI artifact naming for rid '{rid}'.");

        string checksums = await http.GetStringAsync(
            $"{ReleaseBase}/{tag}/checksums.txt", ct).ConfigureAwait(false);
        var map = ParseChecksums(checksums);
        if (!map.TryGetValue(fileName, out var sha))
            throw new InvalidOperationException(
                $"Release {tag} checksums.txt has no entry for {fileName}.");

        return new ResolvedRelease(version, fileName, sha,
            IsZip: fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }
}
