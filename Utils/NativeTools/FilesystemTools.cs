using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Threading;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// Native, in-house Filesystem tools - a session-agnostic C# port of the high-value subset of
/// @modelcontextprotocol/server-filesystem. Owning these in-process (instead of spawning the npx
/// server) removes a default MCP subprocess (faster startup), lets edits render through Mux's own
/// diff machinery, and - crucially - lets Mux ENFORCE real security: every path is canonicalized
/// and checked against <see cref="FilesystemConfig.AllowedPaths"/> per the configured
/// <see cref="FilesystemConfig.SecurityMode"/> (standard/secure/lax/none), with writes able to
/// elevate to the user.
///
/// Tool names keep the <c>Filesystem_</c> prefix so the existing per-agent ToolFilter (swarm.json
/// mcpServers / toolPatterns) gates them EXACTLY as it gated the MCP tools - no config migration.
///
/// Deliberately STRIPPED from the upstream surface (covered natively by the shell/REPL tools, so
/// keeping them would just burn context): read_multiple_files, list_directory_with_sizes,
/// directory_tree, search_files, get_file_info. Mutating ops stay native so SecurityMode governs them.
/// </summary>
public static class FilesystemTools
{
    private static FilesystemConfig Cfg => App.Config.Filesystem;
    private static IReadOnlyList<string> Allowed => Cfg.AllowedPaths ?? (IReadOnlyList<string>)Array.Empty<string>();
    private static string Mode => (Cfg.SecurityMode ?? "standard").Trim().ToLowerInvariant();

    // ---- security decisions -------------------------------------------------------------------

    private enum Access { Read, Write }

    // ---- atomicity / parallel-safety --------------------------------------------------------
    //
    // Sub-agents (esp. delegate_parallel) can target the SAME path concurrently. Guard every
    // mutation with a per-canonical-path lock so read-modify-write (edit_file) is serialized and
    // never interleaves, and make the on-disk replacement ATOMIC (write a temp sibling, flush, then
    // File.Move(overwrite:true) which is a rename) so a concurrent reader / a crash never observes a
    // truncated or half-written file. Locks are keyed by the canonical path (symlinks resolved), so
    // two aliases of one file share one lock. The lock table is process-wide (static), matching the
    // process-wide nature of the filesystem.
    private static readonly ConcurrentDictionary<string, object> _pathLocks = new();

    private static object LockFor(string path) =>
        _pathLocks.GetOrAdd(NativeToolSecurity.Canonicalize(path),
            _ => new object());

    /// <summary>Atomic file replace: temp sibling -> fsync -> rename over the target.</summary>
    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Temp sibling in the SAME directory so the final rename stays on one volume (atomic).
        string tmp = path + ".mux-tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Gate a path operation. Returns null when allowed; otherwise an error string the tool returns
    /// verbatim to the model. Handles all four modes + write-elevation in one place.
    /// </summary>
    private static string? Gate(string path, Access access, string opDescription)
    {
        switch (Mode)
        {
            case "none":
                return null;

            case "lax":
            case "yolo":
                // Anywhere allowed EXCEPT sensitive/system dirs - unless explicitly in AllowedPaths.
                if (NativeToolSecurity.IsUnderAllowed(path, Allowed)) return null;
                if (NativeToolSecurity.IsSensitive(path))
                    return $"[BLOCKED] '{path}' is in a protected system/sensitive directory (lax mode still refuses these).";
                return null;

            case "secure":
                // Reads: must be under AllowedPaths. Writes: under AllowedPaths AND elevate to user.
                if (!NativeToolSecurity.IsUnderAllowed(path, Allowed))
                    return $"[BLOCKED] '{path}' is outside the allowed paths. Allowed: {string.Join(", ", Allowed)}";
                if (access == Access.Write)
                {
                    if (!NativeToolSecurity.Elevate(opDescription))
                        return NativeToolSecurity.DenyMessage(opDescription);
                }
                return null;

            default: // "standard"
                if (!NativeToolSecurity.IsUnderAllowed(path, Allowed))
                    return $"[BLOCKED] '{path}' is outside the allowed paths. Allowed: {string.Join(", ", Allowed)}";
                return null;
        }
    }

    // ---- tool surface -------------------------------------------------------------------------

