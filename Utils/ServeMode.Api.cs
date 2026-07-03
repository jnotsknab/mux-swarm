using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MuxSwarm.Utils;

/// <summary>
/// Additive structured read endpoints for the web app / in-house IDE (v0.11.0
/// Workstreams A + C1). All routes are new surface: nothing here changes the
/// behavior of existing endpoints, the WS bridge, or the text frame stream.
///
///   GET /api/health                  runtime status badge
///   GET /api/agents                  configured agents (name, model, role, provider)
///   GET /api/sessions                session list with metadata
///   GET /api/sessions/{id}           single session metadata + resumable flag
///   GET /api/config                  sanitized runtime config (secrets stripped)
///   GET /api/read/{type}/{**path}    file as text (size-capped, binary-guarded)
///   GET /api/skills                  loaded skill manifest (name, description)
///   GET /api/status                  authoritative session mode / in-session flag
///   GET /api/commands                slash command catalog (from TuiCommands.All) + keybinds
/// </summary>
public static partial class ServeMode
{
    /// <summary>Max bytes returned by /api/read before refusing (binary) or truncating (text).</summary>
    private const long ReadMaxBytes = 1 * 1024 * 1024; // 1 MiB

    /// <summary>
    /// Best-effort label of the runtime mode for /api/health. Serve mode floats
    /// between orchestrators interactively, so this defaults to "interactive";
    /// callers may set it when a specific mode is pinned.
    /// </summary>
    internal static string ActiveMode { get; set; } = "interactive";

