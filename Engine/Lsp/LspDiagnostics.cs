using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MuxSwarm.Engine.Lsp;

/// <summary>
/// Waiting for and formatting diagnostics. A wait that ends without fresh diagnostics returns null
/// ("pending"), never an empty list, so a slow server is never reported as "no problems".
/// </summary>
internal static class LspDiagnostics
{
    /// <summary>Quiet period after the last publish before taking the result (servers publish syntax, then semantic).</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Fresh diagnostics for <paramref name="uri"/> at <paramref name="version"/>: pulled when the server supports
    /// <c>textDocument/diagnostic</c> (including support registered while waiting), else the pushed ones once they
    /// settle. Null when none arrived in time.
    /// </summary>
    internal static async Task<JsonElement?> WaitAsync(LspClient client, string uri, int? version, TimeSpan budget, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        var changed = new SemaphoreSlim(0);
        void OnChange(string u) { if (LspUri.Same(u, uri)) changed.Release(); }
        void OnPullRegistered() => changed.Release();
        client.DiagnosticsChanged += OnChange;
        client.PullDiagnosticsRegistered += OnPullRegistered;
        bool pulled = false;
        try
        {
            while (true)
            {
                if (!pulled && client.SupportsPullDiagnostics)
                {
                    pulled = true;   // one pull per wait; a failed or empty report falls back to push
                    try
                    {
                        var report = await client.RequestAsync("textDocument/diagnostic",
                            new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, timeout.Token);
                        if (report.ValueKind == JsonValueKind.Object && report.TryGetProperty("items", out var items)) return items.Clone();
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
                    catch (LspException) { /* fall back to push */ }
                }
                if (client.DiagnosticsFor(uri) is { } p && (p.Version is null || version is null || p.Version >= version))
                {
                    var quietFor = DateTime.UtcNow - p.At;
                    if (quietFor >= Settle) return p.Diagnostics;
                    try { await changed.WaitAsync(Settle - quietFor, timeout.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return p.Diagnostics; }
                    continue;
                }
                try { await changed.WaitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            }
        }
        finally { client.DiagnosticsChanged -= OnChange; client.PullDiagnosticsRegistered -= OnPullRegistered; }
    }

    /// <summary>Number of error-level diagnostics.</summary>
    internal static int Errors(JsonElement diagnostics) =>
        diagnostics.ValueKind == JsonValueKind.Array ? diagnostics.EnumerateArray().Count(d => SeverityRank(d) == 1) : 0;

    private static int SeverityRank(JsonElement d) => d.TryGetProperty("severity", out var s) && s.TryGetInt32(out var n) ? n : 1;

    /// <summary>
    /// <c>path:line:col severity: message (source)</c> lines, errors first, deduplicated, capped. Empty string when
    /// there is nothing to show (no diagnostics, or none at error level with <paramref name="errorsOnly"/>).
    /// </summary>
    internal static string Format(string path, JsonElement diagnostics, int cap, bool errorsOnly)
    {
        if (diagnostics.ValueKind != JsonValueKind.Array) return "";
        var rows = diagnostics.EnumerateArray()
            .Select(d => (Sev: SeverityRank(d), Line: Start(d).Line, Col: Start(d).Col, Text: Message(d)))
            .Where(r => !errorsOnly || r.Sev == 1)
            .Distinct()
            .OrderBy(r => r.Sev).ThenBy(r => r.Line).ThenBy(r => r.Col)
            .ToList();
        if (rows.Count == 0) return "";
        var rel = LspManager.Rel(path);
        var sb = new StringBuilder();
        foreach (var r in rows.Take(cap))
            sb.Append(rel).Append(':').Append(r.Line + 1).Append(':').Append(r.Col + 1).Append(' ')
              .Append(r.Sev switch { 1 => "error", 2 => "warning", 3 => "info", _ => "hint" }).Append(": ").Append(r.Text).Append('\n');
        if (rows.Count > cap) sb.Append($"... and {rows.Count - cap} more\n");
        return sb.ToString().TrimEnd();
    }

    private static (int Line, int Col) Start(JsonElement d) =>
        d.TryGetProperty("range", out var r) && r.TryGetProperty("start", out var s)
            ? (s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32()) : (0, 0);

    private static string Message(JsonElement d)
    {
        var msg = d.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
        msg = string.Join(' ', msg.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        if (msg.Length > 300) msg = msg[..300] + "...";
        var src = d.TryGetProperty("source", out var s) && s.GetString() is { Length: > 0 } so ? so : null;
        var code = d.TryGetProperty("code", out var c) ? c.ToString() : null;
        var tag = string.Join(" ", new[] { src, code }.Where(x => !string.IsNullOrEmpty(x)));
        return tag.Length > 0 ? $"{msg} ({tag})" : msg;
    }
}
