using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using MuxSwarm.Engine.NativeTools;
using System.Runtime.CompilerServices;

namespace MuxSwarm.Engine;

/// <summary>Read-only tool metadata shared by /tools output and its completion preview.</summary>
internal static class ToolCatalog
{
    /// <summary>Tool name and description with explicit runtime/MCP/native provenance; not invokable.</summary>
    internal sealed record Entry(string Name, string Description, string Group, string Kind);
    /// <summary>One available-tool scope. Uninitialized is distinct from an initialized empty scope.</summary>
    internal sealed record Snapshot(string Scope, IReadOnlyList<Entry> Entries, bool Initialized = true, string? Note = null);
    private sealed record Origin(string Server);
    private static readonly ConditionalWeakTable<AITool, Origin> Origins = new();
    private static Entry[] _external = [];
    private static bool _loading;
    private static int _generation;
    /// <summary>Changes when the global connected-tool metadata is republished.</summary>
    internal static int Generation => Volatile.Read(ref _generation);
    private static readonly Lazy<Entry[]> Native = new(() => FromTools(
        NativeToolRegistry.BuildPool(new AppConfig()), "native").Entries.ToArray());

    /// <summary>Record the exact configured server name at successful MCP initialization.</summary>
    internal static void RegisterExternal(AITool tool, string server)
    {
        Origins.Remove(tool);
        Origins.Add(tool, new Origin(server));
    }

    /// <summary>Publish an immutable connected-tool catalog; no tool invocation or connection is attempted.</summary>
    internal static void PublishExternal(IEnumerable<AITool> tools, bool loading)
    {
        Volatile.Write(ref _external, FromTools(tools, "external").Entries.ToArray());
        Volatile.Write(ref _loading, loading);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Global availability at the menu, not a union of agent permissions.</summary>
    internal static Snapshot Global()
    {
        var config = App.Config;
        var native = Native.Value.Where(e => NativeToolRegistry.ServerEnabled(config, e.Group));
        return new("Global availability", native.Concat(Volatile.Read(ref _external)).ToArray(), true,
            Volatile.Read(ref _loading)
                ? "MCP catalog is loading; native tools are shown now. Agent permissions still apply."
                : "Native and connected MCP tools. Individual agents receive only their configured subset.");
    }

    /// <summary>Project only the supplied tools; never broadens a filtered agent scope.</summary>
    internal static Snapshot FromTools(IEnumerable<AITool> tools, string scope, string? note = null)
    {
        var entries = tools.Select(tool =>
        {
            // External first: a real external Filesystem_read_* must not be labelled native.
            if (Origins.TryGetValue(tool, out var origin))
                return new Entry(tool.Name, tool.Description ?? "", origin.Server, "MCP");
            if (tool is McpClientTool)
                return new Entry(tool.Name, tool.Description ?? "", "Unattributed server", "MCP");
            if (NativeToolRegistry.VirtualServer(tool.Name) is { } native)
                return new Entry(tool.Name, tool.Description ?? "", native, "Native");
            return new Entry(tool.Name, tool.Description ?? "", "Runtime", "Local");
        }).ToArray();
        return new(scope, entries, true, note);
    }

    /// <summary>Parse /tools and an optional fuzzy query without executing any listed function.</summary>
    internal static bool TryQuery(string? text, out string query)
    {
        var parts = (text ?? "").Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        query = parts.Length > 1 ? parts[1] : "";
        return parts.Length > 0 && parts[0].Equals("/tools", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rank name, group, and description matches; all query terms must match. Exact names win.</summary>
    internal static List<Entry> Rank(string? query, IReadOnlyList<Entry> entries)
    {
        string filter = (query ?? "").Trim();
        var terms = filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return entries.Select(e => (Entry: e, Score: Score(e, filter, terms)))
            .Where(x => x.Score >= 0).OrderBy(x => x.Score)
            .ThenBy(x => x.Entry.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Entry.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry).ToList();
    }

    private static int Score(Entry entry, string filter, string[] terms)
    {
        if (filter.Length == 0) return 0;
        if (entry.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)) return 0;
        int total = 1;
        foreach (string term in terms)
        {
            int name = Fuzzy(entry.Name, term), group = Fuzzy(entry.Kind + " " + entry.Group, term);
            int description = entry.Description.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            int best = Math.Min(name < 0 ? int.MaxValue : name,
                Math.Min(group < 0 ? int.MaxValue : group + 100, description < 0 ? int.MaxValue : description + 500));
            if (best == int.MaxValue) return -1;
            total += best;
        }
        return total;
    }

    private static int Fuzzy(string text, string term)
    {
        int hit = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (hit >= 0) return hit;
        int pos = 0, first = -1, last = -1;
        for (int i = 0; i < text.Length && pos < term.Length; i++)
            if (char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(term[pos]))
            { if (first < 0) first = i; last = i; pos++; }
        return pos == term.Length ? 1000 + last - first : -1;
    }
}
