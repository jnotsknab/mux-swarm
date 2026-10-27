using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MuxSwarm.Engine.Lsp;

/// <summary>
/// One running language server: JSON-RPC 2.0 over stdio with <c>Content-Length</c> framing. Requests
/// are matched to replies by id; server notifications (diagnostics) and server requests
/// (<c>workspace/configuration</c>, capability registration) are handled here. Files are synced with
/// full text and an increasing version. Only the process this client started is ever stopped.
/// </summary>
internal sealed class LspClient : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Published> _diagnostics = new(StringComparer.Ordinal);
    private readonly LspServerConfig _config;
    private long _nextId;
    private volatile bool _disposed;

    /// <summary>The latest <c>publishDiagnostics</c> for a document, with the version it named (if any) and when it came.</summary>
    internal sealed record Published(JsonElement Diagnostics, int? Version, DateTime At);

    /// <summary>Config name of the server.</summary>
    internal string Name { get; }

    /// <summary>Project root (folder) this server was started for.</summary>
    internal string Root { get; }

    /// <summary>The server's <c>capabilities</c> from <c>initialize</c>.</summary>
    internal JsonElement Capabilities { get; private set; }

    /// <summary>True until the process exits or the client is disposed.</summary>
    internal bool IsRunning => !_disposed && !_process.HasExited;

    /// <summary>Last use, for idle stop.</summary>
    internal DateTime LastUsed { get; private set; } = DateTime.UtcNow;

    /// <summary>Raised when a document's diagnostics arrive (uri).</summary>
    internal event Action<string>? DiagnosticsChanged;

    private LspClient(string name, string root, LspServerConfig config, Process process)
    {
        Name = name; Root = root; _config = config; _process = process;
        _stdin = process.StandardInput.BaseStream;
    }

    /// <summary>Start the server and run the <c>initialize</c> handshake. Throws when it cannot start or does not answer.</summary>
    internal static async Task<LspClient> StartAsync(string name, string root, LspServerConfig config, string executable, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in config.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in config.Env) psi.Environment[k] = v;
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {executable}");
        var client = new LspClient(name, root, config, process);
        _ = Task.Run(client.ReadLoopAsync);
        _ = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync() is not null) { } } catch { /* stderr is not used */ } });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Clamp(config.StartupTimeoutMs, 1000, 300_000));
            var init = await client.RequestAsync("initialize", new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = LspUri.FromPath(root),
                ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = LspUri.FromPath(root), ["name"] = Path.GetFileName(root) }),
                ["initializationOptions"] = config.InitializationOptions is { } io ? JsonNode.Parse(io.GetRawText()) : null,
                ["capabilities"] = ClientCapabilities(),
            }, timeout.Token);
            client.Capabilities = init.ValueKind == JsonValueKind.Object && init.TryGetProperty("capabilities", out var caps) ? caps.Clone() : default;
            await client.NotifyAsync("initialized", new JsonObject());
            if (config.Settings is { } s)
                await client.NotifyAsync("workspace/didChangeConfiguration", new JsonObject { ["settings"] = JsonNode.Parse(s.GetRawText()) });
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static JsonObject ClientCapabilities() => new()
    {
        ["workspace"] = new JsonObject
        {
            ["configuration"] = true,
            ["workspaceFolders"] = true,
            ["didChangeWatchedFiles"] = new JsonObject { ["dynamicRegistration"] = true },
        },
        ["textDocument"] = new JsonObject
        {
            ["synchronization"] = new JsonObject { ["didSave"] = true },
            ["publishDiagnostics"] = new JsonObject { ["versionSupport"] = true },
            ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = true },
            ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("plaintext", "markdown") },
            ["definition"] = new JsonObject { ["linkSupport"] = true },
            ["references"] = new JsonObject(),
            ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
        },
        ["window"] = new JsonObject { ["workDoneProgress"] = true },
        ["general"] = new JsonObject { ["positionEncodings"] = new JsonArray("utf-16") },
    };

    /// <summary>
    /// Bring the server's copy of <paramref name="path"/> in line with the disk: <c>didOpen</c> the first time,
    /// then <c>didChange</c> (full text, version + 1) when the content changed. Returns the document version,
    /// or null when the file cannot be read.
    /// </summary>
    internal async Task<int?> SyncAsync(string path, string languageId)
    {
        LastUsed = DateTime.UtcNow;
        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        var uri = LspUri.FromPath(path);
        if (!_versions.TryGetValue(uri, out var version))
        {
            _versions[uri] = 1; _texts[uri] = text;
            await NotifyAsync("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = languageId, ["version"] = 1, ["text"] = text },
            });
            return 1;
        }
        if (_texts.TryGetValue(uri, out var known) && known == text) return version;
        version++;
        _versions[uri] = version; _texts[uri] = text;
        _diagnostics.TryRemove(uri, out _);   // older diagnostics describe text that is gone
        await NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        });
        await NotifyAsync("workspace/didChangeWatchedFiles", new JsonObject
        {
            ["changes"] = new JsonArray(new JsonObject { ["uri"] = uri, ["type"] = 2 }),
        });
        return version;
    }

    /// <summary>The last published diagnostics for <paramref name="uri"/>, or null when none arrived since the last change.</summary>
    internal Published? DiagnosticsFor(string uri) => _diagnostics.TryGetValue(uri, out var d) ? d : null;

    /// <summary>Every document with published diagnostics (uri -> latest publish).</summary>
    internal IReadOnlyDictionary<string, Published> AllDiagnostics => _diagnostics;

    /// <summary>True when the server supports pull diagnostics (<c>textDocument/diagnostic</c>).</summary>
    internal bool SupportsPullDiagnostics =>
        Capabilities.ValueKind == JsonValueKind.Object && Capabilities.TryGetProperty("diagnosticProvider", out var p) && p.ValueKind != JsonValueKind.Null;

    /// <summary>Send a request and wait for its result. Throws <see cref="LspException"/> on an error reply.</summary>
    internal async Task<JsonElement> RequestAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        LastUsed = DateTime.UtcNow;
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
            using var reg = ct.Register(() =>
            {
                if (_pending.TryRemove(id, out var t) && t.TrySetCanceled())
                    _ = NotifyAsync("$/cancelRequest", new JsonObject { ["id"] = id });
            });
            return await tcs.Task;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Send a notification.</summary>
    internal Task NotifyAsync(string method, JsonNode? @params) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params });

    private async Task WriteAsync(JsonObject message)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LspClient));
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _stdin.WriteAsync(header);
            await _stdin.WriteAsync(body);
            await _stdin.FlushAsync();
        }
        catch (IOException) { /* process gone: pending requests fail in the read loop */ }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        var stdout = _process.StandardOutput.BaseStream;
        try
        {
            while (!_disposed)
            {
                var body = await LspFraming.ReadMessageAsync(stdout);
                if (body is null) break;
                JsonElement msg;
                try { msg = JsonDocument.Parse(body).RootElement.Clone(); }
                catch (JsonException) { continue; }
                Dispatch(msg);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        foreach (var (_, tcs) in _pending) tcs.TrySetException(new LspException($"{Name}: the server exited"));
    }

    private void Dispatch(JsonElement msg)
    {
        bool hasId = msg.TryGetProperty("id", out var id);
        bool hasMethod = msg.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String;
        if (hasId && !hasMethod)
        {
            if (id.TryGetInt64(out var n) && _pending.TryRemove(n, out var tcs))
            {
                if (msg.TryGetProperty("error", out var err))
                    tcs.TrySetException(new LspException(err.TryGetProperty("message", out var em) ? em.GetString() ?? "error" : "error"));
                else
                    tcs.TrySetResult(msg.TryGetProperty("result", out var r) ? r.Clone() : default);
            }
            return;
        }
        if (!hasMethod) return;
        var method = m.GetString();
        var p = msg.TryGetProperty("params", out var pp) ? pp : default;
        if (hasId) { _ = AnswerAsync(id.Clone(), method!, p); return; }
        if (method == "textDocument/publishDiagnostics" && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("uri", out var u) && u.GetString() is { } uri)
        {
            int? ver = p.TryGetProperty("version", out var v) && v.TryGetInt32(out var vi) ? vi : null;
            var key = _versions.Keys.FirstOrDefault(k => LspUri.Same(k, uri)) ?? uri;
            // A publish for an older version than we sent is stale.
            if (ver is { } pv && _versions.TryGetValue(key, out var cur) && pv < cur) return;
            var diags = p.TryGetProperty("diagnostics", out var d) ? d.Clone() : JsonDocument.Parse("[]").RootElement.Clone();
            _diagnostics[key] = new Published(diags, ver, DateTime.UtcNow);
            DiagnosticsChanged?.Invoke(key);
        }
    }

    // Server -> client requests: answer the ones servers block on; null for the rest.
    private async Task AnswerAsync(JsonElement id, string method, JsonElement p)
    {
        JsonNode? result = null;
        if (method == "workspace/configuration" && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("items", out var items))
        {
            var arr = new JsonArray();
            foreach (var item in items.EnumerateArray())
            {
                JsonNode? value = _config.Settings is { } s ? JsonNode.Parse(s.GetRawText()) : null;
                if (value is JsonObject && item.TryGetProperty("section", out var sec) && sec.GetString() is { Length: > 0 } section)
                    foreach (var part in section.Split('.'))
                        value = value is JsonObject o && o.TryGetPropertyValue(part, out var next) ? next?.DeepClone() : null;
                arr.Add(value);
            }
            result = arr;
        }
        else if (method == "workspace/workspaceFolders")
            result = new JsonArray(new JsonObject { ["uri"] = LspUri.FromPath(Root), ["name"] = Path.GetFileName(Root) });
        try { await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result }); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Ask the server to stop (LSP exit handshake), then end the process tree if it lingers.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (!_process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { RequestAsync("shutdown", null, cts.Token).Wait(cts.Token); NotifyAsync("exit", null).Wait(500); }
                catch { /* best effort */ }
            }
        }
        finally
        {
            _disposed = true;
            try { if (!_process.WaitForExit(1000)) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            _process.Dispose();
        }
    }
}

