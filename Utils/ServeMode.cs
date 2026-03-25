using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using MuxSwarm.State;

namespace MuxSwarm.Utils;

/// <summary>
/// Starts an embedded Kestrel server that serves the web UI and bridges
/// WebSocket connections to the agent loop via MuxConsole's existing
/// StdioMode NDJSON protocol.
///
/// Integration:
///   1. Redirects Console.Out -> WebSocket broadcast (NDJSON lines)
///   2. Sets MuxConsole.InputOverride -> reads from WebSocket messages
///   3. The normal AppLoop interactive while-loop runs unchanged
///
/// Endpoints:
///   /ws                              WebSocket bridge to agent NDJSON
///   /api/list/{type}[/{path}]        File browser (sandbox | sessions)
///   /api/download/{type}/{path}      File download
///   /api/upload?dir=subdir           File upload (multipart or base64 JSON)
///   /*                               Static files from wwwroot
/// </summary>
public static class ServeMode
{
    private static readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private static readonly Channel<string> _inputChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string _sandboxRoot = "";
    private static string _sessionsRoot = "";

    /// <summary>
    /// Start Kestrel in the background, redirect I/O, then return.
    /// The caller (AppLoop) continues with its normal interactive loop,
    /// which now reads from WebSocket input and writes NDJSON to WebSocket clients.
    /// </summary>
    public static async Task StartAsync(int port = 6723)
    {
        MuxConsole.StdioMode = true;

        _sandboxRoot = App.Config.Filesystem?.SandboxPath ?? "";
        _sessionsRoot = PlatformContext.SessionsDirectory;

        if (!string.IsNullOrEmpty(_sandboxRoot))
        {
            try { Directory.CreateDirectory(Path.Combine(_sandboxRoot, "uploads")); }
            catch { /* non-fatal */ }
        }

        var originalOut = Console.Out;
        Console.SetOut(new BroadcastWriter(originalOut));

        MuxConsole.InputOverride = new WsChannelReader(_inputChannel.Reader);

        var wwwroot = ResolveWwwroot();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{App.Config.ServeAddress}:{port}");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        app.Map("/ws", HandleWebSocket);

        app.MapGet("/api/list/{type}", HandleListFiles);
        app.MapGet("/api/list/{type}/{**path}", HandleListFiles);
        app.MapGet("/api/download/{type}/{**path}", HandleDownload);
        app.MapPost("/api/upload", HandleUpload);

        if (wwwroot != null && Directory.Exists(wwwroot))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot),
                RequestPath = "",
            });

            app.MapFallback(async context =>
            {
                var indexPath = Path.Combine(wwwroot, "index.html");
                if (File.Exists(indexPath))
                {
                    context.Response.ContentType = "text/html";
                    context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
                    await context.Response.SendFileAsync(indexPath);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("index.html not found");
                }
            });
        }
        else
        {
            MuxConsole.WriteWarning("wwwroot not found -- web UI will not be available");
        }

        _ = Task.Run(async () =>
        {
            try { await app.RunAsync(); }
            catch (Exception ex) { MuxConsole.WriteError($"Kestrel error: {ex.Message}"); }
        });

        await Task.Delay(200);
        MuxConsole.WriteSuccess($"Web UI available at http://{App.Config.ServeAddress}:{port}");

        // Show all network interfaces if bound to 0.0.0.0
        try
        {
            if (App.Config.ServeAddress == "0.0.0.0")
            {
                foreach (var ip in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                             .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                {
                    MuxConsole.WriteInfo($"  Also available at http://{ip}:{port}");
                }
            }
        }
        catch { /* non-fatal */ }
    }

    private static async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var ws = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid().ToString("N")[..8];
        _clients[id] = ws;

        try
        {
            // Replay buffer
            foreach (var line in _replayBuffer.ToArray())
            {
                if (ws.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }

            // Read loop
            var buf = new byte[8192];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buf, CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var msg = Encoding.UTF8.GetString(buf, 0, result.Count).Trim();

                    if (msg == "__CANCEL__")
                    {
                        StdinCancelMonitor.Instance?.FireCancel();
                        continue;
                    }

                    if (string.IsNullOrEmpty(msg))
                        continue;
                    
                    await _inputChannel.Writer.WriteAsync(msg);
                    HookWorker.Enqueue(new HookEvent
                    {
                        Event = "user_input",
                        Agent = "Unknown",
                        Text = msg,
                        Timestamp = DateTimeOffset.UtcNow
                    });
    
                    BroadcastLine(JsonSerializer.Serialize(new { type = "user_input", text = msg }));
                    
                }
            }
        }
        catch (WebSocketException) { /* client disconnected */ }
        finally
        {
            _clients.TryRemove(id, out _);
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { /* ignore */ }
            }
        }
    }

    private static async Task HandleListFiles(HttpContext context)
    {
        var type = context.Request.RouteValues["type"]?.ToString() ?? "sandbox";
        var subpath = context.Request.RouteValues["path"]?.ToString() ?? "";

        var root = GetRoot(type);
        if (root == null)
        {
            await WriteJson(context, 400, new { error = $"Unknown type: {type}", items = Array.Empty<object>() });
            return;
        }

        if (!Directory.Exists(root))
        {
            await WriteJson(context, 200, new { error = $"Directory not found: {root}", path = "", items = Array.Empty<object>() });
            return;
        }

        var target = string.IsNullOrEmpty(subpath) ? root : SafeJoin(root, subpath);
        if (target == null || !Directory.Exists(target))
        {
            await WriteJson(context, 404, new { error = "Path not found", items = Array.Empty<object>() });
            return;
        }

        try
        {
            var items = new List<object>();
            var di = new DirectoryInfo(target);

            foreach (var entry in di.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new
                {
                    name = entry.Name,
                    path = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'),
                    isDir = true,
                    size = (long?)null,
                    mtime = entry.LastWriteTimeUtc.ToString("o"),
                    ext = (string?)null,
                });
            }

            foreach (var entry in di.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    items.Add(new
                    {
                        name = entry.Name,
                        path = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'),
                        isDir = false,
                        size = (long?)entry.Length,
                        mtime = entry.LastWriteTimeUtc.ToString("o"),
                        ext = entry.Extension.ToLowerInvariant(),
                    });
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }

            var relPath = target == root ? "" : Path.GetRelativePath(root, target).Replace('\\', '/');
            await WriteJson(context, 200, new { path = relPath, items });
        }
        catch (UnauthorizedAccessException)
        {
            await WriteJson(context, 403, new { error = "Permission denied", items = Array.Empty<object>() });
        }
    }


    private static async Task HandleDownload(HttpContext context)
    {
        var type = context.Request.RouteValues["type"]?.ToString() ?? "sandbox";
        var subpath = context.Request.RouteValues["path"]?.ToString() ?? "";

        var root = GetRoot(type);
        if (root == null)
        {
            await WriteJson(context, 400, new { error = $"Unknown type: {type}" });
            return;
        }

        if (string.IsNullOrEmpty(subpath))
        {
            await WriteJson(context, 400, new { error = "No file specified" });
            return;
        }

        var filepath = SafeJoin(root, subpath);
        if (filepath == null || !File.Exists(filepath))
        {
            await WriteJson(context, 404, new { error = "File not found" });
            return;
        }

        var filename = Path.GetFileName(filepath);
        context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{filename}\"");
        await context.Response.SendFileAsync(filepath);
    }


    private static async Task HandleUpload(HttpContext context)
    {
        var targetSubdir = context.Request.Query["dir"].FirstOrDefault() ?? "uploads";

        if (string.IsNullOrEmpty(_sandboxRoot))
        {
            await WriteJson(context, 500, new { error = "Sandbox not configured" });
            return;
        }

        var targetDir = SafeJoin(_sandboxRoot, targetSubdir);
        if (targetDir == null)
        {
            await WriteJson(context, 400, new { error = "Invalid upload directory" });
            return;
        }

        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch (Exception ex)
        {
            await WriteJson(context, 500, new { error = $"Cannot create directory: {ex.Message}" });
            return;
        }

        var contentType = context.Request.ContentType ?? "";

        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(context.Request.Body);
                var root = doc.RootElement;

                var b64 = root.TryGetProperty("base64", out var b64El) ? b64El.GetString() ?? "" : "";
                var filename = root.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() ?? "upload.bin" : "upload.bin";

                if (string.IsNullOrEmpty(b64))
                {
                    await WriteJson(context, 400, new { error = "Missing base64 data" });
                    return;
                }

                if (b64.Contains(','))
                    b64 = b64[(b64.IndexOf(',') + 1)..];

                var fileBytes = Convert.FromBase64String(b64);
                var uniqueName = UniqueFilename(filename);
                var filepath = Path.Combine(targetDir, uniqueName);
                await File.WriteAllBytesAsync(filepath, fileBytes);

                await WriteJson(context, 200, new { filename = filepath, name = uniqueName, size = fileBytes.Length });
            }
            catch (FormatException)
            {
                await WriteJson(context, 400, new { error = "Invalid base64 data" });
            }
            catch (JsonException)
            {
                await WriteJson(context, 400, new { error = "Invalid JSON" });
            }
            return;
        }

        if (!context.Request.HasFormContentType)
        {
            await WriteJson(context, 400, new { error = "Expected multipart form or JSON" });
            return;
        }

        try
        {
            var form = await context.Request.ReadFormAsync();
            var uploaded = new List<object>();

            foreach (var file in form.Files)
            {
                if (file.Length == 0) continue;

                var uniqueName = UniqueFilename(file.FileName);
                var filepath = Path.Combine(targetDir, uniqueName);

                await using (var stream = new FileStream(filepath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                uploaded.Add(new
                {
                    filename = filepath,
                    name = uniqueName,
                    originalName = file.FileName,
                    size = file.Length,
                });
            }

            if (uploaded.Count == 0)
            {
                await WriteJson(context, 400, new { error = "No files in request" });
                return;
            }

            var first = (dynamic)uploaded[0];
            await WriteJson(context, 200, new { filename = (string)first.filename, files = uploaded });
        }
        catch (Exception ex)
        {
            await WriteJson(context, 500, new { error = $"Upload failed: {ex.Message}" });
        }
    }

    //helpers
    private static string? GetRoot(string type) => type.ToLowerInvariant() switch
    {
        "sandbox" => string.IsNullOrEmpty(_sandboxRoot) ? null : _sandboxRoot,
        "sessions" => string.IsNullOrEmpty(_sessionsRoot) ? null : _sessionsRoot,
        _ => null,
    };

    /// <summary>Safely join root + subpath, preventing directory traversal.</summary>
    private static string? SafeJoin(string root, string subpath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, subpath));
            var rootFull = Path.GetFullPath(root);
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static string UniqueFilename(string original)
    {
        var ext = Path.GetExtension(original);
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var uid = Guid.NewGuid().ToString("N")[..8];
        return $"{stamp}_{uid}{ext}";
    }

    private static async Task WriteJson(HttpContext context, int status, object data)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(data, _jsonOpts));
    }


    private static readonly ConcurrentQueue<string> _replayBuffer = new();
    private const int MaxReplay = 500;

    private static void BufferLine(string line)
    {
        _replayBuffer.Enqueue(line);
        while (_replayBuffer.Count > MaxReplay)
            _replayBuffer.TryDequeue(out _);
    }

    private static void BroadcastLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        BufferLine(line);

        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        var stale = new List<string>();

        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open)
            {
                stale.Add(id);
                continue;
            }

            try
            {
                ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted) _clients.TryRemove(id, out _);
                    });
            }
            catch
            {
                stale.Add(id);
            }
        }

        foreach (var id in stale)
            _clients.TryRemove(id, out _);
    }

    private static string? ResolveWwwroot()
    {
        var runtimeDir = Path.Combine(PlatformContext.BaseDirectory, "Runtime", "mux-web-app");
        if (Directory.Exists(runtimeDir)) return Path.GetFullPath(runtimeDir);

        var binDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(binDir)) return Path.GetFullPath(binDir);

        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        if (Directory.Exists(cwd)) return Path.GetFullPath(cwd);

        return null;
    }

    private sealed class BroadcastWriter : TextWriter
    {
        private readonly TextWriter _passthrough;
        private readonly StringBuilder _lineBuffer = new();
        private readonly object _lock = new();

        public override Encoding Encoding => Encoding.UTF8;

        public BroadcastWriter(TextWriter passthrough)
        {
            _passthrough = passthrough;
        }

        public override void Write(char value)
        {
            lock (_lock)
            {
                if (value == '\n')
                {
                    var line = _lineBuffer.ToString();
                    _lineBuffer.Clear();
                    BroadcastLine(line);
                    _passthrough.WriteLine(line);
                }
                else if (value != '\r')
                {
                    _lineBuffer.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            foreach (var c in value) Write(c);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        public override void Flush()
        {
            lock (_lock)
            {
                if (_lineBuffer.Length > 0)
                {
                    var line = _lineBuffer.ToString();
                    _lineBuffer.Clear();
                    BroadcastLine(line);
                    _passthrough.WriteLine(line);
                }
                _passthrough.Flush();
            }
        }
    }

    private sealed class WsChannelReader : TextReader
    {
        private readonly ChannelReader<string> _channel;

        public WsChannelReader(ChannelReader<string> channel)
        {
            _channel = channel;
        }

        public override string? ReadLine()
        {
            try
            {
                return _channel.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }
}