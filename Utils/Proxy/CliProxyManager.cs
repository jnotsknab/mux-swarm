using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MuxSwarm.Utils.Proxy;

/// <summary>
/// Manages the on-demand CLIProxyAPI sidecar that fronts subscription-OAuth providers (Claude Max/Pro,
/// Codex/ChatGPT, Gemini...) behind a single OpenAI-compatible loopback endpoint. The proxy binary is
/// downloaded from GitHub Releases on FIRST USE (never bundled), SHA256-verified against
/// <see cref="CliProxyAssets"/>, and cached on local disk (never NAS, per the no-exec-on-NAS rule).
///
/// This file implements milestone 1: <see cref="EnsureBinaryAsync"/> (download -> verify -> extract ->
/// locate). Spawn/health/login (<c>EnsureRunningAsync</c>, <c>GetAuthFilesAsync</c>, <c>LoginAsync</c>)
/// arrive in later slices.
/// </summary>
internal static class CliProxyManager
{
    /// <summary>
    /// Local install root: %LOCALAPPDATA%/Mux-Swarm/cliproxy/&lt;version&gt;/ on Windows, or
    /// ~/.local/share/Mux-Swarm/cliproxy/&lt;version&gt;/ elsewhere. Versioned so a pin bump installs
    /// side-by-side and an old binary is never silently reused.
    /// </summary>
    public static string InstallDir
    {
        get
        {
            string baseDir = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share");
            return Path.Combine(baseDir, "Mux-Swarm", "cliproxy", CliProxyAssets.Version);
        }
    }

    /// <summary>Absolute path the proxy executable is expected at once provisioned.</summary>
    public static string ExecutablePath =>
        Path.Combine(InstallDir, CliProxyAssets.ExecutableName);

