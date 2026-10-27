using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine.Lsp;

/// <summary>
/// The native <c>lsp</c> tool (virtual server <c>Lsp</c>): read-only questions to the configured language
/// servers. Positions are a 1-based <c>line</c> plus the <c>symbol</c> text on it (the column is found here),
/// because models count columns badly. Results are compact text: <c>path:line:col</c> with the source line,
/// errors first, capped. Present only while <c>lsp.enabled</c> is on.
/// </summary>
internal static class LspTool
{
    /// <summary>Virtual server name used by Swarm.json <c>mcpServers</c> to grant the tool.</summary>
    internal const string Server = "Lsp";

    /// <summary>The tool's name.</summary>
    internal const string Name = "lsp";

    internal const int MaxDiagnostics = 50, MaxReferences = 50, MaxSymbols = 200;

    /// <summary>The tool, or nothing when LSP is off.</summary>
    internal static IEnumerable<AITool> Build()
    {
        if (!LspManager.Active) yield break;
        yield return AIFunctionFactory.Create(
            method: ([Description("diagnostics | definition | references | hover | symbols | status")] string action,
                     [Description("File path (absolute, or relative to the workspace). Not needed for status, or for symbols with a query.")] string? file = null,
                     [Description("1-based line number for definition / references / hover.")] int? line = null,
                     [Description("Text on that line naming the symbol (e.g. a method name); 'name#2' = its 2nd occurrence on the line.")] string? symbol = null,
                     [Description("symbols: search the whole workspace for this name instead of listing the file's symbols.")] string? query = null,
                     CancellationToken ct = default) => RunAsync(action, file, line, symbol, query, ct),
            name: Name,
            description: "Ask the project's language server (read-only): diagnostics (errors/warnings in a file), definition, " +
                         "references, hover (type/docs), symbols (outline of a file, or workspace search with query), status. " +
                         "Point at code with line + symbol text, not columns.");
    }

    /// <summary>Run one action. Never throws for server problems; they come back as text.</summary>
    internal static async Task<string> RunAsync(string action, string? file, int? line, string? symbol, string? query, CancellationToken ct)
    {
        action = (action ?? "").Trim().ToLowerInvariant();
        if (action == "status") return string.Join("\n", LspManager.Status());
        if (action == "symbols" && string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(query))
            return await WorkspaceSymbolsAsync(query!, ct);
        if (string.IsNullOrWhiteSpace(file)) return $"[lsp] '{action}' needs a file.";
        var path = Path.GetFullPath(file, LspManager.WorkspaceRoot());
        if (!File.Exists(path)) return $"[lsp] {path} does not exist.";

        var (client, languageId, why) = await LspManager.ClientForAsync(path, ct);
        if (client is null) return $"[lsp] {why}.";
        try
        {
            var version = await client.SyncAsync(path, languageId);
            var uri = LspUri.FromPath(path);
            switch (action)
            {
                case "diagnostics":
                    var diags = await LspDiagnostics.WaitAsync(client, uri, version, TimeSpan.FromMilliseconds(Math.Max(3000, LspManager.Config().DiagnosticsWaitMs)), ct);
                    return diags is null
                        ? $"[lsp] {client.Name}: diagnostics for {Rel(path)} are still pending (the server has not finished checking). Try again shortly."
                        : LspDiagnostics.Format(path, diags.Value, MaxDiagnostics, errorsOnly: false) is { Length: > 0 } text ? text : $"{Rel(path)}: no problems reported by {client.Name}.";
                case "symbols":
                    if (!string.IsNullOrWhiteSpace(query)) return await WorkspaceSymbolsAsync(query!, ct, client);
                    return FormatDocumentSymbols(path, await client.RequestAsync("textDocument/documentSymbol", Doc(uri), ct));
                case "definition":
                case "references":
                case "hover":
                    if (line is not { } ln || ln < 1) return $"[lsp] '{action}' needs a 1-based line.";
                    if (string.IsNullOrWhiteSpace(symbol)) return $"[lsp] '{action}' needs the symbol text on line {ln}.";
                    var lines = await File.ReadAllLinesAsync(path, ct);
                    if (ln > lines.Length) return $"[lsp] {Rel(path)} has {lines.Length} lines.";
                    if (ResolveColumn(lines[ln - 1], symbol!) is not { } col) return $"[lsp] '{symbol}' is not on line {ln}: {lines[ln - 1].Trim()}";
                    var pos = Doc(uri);
                    pos["position"] = new JsonObject { ["line"] = ln - 1, ["character"] = col };
                    if (action == "hover")
                        return HoverText(await client.RequestAsync("textDocument/hover", pos, ct)) is { Length: > 0 } h ? h : $"No hover information for '{symbol}'.";
                    if (action == "references") pos["context"] = new JsonObject { ["includeDeclaration"] = true };
                    var result = await client.RequestAsync(action == "definition" ? "textDocument/definition" : "textDocument/references", pos, ct);
                    return FormatLocations(Locations(result), action == "definition" ? 20 : MaxReferences, $"No {action} found for '{symbol}'.");
                default:
                    return "[lsp] unknown action. Use diagnostics, definition, references, hover, symbols or status.";
            }
        }
        catch (LspException ex) { return $"[lsp] {client.Name}: {ex.Message}"; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return $"[lsp] {client.Name} did not answer in time."; }
    }

