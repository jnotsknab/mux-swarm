using System.IO.Compression;
using System.Security.Cryptography;

namespace MuxSwarm.Utils.Voice;

/// <summary>
/// First-use provisioner for the local whisper.cpp speech-to-text assets that back /voice.
/// Nothing is vendored in the repo: on the first /voice the pinned whisper.cpp release binary
/// (whisper-cli + its ggml/whisper libraries) and the pinned ggml model are fetched in the
/// BACKGROUND into <c>Runtime/whisper/{bin,models}</c> and SHA256-verified against the table
/// below, so /voice returns instantly and listening starts automatically when assets land.
/// Subsequent starts are instant (assets cached on disk). Follows the CliProxyAssets /
/// MonacoAssets precedent.
/// </summary>
internal static class WhisperAssets
{
    /// <summary>The pinned upstream whisper.cpp release tag.</summary>
    public const string Version = "1.9.1";

    private const string ReleaseBase = "https://github.com/ggml-org/whisper.cpp/releases/download";

    /// <summary>Pinned quantized English base model (~57MB): the best latency/accuracy trade for
    /// live dictation on CPU. Fetched from the official ggerganov/whisper.cpp HF mirror.</summary>
    public const string ModelFileName = "ggml-base.en-q5_1.bin";
    private const string ModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelFileName;
    private const string ModelSha256 =
        "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f";

    /// <summary>A single resolved release artifact for one runtime identifier.</summary>
    public sealed record Asset(string Rid, string FileName, string Sha256, bool IsZip)
    {
        public string Url => $"{ReleaseBase}/v{Version}/{FileName}";
    }

    // Pinned v1.9.1 artifacts. SHA256 verified against the GitHub Releases API on 2026-07-02.
    // No prebuilt macOS CLI asset is published (xcframework only), so /voice is win/linux for now.
    private static readonly IReadOnlyList<Asset> All = new[]
    {
        new Asset("win-x64",    "whisper-bin-x64.zip",            "7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539", IsZip: true),
        new Asset("linux-x64",  "whisper-bin-ubuntu-x64.tar.gz",  "f3bf3b4369a99b54665b0f19b88483b30de27f25963b0414235dea03198515c5", IsZip: false),
        new Asset("linux-arm64","whisper-bin-ubuntu-arm64.tar.gz","e0b66cd551ff6f2a28fabe3c6e89691eea037bb76833493abb9a71ca788994b3", IsZip: false),
    };

    public static Asset? ForRid(string rid) =>
        All.FirstOrDefault(a => string.Equals(a.Rid, rid, StringComparison.OrdinalIgnoreCase));

    public static Asset? ForCurrent()
    {
        var rid = Proxy.CliProxyAssets.CurrentRid();
        return rid is null ? null : ForRid(rid);
    }

    /// <summary>All pinned artifacts (for enumeration/tests).</summary>
    public static IReadOnlyList<Asset> Artifacts => All;

    public static string RootDir   => Path.Combine(PlatformContext.BaseDirectory, "Runtime", "whisper");
    public static string BinDir    => Path.Combine(RootDir, "bin");
    public static string ModelsDir => Path.Combine(RootDir, "models");

    public static string CliName =>
        PlatformContext.IsWindows ? "whisper-cli.exe" : "whisper-cli";
    public static string CliPath   => Path.Combine(BinDir, CliName);
    public static string ModelPath => Path.Combine(ModelsDir, ModelFileName);

    /// <summary>True when both the CLI binary and the model are present on disk.</summary>
    public static bool IsProvisioned => File.Exists(CliPath) && File.Exists(ModelPath);

    private static Task? _ensureTask;
    private static readonly object EnsureLock = new();

    public static string? LastError { get; private set; }

    /// <summary>
    /// Kick off (or join) the background provisioning of binary + model. Returns the shared task;
    /// completes immediately when already provisioned. Never throws to the caller - failures set
    /// <see cref="LastError"/> and surface via the returned task's status.
    /// </summary>
    public static Task EnsureAsync()
    {
        lock (EnsureLock)
        {
            if (IsProvisioned) return Task.CompletedTask;
            if (_ensureTask is { IsCompleted: false }) return _ensureTask;
            LastError = null;
            _ensureTask = Task.Run(DownloadAllAsync);
            return _ensureTask;
        }
    }

