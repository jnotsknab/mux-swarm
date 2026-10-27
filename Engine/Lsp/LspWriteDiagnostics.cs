using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine.Lsp;

/// <summary>
/// Post-edit diagnostics: when a tool call changes files a configured language server covers (file tools,
/// shell, REPL, MCP), a short <c>[LSP]</c> block with the errors is appended to that tool's result. Changed
/// files come from the file tools' own <c>path</c> arguments and from <see cref="FileChangeTracker"/>. A
/// change goes to every tool call running when it landed; one that lands with no call running (a background
/// job) is reported on the next tool result. A server that has not finished checking is reported as
/// pending, never as clean. Inert unless <c>lsp.enabled</c> and <c>lsp.diagnosticsOnWrite</c>.
/// </summary>
internal static class LspWriteDiagnostics
{
    /// <summary>Most changed files reported per tool result; other files' new errors add at most this many more.</summary>
    internal const int MaxFiles = 5;

    /// <summary>Most error lines per file.</summary>
    internal const int MaxErrorsPerFile = 20;

    /// <summary>After a shell/REPL call returns, how long to let file notifications arrive before reporting.</summary>
    internal static TimeSpan ShellSettle = TimeSpan.FromMilliseconds(150);

    private sealed class Call { internal readonly ConcurrentDictionary<string, byte> Dirty = new(PathComparer); internal readonly DateTime Started = DateTime.UtcNow; }

    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    private static readonly ConcurrentDictionary<Call, byte> InFlight = new();
    private static readonly ConcurrentDictionary<string, byte> Orphans = new(PathComparer);

    /// <summary>True when post-edit diagnostics are on.</summary>
    internal static bool Enabled => LspManager.Active && LspManager.Config().DiagnosticsOnWrite;

    /// <summary>A file changed (from <see cref="FileChangeTracker"/>). Ignored unless a configured server covers it.</summary>
    internal static void OnFileChanged(string path, string change)
    {
        if (change == "deleted" || !Enabled || LspManager.ServerFor(path) is null) return;
        var calls = InFlight.Keys.ToArray();
        if (calls.Length == 0) Orphans[path] = 0;
        else foreach (var c in calls) c.Dirty[path] = 0;
    }

    /// <summary>Forget pending changes (tests, <c>/refresh</c>).</summary>
    internal static void Reset() { Orphans.Clear(); InFlight.Clear(); }

