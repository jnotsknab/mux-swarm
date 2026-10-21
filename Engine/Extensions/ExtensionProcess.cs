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
    private readonly NativeTools.ISessionSandbox? _sandbox;   // container/microVM this process runs in, removed on Dispose
    private readonly RpcCorrelator _rpc = new();
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Queue<string> _stderrTail = new();
    private readonly HashSet<string> _subscriptions;

    /// <summary>The extension name (from its manifest).</summary>
    internal string Name { get; }

    /// <summary>True until the process exits or is disposed.</summary>
    internal bool IsRunning { get; private set; } = true;

    /// <summary>Tool-call interceptors this process serves (from its manifest).</summary>
    internal IReadOnlyList<InterceptRule> Intercepts { get; }

    /// <summary>Where the process runs: <c>host</c>, or the sandbox backend name (e.g. <c>bwrap</c>, <c>docker</c>).</summary>
    internal string Isolation { get; }

    /// <summary>Receives <c>command/output</c> text while an extension command runs; null otherwise.</summary>
    internal volatile Action<string>? CommandOutput;

    private ExtensionProcess(string name, Process process, IEnumerable<string> events, NativeTools.ISessionSandbox? sandbox, string isolation,
        IReadOnlyList<InterceptRule> intercepts)
    {
        Name = name;
        _process = process;
        _subscriptions = new HashSet<string>(events, StringComparer.OrdinalIgnoreCase);
        Intercepts = intercepts;
        _sandbox = sandbox;
        Isolation = isolation;
    }

    /// <summary>
    /// Start <paramref name="spec"/> in <paramref name="dir"/> and send <c>initialize</c>.
    /// A command containing a path separator resolves against the extension folder; a bare name
    /// is looked up on PATH. Throws when the process cannot start.
    /// </summary>
    /// <param name="sandbox">The resolved sandbox to run in, or null to run on the host.</param>
    internal static ExtensionProcess Start(string name, string dir, ExtensionProcessSpec spec, IEnumerable<string> events,
        NativeTools.SandboxSpec? sandbox = null, IReadOnlyList<InterceptRule>? intercepts = null)
    {
        var command = spec.Command.Trim();
        if (command.Contains('/') || command.Contains('\\')) command = Path.GetFullPath(command, dir);
        var (file, args, workDir, session) = BuildStart(name, dir, command, spec.Args ?? [], sandbox);

        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workDir,
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
        if (args is null) foreach (var arg in spec.Args ?? []) psi.ArgumentList.Add(arg);
        else psi.Arguments = args;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"could not start '{command}'.");
        }
        catch { session?.Dispose(); throw; }
        ProcessCleanup.Instance.Track(process);

        var ext = new ExtensionProcess(name, process, events, session, sandbox?.Backend ?? "host", intercepts ?? []);
        _ = Task.Run(ext.ReadStdoutAsync);
        _ = Task.Run(ext.ReadStderrAsync);
        _ = Task.Run(ext.DrainOutboxAsync);
        // dir = the extension folder as the process sees it; dataDir = where it may write (sandboxed only).
        string seenDir = sandbox is { Kind: NativeTools.SandboxKind.Oci or NativeTools.SandboxKind.Sbx } ? GuestExtensionDir : dir;
        string? dataDir = sandbox is null ? null
            : sandbox.Kind is NativeTools.SandboxKind.Oci or NativeTools.SandboxKind.Sbx ? NativeTools.OciSandbox.GuestWorkDir : workDir;
        ext.Notify("initialize", new { protocolVersion = ProtocolVersion, muxVersion = App.Version, name, dir = seenDir, dataDir });
        return ext;
    }

    /// <summary>Guest path of the extension folder inside a container/microVM (read-only).</summary>
    internal const string GuestExtensionDir = "/ext";

    /// <summary>
    /// The private writable folder of a sandboxed extension: <c>&lt;local data&gt;/Mux-Swarm/extensions/&lt;name&gt;</c>
    /// (<c>%LOCALAPPDATA%</c> on Windows, <c>~/.local/share</c> elsewhere). Its working directory in the sandbox.
    /// </summary>
    internal static string DataDirectory(string name)
    {
        string root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "Mux-Swarm", "extensions", name);
    }

    /// <summary>
    /// How to start the process. Host: the command itself in the extension folder (args via
    /// ArgumentList, <c>Args</c> null). Sandboxed: the extension folder is read-only and the process
    /// works in its private <see cref="DataDirectory"/>. Wrapper backends (bwrap, firejail,
    /// sandbox-exec) wrap the command; container/microVM backends get a dedicated sandbox for this
    /// extension (data dir at /work, extension folder at <see cref="GuestExtensionDir"/>, plus the usual
    /// allowed-path mounts) and <c>exec -i</c> into it. A custom backend needs <c>sandbox.replStdio</c>.
    /// Throws <see cref="NativeTools.SandboxException"/> when the backend cannot host the process.
    /// </summary>
    internal static (string File, string? Args, string WorkDir, NativeTools.ISessionSandbox? Session) BuildStart(
        string name, string dir, string command, IReadOnlyList<string> args, NativeTools.SandboxSpec? sandbox)
    {
        if (sandbox is null) return (command, null, dir, null);
        string data = Directory.CreateDirectory(DataDirectory(name)).FullName;
        var argv = new List<string> { command };
        argv.AddRange(args);
        switch (sandbox.Kind)
        {
            case NativeTools.SandboxKind.Wrapper:
            {
                var (file, wrapped) = NativeTools.SandboxBackend.WrapProcess(sandbox, argv, data, [dir]);
                return (file, wrapped, data, null);
            }
            case NativeTools.SandboxKind.Custom when sandbox.CustomReplStdio:
            {
                var (file, wrapped) = NativeTools.SandboxBackend.WrapShellCommand(sandbox,
                    string.Join(' ', argv.Select(NativeTools.SandboxBackend.ArgvToken)), data);
                return (file, wrapped, data, null);
            }
            case NativeTools.SandboxKind.Custom:
                throw new NativeTools.SandboxException("the custom sandbox backend cannot host a long-running stdio process; " +
                    "set sandbox.replStdio true if your template supports it, or list the extension in extensions.unsandboxed.");
            case NativeTools.SandboxKind.Oci or NativeTools.SandboxKind.Sbx:
            {
                // Inside the guest the extension folder is /ext, so paths into it are rewritten.
                string Guest(string a) => a.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                    ? GuestExtensionDir + a[dir.Length..].Replace('\\', '/') : a;
                var spec = sandbox.WithMounts([.. sandbox.Mounts, new NativeTools.SandboxMount(dir, GuestExtensionDir, ReadOnly: true)]);
                NativeTools.ISessionSandbox session = spec.Kind == NativeTools.SandboxKind.Sbx
                    ? new NativeTools.SbxSandbox(spec, data, "ext-" + name)
                    : new NativeTools.OciSandbox(spec, data, "ext-" + name);
                try
                {
                    session.EnsureStarted();
                    var (file, exec) = session.ExecInteractive(argv.Select(Guest).ToList());
                    return (file, exec, data, session);
                }
                catch { session.Dispose(); throw; }
            }
            default:
                return (command, null, dir, null);
        }
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
    /// A message from the process that is not a response. Requests (with an <c>id</c>) go to
    /// <see cref="ExtensionHostApi"/> off the reader thread and are answered through the outbox.
    /// Notifications: <c>footer/set</c> <c>{ id, text }</c> (empty text removes the chip) and
    /// <c>command/output</c> <c>{ text }</c>. Anything else is ignored.
    /// </summary>
    private void OnMessage(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String) return;
        msg.TryGetProperty("params", out var p);
        if (msg.TryGetProperty("id", out var reqId))
        {
            // Never handled on the stdout reader: a handler may wait on the console lock.
            var (id, method, args) = (reqId.Clone(), m.GetString()!, p.ValueKind == JsonValueKind.Undefined ? default : p.Clone());
            _ = Task.Run(() => Respond(id, ExtensionHostApi.Handle(Name, method, args)));
            return;
        }
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

    // Answer a request from the process with the same id (result or JSON-RPC error).
    private void Respond(JsonElement id, ExtensionHostApi.Reply reply)
    {
        var msg = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id };
        if (reply.Error is null) msg["result"] = reply.Result ?? new { };
        else msg["error"] = new { code = reply.Code, message = reply.Error };
        _outbox.Writer.TryWrite(JsonSerializer.Serialize(msg, JsonOpts));
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
        // Killing the exec client does not stop what runs in a container/microVM: remove the sandbox itself.
        _sandbox?.Dispose();
    }
}
