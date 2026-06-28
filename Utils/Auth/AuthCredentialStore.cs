using System.Text.Json;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// Reads/writes OAuth credentials to a per-provider JSON file in a LOCAL, restricted directory that is
/// deliberately SEPARATE from Mux's (possibly NAS-synced) Configs/ tree - tokens must never land on a
/// shared/synced path. Location: %LOCALAPPDATA%\Mux-Swarm\auth\&lt;provider&gt;.json on Windows,
/// ~/.config/mux-swarm/auth/&lt;provider&gt;.json on unix. Files are owner-only (unix chmod 0600 + dir
/// 0700; Windows inherits the per-user LOCALAPPDATA ACL). Tokens are NEVER logged.
/// </summary>
internal static class AuthCredentialStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The local auth directory (created on demand, 0700 on unix). Off the synced Configs tree.</summary>
    public static string AuthDirectory
    {
        get
        {
            string baseDir = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mux-Swarm")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                        Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } xdg
                        ? xdg
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
                    "mux-swarm");
            return Path.Combine(baseDir, "auth");
        }
    }

    private static string FilePath(string provider) =>
        Path.Combine(AuthDirectory, SanitizeProvider(provider) + ".json");

    /// <summary>Persist <paramref name="tokens"/> to the provider's restricted auth file.</summary>
    public static void Save(OAuthTokens tokens)
    {
        string dir = AuthDirectory;
        Directory.CreateDirectory(dir);
        HardenDir(dir);
        string path = FilePath(tokens.Provider);
        string json = JsonSerializer.Serialize(tokens, JsonOpts);
        File.WriteAllText(path, json);
        HardenFile(path);
    }

    /// <summary>Load the provider's credential, or null if none exists / unreadable. Warns on lax perms.</summary>
    public static OAuthTokens? Load(string provider)
    {
        string path = FilePath(provider);
        if (!File.Exists(path)) return null;
        try
        {
            WarnIfWorldReadable(path);
            return JsonSerializer.Deserialize<OAuthTokens>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when a credential file exists for the provider (used by setup "reuse existing login").</summary>
    public static bool Exists(string provider) => File.Exists(FilePath(provider));

    /// <summary>Delete a provider's credential (logout).</summary>
    public static void Delete(string provider)
    {
        string path = FilePath(provider);
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    /// <summary>List providers with a stored credential.</summary>
    public static IReadOnlyList<string> ListProviders()
    {
        string dir = AuthDirectory;
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            list.Add(Path.GetFileNameWithoutExtension(f));
        return list;
    }

    // ---- per-OS hardening ----

    private static void HardenDir(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best-effort 0700 */ }
        }
    }

    private static void HardenFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }   // 0600
            catch { /* best-effort */ }
        }
        // On Windows the file lives under the per-user LOCALAPPDATA tree which is already user-scoped by
        // default ACL; DPAPI encryption is a possible future hardening but not required for the store.
    }

    private static void WarnIfWorldReadable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode lax = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                     UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
            if ((mode & lax) != 0)
            {
                MuxConsole.WriteWarning($"[auth] {path} is group/world-accessible; tightening to 0600.");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { /* best-effort */ }
    }

    private static string SanitizeProvider(string provider)
    {
        var sb = new System.Text.StringBuilder(provider.Length);
        foreach (char c in provider) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "provider" : sb.ToString().ToLowerInvariant();
    }
}
