using System.Formats.Tar;
using System.IO.Compression;

namespace MuxSwarm.Utils;

/// <summary>
/// First-startup provisioner for the Monaco editor asset tree that backs the
/// in-browser IDE pane. The ~13 MB minified bundle is NOT vendored in the repo;
/// it is fetched once from the npm registry and cached on disk under
/// <c>&lt;wwwroot&gt;/monaco</c>, served as ordinary static files thereafter.
///
/// The fetch runs in the background and never blocks serve startup. If the
/// assets are already present, or auto-fetch is disabled, this is a no-op. On
/// any failure the IDE pane simply shows its existing "failed to load editor
/// assets" state; nothing else in the runtime is affected.
/// </summary>
internal static class MonacoAssets
{
    // Resolved AMD module loader is served at /monaco/vs/loader.js by index.html.
    private const string MarkerRelPath = "monaco/vs/loader.js";

    /// <summary>
    /// Kicks off a background fetch of the Monaco bundle into
    /// <paramref name="wwwroot"/>/monaco when needed. Returns immediately; the
    /// download (if any) completes asynchronously. Safe to call with a null or
    /// missing wwwroot (no-op).
    /// </summary>
    public static void EnsureInBackground(string? wwwroot, ServeEditorConfig config)
    {
        if (!config.AutoFetch) return;
        if (string.IsNullOrEmpty(wwwroot)) return;

        var monacoDir = Path.Combine(wwwroot, "monaco");
        var marker = Path.Combine(wwwroot, MarkerRelPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(marker)) return; // already provisioned

        var version = string.IsNullOrWhiteSpace(config.Version) ? "0.52.2" : config.Version.Trim();

        _ = Task.Run(async () =>
        {
            try
            {
                MuxConsole.WriteInfo($"Fetching Monaco editor assets (v{version}) in background...");
                await DownloadAndExtractAsync(version, monacoDir);
                if (File.Exists(marker))
                    MuxConsole.WriteSuccess("Monaco editor assets ready.");
                else
                    MuxConsole.WriteWarning("Monaco fetch completed but loader.js was not found; editor pane may be unavailable.");
            }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"Monaco editor assets unavailable ({ex.Message}); IDE pane will be disabled until provisioned.");
            }
        });
    }

    /// <summary>
    /// Downloads the pinned monaco-editor npm tarball and extracts its minified
    /// <c>package/min/vs</c> tree into <paramref name="monacoDir"/>/vs. Extraction
    /// is staged into a temp dir then atomically swapped so a partial download can
    /// never leave a half-populated asset tree behind.
    /// </summary>
    private static async Task DownloadAndExtractAsync(string version, string monacoDir)
    {
        var url = $"https://registry.npmjs.org/monaco-editor/-/monaco-editor-{version}.tgz";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("mux-swarm-monaco-fetch");

        await using var net = await http.GetStreamAsync(url);
        // Buffer to a temp file first so the gzip/tar reader gets a seekable, complete stream.
        var tmpTgz = Path.Combine(Path.GetTempPath(), $"monaco-{version}-{Guid.NewGuid():N}.tgz");
        var stageDir = Path.Combine(Path.GetTempPath(), $"monaco-stage-{Guid.NewGuid():N}");

        try
        {
            await using (var fs = File.Create(tmpTgz))
                await net.CopyToAsync(fs);

            Directory.CreateDirectory(stageDir);

            // Extract only package/min/vs/** -> stageDir/vs/**
            const string wanted = "package/min/vs/";
            await using (var tgz = File.OpenRead(tmpTgz))
            await using (var gz = new GZipStream(tgz, CompressionMode.Decompress))
            await using (var tar = new TarReader(gz))
            {
                TarEntry? entry;
                while ((entry = await tar.GetNextEntryAsync()) is not null)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        continue;

                    var name = entry.Name.Replace('\\', '/');
                    if (!name.StartsWith(wanted, StringComparison.Ordinal)) continue;

                    var rel = name.Substring("package/min/".Length); // -> vs/...
                    var dest = Path.GetFullPath(Path.Combine(stageDir, rel.Replace('/', Path.DirectorySeparatorChar)));

                    // Defense-in-depth against path traversal in archive entries.
                    var stageFull = Path.GetFullPath(stageDir) + Path.DirectorySeparatorChar;
                    if (!dest.StartsWith(stageFull, StringComparison.Ordinal)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await entry.ExtractToFileAsync(dest, overwrite: true);
                }
            }

            var stagedVs = Path.Combine(stageDir, "vs");
            if (!Directory.Exists(stagedVs))
                throw new InvalidOperationException("tarball did not contain package/min/vs");

            // Swap into place: monacoDir/vs.
            Directory.CreateDirectory(monacoDir);
            var finalVs = Path.Combine(monacoDir, "vs");
            if (Directory.Exists(finalVs))
            {
                var old = finalVs + ".old-" + Guid.NewGuid().ToString("N");
                Directory.Move(finalVs, old);
                try { Directory.Delete(old, recursive: true); } catch { /* best effort */ }
            }
            Directory.Move(stagedVs, finalVs);
        }
        finally
        {
            try { if (File.Exists(tmpTgz)) File.Delete(tmpTgz); } catch { }
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true); } catch { }
        }
    }
}