/// <summary>An error reply from a language server, or the server going away mid-request.</summary>
internal sealed class LspException(string message) : Exception(message);

/// <summary>LSP base-protocol framing: <c>Content-Length: N\r\n\r\n</c> followed by N bytes of UTF-8 JSON.</summary>
internal static class LspFraming
{
    /// <summary>Read one message body, or null at end of stream.</summary>
    internal static async Task<byte[]?> ReadMessageAsync(Stream stream)
    {
        int length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            char c = (char)one[0];
            if (c == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0) { if (length >= 0) break; continue; }
                var colon = text.IndexOf(':');
                if (colon > 0 && text[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(text[(colon + 1)..].Trim(), out var n)) length = n;
                continue;
            }
            line.Append(c);
        }
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(read, length - read));
            if (n == 0) return null;
            read += n;
        }
        return body;
    }
}

/// <summary>file:// URIs as servers expect them on every platform (<c>file:///C:/dir/a%20b.cs</c>, <c>file:///home/x</c>).</summary>
internal static class LspUri
{
    /// <summary>The URI for a local path.</summary>
    internal static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>The local path for a <c>file:</c> URI (null for other schemes).</summary>
    internal static string? ToPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? Path.GetFullPath(u.LocalPath) : null;

    /// <summary>True when two URIs name the same file (drive letter case and escaping differ between servers).</summary>
    internal static bool Same(string a, string b) =>
        string.Equals(ToPath(a), ToPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