    private static async Task<string> WorkspaceSymbolsAsync(string query, CancellationToken ct, LspClient? only = null)
    {
        var clients = only is null ? LspManager.Running.ToList() : [only];
        if (clients.Count == 0) return "[lsp] no language server is running yet: ask about a file first so its server starts.";
        var lines = new List<string>();
        foreach (var c in clients)
        {
            try
            {
                var res = await c.RequestAsync("workspace/symbol", new JsonObject { ["query"] = query }, ct);
                if (res.ValueKind != JsonValueKind.Array) continue;
                foreach (var s in res.EnumerateArray())
                {
                    if (lines.Count >= MaxSymbols) break;
                    var name = s.TryGetProperty("name", out var n) ? n.GetString() : "?";
                    var loc = s.TryGetProperty("location", out var l) ? Locations(l).FirstOrDefault() : default;
                    lines.Add($"{Kind(s)} {name}" + (loc.Path is null ? "" : $"  {Rel(loc.Path)}:{loc.Line + 1}"));
                }
            }
            catch (LspException) { }
        }
        return lines.Count == 0 ? $"No workspace symbols match '{query}'." : string.Join("\n", lines);
    }

    /// <summary>The 0-based UTF-16 column of <paramref name="symbol"/> on <paramref name="text"/> (<c>name#N</c> = Nth occurrence), preferring whole-word matches.</summary>
    internal static int? ResolveColumn(string text, string symbol)
    {
        int nth = 1;
        var hash = symbol.LastIndexOf('#');
        if (hash > 0 && int.TryParse(symbol[(hash + 1)..], out var n) && n > 0) { nth = n; symbol = symbol[..hash]; }
        if (symbol.Length == 0) return null;
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            foreach (var wholeWord in new[] { true, false })
            {
                int seen = 0, at = 0;
                while ((at = text.IndexOf(symbol, at, comparison)) >= 0)
                {
                    bool ok = !wholeWord || ((at == 0 || !IsWord(text[at - 1])) && (at + symbol.Length >= text.Length || !IsWord(text[at + symbol.Length])));
                    if (ok && ++seen == nth) return at;
                    at++;
                }
            }
        return null;
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    private static JsonObject Doc(string uri) => new() { ["textDocument"] = new JsonObject { ["uri"] = uri } };

    /// <summary>Locations from a definition/references/symbol result (Location, Location[], LocationLink[]).</summary>
    internal static List<(string? Path, int Line, int Col)> Locations(JsonElement result)
    {
        var list = new List<(string?, int, int)>();
        void Add(JsonElement e)
        {
            var uriProp = e.TryGetProperty("targetUri", out var tu) ? tu : e.TryGetProperty("uri", out var u) ? u : default;
            var range = e.TryGetProperty("targetSelectionRange", out var tr) ? tr : e.TryGetProperty("range", out var r) ? r : default;
            if (uriProp.ValueKind != JsonValueKind.String || range.ValueKind != JsonValueKind.Object) return;
            var start = range.GetProperty("start");
            list.Add((LspUri.ToPath(uriProp.GetString()!), start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32()));
        }
        if (result.ValueKind == JsonValueKind.Array) foreach (var e in result.EnumerateArray()) Add(e);
        else if (result.ValueKind == JsonValueKind.Object) Add(result);
        return list.Distinct().ToList();
    }

    private static string FormatLocations(List<(string? Path, int Line, int Col)> locs, int cap, string empty)
    {
        if (locs.Count == 0) return empty;
        var sb = new StringBuilder();
        foreach (var (path, line, col) in locs.Take(cap))
        {
            sb.Append(path is null ? "?" : Rel(path)).Append(':').Append(line + 1).Append(':').Append(col + 1);
            if (path is not null && SourceLine(path, line) is { } src) sb.Append("  ").Append(src);
            sb.Append('\n');
        }
        if (locs.Count > cap) sb.Append($"... and {locs.Count - cap} more\n");
        return sb.ToString().TrimEnd();
    }

    private static string FormatDocumentSymbols(string path, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) return $"No symbols in {Rel(path)}.";
        var lines = new List<string>();
        void Walk(JsonElement s, int depth)
        {
            if (lines.Count >= MaxSymbols) return;
            var name = s.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var range = s.TryGetProperty("selectionRange", out var sr) ? sr : s.TryGetProperty("range", out var r) ? r
                : s.TryGetProperty("location", out var l) && l.TryGetProperty("range", out var lr) ? lr : default;
            int ln = range.ValueKind == JsonValueKind.Object ? range.GetProperty("start").GetProperty("line").GetInt32() + 1 : 0;
            lines.Add($"{new string(' ', depth * 2)}{Kind(s)} {name}  :{ln}");
            if (s.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var k in kids.EnumerateArray()) Walk(k, depth + 1);
        }
        foreach (var s in result.EnumerateArray()) Walk(s, 0);
        return $"{Rel(path)}\n" + string.Join("\n", lines) + (lines.Count >= MaxSymbols ? $"\n... (first {MaxSymbols})" : "");
    }

