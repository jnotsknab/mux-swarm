using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MuxSwarm.Engine.Browser;

/// <summary>
/// The identity the built-in browser presents (User-Agent + client hints), so sites treat it like
/// the user's normal browser instead of a headless test build. Taken from a Chromium browser
/// installed on this machine (Chrome, Edge, Brave, Chromium; first found), else computed from the
/// bundled version. Headless Chrome reports "HeadlessChrome" and a "Chromium"-only brand list,
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

    /// <summary>Identity for this machine (cached for the engine run).</summary>
    internal static BrowserIdentity Current => _cached ??= Detect() ?? new BrowserIdentity("Google Chrome", BrowserHost.Version, "computed");

    /// <summary>First installed Chromium browser with a readable version, or null.</summary>
    internal static BrowserIdentity? Detect()
    {
        foreach (var (brand, path) in Candidates())
        {
            try { if (VersionOf(path) is { } v) return new BrowserIdentity(brand, v, path); }
            catch { /* unreadable; try the next */ }
        }
        return null;
    }

    private static IEnumerable<(string Brand, string Path)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var root in new[] { pf, pf86, local })
            {
                yield return ("Google Chrome", Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                yield return ("Microsoft Edge", Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                yield return ("Brave", Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return ("Google Chrome", "/Applications/Google Chrome.app/Contents/Info.plist");
            yield return ("Microsoft Edge", "/Applications/Microsoft Edge.app/Contents/Info.plist");
            yield return ("Brave", "/Applications/Brave Browser.app/Contents/Info.plist");
            yield return ("Chromium", "/Applications/Chromium.app/Contents/Info.plist");
        }
        else
        {
            foreach (var (brand, bin) in new[] { ("Google Chrome", "google-chrome"), ("Google Chrome", "google-chrome-stable"),
                         ("Microsoft Edge", "microsoft-edge"), ("Brave", "brave-browser"), ("Chromium", "chromium"), ("Chromium", "chromium-browser") })
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
                    yield return (brand, Path.Combine(dir, bin));
        }
    }

    /// <summary>Version of the browser at <paramref name="path"/>: file version (Windows), Info.plist (macOS), --version (Linux).</summary>
    internal static string? VersionOf(string path)
    {
        if (!File.Exists(path)) return null;
        string? raw;
        if (OperatingSystem.IsWindows()) raw = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        else if (path.EndsWith(".plist", StringComparison.Ordinal))
            raw = Regex.Match(File.ReadAllText(path), @"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>").Groups[1].Value;
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
