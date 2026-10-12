using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// One running extension process. Mux talks to it with newline-delimited JSON-RPC 2.0 over
/// stdin/stdout: notifications (<c>initialize</c>, <c>event</c>) and requests answered by
/// <see cref="RequestAsync"/>. stdout carries only protocol messages; stderr is kept as a short
/// tail for <c>/extensions</c>. Never started through a shell. Every outgoing message goes
/// through one bounded queue, so the process sees them in send order; writes never block the
/// caller, and new messages are dropped (requests answered with <c>default</c>) when it falls behind.
/// </summary>
internal sealed class ExtensionProcess : IDisposable
{
    /// <summary>Protocol version sent in <c>initialize</c>; bumped on breaking changes.</summary>
    internal const int ProtocolVersion = 1;

    private const int StderrTailLines = 20;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process _process;
    private readonly RpcCorrelator _rpc = new();
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Queue<string> _stderrTail = new();
    private readonly HashSet<string> _subscriptions;

    /// <summary>The extension name (from its manifest).</summary>
    internal string Name { get; }

    /// <summary>True until the process exits or is disposed.</summary>
    internal bool IsRunning { get; private set; } = true;

    /// <summary>Receives <c>command/output</c> text while an extension command runs; null otherwise.</summary>
    internal volatile Action<string>? CommandOutput;

    private ExtensionProcess(string name, Process process, IEnumerable<string> events)
    {
        Name = name;
        _process = process;
        _subscriptions = new HashSet<string>(events, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Start <paramref name="spec"/> in <paramref name="dir"/> and send <c>initialize</c>.
    /// A command containing a path separator resolves against the extension folder; a bare name
    /// is looked up on PATH. Throws when the process cannot start.
    /// </summary>
    internal static ExtensionProcess Start(string name, string dir, ExtensionProcessSpec spec, IEnumerable<string> events)
    {
        var command = spec.Command.Trim();
        if (command.Contains('/') || command.Contains('\\')) command = Path.GetFullPath(command, dir);

        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // No BOM: a BOM before the first JSON line breaks the extension's parser on Windows.
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in spec.Args ?? []) psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"could not start '{command}'.");
        ProcessCleanup.Instance.Track(process);

        var ext = new ExtensionProcess(name, process, events);
        _ = Task.Run(ext.ReadStdoutAsync);
        _ = Task.Run(ext.ReadStderrAsync);
        _ = Task.Run(ext.DrainOutboxAsync);
        ext.Notify("initialize", new { protocolVersion = ProtocolVersion, muxVersion = App.Version, name, dir });
        return ext;
    }

    /// <summary>True when the extension subscribed to <paramref name="eventName"/> (or to "*").</summary>
    internal bool Wants(string eventName) => IsRunning && (_subscriptions.Contains(eventName) || _subscriptions.Contains("*"));

    /// <summary>Queue an <c>event</c> notification. Dropped (never blocks) when the queue is full.</summary>
    internal void PostEvent(HookEvent e) => _outbox.Writer.TryWrite(Message("event", e, id: null));

    /// <summary>Send a notification (no response expected).</summary>
    internal void Notify(string method, object? @params) => _outbox.Writer.TryWrite(Message(method, @params, id: null));

    /// <summary>
    /// Send a request and wait for its response message. Returns <c>default</c> (Undefined) on
    /// timeout, cancellation or when the process exits first.
    /// </summary>
    internal async Task<JsonElement> RequestAsync(string method, object? @params, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsRunning) return default;
        var (id, response) = _rpc.Begin(timeout, ct);
        if (!_outbox.Writer.TryWrite(Message(method, @params, id))) _rpc.Complete(id, default);
        var reply = await response;
        // Cancelled by the caller (Esc) rather than timed out or exited: tell the process to stop.
        if (ct.IsCancellationRequested && IsRunning) Notify("$/cancelRequest", new { id });
        return reply;
    }

    /// <summary>The last lines the extension wrote to stderr (oldest first).</summary>
    internal IReadOnlyList<string> StderrTail { get { lock (_stderrTail) return _stderrTail.ToList(); } }

    private static string Message(string method, object? @params, long? id)
    {
        var msg = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is { } i) msg["id"] = i;
        if (@params is not null) msg["params"] = @params;
        return JsonSerializer.Serialize(msg, JsonOpts);
    }

    // Single writer: the only code that touches stdin while the process runs.
    private async Task DrainOutboxAsync()
    {
        try
        {
            await foreach (var line in _outbox.Reader.ReadAllAsync())
            {
                if (!IsRunning) break;
                await _process.StandardInput.WriteLineAsync(line);
                await _process.StandardInput.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or ChannelClosedException) { }
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!_rpc.TryComplete(doc.RootElement)) OnMessage(doc.RootElement);
                }
                catch (JsonException) { AddStderr("[stdout, not JSON] " + line); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { OnExited(); }
    }

    /// <summary>
    /// A message from the process that is not a response: notifications <c>footer/set</c>
    /// <c>{ id, text }</c> (empty text removes the chip) and <c>command/output</c> <c>{ text }</c>.
    /// Anything else is ignored. Requests from the process (with an id) are not supported yet.
    /// </summary>
    private void OnMessage(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String) return;
        msg.TryGetProperty("params", out var p);
        string? Str(string key) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (m.GetString())
        {
            case "footer/set":
                if (ExtensionFooter.Set(Name, Str("id") ?? "", Str("text"))) MuxConsole.TuiRepaintSoon();
                break;
            case "command/output":
                if (Str("text") is { Length: > 0 } text) CommandOutput?.Invoke(text);
                break;
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line) AddStderr(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void AddStderr(string line)
    {
        lock (_stderrTail)
        {
            _stderrTail.Enqueue(line.Length > 500 ? line[..500] : line);
            while (_stderrTail.Count > StderrTailLines) _stderrTail.Dequeue();
        }
    }

    private void OnExited()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _outbox.Writer.TryComplete();
        _rpc.CancelAll();
        if (ExtensionFooter.Clear(Name)) MuxConsole.TuiRepaintSoon();
        int? code = null;
        try { if (_process.WaitForExit(1000)) code = _process.ExitCode; } catch (InvalidOperationException) { }
        AddStderr($"[exited{(code is { } c ? $" with code {c}" : "")}]");
    }

    /// <summary>Close stdin, give the process a moment to exit on its own, then kill it.</summary>
    public void Dispose()
    {
        IsRunning = false;
        if (ExtensionFooter.Clear(Name)) MuxConsole.TuiRepaintSoon();
        _outbox.Writer.TryComplete();
        _rpc.CancelAll();
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            ProcessCleanup.Instance.Untrack(_process.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
    }
}
