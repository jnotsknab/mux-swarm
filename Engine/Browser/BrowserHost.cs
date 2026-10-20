using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MuxSwarm.Engine.Browser;

/// <summary>
/// The built-in browser: by default a pinned Chrome for Testing build fetched on first use into
/// <c>Runtime/browser/&lt;version&gt;</c> (SHA-256 checked), or an installed Chromium browser picked
/// with <c>serve.browser.engine</c>; launched headless with a persistent
/// profile and a loopback-only DevTools port. Shared by the web UI's Browser tab (screencast via
/// <see cref="BrowserRelay"/>) and the built-in Playwright MCP server (<c>--cdp-endpoint</c>).
/// Chrome for Testing is distributed by Google and downloaded from Google's bucket at runtime; Mux
/// does not redistribute it.
/// </summary>
internal static class BrowserHost
{
    /// <summary>Pinned Chrome for Testing version (Stable channel).</summary>
    internal const string Version = "155.0.8059.84";

    /// <summary>SHA-256 of each platform's <c>chrome-&lt;platform&gt;.zip</c> for <see cref="Version"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Sha256 = new Dictionary<string, string>
    {
        ["win64"] = "507F2A031441D10C62B5E3D4E863F09AC1198A571C1419976CBD8387F639AFDE",
        ["mac-arm64"] = "13B9E98CE56F5CE18E100A009F455C9285F399E7C9DC8A76FDF2F3A9755BB5F2",
        ["mac-x64"] = "D805A173118DAA5F6F5CF464500789E1E00022A9A6AED8ABFEC821AB828FA862",
        ["linux64"] = "CF0F049892DEA22E6A6DB26D47ED0D7776B7158EDCBEC4634086633DF8BAFCBC",
        ["linux-arm64"] = "1692A407908867DE88EB1F1B2A93CA47C348D8FB2E3C04752D966E0F2E0344B4",
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _proc;
    private static int _port;
    private static string _status = "idle";   // idle|downloading|starting|ready|error|unsupported
    private static string? _error;
    private static long _got, _total;

    /// <summary>Chrome for Testing platform id for this OS/arch, or null when none is published.</summary>
    internal static string? Platform() => (RuntimeInformation.IsOSPlatform(OSPlatform.Windows), RuntimeInformation.IsOSPlatform(OSPlatform.OSX), RuntimeInformation.OSArchitecture) switch
    {
        (true, _, Architecture.X64) => "win64",
        (_, true, Architecture.Arm64) => "mac-arm64",
        (_, true, Architecture.X64) => "mac-x64",
        (false, false, Architecture.X64) => "linux64",
        (false, false, Architecture.Arm64) => "linux-arm64",
        _ => null,
    };

    /// <summary>DevTools port chosen once per engine run, so Playwright can be pointed at it before Chrome starts.</summary>
    internal static readonly int ReservedPort = FreePort();

    /// <summary>True when the built-in Playwright server is routed to this browser (set at MCP connect).</summary>
    internal static bool SharedWithPlaywright;

    internal static string InstallDir => Path.Combine(PlatformContext.BaseDirectory, "Runtime", "browser", Version);

    /// <summary>Values of <c>serve.browser.engine</c>: <c>managed</c> = the Chrome for Testing download.</summary>
    internal static readonly string[] Engines = { "managed", "auto", "chrome", "edge", "brave", "chromium" };

    /// <summary>Engine used when <c>serve.browser.engine</c> is unset or empty: an installed browser, else managed.</summary>
    internal const string DefaultEngine = "auto";

    /// <summary>
    /// The installed browser <paramref name="engine"/> selects (<c>auto</c> = first found), or null for
    /// <c>managed</c> / <c>auto</c> with nothing installed. Throws when a named browser is not installed
    /// or the value is unknown.
    /// </summary>
    internal static (string Engine, string Exe, BrowserIdentity Identity)? ResolveInstalled(string? engine)
    {
        var e = string.IsNullOrWhiteSpace(engine) ? DefaultEngine : engine.Trim().ToLowerInvariant();
        if (!Engines.Contains(e)) throw new ArgumentException($"serve.browser.engine '{engine}' is not one of: {string.Join(", ", Engines)}.");
        if (e == "managed") return null;
        foreach (var b in BrowserIdentity.Installed())
            if (e == "auto" || b.Engine == e) return b;
        if (e == "auto") return null;
        throw new InvalidOperationException($"serve.browser.engine is '{e}' but that browser is not installed. Install it or use \"managed\".");
    }

    /// <summary>
    /// True when the browser can be offered on this host: a named installed browser (if it is missing,
    /// starting it reports why), or a managed build for this platform, or (<c>auto</c>) any installed one.
    /// </summary>
    internal static bool CanRun(string? engine)
    {
        var e = string.IsNullOrWhiteSpace(engine) ? DefaultEngine : engine.Trim().ToLowerInvariant();
        return e is not ("managed" or "auto") || Platform() != null || (e == "auto" && BrowserIdentity.Installed().Any());
    }

    /// <summary>Root of the default browser profiles: <c>&lt;install&gt;/BrowserProfiles</c>, outside
    /// <c>Sessions</c> so profiles are never mistaken for saved sessions.</summary>
    internal static string ProfilesRoot => Path.Combine(PlatformContext.BaseDirectory, "BrowserProfiles");

    /// <summary>Default profile folder for <paramref name="engine"/>: <c>&lt;root&gt;/managed</c> or <c>&lt;root&gt;/&lt;engine&gt;</c>
    /// (one per browser: profiles are not portable across browsers or versions).</summary>
    internal static string DefaultProfile(string root, string? engine) =>
        Path.Combine(root, engine is null or "auto" ? "managed" : engine);

    /// <summary>Chrome executable inside the extracted archive for <paramref name="platform"/>.</summary>
    internal static string ExePath(string platform) => platform switch
    {
        "win64" => Path.Combine(InstallDir, "chrome-win64", "chrome.exe"),
        "mac-arm64" or "mac-x64" => Path.Combine(InstallDir, $"chrome-{platform}", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"),
        _ => Path.Combine(InstallDir, $"chrome-{platform}", "chrome"),
    };

    /// <summary>Loopback DevTools endpoint while the browser runs (for Playwright <c>--cdp-endpoint</c>).</summary>
    internal static string? CdpEndpoint => _status == "ready" ? $"http://127.0.0.1:{_port}" : null;

    /// <summary>State for <c>GET /api/browser</c>.</summary>
    internal static object Status() => new
    {
        enabled = App.Config.Serve.Browser.Enabled,
        engine = App.Config.Serve.Browser.Engine,
        status = CanRun(App.Config.Serve.Browser.Engine) ? _status : "unsupported",
        identity = new { brand = BrowserIdentity.Current.Brand, version = BrowserIdentity.Current.Version, source = BrowserIdentity.Current.Source },
        installed = Platform() is { } p && File.Exists(ExePath(p)),
        version = Version,
        downloaded = _got, total = _total,
        error = _error,
    };

    /// <summary>Download (first use), launch and wait for DevTools. Idempotent; returns the DevTools port.</summary>
    internal static async Task<int> EnsureRunningAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_proc is { HasExited: false } && _port > 0) return _port;
            _error = null;
            var installed = ResolveInstalled(App.Config.Serve.Browser.Engine);
            string exe;
            if (installed is { } b) exe = b.Exe;
            else
            {
                var platform = Platform() ?? throw new PlatformNotSupportedException("No Chrome for Testing build for this platform; set serve.browser.engine to an installed browser.");
                exe = ExePath(platform);
                if (!File.Exists(exe)) { _status = "downloading"; await DownloadAsync(platform, ct); }
            }
            _status = "starting";
            _port = ReservedPort;
            var profile = string.IsNullOrWhiteSpace(App.Config.Serve.Browser.ProfileDir)
                ? DefaultProfile(ProfilesRoot, installed?.Engine) : App.Config.Serve.Browser.ProfileDir;
            Directory.CreateDirectory(profile);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            foreach (var a in LaunchArgs(_port, profile, App.Config.Serve.Browser.Headless)) psi.ArgumentList.Add(a);
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("Chrome did not start.");
            ProcessCleanup.Instance.Track(_proc);   // dies with the engine (Job Object / tracked PID)
            _proc.StandardInput.Close();
            _proc.ErrorDataReceived += (_, _) => { }; _proc.OutputDataReceived += (_, _) => { };
            _proc.BeginErrorReadLine(); _proc.BeginOutputReadLine();
            await WaitForDevToolsAsync(_port, ct);
            _status = "ready";
            return _port;
        }
        catch (Exception ex)
        {
            _status = "error"; _error = ex.Message;
            throw;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Chrome flags: headless, loopback-only DevTools, persistent profile, no first-run UI.</summary>
    internal static List<string> LaunchArgs(int port, string profile, bool headless = true)
    {
        var args = new List<string>
        {
            $"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1",
            $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check",
            "--disable-features=Translate", "--window-size=1280,800",
            // Present as the user's installed browser (see BrowserIdentity), not "HeadlessChrome".
            $"--user-agent={BrowserIdentity.Current.UserAgent}", $"--lang={AcceptLanguage.Split(',')[0]}",
            "about:blank",
        };
        if (headless) args.Insert(0, "--headless=new");
        // Linux containers often run as root, where Chrome refuses to start sandboxed.
        if (OperatingSystem.IsLinux() && Environment.UserName == "root") args.Insert(0, "--no-sandbox");
        return args;
    }

    /// <summary>
    /// Rewrite Playwright MCP args to attach to <paramref name="cdp"/>: drop the flags that only
    /// apply to a browser Playwright launches itself, then add <c>--cdp-endpoint</c>.
    /// </summary>
    internal static string[] PlaywrightArgs(string[] args, string cdp)
    {
        var withValue = new HashSet<string>(StringComparer.Ordinal) { "--browser", "--executable-path", "--user-data-dir", "--cdp-endpoint", "--viewport-size", "--device" };
        var flags = new HashSet<string>(StringComparer.Ordinal) { "--headless", "--no-sandbox", "--isolated", "--extension" };
        var outp = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i]; var key = a.Split('=')[0];
            if (withValue.Contains(key)) { if (!a.Contains('=')) i++; continue; }
            if (flags.Contains(a)) continue;
            outp.Add(a);
        }
        outp.Add("--cdp-endpoint"); outp.Add(cdp);
        return outp.ToArray();
    }

    /// <summary>Address new tabs open on (<c>serve.browser.homePage</c>; blank = <c>about:blank</c>).</summary>
    internal static string HomeUrl => BrowserRelay.NormalizeUrl(App.Config.Serve.Browser.HomePage);

    /// <summary>Language list from the OS UI culture, e.g. <c>en-US,en</c> (Chrome adds the q-weights itself).</summary>
    internal static string AcceptLanguage
    {
        get
        {
            var c = System.Globalization.CultureInfo.CurrentUICulture;
            var name = string.IsNullOrEmpty(c.Name) ? "en-US" : c.Name;
            var lang = name.Split('-')[0];
            return lang == name ? name : $"{name},{lang}";
        }
    }

    /// <summary>Stop the browser (engine shutdown).</summary>
    internal static void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _proc = null; _port = 0; _status = "idle";
    }

    private static async Task DownloadAsync(string platform, CancellationToken ct)
    {
        var url = $"https://storage.googleapis.com/chrome-for-testing-public/{Version}/{platform}/chrome-{platform}.zip";
        var tmp = Path.Combine(Path.GetTempPath(), $"mux-cft-{Guid.NewGuid():N}.zip");
        var stage = InstallDir + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("mux-swarm-browser-fetch");
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                _total = resp.Content.Headers.ContentLength ?? 0; _got = 0;
                await using var net = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = File.Create(tmp);
                var buf = new byte[1 << 16]; int n;
                while ((n = await net.ReadAsync(buf, ct)) > 0) { await fs.WriteAsync(buf.AsMemory(0, n), ct); _got += n; }
            }
            string hash;
            await using (var fs = File.OpenRead(tmp)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!Sha256.TryGetValue(platform, out var want) || !hash.Equals(want, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Chrome download checksum mismatch ({hash}).");
            ZipFile.ExtractToDirectory(tmp, stage);
            if (!OperatingSystem.IsWindows())
                foreach (var f in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                    if (string.IsNullOrEmpty(Path.GetExtension(f)) || f.EndsWith(".so", StringComparison.Ordinal) || f.Contains(".so.", StringComparison.Ordinal) || f.EndsWith(".dylib", StringComparison.Ordinal))
                        File.SetUnixFileMode(f, (UnixFileMode)0b111_101_101);
            Directory.CreateDirectory(Path.GetDirectoryName(InstallDir)!);
            if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true);
            Directory.Move(stage, InstallDir);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    private static async Task WaitForDevToolsAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (_proc is { HasExited: true }) throw new InvalidOperationException($"Chrome exited (code {_proc.ExitCode}).");
            try { await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct); return; } catch { }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Chrome DevTools did not come up.");
    }

    /// <summary>List page targets (tabs) from the DevTools HTTP endpoint.</summary>
    internal static async Task<List<JsonElement>> PagesAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var json = await http.GetStringAsync($"http://127.0.0.1:{_port}/json/list", ct);
        return JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Where(t => t.GetProperty("type").GetString() == "page").Select(t => t.Clone()).ToList();
    }

    private static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
}
