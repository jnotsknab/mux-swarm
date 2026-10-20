using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MuxSwarm.Engine.Browser;

/// <summary>
/// The identity the built-in browser presents (User-Agent + client hints), so sites treat it like
/// the user's normal browser instead of a headless test build. Taken from a Chromium browser
/// installed on this machine (the one <c>serve.browser.engine</c> runs, else the first found), else
/// computed from the managed version. Headless Chrome reports "HeadlessChrome" and a "Chromium"-only brand list,
/// which is what bot checks flag; the override replaces both on every tab.
/// </summary>
internal sealed record BrowserIdentity(string Brand, string Version, string Source)
{
    /// <summary>Major version, e.g. "155".</summary>
    internal string Major => Version.Split('.')[0];

    /// <summary>Platform token inside the User-Agent string (frozen per the UA reduction rules).</summary>
    internal static string UaPlatform =>
        OperatingSystem.IsWindows() ? "Windows NT 10.0; Win64; x64" :
        OperatingSystem.IsMacOS() ? "Macintosh; Intel Mac OS X 10_15_7" :
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "X11; Linux aarch64" : "X11; Linux x86_64";

    /// <summary>Client-hint platform name.</summary>
    internal static string HintPlatform => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>Reduced User-Agent, as real Chrome sends it (minor version zeroed). Edge adds an Edg/ token.</summary>
    internal string UserAgent
    {
        get
        {
            var ua = $"Mozilla/5.0 ({UaPlatform}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{Major}.0.0.0 Safari/537.36";
            return Brand == "Microsoft Edge" ? ua + $" Edg/{Major}.0.0.0" : ua;
        }
    }

    /// <summary>Params for <c>Emulation.setUserAgentOverride</c> (User-Agent, language, client hints).</summary>
    internal JsonObject OverrideParams(string acceptLanguage)
    {
        JsonArray Brands(bool full)
        {
            var a = new JsonArray();
            if (Brand != "Chromium") a.Add(new JsonObject { ["brand"] = Brand, ["version"] = full ? Version : Major });
            a.Add(new JsonObject { ["brand"] = "Chromium", ["version"] = full ? Version : Major });
            a.Add(new JsonObject { ["brand"] = "Not(A:Brand", ["version"] = full ? "24.0.0.0" : "24" });
            return a;
        }
        return new JsonObject
        {
            ["userAgent"] = UserAgent,
            ["acceptLanguage"] = acceptLanguage,
            ["platform"] = OperatingSystem.IsWindows() ? "Win32" : OperatingSystem.IsMacOS() ? "MacIntel" : "Linux x86_64",
            ["userAgentMetadata"] = new JsonObject
            {
                ["brands"] = Brands(false),
                ["fullVersionList"] = Brands(true),
                ["fullVersion"] = Version,
                ["platform"] = HintPlatform,
                ["platformVersion"] = OperatingSystem.IsWindows() ? "19.0.0" : Environment.OSVersion.Version.ToString(3),
                ["architecture"] = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm" : "x86",
                ["bitness"] = "64",
                ["model"] = "",
                ["mobile"] = false,
                ["wow64"] = false,
            },
        };
    }

    private static BrowserIdentity? _cached;

    /// <summary>
    /// Identity for this run (cached): the browser <c>serve.browser.engine</c> launches when it is an
    /// installed one, else the first installed Chromium browser, else computed from the managed version.
    /// </summary>
    internal static BrowserIdentity Current => _cached ??= Selected() ?? Detect() ?? new BrowserIdentity("Google Chrome", BrowserHost.Version, "computed");

    private static BrowserIdentity? Selected()
    {
        try { return BrowserHost.ResolveInstalled(App.Config.Serve.Browser.Engine)?.Identity; }
        catch { return null; }   // misconfigured engine: starting the browser reports it
    }

    /// <summary>First installed Chromium browser with a readable version, or null.</summary>
    internal static BrowserIdentity? Detect() => Installed().Select(i => i.Identity).FirstOrDefault();

    /// <summary>Installed Chromium browsers with a readable version, in preference order (Chrome, Edge, Brave, Chromium).</summary>
    internal static IEnumerable<(string Engine, string Exe, BrowserIdentity Identity)> Installed()
    {
        foreach (var (engine, brand, exe) in Candidates())
        {
            string? v = null;
            try { v = VersionOf(exe); } catch { /* unreadable; try the next */ }
            if (v != null) yield return (engine, exe, new BrowserIdentity(brand, v, exe));
        }
    }

    private static IEnumerable<(string Engine, string Brand, string Exe)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var (engine, brand, rel) in new[] { ("chrome", "Google Chrome", @"Google\Chrome\Application\chrome.exe"),
                         ("edge", "Microsoft Edge", @"Microsoft\Edge\Application\msedge.exe"), ("brave", "Brave", @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                         ("chromium", "Chromium", @"Chromium\Application\chrome.exe") })
                foreach (var root in new[] { pf, pf86, local })
                    yield return (engine, brand, Path.Combine(root, rel));
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return ("chrome", "Google Chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            yield return ("edge", "Microsoft Edge", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            yield return ("brave", "Brave", "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser");
            yield return ("chromium", "Chromium", "/Applications/Chromium.app/Contents/MacOS/Chromium");
        }
        else
        {
            foreach (var (engine, brand, bin) in new[] { ("chrome", "Google Chrome", "google-chrome"), ("chrome", "Google Chrome", "google-chrome-stable"),
                         ("edge", "Microsoft Edge", "microsoft-edge"), ("brave", "Brave", "brave-browser"), ("chromium", "Chromium", "chromium"), ("chromium", "Chromium", "chromium-browser") })
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
                    yield return (engine, brand, Path.Combine(dir, bin));
        }
    }

    /// <summary>Version of the browser at <paramref name="path"/>: file version (Windows), the app's Info.plist (macOS), --version (Linux).</summary>
    internal static string? VersionOf(string path)
    {
        if (!File.Exists(path)) return null;
        string? raw;
        if (OperatingSystem.IsWindows()) raw = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        else if (OperatingSystem.IsMacOS())   // <App>.app/Contents/MacOS/<exe> -> <App>.app/Contents/Info.plist
            raw = Regex.Match(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, "Info.plist")),
                @"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>").Groups[1].Value;
        else
        {
            using var p = Process.Start(new ProcessStartInfo(path, "--version") { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return null;
            p.StandardInput.Close();
            raw = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
        }
        var m = Regex.Match(raw ?? "", @"\d+\.\d+\.\d+\.\d+");
        return m.Success ? m.Value : null;
    }
}
