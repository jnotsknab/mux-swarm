using System.Diagnostics;
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

    // ───────────────────────── Slice 2/3: spawn + health + auth-files ─────────────────────────

    /// <summary>Preferred fixed loopback port for the managed sidecar (IANA dynamic/private range).</summary>
    public const int PreferredPort = 49317;

    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private static Process? _proc;
    private static int _runningPort;
    private static string? _apiKey;       // client bearer for /v1
    private static string? _mgmtKey;      // management bearer for /v0/management
    private static HttpClient? _client;

    /// <summary>True if the sidecar is currently spawned and tracked by this process.</summary>
    public static bool IsRunning
    {
        get
        {
            var p = _proc;
            if (p is null) return false;
            try { return !p.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>The loopback base URL ("http://127.0.0.1:&lt;port&gt;") once running; null otherwise.</summary>
    public static string? BaseUrl => _runningPort > 0 ? $"http://127.0.0.1:{_runningPort}" : null;

    /// <summary>The OpenAI-compatible endpoint ("http://127.0.0.1:&lt;port&gt;/v1") Mux's client targets.</summary>
    public static string? OpenAiEndpoint => BaseUrl is { } b ? b + "/v1" : null;

    /// <summary>The per-process client bearer to send as the OpenAI api key; null until running.</summary>
    public static string? ClientApiKey => _apiKey;

    /// <summary>Local directory holding the generated config.yaml and persistent token store (auth-dir).</summary>
    public static string ConfigDir
    {
        get
        {
            string baseDir = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share");
            return Path.Combine(baseDir, "Mux-Swarm", "cliproxy");
        }
    }

    /// <summary>Path to the generated config.yaml.</summary>
    public static string ConfigPath => Path.Combine(ConfigDir, "config.yaml");

    /// <summary>The persistent OAuth token store (survives restarts); shared across pinned versions.</summary>
    public static string AuthDir => Path.Combine(ConfigDir, "auth");

    /// <summary>
    /// Ensures the sidecar binary is present AND a managed server instance is running, returning the
    /// loopback OpenAI-compatible endpoint. Idempotent: a healthy running instance is reused. Picks the
    /// fixed <see cref="PreferredPort"/>, falling back to an ephemeral free port if taken. The child is
    /// covered by the process-wide Windows Job Object (descendants inherit kill-on-close) and Track()ed on
    /// Unix for the SIGTERM shutdown path.
    /// </summary>
    public static async Task<string> EnsureRunningAsync(CancellationToken ct = default)
    {
        await EnsureBinaryAsync(ct).ConfigureAwait(false);

        // Fast path: already healthy.
        if (IsRunning && BaseUrl is not null && await IsHealthyAsync(ct).ConfigureAwait(false))
            return OpenAiEndpoint!;

        await RunGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning && BaseUrl is not null && await IsHealthyAsync(ct).ConfigureAwait(false))
                return OpenAiEndpoint!;

            StopInternal(); // clear any dead handle

            int port = PortIsFree(PreferredPort) ? PreferredPort : FindFreePort();
            _apiKey ??= "mux-" + Guid.NewGuid().ToString("N");
            _mgmtKey ??= "mux-mgmt-" + Guid.NewGuid().ToString("N");

            Directory.CreateDirectory(AuthDir);
            await File.WriteAllTextAsync(ConfigPath, BuildConfigYaml(port, _apiKey!, _mgmtKey!, AuthDir), ct)
                .ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Do NOT redirect stdout/stderr: CLIProxyAPI logs continuously, and draining redirected
                // pipes via async readers deadlocks under a single-threaded sync context (e.g. xunit). We
                // don't consume the proxy's logs, so let them go to the (hidden) console with no pipe.
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = InstallDir,
            };
            psi.ArgumentList.Add("-config");
            psi.ArgumentList.Add(ConfigPath);

            var proc = new Process { StartInfo = psi };
            proc.Start();

            // On Unix, register for SIGTERM-on-shutdown. On Windows the self-assigned Job Object already
            // makes every descendant inherit kill-on-job-close, so no explicit Track() is required.
            if (!OperatingSystem.IsWindows())
                ProcessCleanup.Instance.Track(proc);

            _proc = proc;
            _runningPort = port;

            bool healthy = await WaitForHealthyAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (!healthy)
            {
                StopInternal();
                throw new InvalidOperationException(
                    $"CLIProxyAPI started on port {port} but did not become healthy within 20s.");
            }

            MuxConsole.WriteSuccess($"CLIProxyAPI sidecar ready on 127.0.0.1:{port}.");
            return OpenAiEndpoint!;
        }
        finally
        {
            RunGate.Release();
        }
    }

    /// <summary>Stops the managed sidecar if running. Safe to call repeatedly.</summary>
    public static void Stop()
    {
        RunGate.Wait();
        try { StopInternal(); }
        finally { RunGate.Release(); }
    }

    private static void StopInternal()
    {
        var p = _proc;
        _proc = null;
        _runningPort = 0;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }

    /// <summary>
    /// Queries the management API for the current set of authenticated provider credentials. Returns the
    /// parsed list (possibly empty) so the caller can decide whether a provider needs an interactive login.
    /// Requires the sidecar to be running.
    /// </summary>
    public static async Task<IReadOnlyList<AuthFile>> GetAuthFilesAsync(CancellationToken ct = default)
    {
        if (!IsRunning || BaseUrl is null)
            throw new InvalidOperationException("CLIProxyAPI sidecar is not running.");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v0/management/auth-files");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_mgmtKey}");
        using var resp = await Http().SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var files = new List<AuthFile>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("files", out var arr) &&
            arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                files.Add(new AuthFile(
                    Id: GetStr(el, "id"),
                    Provider: GetStr(el, "provider"),
                    Status: GetStr(el, "status"),
                    Email: GetStr(el, "email"),
                    Disabled: GetBool(el, "disabled"),
                    Unavailable: GetBool(el, "unavailable")));
            }
        }
        return files;
    }

    /// <summary>
    /// True if at least one credential for <paramref name="provider"/> is present and usable (not disabled,
    /// not unavailable, and not in an explicit error status). Provider match is case-insensitive.
    /// </summary>
    public static async Task<bool> IsProviderReadyAsync(string provider, CancellationToken ct = default)
    {
        var files = await GetAuthFilesAsync(ct).ConfigureAwait(false);
        return files.Any(f =>
            string.Equals(f.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && !f.Disabled && !f.Unavailable
            && !string.Equals(f.Status, "error", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A single authenticated-credential record from the management auth-files endpoint.</summary>
    public sealed record AuthFile(
        string Id, string Provider, string Status, string Email, bool Disabled, bool Unavailable);

    // ───────────────────────── health + ports + config ─────────────────────────

    private static HttpClient Http() => _client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task<bool> WaitForHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            if (_proc is { HasExited: true }) return false;
            if (await IsHealthyAsync(ct).ConfigureAwait(false)) return true;
            try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { return false; }
        }
        return false;
    }

    /// <summary>Health probe: GET /v1/models with the client bearer should return 200.</summary>
    private static async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        if (BaseUrl is null) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            using var resp = await Http().SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static bool PortIsFree(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start(); l.Stop();
            return true;
        }
        catch { return false; }
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Builds the minimal config.yaml: bind loopback only, the chosen port, a per-process client api-key and
    /// management secret-key (so /v0/management routes are enabled), and the persistent auth-dir. Everything
    /// else stays at upstream defaults (notably Claude cloaking ON — the proxy supplies the Claude-Code
    /// disguise + system-prompt replacement, so Mux does NOT hand-roll it).
    /// </summary>
    internal static string BuildConfigYaml(int port, string apiKey, string mgmtKey, string authDir)
    {
        string ad = authDir.Replace("\\", "/");
        return
            "host: \"127.0.0.1\"\n" +
            $"port: {port}\n" +
            $"auth-dir: \"{ad}\"\n" +
            "api-keys:\n" +
            $"  - \"{apiKey}\"\n" +
            "remote-management:\n" +
            "  allow-remote: false\n" +
            $"  secret-key: \"{mgmtKey}\"\n" +
            "debug: false\n";
    }

    private static string GetStr(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static bool GetBool(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) &&
        (v.ValueKind == System.Text.Json.JsonValueKind.True ||
         (v.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));
}