    private static async Task DownloadAllAsync()
    {
        try
        {
            var asset = ForCurrent();
            if (asset is null)
                throw new PlatformNotSupportedException(
                    "no pinned whisper.cpp binary for this OS/arch (win-x64/linux-x64/linux-arm64 only)");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("mux-swarm-voice-fetch");

            if (!File.Exists(CliPath))
                await DownloadBinaryAsync(http, asset);
            if (!File.Exists(ModelPath))
                await DownloadModelAsync(http);

            MuxConsole.WriteSuccess("[voice] Whisper assets ready.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            MuxConsole.WriteWarning($"[voice] Whisper asset download failed: {ex.Message}");
            throw;
        }
    }

    private static async Task DownloadBinaryAsync(HttpClient http, Asset asset)
    {
        MuxConsole.WriteInfo($"[voice] Fetching whisper.cpp v{Version} ({asset.FileName}) in background...");
        var tmp = Path.Combine(Path.GetTempPath(), $"whisper-{Version}-{Guid.NewGuid():N}{(asset.IsZip ? ".zip" : ".tar.gz")}");
        try
        {
            await using (var net = await http.GetStreamAsync(asset.Url))
            await using (var fs = File.Create(tmp))
                await net.CopyToAsync(fs);

            VerifySha256(tmp, asset.Sha256, asset.FileName);

            Directory.CreateDirectory(BinDir);
            if (asset.IsZip) ExtractZip(tmp);
            else await ExtractTarGzAsync(tmp);

            if (!File.Exists(CliPath))
                throw new InvalidOperationException($"archive did not contain {CliName}");

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try { File.SetUnixFileMode(CliPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // Windows zip layout: Release/whisper-cli.exe + Release/*.dll. Extract only the CLI and its
    // libraries (skip the demo/test/SDL binaries) flattened into bin/.
    private static void ExtractZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (name.Length == 0) continue;
            if (!WantedBinFile(name)) continue;
            var dest = Path.GetFullPath(Path.Combine(BinDir, name));
            if (!dest.StartsWith(Path.GetFullPath(BinDir), StringComparison.Ordinal)) continue;
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    // Ubuntu tar.gz layout: build/bin/whisper-cli + build/src/lib*.so etc. Flatten wanted files.
    private static async Task ExtractTarGzAsync(string tgzPath)
    {
        await using var fs = File.OpenRead(tgzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await using var tar = new System.Formats.Tar.TarReader(gz);
        System.Formats.Tar.TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync()) is not null)
        {
            if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile))
                continue;
            var name = Path.GetFileName(entry.Name.Replace('\\', '/'));
            if (name.Length == 0 || !WantedBinFile(name)) continue;
            var dest = Path.GetFullPath(Path.Combine(BinDir, name));
            if (!dest.StartsWith(Path.GetFullPath(BinDir), StringComparison.Ordinal)) continue;
            await entry.ExtractToFileAsync(dest, overwrite: true);
        }
    }

    /// <summary>The CLI itself plus any whisper/ggml shared library; nothing else from the archive.</summary>
    internal static bool WantedBinFile(string fileName)
    {
        if (string.Equals(fileName, CliName, StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName is "whisper-cli" or "whisper-cli.exe") return true;
        bool lib = fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(".so", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase);
        if (!lib) return false;
        return fileName.StartsWith("ggml", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("whisper", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("libggml", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("libwhisper", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadModelAsync(HttpClient http)
    {
        MuxConsole.WriteInfo($"[voice] Fetching speech model {ModelFileName} (~57MB) in background...");
        Directory.CreateDirectory(ModelsDir);
        var tmp = ModelPath + ".part";
        try
        {
            await using (var net = await http.GetStreamAsync(ModelUrl))
            await using (var fs = File.Create(tmp))
                await net.CopyToAsync(fs);

            VerifySha256(tmp, ModelSha256, ModelFileName);
            File.Move(tmp, ModelPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static void VerifySha256(string path, string expected, string label)
    {
        using var fs = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SHA256 mismatch for {label}: expected {expected}, got {hash}");
    }
}
