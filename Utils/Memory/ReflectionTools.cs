using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using MuxSwarm.Utils.NativeTools;

namespace MuxSwarm.Utils.Memory;

/// <summary>
/// STRICTLY READ-ONLY investigation tools for the deep-memory gatherer's deep "dig" pass. These let
/// the background reflection investigator chase a concrete cue from live context - a path the user
/// mentioned, an error/issue being nailed down, a file reference - by grepping the tree, reading the
/// relevant slice of a file, and querying the Chroma/KG stores. There is NO write/edit/move/delete
/// surface here by design: a background, auto-firing actor with write access is a memory-poisoning
/// footgun (OWASP ASI06). Every path is gated through <see cref="NativeToolSecurity.IsUnderAllowed"/>
/// exactly like the native filesystem read tools. All tools are best-effort and never throw.
/// </summary>
public static class ReflectionTools
{
    private static FilesystemConfig Cfg => App.Config.Filesystem;
    private static IReadOnlyList<string> Allowed =>
        Cfg.AllowedPaths ?? (IReadOnlyList<string>)Array.Empty<string>();

    // Bound the dig so a background pass can never run away: caps on files scanned, matches
    // returned, file size read, and per-file line scan. The first three are configurable via
    // reflectionAgent.dig* (floored so a bad value can't disable the guard); MaxFileBytes stays fixed.
    private static int MaxFilesScanned
    {
        get { try { return Math.Max(50, App.SwarmConfig?.ResolveReflection().DigMaxFilesScanned ?? 4000); } catch { return 4000; } }
    }
    private static int MaxMatches
    {
        get { try { return Math.Max(1, App.SwarmConfig?.ResolveReflection().DigMaxMatches ?? 40); } catch { return 40; } }
    }
    private const long MaxFileBytes = 2_000_000;
    private static int MaxReadChars
    {
        get { try { return Math.Max(200, App.SwarmConfig?.ResolveReflection().DigMaxReadChars ?? 8000); } catch { return 8000; } }
    }

    /// <summary>The read-only toolset handed to the investigator client. Write tools are NEVER here.</summary>
    public static IReadOnlyList<AITool> Build()
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(
                method: (
                    [Description("Substring or case-insensitive text to search for in file contents.")] string query,
                    [Description("Absolute directory to search under (must be inside an allowed path). Defaults to the workspace root.")] string? root = null,
                    [Description("Optional comma-separated file extensions to include, e.g. 'cs,md,json'. Empty = common text types.")] string? extensions = null) =>
                    Grep(query, root, extensions),
                name: "reflect_grep",
                description: "READ-ONLY content search: find files under an allowed path whose text contains the query. " +
                             "Returns 'path:line: matchtext' rows (capped). Use to locate a path/issue/reference the user mentioned."),

            AIFunctionFactory.Create(
                method: (
                    [Description("Absolute path of the text file to read (must be inside an allowed path).")] string path,
                    [Description("If set, return only the first N lines.")] int? head = null,
                    [Description("If set, return only the last N lines.")] int? tail = null) =>
                    ReadFile(path, head, tail),
                name: "reflect_read_file",
                description: "READ-ONLY: read a UTF-8 text file (head/tail to read just a slice). Output is length-capped. " +
                             "Use after reflect_grep to confirm the concrete detail."),

            AIFunctionFactory.Create(
                method: (
                    [Description("Absolute directory to list (must be inside an allowed path).")] string path) =>
                    ListDir(path),
                name: "reflect_list_dir",
                description: "READ-ONLY: list a directory's entries ([FILE]/[DIR] prefixed). Use to orient before grep/read."),