    public static IReadOnlyList<AITool> Build()
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(
                method: ([Description("Absolute path of the text file to read.")] string path,
                         [Description("If set, return only the first N lines.")] int? head = null,
                         [Description("If set, return only the last N lines.")] int? tail = null) =>
                    ReadTextFile(path, head, tail),
                name: "Filesystem_read_text_file",
                description: "Read a file as UTF-8 text. Optional 'head'/'tail' return only the first/last N lines. " +
                             "NOTE: prefer a shell tool (e.g. wc -l / sed / rg) for large files or codebases - reading a " +
                             "big file whole can blow context. Verify size first unless the user explicitly says to read it."),

            AIFunctionFactory.Create(
                method: ([Description("Absolute path of the image/audio file to read.")] string path) =>
                    ReadMediaFile(path),
                name: "Filesystem_read_media_file",
                description: "Read an image or audio file and return base64 data + MIME type."),

            AIFunctionFactory.Create(
                method: ([Description("Absolute path to write.")] string path,
                         [Description("Full file content (overwrites).")] string content) =>
                    WriteFile(path, content),
                name: "Filesystem_write_file",
                description: "Create a new file or completely overwrite an existing file with the given content."),

            AIFunctionFactory.Create(
                method: ([Description("Absolute path of the file to edit.")] string path,
                         [Description("Exact text to find (must match including whitespace).")] string oldText,
                         [Description("Replacement text.")] string newText) =>
                    EditFile(path, oldText, newText),
                name: "Filesystem_edit_file",
                description: "Replace an exact text block in a file and return a git-style unified diff of the change."),

            AIFunctionFactory.Create(
                method: ([Description("Absolute path of the directory to create (parents created as needed).")] string path) =>
                    CreateDirectory(path),
                name: "Filesystem_create_directory",
                description: "Create a directory (and any missing parents). Succeeds silently if it already exists."),

            AIFunctionFactory.Create(
                method: ([Description("Absolute source path.")] string source,
                         [Description("Absolute destination path (must not already exist).")] string destination) =>
                    MoveFile(source, destination),
                name: "Filesystem_move_file",
                description: "Move or rename a file or directory. Fails if the destination already exists."),

            AIFunctionFactory.Create(
                method: ([Description("Absolute path of the directory to list.")] string path) =>
                    ListDirectory(path),
                name: "Filesystem_list_directory",
                description: "List the entries of a directory, each prefixed [FILE] or [DIR]."),

