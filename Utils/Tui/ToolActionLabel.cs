using System.Text;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Turns a raw tool/function identifier (which can be anything once users add MCP servers) into a
/// short human "action" label for the thinking indicator and tool-call lines - e.g.
/// <c>ReplShellMcp_execute_command_async</c> -> <c>Running command</c>,
/// <c>Filesystem_read_text_file</c> -> <c>Reading text file</c>,
/// <c>analyze_image</c> -> <c>Analyzing image</c>.
///
/// It parses the VERB, never matches whole tool names, so it needs zero per-tool maintenance and
/// degrades gracefully for unknown tools: unknown verb -> humanized name, empty -> "Working".
/// Pure (string in, string out) so it is fully unit-testable.
/// </summary>
internal static class ToolActionLabel
{
    // Verb stem -> gerund. Keep small; this is the ONLY thing to ever extend, and it is optional
    // (an unmapped verb falls back to a humanized name, never a raw identifier).
    private static readonly Dictionary<string, string> _verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = "Reading", ["get"] = "Reading", ["fetch"] = "Reading", ["view"] = "Reading",
        ["cat"] = "Reading", ["show"] = "Reading", ["load"] = "Reading", ["open"] = "Reading",
        ["write"] = "Writing", ["save"] = "Writing", ["create"] = "Writing", ["put"] = "Writing",
        ["edit"] = "Editing", ["patch"] = "Editing", ["update"] = "Updating", ["append"] = "Writing",
        ["insert"] = "Writing", ["set"] = "Updating", ["modify"] = "Editing",
        ["run"] = "Running", ["execute"] = "Running", ["exec"] = "Running", ["spawn"] = "Running",
        ["invoke"] = "Running", ["eval"] = "Running", ["call"] = "Running",
        ["search"] = "Searching", ["find"] = "Searching", ["query"] = "Searching", ["grep"] = "Searching",
        ["lookup"] = "Searching", ["browse"] = "Browsing", ["crawl"] = "Browsing",
        ["list"] = "Listing", ["ls"] = "Listing", ["scan"] = "Scanning", ["enumerate"] = "Listing",
        ["tree"] = "Listing",
        ["delete"] = "Removing", ["remove"] = "Removing", ["rm"] = "Removing", ["drop"] = "Removing",
        ["move"] = "Moving", ["rename"] = "Moving", ["mv"] = "Moving", ["copy"] = "Copying", ["cp"] = "Copying",
        ["check"] = "Checking", ["status"] = "Checking", ["poll"] = "Checking", ["wait"] = "Waiting",
        ["inspect"] = "Inspecting", ["test"] = "Testing", ["verify"] = "Checking",
        ["analyze"] = "Analyzing", ["analyse"] = "Analyzing", ["describe"] = "Analyzing",
        ["classify"] = "Analyzing", ["detect"] = "Analyzing", ["summarize"] = "Summarizing",
        ["delegate"] = "Dispatching", ["dispatch"] = "Dispatching", ["assign"] = "Dispatching",
        ["send"] = "Sending", ["post"] = "Sending", ["publish"] = "Sending", ["emit"] = "Sending",
        ["notify"] = "Sending", ["push"] = "Sending",
        ["install"] = "Installing", ["add"] = "Adding", ["refresh"] = "Refreshing",
        ["build"] = "Building", ["compile"] = "Building", ["sleep"] = "Sleeping", ["pause"] = "Sleeping",
    };

    // Tokens dropped from the object/verb stream (noise that adds nothing to the label).
    private static readonly HashSet<string> _noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "async", "sync", "mcp", "tool", "v1", "v2", "api", "the", "a", "func", "function",
    };

    /// <summary>
    /// Compose a label like "Reading text file" / "Running command" / "Working". Never returns a
    /// raw identifier. <paramref name="raw"/> may be null/empty (-&gt; "Working").
    /// </summary>
    public static string Describe(string? raw)
    {
        var tokens = Tokenize(raw);
        if (tokens.Count == 0) return "Working";

        string verbToken = tokens[0];
        var rest = tokens.Skip(1).Where(t => !_noise.Contains(t)).ToList();

        if (_verbs.TryGetValue(verbToken, out var gerund))
        {
            // Object = up to the first two remaining words, humanized.
            string obj = string.Join(' ', rest.Take(2)).Trim();
            return obj.Length == 0 ? gerund : $"{gerund} {obj}";
        }

        // Unknown verb: humanize the whole de-prefixed token stream (verb included) so it is still
        // readable - e.g. "notion_create_page" with an unknown server-strip still reads sensibly,
        // and a totally unknown "frobnicate_widget" -> "Frobnicate widget".
        var all = tokens.Where(t => !_noise.Contains(t)).ToList();
        if (all.Count == 0) return "Working";
        string humanized = string.Join(' ', all.Take(3));
        return Capitalize(humanized);
    }

    /// <summary>Split a tool id into lowercase word tokens, dropping a leading server/namespace
    /// prefix when present. Handles snake_case, camelCase and PascalCase boundaries.</summary>
    private static List<string> Tokenize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        string s = raw.Trim();

        // Split on underscores first to find a server-prefix segment.
        var segs = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        // Drop a leading prefix segment when there is more than one segment AND the first segment
        // looks like a server/namespace tag: it ends in "Mcp", is a single CamelCase word with no
        // recognizable verb, or is a known wrapper. We only drop when >=2 segments remain after.
        if (segs.Length >= 2)
        {
            string first = segs[0];
            bool looksLikePrefix =
                first.EndsWith("Mcp", StringComparison.OrdinalIgnoreCase) ||
                first.EndsWith("Server", StringComparison.OrdinalIgnoreCase) ||
                (!_verbs.ContainsKey(SplitCamel(first).FirstOrDefault() ?? first) &&
                 char.IsUpper(first.Length > 0 ? first[0] : 'a'));
            if (looksLikePrefix)
                segs = segs.Skip(1).ToArray();
        }

        var tokens = new List<string>();
        foreach (var seg in segs)
            foreach (var w in SplitCamel(seg))
            {
                string lw = w.ToLowerInvariant();
                if (lw.Length > 0) tokens.Add(lw);
            }
        return tokens;
    }

    /// <summary>Break a single segment on camelCase / PascalCase boundaries into words.</summary>
    private static IEnumerable<string> SplitCamel(string seg)
    {
        if (string.IsNullOrEmpty(seg)) yield break;
        var sb = new StringBuilder();
        for (int i = 0; i < seg.Length; i++)
        {
            char c = seg[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(seg[i - 1]))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