            AIFunctionFactory.Create(
                method: async (
                    [Description("Natural-language or keyword query to run against the persisted memory stores.")] string query,
                    CancellationToken ct) =>
                    await QueryStoreAsync(query, ct),
                name: "reflect_query_store",
                description: "READ-ONLY semantic recall: query the ChromaDB reflection collection and the knowledge graph " +
                             "for prior context. Silently returns '(stores unavailable)' when neither MCP server is connected."),
        };
    }

    // ---- implementations ----------------------------------------------------------------------

    private static readonly HashSet<string> DefaultExts = new(StringComparer.OrdinalIgnoreCase)
    { "cs", "md", "json", "jsonc", "txt", "py", "js", "ts", "yml", "yaml", "toml", "ini", "cfg", "log", "csproj", "sh", "ps1" };

    private static string Grep(string query, string? root, string? extensions)
    {
        if (string.IsNullOrWhiteSpace(query)) return "[reflect_grep] empty query.";
        var start = string.IsNullOrWhiteSpace(root) ? PlatformContext.WorkspaceRoot : root!;
        if (!NativeToolSecurity.IsUnderAllowed(start, Allowed))
            return $"[BLOCKED] '{start}' is outside the allowed paths.";
        if (!Directory.Exists(start)) return $"[reflect_grep] directory not found: {start}";

        var exts = string.IsNullOrWhiteSpace(extensions)
            ? DefaultExts
            : new HashSet<string>(extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        int scanned = 0, matches = 0;
        try
        {
            foreach (var file in EnumerateFilesSafe(start))
            {
                if (scanned >= MaxFilesScanned || matches >= MaxMatches) break;
                var ext = Path.GetExtension(file).TrimStart('.');
                if (exts.Count > 0 && !exts.Contains(ext)) continue;
                scanned++;
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length > MaxFileBytes) continue;
                    int lineNo = 0;
                    foreach (var line in File.ReadLines(file))
                    {
                        lineNo++;
                        if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length > 160) trimmed = trimmed[..160] + " ...";
                            sb.AppendLine($"{file}:{lineNo}: {trimmed}");
                            if (++matches >= MaxMatches) break;
                        }
                    }
                }
                catch { /* skip unreadable file */ }
            }
        }
        catch { /* return what we have */ }

        if (matches == 0) return $"[reflect_grep] no matches for '{query}' under {start} (scanned {scanned} files).";
        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        // Manual recursion that swallows per-dir access errors instead of aborting the whole walk.
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs = Array.Empty<string>();
            try { subdirs = Directory.GetDirectories(dir); } catch { }
            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                if (name is "obj" or "bin" or ".git" or "node_modules" or ".venv" or "__pycache__") continue;
                stack.Push(d);
            }
            string[] files = Array.Empty<string>();
            try { files = Directory.GetFiles(dir); } catch { }
            foreach (var f in files) yield return f;
        }
    }

    private static string ReadFile(string path, int? head, int? tail)
    {
        if (!NativeToolSecurity.IsUnderAllowed(path, Allowed))
            return $"[BLOCKED] '{path}' is outside the allowed paths.";
        if (!File.Exists(path)) return $"[reflect_read_file] not found: {path}";
        try
        {
            string text;
            if (head is > 0) text = string.Join("\n", File.ReadLines(path).Take(head.Value));
            else if (tail is > 0)
            {
                var all = File.ReadAllLines(path);
                text = string.Join("\n", all.Skip(Math.Max(0, all.Length - tail.Value)));
            }
            else text = File.ReadAllText(path);
            if (text.Length > MaxReadChars) text = text[..MaxReadChars] + "\n... [truncated]";
            return text;
        }
        catch (Exception ex) { return $"[reflect_read_file] {ex.Message}"; }
    }

    private static string ListDir(string path)
    {
        if (!NativeToolSecurity.IsUnderAllowed(path, Allowed))
            return $"[BLOCKED] '{path}' is outside the allowed paths.";
        if (!Directory.Exists(path)) return $"[reflect_list_dir] not found: {path}";
        try
        {
            var sb = new StringBuilder();
            foreach (var d in Directory.GetDirectories(path)) sb.AppendLine($"[DIR]  {Path.GetFileName(d)}");
            foreach (var f in Directory.GetFiles(path)) sb.AppendLine($"[FILE] {Path.GetFileName(f)}");
            return sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[reflect_list_dir] {ex.Message}"; }
    }

    private static async Task<string> QueryStoreAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return "(empty query)";
        var sb = new StringBuilder();

        // Chroma semantic recall over the reflection collection (best-effort).
        try
        {
            if (App.McpClients.TryGetValue("ChromaDB", out var chroma))
            {
                var res = await chroma.CallToolAsync("chroma_query_documents", new Dictionary<string, object?>
                {
                    ["collection_name"] = "mux_reflections",
                    ["query_texts"] = new[] { query },
                    ["n_results"] = 5
                }!, cancellationToken: ct);
                var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(b => b.Text));
                if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine("=== chroma ===").AppendLine(text);
            }
        }
        catch { /* silent */ }

        // Knowledge-graph recall (best-effort).
        try
        {
            if (App.McpClients.TryGetValue("Memory", out var kg))
            {
                var res = await kg.CallToolAsync("search_nodes", new Dictionary<string, object?>
                {
                    ["query"] = query
                }!, cancellationToken: ct);
                var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(b => b.Text));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (text.Length > 3000) text = text[..3000] + " ...";
                    sb.AppendLine("=== knowledge graph ===").AppendLine(text);
                }
            }
        }
        catch { /* silent */ }

        return sb.Length == 0 ? "(stores unavailable or no hits)" : sb.ToString().TrimEnd();
    }
}
