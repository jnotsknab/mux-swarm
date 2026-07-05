using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MuxSwarm.Utils;

namespace MuxSwarm.State;

/// <summary>
/// Native self-update: pulls the latest GitHub release, verifies the platform asset against the
/// SHA256 digest GitHub publishes on every asset, extracts it, and replaces only SHIPPED files
/// (exe, Runtime/, Prompts/, bundled skills, docs) whose content hash differs -- USER-owned paths
/// (Config.json/Swarm.json, Sessions/, Teams/, and the durable Context/* memory files) are never
/// touched. The running binary cannot be overwritten in place while locked, so the new exe is staged
/// and swapped on relaunch (rename-current-aside, drop new, re-exec, exit).
///
/// Backs both the <c>--update</c> CLI arg and the <c>/update</c> command; the pure planning stage
/// (<see cref="PlanAsync"/>) is also surfaced read-only over <c>POST /api/update</c>.
/// </summary>
public static class SelfUpdater
{
    private const string Repo = "jnotsknab/mux-swarm";

    /// <summary>Relative paths (under the install/base dir) that hold USER data and must NEVER be
    /// overwritten by an update. Matched case-insensitively as a path prefix.</summary>
    internal static readonly string[] UserOwnedPrefixes =
    {
        Path.Combine("Configs", "Config.json"),
        Path.Combine("Configs", "Swarm.json"),
        "Sessions",
        "Teams",
        Path.Combine("Context", "reflections.json"),
        Path.Combine("Context", "MEMORY.md"),
        Path.Combine("Context", "BRAIN.md"),
    };

    // ---- GitHub release DTOs (only the fields we consume) ----
    private sealed record GhAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