    private static void MapApiRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", HandleHealth);
        app.MapGet("/api/agents", HandleAgents);
        app.MapGet("/api/sessions", HandleSessions);
        app.MapGet("/api/sessions/{id}", HandleSessionDetail);
        app.MapGet("/api/config", HandleConfig);
        app.MapGet("/api/read/{type}/{**path}", HandleRead);
        app.MapGet("/api/skills", HandleSkills);
        app.MapGet("/api/status", HandleStatus);
        app.MapGet("/api/commands", HandleCommands);
        app.MapPost("/api/save/{type}/{**path}", HandleSave);
        app.MapPost("/api/fs", HandleFs);
        app.MapPost("/api/hook/{id}", HandleWebhook);
        app.MapPost("/api/shutdown", HandleShutdown);
        app.MapPost("/api/restart", HandleRestart);
        app.MapGet("/api/config-files/{which}", HandleConfigFileGet);
        app.MapPut("/api/config-files/{which}", HandleConfigFilePut);
        app.MapGet("/api/update", HandleUpdateCheck);
        app.MapPost("/api/update", HandleUpdateApply);
    }

    // Inbound webhook: POST /api/hook/{id} -> fires the matching daemon "webhook" trigger's goal
    // with the request body templated in as {payload}. Auth is per-trigger (HMAC secret) rather than
    // the runtime bearer -- see RequiresAuth, which excludes /api/hook so external senders reach here.
    private static async Task HandleWebhook(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        var runner = App.DaemonRunner;
        if (runner is null || !runner.HasWebhook(id))
        {
            await WriteJson(context, 404, new { error = "No such webhook trigger" });
            return;
        }

        // Read the raw body once (needed verbatim for HMAC verification + templating).
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync();

        var trigger = App.Config.Daemon?.Triggers
            .FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(t.Type, "webhook", StringComparison.OrdinalIgnoreCase));

        // Auth: prefer per-trigger HMAC. If a secret is configured, a valid X-Hub-Signature-256 is
        // mandatory. With no secret, fall back to the runtime bearer gate when global auth is on;
        // when auth is off and no secret is set, the endpoint is open (documented, opt-in surface).
        var secret = trigger?.Secret;
        if (!string.IsNullOrEmpty(secret))
        {
            var sig = context.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!VerifyHmacSignature(body, secret, sig))
            {
                await WriteJson(context, 401, new { error = "Invalid signature" });
                return;
            }
        }
        else if (_authEnabled && !IsAuthorized(context))
        {
            context.Response.Headers.Append("WWW-Authenticate", "Bearer");
            await WriteJson(context, 401, new { error = "Unauthorized" });
            return;
        }

        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!runner.EnqueueWebhook(id, body, source))
        {
            await WriteJson(context, 404, new { error = "No such webhook trigger" });
            return;
        }

        await WriteJson(context, 202, new { accepted = true, id });
    }

    /// <summary>
    /// Constant-time verify of a GitHub-style <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> header
    /// against an HMAC-SHA256 of the raw body under the shared secret.
    /// </summary>
    private static bool VerifyHmacSignature(string body, string secret, string header)
    {
        if (string.IsNullOrEmpty(header)) return false;
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var presentedHex = header[prefix.Length..].Trim();
        byte[] presented;
        try { presented = Convert.FromHexString(presentedHex); }
        catch (FormatException) { return false; }

        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(body);
        var expected = System.Security.Cryptography.HMACSHA256.HashData(key, data);

        return presented.Length == expected.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    // A1 -- GET /api/health
    private static async Task HandleHealth(HttpContext context)
    {
        var uptime = (long)(DateTime.UtcNow - _startedAtUtc).TotalSeconds;
        var agentCount = App.SwarmConfig?.Agents?.Count ?? 0;

        await WriteJson(context, 200, new
        {
            version = App.Version,
            uptimeSec = uptime < 0 ? 0 : uptime,
            serveAddress = App.Config.ServeAddress,
            port = App.ServePort,
            mode = ActiveMode,
            agentCount,
        });
    }

    // A2 -- GET /api/agents
    private static async Task HandleAgents(HttpContext context)
    {
        var provider = App.ActiveProvider?.Name;
        var agents = (App.SwarmConfig?.Agents ?? [])
            .Select(a => new
            {
                name = a.Name,
                model = a.Model,
                role = a.Description,
                provider,
            })
            .ToList();

        await WriteJson(context, 200, agents);
    }

    // A3 -- GET /api/sessions (list + metadata)
    private static async Task HandleSessions(HttpContext context)
    {
        var root = GetRoot("sessions");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            await WriteJson(context, 200, new { items = Array.Empty<object>() });
            return;
        }

        var items = new List<object>();
        try
        {
            foreach (var dir in new DirectoryInfo(root)
                         .EnumerateDirectories()
                         .OrderByDescending(d => d.LastWriteTimeUtc))
            {
                var sessionFile = Path.Combine(dir.FullName, "agent_session.json");
                long sizeBytes = 0;
                int turnCount = 0;
                string mode = "agent";
                DateTime mtime = dir.LastWriteTimeUtc;

                if (File.Exists(sessionFile))
                {
                    try
                    {
                        var fi = new FileInfo(sessionFile);
                        sizeBytes = fi.Length;
                        mtime = fi.LastWriteTimeUtc;
                        turnCount = CountSessionTurns(sessionFile);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                items.Add(new
                {
                    name = dir.Name,
                    mode,
                    mtime = mtime.ToString("o"),
                    sizeBytes,
                    turnCount,
                    tag = SessionTags.TagLabel(dir.FullName),
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            await WriteJson(context, 403, new { error = "Permission denied", items = Array.Empty<object>() });
            return;
        }

        await WriteJson(context, 200, new { items });
    }

    // R4 -- GET /api/sessions/{id}
    // Single-session metadata plus a `resumable` flag, so the web app's Resume
    // button can validate before sending "/resume <id>" over the WS. Resumable
    // mirrors the CLI rule: a single-agent session dir (<= 2 *.json files).
    private static async Task HandleSessionDetail(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id))
        {
            await WriteJson(context, 400, new { error = "Missing session id" });
            return;
        }

        var root = GetRoot("sessions");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            await WriteJson(context, 404, new { error = "No sessions directory" });
            return;
        }

        // Resolve strictly inside the sessions root; reject traversal / nested paths.
        var dir = SafeJoin(root, id);
        if (dir == null || !Directory.Exists(dir) ||
            !string.Equals(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)), id, StringComparison.Ordinal))
        {
            await WriteJson(context, 404, new { error = "Session not found" });
            return;
        }

        try
        {
            var jsonFiles = Directory.GetFiles(dir, "*.json");
            var resumable = jsonFiles.Length is > 0 and <= 2; // single-agent session heuristic (matches CLI)

            var sessionFile = Path.Combine(dir, "agent_session.json");
            if (!File.Exists(sessionFile) && jsonFiles.Length > 0) sessionFile = jsonFiles[0];

            long sizeBytes = 0;
            int turnCount = 0;
            DateTime mtime = new DirectoryInfo(dir).LastWriteTimeUtc;
            if (File.Exists(sessionFile))
            {
                var fi = new FileInfo(sessionFile);
                sizeBytes = fi.Length;
                mtime = fi.LastWriteTimeUtc;
                turnCount = CountSessionTurns(sessionFile);
            }

            await WriteJson(context, 200, new
            {
                name = id,
                mode = "agent",
                mtime = mtime.ToString("o"),
                sizeBytes,
                turnCount,
                resumable,
                tag = SessionTags.TagLabel(dir),
            });
        }
        catch (UnauthorizedAccessException)
        {
            await WriteJson(context, 403, new { error = "Permission denied" });
        }
    }

    /// <summary>Best-effort user-turn count from a persisted session file.</summary>
    private static int CountSessionTurns(string sessionFile)
    {
        try
        {
            using var stream = File.OpenRead(sessionFile);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("stateBag", out var bag)
                && bag.TryGetProperty("InMemoryChatHistoryProvider", out var prov)
                && prov.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                var count = 0;
                foreach (var msg in messages.EnumerateArray())
                {
                    if (msg.TryGetProperty("role", out var role)
                        && string.Equals(role.GetString(), "user", StringComparison.OrdinalIgnoreCase))
                        count++;
                }
                return count;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 0;
    }

    // A4 -- GET /api/config (sanitized; no secrets)
    private static async Task HandleConfig(HttpContext context)
    {
        var cfg = App.Config;
        var swarm = App.SwarmConfig;

        var modes = new[] { "agent", "swarm", "pswarm", "stateless" };

        var flags = new
        {
            isUsingDockerForExec = cfg.IsUsingDockerForExec,
            setupCompleted = cfg.SetupCompleted,
            telemetryEnabled = cfg.Telemetry?.Enabled ?? false,
            daemonEnabled = cfg.Daemon?.Enabled ?? false,
        };

        var compactor = swarm?.CompactionAgent == null ? null : new
        {
            model = swarm.CompactionAgent.Model,
            autoCompactTokenThreshold = swarm.CompactionAgent.AutoCompactTokenThreshold,
        };

        var serve = new
        {
            address = cfg.ServeAddress,
            port = App.ServePort,
            editable = cfg.Serve?.Editable == true,
            configExposed = cfg.Serve?.ConfigExposed == true,
            authRequired = cfg.Serve?.Auth?.Enabled == true,
        };

        await WriteJson(context, 200, new { modes, flags, compactor, serve });
    }

    // C1 -- GET /api/read/{type}/{**path}
    private static async Task HandleRead(HttpContext context)
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

        long length;
        try { length = new FileInfo(filepath).Length; }
        catch (IOException) { await WriteJson(context, 404, new { error = "File not found" }); return; }
        catch (UnauthorizedAccessException) { await WriteJson(context, 403, new { error = "Permission denied" }); return; }

        var readLen = (int)Math.Min(length, ReadMaxBytes);
        byte[] buffer;
        try
        {
            buffer = new byte[readLen];
            await using var fs = File.OpenRead(filepath);
            var offset = 0;
            while (offset < readLen)
            {
                var n = await fs.ReadAsync(buffer.AsMemory(offset, readLen - offset));
                if (n == 0) break;
                offset += n;
            }
            if (offset != readLen) Array.Resize(ref buffer, offset);
        }
        catch (UnauthorizedAccessException)
        {
            await WriteJson(context, 403, new { error = "Permission denied" });
            return;
        }
        catch (IOException ex)
        {
            await WriteJson(context, 500, new { error = $"Read failed: {ex.Message}" });
            return;
        }

        if (LooksBinary(buffer))
        {
            await WriteJson(context, 415, new { error = "Binary file; use /api/download" });
            return;
        }

        var rel = Path.GetRelativePath(root, filepath).Replace('\\', '/');
        var ext = Path.GetExtension(filepath).ToLowerInvariant();
        var content = DecodeText(buffer);

        await WriteJson(context, 200, new
        {
            path = rel,
            content,
            ext,
            sizeBytes = length,
            truncated = length > ReadMaxBytes,
        });
    }

    // ---- Write surface (C2/C3) -- gated behind serve.editable, sandbox-only ----

    /// <summary>True when write endpoints are enabled (serve.editable = true).</summary>
    private static bool WritesEnabled => App.Config.Serve?.Editable == true;

    /// <summary>
    /// Resolve a writable root. Only the sandbox is ever writable; sessions are
    /// live runtime state and remain read-only even when editable is on.
    /// Returns null for any non-writable / unknown type.
    /// </summary>
    private static string? GetWritableRoot(string type) => type.ToLowerInvariant() switch
    {
        "sandbox" => string.IsNullOrEmpty(_sandboxRoot) ? null : _sandboxRoot,
        _ => null,
    };

    // C2 -- POST /api/save/{type}/{**path}   body: { content }
    private static async Task HandleSave(HttpContext context)
    {
        if (!WritesEnabled)
        {
            await WriteJson(context, 403, new { error = "Editing disabled; set serve.editable=true" });
            return;
        }

        var type = context.Request.RouteValues["type"]?.ToString() ?? "sandbox";
        var subpath = context.Request.RouteValues["path"]?.ToString() ?? "";

        var root = GetWritableRoot(type);
        if (root == null)
        {
            await WriteJson(context, 403, new { error = $"Type not writable: {type}" });
            return;
        }
        if (string.IsNullOrEmpty(subpath))
        {
            await WriteJson(context, 400, new { error = "No file specified" });
            return;
        }

        var filepath = SafeJoin(root, subpath);
        if (filepath == null)
        {
            await WriteJson(context, 400, new { error = "Invalid path" });
            return;
        }

        string content;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            content = doc.RootElement.TryGetProperty("content", out var el)
                ? el.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "Invalid JSON body" });
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(filepath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(filepath, content, new UTF8Encoding(false));
        }
        catch (UnauthorizedAccessException)
        {
            await WriteJson(context, 403, new { error = "Permission denied" });
            return;
        }
        catch (IOException ex)
        {
            await WriteJson(context, 500, new { error = $"Write failed: {ex.Message}" });
            return;
        }

        var rel = Path.GetRelativePath(root, filepath).Replace('\\', '/');
        await WriteJson(context, 200, new { path = rel, sizeBytes = Encoding.UTF8.GetByteCount(content), saved = true });
    }

    // C3 -- POST /api/fs   body: { op: mkdir|rename|delete, type, path, to? }
    private static async Task HandleFs(HttpContext context)
    {
        if (!WritesEnabled)
        {
            await WriteJson(context, 403, new { error = "Editing disabled; set serve.editable=true" });
            return;
        }

        JsonElement r;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            r = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "Invalid JSON body" });
            return;
        }

        var op = r.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";
        var type = r.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "sandbox" : "sandbox";
        var path = r.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";

        var root = GetWritableRoot(type);
        if (root == null)
        {
            await WriteJson(context, 403, new { error = $"Type not writable: {type}" });
            return;
        }
        if (string.IsNullOrEmpty(path))
        {
            await WriteJson(context, 400, new { error = "No path specified" });
            return;
        }

        var full = SafeJoin(root, path);
        if (full == null)
        {
            await WriteJson(context, 400, new { error = "Invalid path" });
            return;
        }

        try
        {
            switch (op.ToLowerInvariant())
            {
                case "mkdir":
                    Directory.CreateDirectory(full);
                    break;

                case "delete":
                    if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                    else if (File.Exists(full)) File.Delete(full);
                    else { await WriteJson(context, 404, new { error = "Not found" }); return; }
                    break;

                case "rename":
                    var to = r.TryGetProperty("to", out var toEl) ? toEl.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(to))
                    {
                        await WriteJson(context, 400, new { error = "rename requires 'to'" });
                        return;
                    }
                    var dest = SafeJoin(root, to);
                    if (dest == null)
                    {
                        await WriteJson(context, 400, new { error = "Invalid destination" });
                        return;
                    }
                    if (Directory.Exists(full)) Directory.Move(full, dest);
                    else if (File.Exists(full)) File.Move(full, dest, overwrite: false);
                    else { await WriteJson(context, 404, new { error = "Not found" }); return; }
                    break;

                default:
                    await WriteJson(context, 400, new { error = $"Unknown op: {op}" });
                    return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            await WriteJson(context, 403, new { error = "Permission denied" });
            return;
        }
        catch (IOException ex)
        {
            await WriteJson(context, 500, new { error = $"Operation failed: {ex.Message}" });
            return;
        }

        await WriteJson(context, 200, new { op, ok = true });
    }

    // B5c -- GET /api/skills
    // Authoritative skill manifest, sourced directly from SkillLoader. Replaces the
    // web app's old hack of silently sending "/skills" over the WS to scrape the
    // panel render for completion data.
    private static async Task HandleSkills(HttpContext context)
    {
        var skills = SkillLoader.GetSkillMetadata()
            .Select(s => new { name = s.Name, description = s.Description })
            .OrderBy(s => s.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await WriteJson(context, 200, new { count = skills.Count, items = skills });
    }

    // B5d -- GET /api/status
    // Authoritative runtime/session state so the web app no longer has to infer
    // "am I in a session?" by string-matching event text (e.g. "Type /qc to exit").
    // ActiveMode is pinned by the App interactive loop around each orchestrator call.
    private static async Task HandleStatus(HttpContext context)
    {
        var mode = ActiveMode;
        var inSession = !string.Equals(mode, "interactive", StringComparison.OrdinalIgnoreCase);

        var tokens = SingleAgentOrchestrator.SessionTokens;
        var threshold = SingleAgentOrchestrator.AutoCompactThreshold;
        double pct = threshold > 0 ? (double)tokens / threshold * 100.0 : 0;

        await WriteJson(context, 200, new
        {
            mode,
            inSession,
            provider = App.ActiveProvider?.Name,
            tokens,
            tokenThreshold = threshold,
            tokenPct = Math.Round(pct, 1),
            plan = App.PlanMode,
            ultra = App.UltraMode,
            giga = App.GigaMode,
        });
    }

    // B5e -- GET /api/commands
    // Slash command catalog with the in-session-safe flag, so the web app palette
    // and session-switch gating stop hardcoding their own command tables.
    private static async Task HandleCommands(HttpContext context)
    {
        // Project the SINGLE canonical TUI catalog (TuiCommands.All) so the web app, the
        // /shortcuts command, and the Help.cs reference can never drift apart. inSessionSafe
        // maps to SessionOnly scope: the command is handled by the in-session loop itself and
        // does NOT imply quitting the current agent session (everything else triggers a /qc).
        var items = Tui.TuiCommands.All
            .GroupBy(e => e.Cmd, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(e => new
            {
                cmd = e.Cmd,
                desc = e.Desc,
                scope = e.Scope == Tui.TuiCommands.Scope.SessionOnly ? "session" : "repl",
                inSessionSafe = e.Scope == Tui.TuiCommands.Scope.SessionOnly,
            })
            .ToList();

        var keybinds = Tui.TuiCommands.Keys
            .Select(k => new { keys = k.Keys, desc = k.Desc, context = k.Context })
            .ToList();

        await WriteJson(context, 200, new { items, keybinds });
    }

    /// <summary>Heuristic binary guard: NUL byte in the sampled prefix.</summary>
    private static bool LooksBinary(byte[] data)
    {
        var sample = Math.Min(data.Length, 8000);
        for (var i = 0; i < sample; i++)
            if (data[i] == 0) return true;
        return false;
    }

    /// <summary>Decode UTF-8, stripping a leading BOM if present.</summary>
    private static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        return Encoding.UTF8.GetString(data);
    }

    // ---- Lifecycle: shutdown / restart (POST) ----

    // L1 -- POST /api/shutdown : graceful process exit. Fire-and-forget after a short delay so the
    // HTTP response can flush to the caller before the process goes down.
    private static async Task HandleShutdown(HttpContext context)
    {
        await WriteJson(context, 202, new { status = "shutting down" });
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            MuxConsole.DisableDockedFooter();
            State.HookWorker.Stop();
            ProcessCleanup.Instance.Shutdown();
            Environment.Exit(0);
        });
    }

    // L2 -- POST /api/restart : spawn a successor that waits on this PID, then exit. Replaces the old
    // flaky "__CANCEL__ -> /qc -> /exit over the WS" dance the web app used for a server restart.
    private static async Task HandleRestart(HttpContext context)
    {
        await WriteJson(context, 202, new { status = "restarting" });
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            State.Relauncher.RestartNow(() => MuxConsole.DisableDockedFooter());
        });
    }

    // ---- Native config editor (gated by serve.configExposed) ----

    /// <summary>True when the config-editor endpoints are enabled (serve.configExposed = true).</summary>
    private static bool ConfigEditingEnabled => App.Config.Serve?.ConfigExposed == true;

    /// <summary>Map the {which} route token to a concrete config file path. null = unknown token.</summary>
    private static string? ResolveConfigFile(string? which) => (which ?? "").ToLowerInvariant() switch
    {
        "config" => PlatformContext.ConfigPath,
        "swarm" => PlatformContext.SwarmPath,
        _ => null,
    };

    // CE1 -- GET /api/config-files/{config|swarm} : raw file contents (for the Monaco editor).
    private static async Task HandleConfigFileGet(HttpContext context)
    {
        if (!ConfigEditingEnabled)
        {
            await WriteJson(context, 403, new { error = "Config editing disabled; set serve.configExposed=true" });
            return;
        }
        var which = context.Request.RouteValues["which"]?.ToString();
        var path = ResolveConfigFile(which);
        if (path == null)
        {
            await WriteJson(context, 404, new { error = "Unknown config file (use 'config' or 'swarm')" });
            return;
        }
        if (!File.Exists(path))
        {
            await WriteJson(context, 200, new { which, path, exists = false, content = "" });
            return;
        }
        var bytes = await File.ReadAllBytesAsync(path);
        await WriteJson(context, 200, new { which, path, exists = true, content = DecodeText(bytes) });
    }

    // CE2 -- PUT /api/config-files/{config|swarm}  body: { content } : validate JSON, then write.
    private static async Task HandleConfigFilePut(HttpContext context)
    {
        if (!ConfigEditingEnabled)
        {
            await WriteJson(context, 403, new { error = "Config editing disabled; set serve.configExposed=true" });
            return;
        }
        var which = context.Request.RouteValues["which"]?.ToString();
        var path = ResolveConfigFile(which);
        if (path == null)
        {
            await WriteJson(context, 404, new { error = "Unknown config file (use 'config' or 'swarm')" });
            return;
        }

        string content;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            content = doc.RootElement.TryGetProperty("content", out var el) ? el.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "Invalid request body (expected { content })" });
            return;
        }

        // Guard: the new content itself must be valid JSON, or we would brick the install on next load.
        try { using var _ = JsonDocument.Parse(content); }
        catch (JsonException jx)
        {
            await WriteJson(context, 422, new { error = $"Content is not valid JSON: {jx.Message}" });
            return;
        }

        try
        {
            // Atomic-ish write: temp beside the target, then move over it.
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            await WriteJson(context, 500, new { error = $"Write failed: {ex.Message}" });
            return;
        }

        await WriteJson(context, 200, new { which, path, saved = true, note = "Saved. Some changes take effect on restart." });
    }

    // ---- Self-update (GET check / POST apply) ----

    // U1 -- GET /api/update : read-only availability check.
    private static async Task HandleUpdateCheck(HttpContext context)
    {
        var plan = await State.SelfUpdater.PlanAsync(context.RequestAborted);
        await WriteJson(context, 200, new
        {
            updateAvailable = plan.UpdateAvailable,
            currentVersion = plan.CurrentVersion,
            latestTag = plan.LatestTag,
            asset = plan.AssetName,
            assetSize = plan.AssetSize,
            message = plan.Message,
        });
    }

    // U2 -- POST /api/update : download + verify + apply; if the binary was staged, relaunch.
    private static async Task HandleUpdateApply(HttpContext context)
    {
        var (staged, msg) = await State.SelfUpdater.RunAsync(null, context.RequestAborted);
        await WriteJson(context, 200, new { applied = true, restarting = staged, message = msg });
        if (staged)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                State.Relauncher.RestartNow(() => MuxConsole.DisableDockedFooter());
            });
        }
    }

}
