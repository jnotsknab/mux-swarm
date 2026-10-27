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
    /// <c>textDocument/diagnostic</c>, else the pushed ones once they settle. Null when none arrived in time.
    /// </summary>
    internal static async Task<JsonElement?> WaitAsync(LspClient client, string uri, int? version, TimeSpan budget, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        if (client.SupportsPullDiagnostics)
        {
            try
            {
                var report = await client.RequestAsync("textDocument/diagnostic",
                    new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, timeout.Token);
                if (report.ValueKind == JsonValueKind.Object && report.TryGetProperty("items", out var items)) return items.Clone();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            catch (LspException) { /* fall back to push */ }
        }
        var changed = new SemaphoreSlim(0);
        void OnChange(string u) { if (LspUri.Same(u, uri)) changed.Release(); }
        client.DiagnosticsChanged += OnChange;
        try
        {
            while (true)
            {
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
        finally { client.DiagnosticsChanged -= OnChange; }
    }

    /// <summary>Number of error-level diagnostics.</summary>
    internal static int Errors(JsonElement diagnostics) =>
        diagnostics.ValueKind == JsonValueKind.Array ? diagnostics.EnumerateArray().Count(d => SeverityRank(d) == 1) : 0;

    private static int SeverityRank(JsonElement d) => d.TryGetProperty("severity", out var s) && s.TryGetInt32(out var n) ? n : 1;
}