    private sealed record GhRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<GhAsset> Assets);

    /// <summary>Outcome of planning an update -- what would change, before anything is written.</summary>
    public sealed record UpdatePlan(
        bool UpdateAvailable,
        string CurrentVersion,
        string LatestTag,
        string? AssetName,
        long AssetSize,
        string Message);

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mux-Swarm-SelfUpdater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                 ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    /// <summary>The GitHub release-asset name for the current OS/arch.</summary>
    internal static string PlatformAssetName()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"mux-swarm-win-{arch}.zip";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"mux-swarm-osx-{arch}.tar.gz";
        return $"mux-swarm-linux-{arch}.tar.gz";
    }

    private static string BinaryName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "MuxSwarm.exe" : "MuxSwarm";

    /// <summary>
    /// Compare the running version tag against the latest published release. Semver-lite: strips a
    /// leading 'v' and any '-alpha'/pre-release suffix and compares the numeric dotted core; a
    /// differing tag with an equal-or-newer core is treated as available (tag string differs).
    /// </summary>
    internal static bool IsNewer(string latestTag, string currentVersion)
    {
        static int[] Core(string s)
        {
            var t = s.TrimStart('v', 'V');
            var dash = t.IndexOf('-');
            if (dash >= 0) t = t[..dash];
            var parts = t.Split('.');
            var nums = new int[3];
            for (int i = 0; i < 3 && i < parts.Length; i++)
                int.TryParse(parts[i], out nums[i]);
            return nums;
        }
        var l = Core(latestTag);
        var c = Core(currentVersion);
        for (int i = 0; i < 3; i++)
        {
            if (l[i] > c[i]) return true;
            if (l[i] < c[i]) return false;
        }
        // Equal numeric core: only "newer" if the tag text actually differs (e.g. a re-tag/build).
        return !string.Equals(latestTag.TrimStart('v', 'V'), currentVersion.TrimStart('v', 'V'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<GhRelease?> FetchLatestAsync(HttpClient http, CancellationToken ct)
    {
        // /releases/latest excludes prereleases; the project ships -alpha tags, so take the newest
        // of the full list instead (first entry is most-recent by GitHub's ordering).
        var releases = await http.GetFromJsonAsync<List<GhRelease>>(
            $"https://api.github.com/repos/{Repo}/releases", ct);
        return releases is { Count: > 0 } ? releases[0] : null;
    }

    /// <summary>Read-only: determine whether an update is available and what asset would be used.</summary>
    public static async Task<UpdatePlan> PlanAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = NewClient();
            var rel = await FetchLatestAsync(http, ct);
            if (rel is null)
                return new(false, App.Version, "", null, 0, "No releases found.");

            var wanted = PlatformAssetName();
            var asset = rel.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase));

            if (!IsNewer(rel.TagName, App.Version))
                return new(false, App.Version, rel.TagName, asset?.Name, asset?.Size ?? 0,
                    $"Already up to date (current {App.Version}, latest {rel.TagName}).");

            if (asset is null)
                return new(false, App.Version, rel.TagName, null, 0,
                    $"Release {rel.TagName} has no asset '{wanted}' for this platform.");

            return new(true, App.Version, rel.TagName, asset.Name, asset.Size,
                $"Update available: {App.Version} -> {rel.TagName} ({asset.Name}, {asset.Size / (1024 * 1024)} MB).");
        }
        catch (Exception ex)
        {
            return new(false, App.Version, "", null, 0, $"Update check failed: {ex.Message}");
        }
    }

    /// <summary>Progress/status callback sink (a line at a time).</summary>
    public delegate void Log(string line);

    /// <summary>
    /// Full update: download + verify + extract + diff + stage. Returns (staged, message). When
    /// <paramref name="staged"/> is true a relaunch is required to finish (the new exe is staged as
    /// <c>MuxSwarm.new</c> next to the running one and applied by <see cref="ApplyStagedBinaryIfPresent"/>
    /// on next boot; all other changed shipped files are already written in place).
    /// </summary>
    public static async Task<(bool Staged, string Message)> RunAsync(Log? log = null, CancellationToken ct = default)
    {
        void Emit(string s) { log?.Invoke(s); }
        var installDir = PlatformContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var plan = await PlanAsync(ct);
        if (!plan.UpdateAvailable)
            return (false, plan.Message);
        Emit(plan.Message);

        using var http = NewClient();
        var rel = await FetchLatestAsync(http, ct);
        var wanted = PlatformAssetName();
        var asset = rel?.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return (false, $"Asset '{wanted}' not found in the latest release.");

        // ---- download ----
        var tmpRoot = Path.Combine(Path.GetTempPath(), $"mux-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpRoot);
        var archivePath = Path.Combine(tmpRoot, asset.Name);
        try
        {
            Emit($"Downloading {asset.Name} ...");
            await using (var resp = await http.GetStreamAsync(asset.BrowserDownloadUrl, ct))
            await using (var fs = File.Create(archivePath))
                await resp.CopyToAsync(fs, ct);

            // ---- verify against GitHub's published digest ----
            var expected = (asset.Digest ?? "").Replace("sha256:", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (string.IsNullOrEmpty(expected))
            {
                Emit("WARNING: release asset has no published digest; cannot verify integrity. Aborting.");
                return (false, "Update aborted: no digest published for the release asset (cannot verify).");
            }
            var actual = await Sha256FileAsync(archivePath, ct);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                Emit($"Integrity check FAILED. expected={expected} actual={actual}");
                return (false, "Update aborted: downloaded asset failed SHA256 verification.");
            }
            Emit($"Integrity OK (sha256 {actual[..12]}...).");

            // ---- extract ----
            var extractDir = Path.Combine(tmpRoot, "extracted");
            Directory.CreateDirectory(extractDir);
            Emit("Extracting ...");
            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
            else
                await ExtractTarGzAsync(archivePath, extractDir, ct);

            // Publish archives sometimes wrap everything in a single top-level dir; unwrap it.
            var payloadRoot = ResolvePayloadRoot(extractDir);

            // ---- diff + apply ----
            int updated = 0, skippedUser = 0, unchanged = 0;
            bool binaryStaged = false;

            foreach (var srcFile in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel2 = Path.GetRelativePath(payloadRoot, srcFile);

                if (IsUserOwned(rel2)) { skippedUser++; continue; }

                var destFile = Path.Combine(installDir, rel2);
                var isRunningBinary = string.Equals(Path.GetFileName(rel2), BinaryName, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(Path.GetDirectoryName(rel2) ?? "", "", StringComparison.Ordinal);

                if (File.Exists(destFile))
                {
                    var same = await FilesEqualAsync(srcFile, destFile, ct);
                    if (same) { unchanged++; continue; }
                }

                if (isRunningBinary)
                {
                    // Cannot overwrite the locked running exe -- stage it beside itself.
                    var staged = Path.Combine(installDir, BinaryName + ".new");
                    File.Copy(srcFile, staged, overwrite: true);
                    binaryStaged = true;
                    updated++;
                    Emit($"staged  {rel2} (applied on restart)");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(srcFile, destFile, overwrite: true);
                updated++;
                Emit($"updated {rel2}");
            }

            var summary = $"Update to {rel!.TagName}: {updated} file(s) updated, {unchanged} unchanged, {skippedUser} user file(s) preserved.";
            Emit(summary);
            if (binaryStaged)
                return (true, summary + " Restart required to swap the binary.");
            return (false, summary + " No restart needed.");
        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// On startup: if a staged <c>MuxSwarm.new</c> exists next to the running binary, swap it in
    /// (rename current -> <c>.old</c>, staged -> live) and clean any prior <c>.old</c>. The running
    /// process keeps using the already-loaded old image; the swap takes effect for the NEXT launch,
    /// so a relaunch is triggered right after staging. This call also garbage-collects a leftover
    /// <c>.old</c> from a completed swap. Best-effort; never throws.
    /// </summary>
    public static void ApplyStagedBinaryIfPresent()
    {
        try
        {
            var installDir = PlatformContext.BaseDirectory;
            var live = Path.Combine(installDir, BinaryName);
            var staged = live + ".new";
            var old = live + ".old";

            // GC a completed swap's leftover.
            if (File.Exists(old))
            {
                try { File.Delete(old); } catch { /* still locked; try next boot */ }
            }

            if (!File.Exists(staged)) return;

            // rename-aside the live exe, then move the staged one into place.
            if (File.Exists(live))
            {
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                File.Move(live, old);
            }
            File.Move(staged, live);
            MuxConsole.WriteInfo("Applied staged update binary.");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"Could not apply staged update binary: {ex.Message}");
        }
    }

    /// <summary>True if the relative path is under a user-owned prefix (never overwrite).</summary>
    internal static bool IsUserOwned(string relativePath)
    {
        var norm = relativePath.Replace('/', Path.DirectorySeparatorChar)
                               .Replace('\\', Path.DirectorySeparatorChar)
                               .TrimStart(Path.DirectorySeparatorChar);
        foreach (var p in UserOwnedPrefixes)
        {
            if (norm.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
            if (norm.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>If <paramref name="extractDir"/> contains exactly one directory (and no files), the
    /// real payload is inside it -- unwrap. Otherwise the payload is the extract dir itself.</summary>
    private static string ResolvePayloadRoot(string extractDir)
    {
        var files = Directory.GetFiles(extractDir);
        var dirs = Directory.GetDirectories(extractDir);
        if (files.Length == 0 && dirs.Length == 1)
        {
            // Only unwrap if the exe lives inside the single subdir (guards against a Runtime/-only layout).
            var inner = dirs[0];
            if (File.Exists(Path.Combine(inner, BinaryName)) || !File.Exists(Path.Combine(extractDir, BinaryName)))
                return inner;
        }
        return extractDir;
    }

    private static async Task ExtractTarGzAsync(string archivePath, string destDir, CancellationToken ct)
    {
        await using var fs = File.OpenRead(archivePath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, destDir, overwriteFiles: true, ct);
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<bool> FilesEqualAsync(string a, string b, CancellationToken ct)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return string.Equals(await Sha256FileAsync(a, ct), await Sha256FileAsync(b, ct),
            StringComparison.OrdinalIgnoreCase);
    }
}
