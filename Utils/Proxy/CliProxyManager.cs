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
    private static int _runningPort;
    private static string? _apiKey;       // client bearer for /v1
    private static string? _mgmtKey;      // management bearer for /v0/management
    private static HttpClient? _client;

    /// <summary>
    /// True if THIS process currently considers a sidecar to be up on the chosen port. Note the sidecar is
    /// deliberately DETACHED (survives Mux exit), so "running" is determined by a port/health probe rather
    /// than by owning a child Process handle: a proxy started by a previous Mux session counts as running.
    /// </summary>
    public static bool IsRunning => _runningPort > 0;

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

    /// <summary>Path to the persisted client api-key (loopback bearer). Stable across restarts.</summary>
    private static string ClientKeyPath => Path.Combine(ConfigDir, "client.key");

    /// <summary>Path to the persisted management secret-key. Stable across restarts.</summary>
    private static string MgmtKeyPath => Path.Combine(ConfigDir, "mgmt.key");

    /// <summary>Env var the registered provider's apiKeyEnvVar points at; set at spawn/adopt time.</summary>
    public const string ClientKeyEnvVar = "MUX_CLIPROXY_KEY";

    /// <summary>
    /// Loads a persisted secret from <paramref name="path"/>, generating + writing one on first use. Keys
    /// are stable across restarts so a provider entry (and a sidecar from a prior session) keep working.
    /// </summary>
    private static string LoadOrCreateKey(string path, string prefix)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length > 0) return existing;
            }
        }
        catch { /* fall through to regenerate */ }

        var key = prefix + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(path, key);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
        }
        catch { /* best effort; in-memory key still works for this session */ }
        return key;
    }

    private static void EnsureKeysLoaded()
    {
        _apiKey ??= LoadOrCreateKey(ClientKeyPath, "mux-");
        _mgmtKey ??= LoadOrCreateKey(MgmtKeyPath, "mux-mgmt-");
        // Expose the client key to the OpenAI provider path via env var (apiKeyEnvVar resolution).
        Environment.SetEnvironmentVariable(ClientKeyEnvVar, _apiKey);
    }

    /// <summary>
    /// Export the persisted loopback bearer key to <see cref="ClientKeyEnvVar"/> WITHOUT spawning or
    /// contacting the sidecar. Cheap, disk-only, safe to call at startup so the cliproxy provider's
    /// apiKeyEnvVar always resolves (the actual sidecar is still started lazily on first request).
    /// This prevents a spurious "API key env var not set" warning on every launch after /login.
    /// </summary>
    public static void ExportPersistedKey()
    {
        try { EnsureKeysLoaded(); }
        catch { /* best-effort; lazy EnsureRunningAsync will set it later */ }
    }

    /// <summary>
    /// Ensures the sidecar binary is present AND a server instance is reachable, returning the loopback
    /// OpenAI-compatible endpoint. The sidecar is DETACHED so it outlives Mux: on entry we first probe the
    /// fixed <see cref="PreferredPort"/> and ADOPT a healthy instance (possibly started by a prior Mux
    /// session) with zero spawn. Only if nothing healthy is listening do we spawn a new detached process
    /// (Windows: CREATE_BREAKAWAY_FROM_JOB so it escapes Mux's kill-on-close Job Object; Unix: setsid so it
    /// leaves Mux's process group and survives the group SIGTERM). Lazy: only called on first
    /// subscription-provider use.
    /// </summary>
    public static async Task<string> EnsureRunningAsync(CancellationToken ct = default)
    {
        await EnsureBinaryAsync(ct).ConfigureAwait(false);
        EnsureKeysLoaded();

        // Fast path: we already adopted/started one this session and it is still healthy.
        if (IsRunning && await IsHealthyAsync(ct).ConfigureAwait(false))
            return OpenAiEndpoint!;

        await RunGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning && await IsHealthyAsync(ct).ConfigureAwait(false))
                return OpenAiEndpoint!;

            // Adopt-by-port: a detached sidecar from a previous session may already be serving the fixed
            // port. If it answers /v1/models with our persisted key, reuse it — no spawn, minimal latency.
            if (await IsPortHealthyAsync(PreferredPort, ct).ConfigureAwait(false))
            {
                _runningPort = PreferredPort;
                return OpenAiEndpoint!;
            }

            int port = PortIsFree(PreferredPort) ? PreferredPort : FindFreePort();

            Directory.CreateDirectory(AuthDir);
            await File.WriteAllTextAsync(ConfigPath, BuildConfigYaml(port, _apiKey!, _mgmtKey!, AuthDir), ct)
                .ConfigureAwait(false);

            SpawnDetached(port);
            _runningPort = port;

            bool healthy = await WaitForHealthyAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (!healthy)
            {
                _runningPort = 0;
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

    /// <summary>
    /// Spawns the proxy DETACHED from Mux's lifecycle so it survives Mux exit. Windows uses CreateProcess
    /// with CREATE_BREAKAWAY_FROM_JOB (escapes the kill-on-job-close Job Object) + CREATE_NO_WINDOW; if
    /// breakaway is denied it falls back to a normal spawn (then it dies with Mux and is simply re-spawned
    /// next launch). Unix launches via `setsid` to start a new session/process-group so Mux's shutdown
    /// `kill -TERM -&lt;pgid&gt;` group sweep cannot reach it; stdio is redirected to the null device.
    /// </summary>
    private static void SpawnDetached(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            if (WindowsProcess.TryCreateBreakaway(ExecutablePath, $"-config \"{ConfigPath}\"", InstallDir))
                return;
            // Fallback: normal (job-bound) spawn — survives only for this session.
            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = InstallDir,
            };
            psi.ArgumentList.Add("-config");
            psi.ArgumentList.Add(ConfigPath);
            Process.Start(psi);
            return;
        }

        // Unix: setsid <exe> -config <cfg>, stdio -> /dev/null, new session => detached from Mux's group.
        // The stdio redirect is DONE VIA THE SHELL (</dev/null >/dev/null 2>&1), not .NET pipes: the sidecar
        // is detached and must OUTLIVE Mux, so a .NET-redirected pipe would break on Mux exit (SIGPIPE/block)
        // and would need a live drain thread. Without this redirect the Go sidecar INHERITS Mux's TTY and its
        // logs (gin access lines, auth-file-watch events) bleed straight into the alt-screen TUI viewport.
        // setsid only detaches the controlling terminal for SIGNAL purposes; it does NOT reassign fd 0/1/2,
        // which still point at the inherited tty. Positional args ($0/$1) keep paths-with-spaces injection-safe.
        var upsi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            WorkingDirectory = InstallDir,
        };
        upsi.ArgumentList.Add("-c");
        upsi.ArgumentList.Add("setsid \"$0\" -config \"$1\" </dev/null >/dev/null 2>&1");
        upsi.ArgumentList.Add(ExecutablePath);
        upsi.ArgumentList.Add(ConfigPath);
        try
        {
            Process.Start(upsi); // NOT Track()ed: must survive Mux exit
        }
        catch
        {
            // setsid/sh missing (rare): fall back to a plain spawn without tracking. It may still be swept by
            // the group SIGTERM, but will be re-spawned on next launch. Redirect stdio to the null device so
            // its logs never leak into the TUI viewport even on this degraded path.
            var fpsi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                WorkingDirectory = InstallDir,
            };
            fpsi.ArgumentList.Add("-c");
            fpsi.ArgumentList.Add("exec \"$0\" -config \"$1\" </dev/null >/dev/null 2>&1");
            fpsi.ArgumentList.Add(ExecutablePath);
            fpsi.ArgumentList.Add(ConfigPath);
            Process.Start(fpsi);
        }
    }

    /// <summary>
    /// Explicitly stops the sidecar (used by `/proxy` and tests). Because the proxy is detached we do NOT
    /// own a child handle, so we terminate whatever process is listening on our port. NOT called on Mux
    /// exit — the sidecar is meant to persist for future sessions.
    /// </summary>
    public static void Stop()
    {
        RunGate.Wait();
        try { StopInternal(); }
        finally { RunGate.Release(); }
    }

    private static void StopInternal()
    {
        int port = _runningPort;
        _runningPort = 0;
        if (port <= 0) return;
        try { KillListenerOnPort(port); } catch { }
    }

    /// <summary>
    /// Test hook: clears this process's in-memory "running" state WITHOUT stopping the detached proxy, to
    /// simulate a fresh Mux session for the adopt-by-port path. Does not touch persisted keys.
    /// </summary>
    internal static void ResetSessionStateForTests() => _runningPort = 0;

    // ───────────────────────── Slice 5/2.6: login + /proxy surface ─────────────────────────

    /// <summary>
    /// Subscription providers whose OAuth login CLIProxyAPI performs natively, mapped to the binary's login
    /// flag (from `cli-proxy-api --help`). The proxy opens the browser, runs the PKCE flow, and writes the
    /// token into <see cref="AuthDir"/>; the running server hot-reloads the new credential.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LoginProviders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = "-claude-login",
            ["codex"] = "-codex-login",
            ["kimi"] = "-kimi-login",
            ["antigravity"] = "-antigravity-login",
            ["xai"] = "-xai-login",
            ["codex-device"] = "-codex-device-login",
        };

    /// <summary>
    /// Heuristic: does the current process look like it is running on a headless / remote host where
    /// auto-opening a desktop browser for OAuth would fail (SSH session, cloud VPS, no display server)?
    /// Windows desktops always report false; on Unix we treat an SSH connection or a missing DISPLAY/
    /// WAYLAND_DISPLAY as headless. Callers use this only to pick a sensible DEFAULT for the login mode;
    /// the user can always override. Never throws.
    /// </summary>
    public static bool LooksHeadless()
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY")))
                return true;
            // On Linux/macOS a missing display server means no browser can be launched.
            if (!OperatingSystem.IsWindows())
            {
                bool hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
                if (!hasDisplay) return true;
            }
        }
        catch { /* env probing is best-effort */ }
        return false;
    }

    /// <summary>
    /// Runs CLIProxyAPI's native OAuth login for <paramref name="provider"/>. Ensures the binary +
    /// server are up first (so the config/auth-dir exist and the server can hot-reload the new token), then
    /// launches `cli-proxy-api -config &lt;cfg&gt; -&lt;provider&gt;-login`. In the default (headful) mode the
    /// proxy auto-opens the local browser; when <paramref name="noBrowser"/> is set the `-no-browser` flag is
    /// passed so it PRINTS the auth URL instead - the flow needed on a headless / remote VPS host, where the
    /// user opens the URL on another machine and the OAuth callback completes out-of-band. Inherits the
    /// console so the flow is interactive. Returns true on a zero exit code. Throws for an unknown provider.
    /// </summary>
    public static async Task<bool> LoginAsync(string provider, CancellationToken ct = default, bool noBrowser = false)
    {
        if (!LoginProviders.TryGetValue(provider, out var flag))
            throw new ArgumentException(
                $"CLIProxyAPI has no native login for '{provider}'. Supported: {string.Join(", ", LoginProviders.Keys)}.");

        await EnsureRunningAsync(ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,   // inherit console for the interactive login flow
            WorkingDirectory = InstallDir,
        };
        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(ConfigPath);
        psi.ArgumentList.Add(flag);

        if (!noBrowser)
        {
            // Headful (local desktop): the child auto-opens the browser and completes the OAuth via a
            // loopback callback. It needs no console I/O of its own, so let it inherit ours as before.
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the CLIProxyAPI login process.");
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }

        // Headless (remote / VPS / SSH): pass -no-browser so the child PRINTS the auth URL and then
        // waits on its loopback callback (redirect_uri=http://localhost:<port>/callback) for the user to
        // complete the login from a browser on another machine (tunnelling that port if needed). The
        // child performs NO stdin reads in this flow. We must NOT let it inherit the live TUI's raw
        // console: (a) the ~100ms live-region repaint ticker interleaves with the child's raw stdout and
        // TRUNCATES the long auth URL (the "code_challenge_meth9" clip), and (b) leftover raw-mode input
        // bytes (bracketed-paste markers, a stray Enter) reach the child's inherited stdin and abort it
        // early ("Exiting..."). So instead we REDIRECT the child's stdout/stderr and re-emit each line
        // through MuxConsole (which commits cleanly above the live footer, URL intact), and hand it a
        // closed stdin so no stray console bytes can reach it. Verified against the bundled 7.2.44 binary:
        // with a closed stdin it prints the full URL and waits for the callback.
        psi.ArgumentList.Add("-no-browser");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
        psi.StandardErrorEncoding = new System.Text.UTF8Encoding(false);

        using (var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the CLIProxyAPI login process."))
        {
            // Close the child's stdin immediately: the loopback flow reads none, and a closed handle
            // guarantees no stray raw-console bytes can be delivered to it.
            try { proc.StandardInput.Close(); } catch { /* best-effort */ }

            void Pump(System.IO.TextReader reader, bool err)
            {
                try
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // Surface every child line intact through the console sink so the URL + the
                        // "Waiting for callback..." / success lines render above the live footer.
                        if (err) MuxConsole.WriteMuted(line);
                        else MuxConsole.WriteInfo(line);
                    }
                }
                catch { /* stream closed on exit */ }
            }

            var outPump = new Thread(() => Pump(proc.StandardOutput, err: false)) { IsBackground = true, Name = "CliProxyLogin-stdout" };
            var errPump = new Thread(() => Pump(proc.StandardError, err: true)) { IsBackground = true, Name = "CliProxyLogin-stderr" };
            outPump.Start();
            errPump.Start();

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            // Let the pumps drain any final buffered lines before returning.
            outPump.Join(TimeSpan.FromSeconds(2));
            errPump.Join(TimeSpan.FromSeconds(2));
            return proc.ExitCode == 0;
        }
    }

    /// <summary>
    /// Force a cold restart of the detached sidecar: stop whatever is listening on our port, then spawn a
    /// fresh instance. Unlike relying on CLIProxyAPI's in-process fsnotify auth-watcher (which is known to
    /// occasionally miss a credential rewrite - upstream #1508/#2556, leaving the server serving a stale or
    /// dropped token), a cold start re-reads the auth-dir from scratch. This is the automated form of the
    /// manual "kill the proxy process and let Mux re-spawn it" recovery. Bounded + best-effort; never throws.
    /// Uses RunGate (a SemaphoreSlim) rather than a lock so it is safe to hold across the spawn/health awaits.
    /// </summary>
    public static async Task<bool> RecycleAsync(CancellationToken ct = default)
    {
        await EnsureBinaryAsync(ct).ConfigureAwait(false);
        EnsureKeysLoaded();
        await RunGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StopInternal();   // kills the listener on our tracked port + clears _runningPort

            // Reclaim the fixed PreferredPort: a foreign/orphaned sidecar (a prior crashed session, or one
            // StopInternal could not see because we were not tracking its port) may still be squatting on it.
            // Without this, PortIsFree(PreferredPort) is false and we would silently fall back to a RANDOM
            // free port - which then forces the user to hand-edit config.yaml. /proxy restart must instead
            // kill whatever holds the port and PIN back to PreferredPort. Random fallback only if that fails.
            int port = AcquirePreferredPort();
            Directory.CreateDirectory(AuthDir);
            await File.WriteAllTextAsync(ConfigPath, BuildConfigYaml(port, _apiKey!, _mgmtKey!, AuthDir), ct)
                .ConfigureAwait(false);

            SpawnDetached(port);
            _runningPort = port;

            bool healthy = await WaitForHealthyAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (!healthy) { _runningPort = 0; return false; }
            return true;
        }
        catch { return false; }
        finally { RunGate.Release(); }
    }

    /// <summary>
    /// After a login writes a fresh credential, confirm the sidecar is actually serving it. The reliable
    /// signal is not "did the fsnotify watcher fire" (that timing is racy - upstream #1508/#2556) but "a
    /// freshly-spawned sidecar is healthy", which guarantees it cold-read the auth-dir on startup. So:
    ///   1. A brief FAST-PATH check skips a needless restart in the benign case (e.g. logging into a second
    ///      provider while the first still works and the running sidecar hot-loaded the new token).
    ///   2. Otherwise force one <see cref="RecycleAsync"/> - which spawns a fresh process and waits for it to
    ///      become port-healthy - and only THEN poll readiness, against that known-good spawn, with a
    ///      generous window (a cold start loads auth files + may refresh tokens slightly AFTER the port goes
    ///      healthy, so the post-spawn poll must not be tight).
    /// Returns true once the provider is ready (or if readiness cannot be determined - never blocks the user
    /// on a false negative). Bounded: at most a single recycle. Never throws.
    /// </summary>
    public static async Task<bool> VerifyProviderReadyAfterLoginAsync(
        string provider, CancellationToken ct = default)
    {
        // 1. Fast path: brief check so we DON'T restart a sidecar that already loaded the credential.
        if (await PollProviderReadyAsync(provider, attempts: 4, delayMs: 500, ct).ConfigureAwait(false))
            return true;

        // 2. Cold-recycle so the sidecar re-reads the auth-dir from a fresh spawn. RecycleAsync internally
        //    waits (up to 20s) for the new process to become port-healthy, so the poll below runs AFTER the
        //    process has spawned and is serving - the point at which a readiness check is actually meaningful.
        MuxConsole.WriteMuted("[cliproxy] New credential not live yet; refreshing the sidecar to pick it up...");
        if (!await RecycleAsync(ct).ConfigureAwait(false)) return false;

        // 3. Generous post-spawn window: auth-file load / token refresh can lag the port becoming healthy.
        return await PollProviderReadyAsync(provider, attempts: 20, delayMs: 500, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls <see cref="IsProviderReadyAsync"/> up to <paramref name="attempts"/> times (with
    /// <paramref name="delayMs"/> between). Returns true as soon as the provider reports ready. A management
    /// API hiccup (throw) is treated as "cannot determine" and returns true so a false negative never blocks
    /// the user. Best-effort; never throws.
    /// </summary>
    private static async Task<bool> PollProviderReadyAsync(
        string provider, int attempts, int delayMs, CancellationToken ct)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { if (await IsProviderReadyAsync(provider, ct).ConfigureAwait(false)) return true; }
            catch { return true; /* cannot verify - do NOT punish the user with a false negative */ }
            if (i < attempts - 1)
                try { await Task.Delay(delayMs, ct).ConfigureAwait(false); } catch { return true; }
        }
        return false;
    }

    /// <summary>The pinned proxy version this build expects (mirrors <see cref="CliProxyAssets.Version"/>).</summary>
    public static string PinnedVersion => CliProxyAssets.Version;

    /// <summary>
    /// Re-provisions the pinned binary: removes the installed copy and re-downloads + verifies it, then (if a
    /// sidecar was running) restarts it so the new binary takes effect. Used by `/proxy update`.
    /// </summary>
    public static async Task UpdateAsync(CancellationToken ct = default)
    {
        bool wasRunning = IsRunning;
        Stop();
        try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true); } catch { }
        await EnsureBinaryAsync(ct).ConfigureAwait(false);
        if (wasRunning)
            await EnsureRunningAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Lists the model ids the running sidecar currently serves (GET /v1/models). Returns an empty list
    /// if the sidecar is not running or the call fails. Used by setup to derive real default model ids for
    /// the just-logged-in subscription provider instead of guessing from the loopback endpoint URL.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        if (!IsRunning || BaseUrl is null) return [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            using var resp = await Http().SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseModelIds(json);
        }
        catch { return []; }
    }

    /// <summary>
    /// Probes an arbitrary OpenAI-compatible endpoint for its model ids (GET {endpoint}/models with an
    /// optional bearer key). Best-effort: returns an empty list on any failure. Used by the manual
    /// endpoint+key setup path so we can offer real default model ids there too.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ProbeEndpointModelsAsync(string endpoint, string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return [];
        try
        {
            var url = endpoint.TrimEnd('/') + "/models";
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            using var resp = await probe.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseModelIds(json);
        }
        catch { return []; }
    }

    /// <summary>Parses an OpenAI /v1/models response body into the list of model id strings.</summary>
    private static IReadOnlyList<string> ParseModelIds(string json)
    {
        var ids = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var arr) &&
                arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var id = GetStr(el, "id");
                    if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                }
            }
        }
        catch { /* malformed - return what we have */ }
        return ids;
    }

    // ───────────────────────── health + ports + config ─────────────────────────

    private static HttpClient Http() => _client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task<bool> WaitForHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            // Detached spawn: we don't hold a child handle, so health is polled purely via the port.
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

    /// <summary>
    /// Probes a specific port for a healthy CLIProxyAPI answering /v1/models with our persisted client key.
    /// Used to ADOPT a detached sidecar started by a previous Mux session (reuse, no spawn). A short timeout
    /// keeps the no-survivor case cheap.
    /// </summary>
    private static async Task<bool> IsPortHealthyAsync(int port, CancellationToken ct)
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/v1/models");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            using var resp = await probe.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Terminates the process listening on <paramref name="port"/>. Since the sidecar is detached we don't
    /// hold its handle; we resolve the owning PID from the OS (Windows: netstat; Unix: lsof/fuser) and kill
    /// its tree. Best-effort.
    /// </summary>
    private static void KillListenerOnPort(int port)
    {
        foreach (int pid in ListenerPids(port))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
            }
            catch { /* already gone / access — best effort */ }
        }
    }

    /// <summary>Resolves PIDs listening on a loopback port via OS tooling. Empty if none/unsupported.</summary>
    private static IEnumerable<int> ListenerPids(int port)
    {
        var pids = new HashSet<int>();
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("netstat", "-ano")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc = Process.Start(psi)!;
                string outp = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                foreach (var line in outp.Split('\n'))
                {
                    // e.g.  TCP    127.0.0.1:49317   0.0.0.0:0   LISTENING   12345
                    if (!line.Contains("LISTENING")) continue;
                    if (!line.Contains($":{port} ") && !line.Contains($":{port}\t")) continue;
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && int.TryParse(parts[^1], out int pid)) pids.Add(pid);
                }
            }
            else
            {
                // lsof -t -iTCP:<port> -sTCP:LISTEN  -> one PID per line
                psi = new ProcessStartInfo("lsof", $"-t -iTCP:{port} -sTCP:LISTEN")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc = Process.Start(psi)!;
                string outp = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                foreach (var line in outp.Split('\n'))
                    if (int.TryParse(line.Trim(), out int pid)) pids.Add(pid);
            }
        }
        catch { /* tooling missing — nothing to kill */ }
        return pids;
    }

    internal static bool PortIsFree(int port)
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
    /// Returns <see cref="PreferredPort"/>, first evicting anything already listening on it (a foreign or
    /// orphaned cli-proxy-api from a prior/crashed session that our port-scoped StopInternal never saw). This
    /// makes /proxy restart deterministic: kill the squatter, then PIN back to the config port instead of
    /// silently drifting to a random port (which forces a manual config.yaml edit). Falls back to a random
    /// free port only if the port genuinely cannot be reclaimed (e.g. a foreign owner we lack rights to kill).
    /// </summary>
    internal static int AcquirePreferredPort()
    {
        if (PortIsFree(PreferredPort)) return PreferredPort;

        try { KillListenerOnPort(PreferredPort); } catch { /* best effort */ }

        // The OS releases the port asynchronously after the owning process dies; poll briefly before giving up.
        for (int i = 0; i < 20; i++)   // up to ~2s
        {
            if (PortIsFree(PreferredPort)) return PreferredPort;
            Thread.Sleep(100);
        }

        // Could not reclaim it (foreign owner, no rights, or a lingering socket): don't hang the user - use a
        // free port so the sidecar still starts. Config.yaml is rewritten to match, so no manual edit needed.
        return PortIsFree(PreferredPort) ? PreferredPort : FindFreePort();
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

/// <summary>
/// Windows-only P/Invoke helper to launch a process that BREAKS AWAY from the parent's Job Object, so a
/// deliberately persistent sidecar survives Mux exit (Mux's job is created with BREAKAWAY_OK). Uses raw
/// CreateProcess because System.Diagnostics.Process offers no breakaway-flag knob.
/// </summary>
internal static partial class WindowsProcess
{
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint DETACHED_PROCESS = 0x00000008;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public uint cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Launches <paramref name="exePath"/> <paramref name="args"/> detached from the parent's Job Object.
    /// Returns true on success; false if CreateProcess fails (caller falls back to a normal spawn).
    /// </summary>
    public static bool TryCreateBreakaway(string exePath, string args, string workingDir)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var si = new STARTUPINFO { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<STARTUPINFO>() };
        // Quote the exe path; args already quoted by caller.
        string cmd = $"\"{exePath}\" {args}";
        uint flags = CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW | DETACHED_PROCESS;
        try
        {
            bool ok = CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                flags, IntPtr.Zero, workingDir, ref si, out var pi);
            if (!ok) return false;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        catch { return false; }
    }
}