    /// <summary>True if a provisioned executable already exists for the pinned version.</summary>
    public static bool IsBinaryPresent => File.Exists(ExecutablePath);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Ensures the pinned CLIProxyAPI executable is present locally, downloading + verifying + extracting
    /// it on first use. Idempotent and concurrency-safe (a process-wide gate serializes the first install
    /// so two agents triggering at once don't race). Returns the absolute executable path.
    /// Throws <see cref="PlatformNotSupportedException"/> if no pinned artifact matches this runtime, or
    /// <see cref="InvalidOperationException"/> on a SHA256 mismatch or missing executable post-extract.
    /// </summary>
    public static async Task<string> EnsureBinaryAsync(CancellationToken ct = default)
    {
        if (IsBinaryPresent) return ExecutablePath;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: another caller may have provisioned while we waited.
            if (IsBinaryPresent) return ExecutablePath;

            var asset = CliProxyAssets.ForCurrent()
                ?? throw new PlatformNotSupportedException(
                    $"No pinned CLIProxyAPI {CliProxyAssets.Version} artifact for this OS/architecture " +
                    $"({CliProxyAssets.CurrentRid() ?? "unknown"}).");

            MuxConsole.WriteInfo($"Fetching CLIProxyAPI v{CliProxyAssets.Version} ({asset.Rid}) in background...");
            await DownloadVerifyExtractAsync(asset, ct).ConfigureAwait(false);

            if (!IsBinaryPresent)
                throw new InvalidOperationException(
                    $"CLIProxyAPI archive extracted but executable '{CliProxyAssets.ExecutableName}' " +
                    $"was not found under {InstallDir}.");

            MuxConsole.WriteSuccess($"CLIProxyAPI v{CliProxyAssets.Version} ready.");
            return ExecutablePath;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Downloads the artifact to a temp file, verifies its SHA256 against the pin, extracts into a staging
    /// dir, locates the executable within (archive layout is not assumed), and atomically swaps the
    /// provisioned files into <see cref="InstallDir"/>. A partial/failed download can never leave a
    /// half-populated install behind.
    /// </summary>
    private static async Task DownloadVerifyExtractAsync(CliProxyAssets.Asset asset, CancellationToken ct)
    {
        string tmpArchive = Path.Combine(Path.GetTempPath(),
            $"cliproxy-{CliProxyAssets.Version}-{Guid.NewGuid():N}{(asset.IsZip ? ".zip" : ".tar.gz")}");
        string stageDir = Path.Combine(Path.GetTempPath(), $"cliproxy-stage-{Guid.NewGuid():N}");

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("mux-swarm-cliproxy-fetch");
                await using var net = await http.GetStreamAsync(asset.Url, ct).ConfigureAwait(false);
                await using var fs = File.Create(tmpArchive);
                await net.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            await VerifySha256Async(tmpArchive, asset.Sha256, ct).ConfigureAwait(false);

            Directory.CreateDirectory(stageDir);
            if (asset.IsZip)
                ZipFile.ExtractToDirectory(tmpArchive, stageDir, overwriteFiles: true);
            else
                await ExtractTarGzAsync(tmpArchive, stageDir, ct).ConfigureAwait(false);

            string stagedExe = LocateExecutable(stageDir)
                ?? throw new InvalidOperationException(
                    $"CLIProxyAPI archive did not contain '{CliProxyAssets.ExecutableName}'.");

            // Swap the whole extracted tree (the binary may ship alongside a default config/README) into
            // place, then ensure the executable lands at the canonical ExecutablePath.
            Directory.CreateDirectory(Path.GetDirectoryName(InstallDir)!);
            if (Directory.Exists(InstallDir))
            {
                var old = InstallDir + ".old-" + Guid.NewGuid().ToString("N");
                Directory.Move(InstallDir, old);
                try { Directory.Delete(old, recursive: true); } catch { /* best effort */ }
            }

            string stagedRoot = Path.GetDirectoryName(stagedExe)!;
            Directory.Move(stagedRoot, InstallDir);

            // If the executable sat in a subdirectory of the staged root, it is now under InstallDir at the
            // same relative spot; normalize it to ExecutablePath so callers have a stable path.
            string movedExe = Path.Combine(InstallDir, Path.GetFileName(stagedExe));
            if (!File.Exists(ExecutablePath) && File.Exists(movedExe) &&
                !string.Equals(movedExe, ExecutablePath, StringComparison.Ordinal))
            {
                File.Move(movedExe, ExecutablePath, overwrite: true);
            }

            MakeExecutable(ExecutablePath);
        }
        finally
        {
            try { if (File.Exists(tmpArchive)) File.Delete(tmpArchive); } catch { }
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true); } catch { }
        }
    }

    /// <summary>Computes the file's SHA256 and throws if it does not match the pinned hex digest.</summary>
    internal static async Task VerifySha256Async(string filePath, string expectedHex, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        string actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"CLIProxyAPI download SHA256 mismatch: expected {expectedHex}, got {actual}. Aborting.");
    }

    /// <summary>Extracts a .tar.gz into <paramref name="destDir"/> with path-traversal defense.</summary>
    private static async Task ExtractTarGzAsync(string tgzPath, string destDir, CancellationToken ct)
    {
        await using var file = File.OpenRead(tgzPath);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        await using var tar = new TarReader(gz);

        string destFull = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false)) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;

            string rel = entry.Name.Replace('\\', '/').TrimStart('/');
            string dest = Path.GetFullPath(Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(destFull, StringComparison.Ordinal)) continue; // traversal guard

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await entry.ExtractToFileAsync(dest, overwrite: true, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the proxy executable anywhere under <paramref name="root"/>. The archive's internal layout is
    /// not assumed (binary may sit at the root or in a subdir); matches by the platform executable name.
    /// </summary>
    internal static string? LocateExecutable(string root)
    {
        if (!Directory.Exists(root)) return null;
        string exeName = CliProxyAssets.ExecutableName;

        // Exact name first.
        var exact = Directory.EnumerateFiles(root, exeName, SearchOption.AllDirectories).FirstOrDefault();
        if (exact is not null) return exact;

        // Fallback: a file whose name starts with cli-proxy-api (handles unexpected casing/suffixes).
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .FirstOrDefault(p =>
            {
                string n = Path.GetFileName(p);
                return n.StartsWith("cli-proxy-api", StringComparison.OrdinalIgnoreCase)
                    && (OperatingSystem.IsWindows()
                        ? n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        : !n.Contains('.'));
            });
    }

    /// <summary>chmod +x on non-Windows so the freshly extracted binary is runnable.</summary>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { /* best effort; spawn will surface a real error if it truly isn't executable */ }
    }
}