    /// <summary>
    /// Function invoker: runs the tool through <paramref name="inner"/> (or directly), then appends the
    /// diagnostics of the files it changed. The result is unchanged when nothing covered changed.
    /// </summary>
    internal static async ValueTask<object?> InvokeAsync(FunctionInvocationContext ctx, CancellationToken ct,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? inner)
    {
        if (!Enabled) return inner != null ? await inner(ctx, ct) : await ctx.Function.InvokeAsync(ctx.Arguments, ct);
        var call = new Call();
        InFlight[call] = 0;
        object? result;
        try
        {
            result = inner != null ? await inner(ctx, ct) : await ctx.Function.InvokeAsync(ctx.Arguments, ct);
            foreach (var p in ArgPaths(ctx))
                if (LspManager.ServerFor(p) is not null) call.Dirty[p] = 0;
            if (NativeTools.NativeToolRegistry.VirtualServer(ctx.Function.Name) == NativeTools.NativeToolRegistry.ShellServer)
                await Task.Delay(ShellSettle, ct);
        }
        finally { InFlight.TryRemove(call, out _); }
        foreach (var p in Orphans.Keys)
            if (Orphans.TryRemove(p, out _)) call.Dirty[p] = 0;
        if (call.Dirty.IsEmpty) return result;
        string block;
        try { block = await ReportAsync(call.Dirty.Keys.Where(File.Exists).ToList(), call.Started, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return result; }
        return block.Length == 0 ? result : Append(result, block);
    }

    // The native file tools that write say which file they wrote; no need to wait for the watcher.
    private static readonly string[] WritingFileTools = ["Filesystem_write_file", "Filesystem_edit_file", "Filesystem_move_file"];

    private static IEnumerable<string> ArgPaths(FunctionInvocationContext ctx)
    {
        if (!WritingFileTools.Contains(ctx.Function.Name, StringComparer.OrdinalIgnoreCase)) yield break;
        foreach (var key in new[] { "path", "destination" })
            if (ctx.Arguments.TryGetValue(key, out var v) && (v is string s ? s : v is JsonElement { ValueKind: JsonValueKind.String } j ? j.GetString() : null) is { Length: > 0 } p)
            {
                string? full = null;
                try { full = Path.GetFullPath(p, LspManager.WorkspaceRoot()); } catch (ArgumentException) { } catch (NotSupportedException) { }
                if (full is not null) yield return full;
            }
    }

    /// <summary>The <c>[LSP]</c> block for <paramref name="paths"/>, or empty when there is nothing to say.</summary>
    internal static async Task<string> ReportAsync(IReadOnlyList<string> paths, DateTime since, CancellationToken ct)
    {
        var budget = TimeSpan.FromMilliseconds(Math.Clamp(LspManager.Config().DiagnosticsWaitMs, 100, 30_000));
        var files = paths.OrderBy(p => p, PathComparer).Take(MaxFiles).ToList();
        var results = await Task.WhenAll(files.Select(async path =>
        {
            var (client, languageId, why) = await LspManager.ClientForAsync(path, ct);
            if (client is null) return (path, client, Text: (string?)null, Pending: false, Why: why);
            var version = await client.SyncAsync(path, languageId);
            var diags = await LspDiagnostics.WaitAsync(client, LspUri.FromPath(path), version, budget, ct);
            return (path, client, Text: diags is { } d ? LspDiagnostics.Format(path, d, MaxErrorsPerFile, errorsOnly: true) : null, Pending: diags is null, Why: why);
        }));

        var sb = new StringBuilder();
        var clean = new List<string>();
        foreach (var r in results)
        {
            if (r.client is null) { sb.Append("[LSP] ").Append(LspManager.Rel(r.path)).Append(": ").Append(r.Why).Append('\n'); continue; }
            if (r.Pending) { sb.Append("[LSP] ").Append(LspManager.Rel(r.path)).Append(": diagnostics pending (").Append(r.client.Name).Append(" is still checking)\n"); continue; }
            if (string.IsNullOrEmpty(r.Text)) { clean.Add(LspManager.Rel(r.path)); continue; }
            sb.Append("[LSP] errors in ").Append(LspManager.Rel(r.path)).Append(":\n").Append(r.Text).Append('\n');
        }
        if (paths.Count > files.Count) sb.Append($"[LSP] ... {paths.Count - files.Count} more changed file(s) not checked\n");

        // Errors this edit caused in other open files (e.g. a renamed method's callers).
        var seen = new HashSet<string>(files, PathComparer);
        int others = 0;
        foreach (var client in results.Select(r => r.client).OfType<LspClient>().Distinct())
            foreach (var (uri, pub) in client.AllDiagnostics)
            {
                if (others >= MaxFiles || pub.At < since || LspUri.ToPath(uri) is not { } other || !seen.Add(other)) continue;
                var text = LspDiagnostics.Format(other, pub.Diagnostics, MaxErrorsPerFile, errorsOnly: true);
                if (text.Length == 0) continue;
                sb.Append("[LSP] errors in another file, ").Append(LspManager.Rel(other)).Append(":\n").Append(text).Append('\n');
                others++;
            }

        if (clean.Count > 0) sb.Append("[LSP] no errors in ").Append(string.Join(", ", clean)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    // Keep the tool's own result; add the block after it (MCP content lists keep their parts).
    private static object? Append(object? result, string block) => result switch
    {
        null => block,
        string s => s + "\n\n" + block,
        JsonElement { ValueKind: JsonValueKind.String } j => j.GetString() + "\n\n" + block,
        IEnumerable<AIContent> parts => parts.Append(new TextContent(block)).ToList(),
        _ => JsonSerializer.Serialize(result) + "\n\n" + block,
    };
}