    private static string? HoverText(JsonElement hover)
    {
        if (hover.ValueKind != JsonValueKind.Object || !hover.TryGetProperty("contents", out var c)) return null;
        static string Part(JsonElement p) => p.ValueKind == JsonValueKind.String ? p.GetString() ?? ""
            : p.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        var text = c.ValueKind == JsonValueKind.Array ? string.Join("\n", c.EnumerateArray().Select(Part)) : Part(c);
        text = text.Trim();
        return text.Length > 4000 ? text[..4000] + "\n..." : text;
    }

    private static string Kind(JsonElement s) =>
        s.TryGetProperty("kind", out var k) && k.TryGetInt32(out var n) && n is >= 1 and <= 26
            ? new[] { "file", "module", "namespace", "package", "class", "method", "property", "field", "constructor", "enum",
                      "interface", "function", "variable", "constant", "string", "number", "boolean", "array", "object", "key",
                      "null", "enum-member", "struct", "event", "operator", "type-param" }[n - 1]
            : "symbol";

    /// <summary>Line <paramref name="line"/> (0-based) of a file, trimmed and capped, or null.</summary>
    internal static string? SourceLine(string path, int line)
    {
        try
        {
            var text = File.ReadLines(path).Skip(line).FirstOrDefault()?.Trim();
            return text is null ? null : text.Length > 160 ? text[..160] + "..." : text;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Rel(string path) => LspManager.Rel(path);
}