            AIFunctionFactory.Create(
                method: () => ListAllowedDirectories(),
                name: "Filesystem_list_allowed_directories",
                description: "List the directories this agent is permitted to access."),
        };
    }

    // ---- implementations ----------------------------------------------------------------------

    private static string ReadTextFile(string path, int? head, int? tail)
    {
        var gate = Gate(path, Access.Read, $"read {path}");
        if (gate is not null) return gate;
        if (!File.Exists(path)) return $"[ERROR] File not found: {path}";
        try
        {
            if (head is > 0)
                return string.Join("\n", File.ReadLines(path).Take(head.Value));
            if (tail is > 0)
            {
                var all = File.ReadAllLines(path);
                return string.Join("\n", all.Skip(Math.Max(0, all.Length - tail.Value)));
            }
            return File.ReadAllText(path);
        }
        catch (Exception ex) { return $"[ERROR] {ex.Message}"; }
    }

    private static string ReadMediaFile(string path)
    {
        var gate = Gate(path, Access.Read, $"read {path}");
        if (gate is not null) return gate;
        if (!File.Exists(path)) return $"[ERROR] File not found: {path}";
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            string mime = ext switch
            {
                "png" => "image/png",
                "jpg" or "jpeg" => "image/jpeg",
                "gif" => "image/gif",
                "webp" => "image/webp",
                "bmp" => "image/bmp",
                "svg" => "image/svg+xml",
                "mp3" => "audio/mpeg",
                "wav" => "audio/wav",
                "ogg" => "audio/ogg",
                "m4a" => "audio/mp4",
                "flac" => "audio/flac",
                _ => "application/octet-stream"
            };
            return $"MIME: {mime}\nBase64: {Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex) { return $"[ERROR] {ex.Message}"; }
    }

    private static string WriteFile(string path, string content)
    {
        var gate = Gate(path, Access.Write, $"write {path}");
        if (gate is not null) return gate;
        try
        {
            lock (LockFor(path)) AtomicWrite(path, content);
            return $"Wrote {content.Length} chars to {path}";
        }
        catch (Exception ex) { return $"[ERROR] {ex.Message}"; }
    }

    private static string EditFile(string path, string oldText, string newText)
    {
        var gate = Gate(path, Access.Write, $"edit {path}");
        if (gate is not null) return gate;
        if (!File.Exists(path)) return $"[ERROR] File not found: {path}";
        try
        {
            // Serialize the whole read-modify-write so two sub-agents editing the same file cannot
            // interleave (lost-update / torn-read). The replace + atomic rename happen under one lock.
            lock (LockFor(path))
            {
                string original = File.ReadAllText(path);
                if (!original.Contains(oldText))
                    return "[ERROR] oldText not found in file (must match exactly, including whitespace).";
                string updated = ReplaceFirst(original, oldText, newText);
                AtomicWrite(path, updated);
                return BuildUnifiedDiff(path, original, updated);
            }
        }
        catch (Exception ex) { return $"[ERROR] {ex.Message}"; }
    }

    private static string CreateDirectory(string path)
    {
        var gate = Gate(path, Access.Write, $"create directory {path}");
        if (gate is not null) return gate;
        try { Directory.CreateDirectory(path); return $"Created directory: {path}"; }
        catch (Exception ex) { return $"[ERROR] {ex.Message}"; }
    }

    private static string MoveFile(string source, string destination)
    {
        var g1 = Gate(source, Access.Write, $"move {source}");
        if (g1 is not null) return g1;
        var g2 = Gate(destination, Access.Write, $"move into {destination}");
        if (g2 is not null) return g2;
        try
        {
            if (File.Exists(destination) || Directory.Exists(destination))
                return $"[ERROR] Destination already exists: {destination}";
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
            return $"Moved {source} -> {destination}";
        }
        catch (Exception ex) { return $"[ERROR] {ex.Message}"; }
    }

    private static string ListDirectory(string path)
    {
        var gate = Gate(path, Access.Read, $"list {path}");
        if (gate is not null) return gate;
        if (!Directory.Exists(path)) return $"[ERROR] Directory not found: {path}";
        try
        {
            var sb = new StringBuilder();
            foreach (var d in Directory.GetDirectories(path).OrderBy(x => x))
                sb.Append("[DIR] ").Append(Path.GetFileName(d)).Append('\n');
            foreach (var f in Directory.GetFiles(path).OrderBy(x => x))
                sb.Append("[FILE] ").Append(Path.GetFileName(f)).Append('\n');
            return sb.Length == 0 ? "(empty directory)" : sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[ERROR] {ex.Message}"; }
    }

    private static string ListAllowedDirectories()
    {
        if (Mode == "none") return "Security mode 'none': all paths permitted.";
        if (Mode is "lax" or "yolo")
            return "Security mode 'lax': all paths permitted except system/sensitive directories. " +
                   (Allowed.Count > 0 ? "Explicitly allowed: " + string.Join(", ", Allowed) : "");
        return Allowed.Count == 0
            ? "No allowed directories configured."
            : "Allowed directories:\n" + string.Join("\n", Allowed);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        int i = haystack.IndexOf(needle, StringComparison.Ordinal);
        return i < 0 ? haystack : haystack[..i] + replacement + haystack[(i + needle.Length)..];
    }

    /// <summary>
    /// Minimal git-style unified diff (one hunk spanning the changed region) so the TUI's diff
    /// renderer (LooksLikeDiff / CommitDiffCollapsible) can present it as a real diff card.
    /// </summary>
    private static string BuildUnifiedDiff(string path, string before, string after)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');

        int start = 0;
        int minLen = Math.Min(a.Length, b.Length);
        while (start < minLen && a[start] == b[start]) start++;

        int endA = a.Length - 1, endB = b.Length - 1;
        while (endA >= start && endB >= start && a[endA] == b[endB]) { endA--; endB--; }

        var sb = new StringBuilder();
        string name = Path.GetFileName(path);
        sb.Append("--- a/").Append(name).Append('\n');
        sb.Append("+++ b/").Append(name).Append('\n');
        int aCount = endA - start + 1;
        int bCount = endB - start + 1;
        sb.Append("@@ -").Append(start + 1).Append(',').Append(Math.Max(0, aCount))
          .Append(" +").Append(start + 1).Append(',').Append(Math.Max(0, bCount)).Append(" @@\n");
        for (int i = start; i <= endA; i++) sb.Append('-').Append(a[i]).Append('\n');
        for (int i = start; i <= endB; i++) sb.Append('+').Append(b[i]).Append('\n');
        return sb.ToString().TrimEnd();
    }
}
