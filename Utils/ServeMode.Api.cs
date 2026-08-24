using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
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
///   GET /api/tools                   native + MCP tool catalog (name, description, server)
///   GET /api/models                  model ids advertised by the active provider endpoint
///   POST /api/model                  { slot, model } -> persist a slot's model to Swarm.json
///   POST /api/agent                  { name } -> swap the active single-agent definition
///   GET  /api/workflows              workflow runs (sections/tasks) + saved definitions
///   POST /api/workflows/{id}/cancel  cancel a running workflow
///   GET  /api/daemon                 daemon state + trigger catalog with next fire times
///   POST /api/daemon                 { enabled } -> toggle the daemon
///   POST /api/daemon/{id}/toggle     enable/disable a single trigger
///   POST /api/daemon/trigger         create a cron/watch/status trigger (persisted)
///   DELETE /api/daemon/{id}          remove a trigger (persisted)
///   POST /api/workflows              { name, goal, mode } -> launch a workflow run
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
        app.MapGet("/api/tools", HandleTools);
        app.MapGet("/api/models", HandleModels);
        app.MapPost("/api/model", HandleSetModel);
        app.MapPost("/api/agent", HandleSetAgent);
        app.MapGet("/api/workflows", HandleWorkflows);
        app.MapPost("/api/workflows/{id}/cancel", HandleWorkflowCancel);
        app.MapGet("/api/daemon", HandleDaemon);
        app.MapPost("/api/daemon", HandleDaemonToggle);
        app.MapPost("/api/daemon/{id}/toggle", HandleTriggerToggle);
        app.MapPost("/api/daemon/trigger", HandleTriggerCreate);
        app.MapDelete("/api/daemon/{id}", HandleTriggerDelete);
        app.MapPost("/api/workflows", HandleWorkflowStart);
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

    // GET /api/tools
    // Built-in (native, in-process) tools plus every MCP server's tools, with
    // descriptions. Mirrors HandleSkills: read-only, never touches the agent
    // input stream. Native tools are grouped by their VIRTUAL server name so the
    // grouping matches how the per-agent ToolFilter actually gates them.
    private sealed record ToolInfo(string Name, string? Description, string Server, string Kind);

    private static async Task HandleTools(HttpContext context)
    {
        var items = new List<ToolInfo>();

        // Native/built-in pool. BuildPool already honours the global
        // mcpServers[..].Enabled gate, so a disabled native server contributes nothing.
        foreach (var tool in NativeTools.NativeToolRegistry.BuildPool(App.Config))
        {
            if (tool is not AIFunction fn) continue;
            items.Add(new ToolInfo(
                fn.Name,
                fn.Description,
                NativeTools.NativeToolRegistry.VirtualServer(fn.Name) ?? "Native",
                "native"));
        }

        // MCP tools. Server name is the {Server}_ prefix the loader stamps on
        // every tool it registers; anything unprefixed is reported as "MCP".
        foreach (var tool in App.McpTools ?? [])
        {
            var underscore = tool.Name.IndexOf('_');
            items.Add(new ToolInfo(
                tool.Name,
                tool.Description,
                underscore > 0 ? tool.Name[..underscore] : "MCP",
                "mcp"));
        }

        var ordered = items
            .OrderBy(t => t.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new { name = t.Name, description = t.Description, server = t.Server, kind = t.Kind })
            .ToList();

        await WriteJson(context, 200, new { count = ordered.Count, items = ordered });
    }

    // GET /api/models
    // Model ids the active provider endpoint actually advertises (OpenAI-compatible
    // /v1/models). Best-effort by design: a provider that does not serve a catalog
    // yields an empty list and the caller falls back to free-text entry, so this can
    // never block a model change.
    private static async Task HandleModels(HttpContext context)
    {
        IReadOnlyList<string> ids = [];
        var source = "none";

        // Subscription providers front their catalog through the local sidecar.
        if (Proxy.CliProxyManager.IsRunning)
        {
            ids = await Proxy.CliProxyManager.ListModelsAsync(context.RequestAborted);
            if (ids.Count > 0) source = "cliproxy";
        }

        // Plain OpenAI-compatible endpoint + key.
        if (ids.Count == 0 && App.ActiveProvider is { Endpoint: { Length: > 0 } endpoint } p)
        {
            var key = string.IsNullOrEmpty(p.ApiKeyEnvVar) ? null : Environment.GetEnvironmentVariable(p.ApiKeyEnvVar);
            ids = await Proxy.CliProxyManager.ProbeEndpointModelsAsync(endpoint, key, context.RequestAborted);
            if (ids.Count > 0) source = "endpoint";
        }

        await WriteJson(context, 200, new
        {
            provider = App.ActiveProvider?.Name,
            source,
            count = ids.Count,
            items = ids.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
        });
    }

    // The set of assignable model slots in Swarm.json, in the same order and with the
    // same labels the interactive /setmodel picker uses, so the web UI and the REPL
    // always agree on what "CodeAgent" or "Orchestrator" means.
    private static List<(string Label, string? Model, Action<string> Set)> ModelSlots(SwarmConfig config)
    {
        var slots = new List<(string, string?, Action<string>)>();
        if (config.CompactionAgent != null)
            slots.Add(("CompactionAgent", config.CompactionAgent.Model, v => config.CompactionAgent.Model = v));
        if (config.SingleAgent != null)
            slots.Add((config.SingleAgent.Name.Length > 0 ? config.SingleAgent.Name : "SingleAgent",
                       config.SingleAgent.Model, v => config.SingleAgent.Model = v));
        if (config.Orchestrator != null)
            slots.Add(("Orchestrator", config.Orchestrator.Model, v => config.Orchestrator.Model = v));
        foreach (var agent in config.Agents)
            slots.Add((agent.Name, agent.Model, v => agent.Model = v));
        return slots;
    }

    // POST /api/model   body: { slot, model }
    // Non-interactive equivalent of /setmodel. Reads Swarm.json, assigns the model to
    // the named slot, persists, and refreshes the in-memory config. Exists so the web
    // UI never has to puppet the two-prompt REPL flow over the agent input stream.
    private static async Task HandleSetModel(HttpContext context)
    {
        string slot, model;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            slot = doc.RootElement.TryGetProperty("slot", out var s) ? s.GetString() ?? "" : "";
            model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "Invalid JSON body" });
            return;
        }

        if (string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(model))
        {
            await WriteJson(context, 400, new { error = "Both 'slot' and 'model' are required" });
            return;
        }

        SwarmConfig config;
        try
        {
            config = JsonSerializer.Deserialize<SwarmConfig>(await File.ReadAllTextAsync(PlatformContext.SwarmPath, context.RequestAborted))
                     ?? throw new JsonException("null config");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            await WriteJson(context, 500, new { error = "Could not read Swarm.json: " + ex.Message });
            return;
        }

        var slots = ModelSlots(config);
        var idx = slots.FindIndex(s => s.Label.Equals(slot.Trim(), StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            await WriteJson(context, 404, new { error = $"No such slot: {slot}", slots = slots.Select(s => s.Label).ToList() });
            return;
        }

        var target = slots[idx];
        var previous = target.Model;
        target.Set(model.Trim());

        try
        {
            await File.WriteAllTextAsync(PlatformContext.SwarmPath,
                JsonSerializer.Serialize(config, Setup.Setup.CfgSerialOpts), context.RequestAborted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await WriteJson(context, 500, new { error = "Could not write Swarm.json: " + ex.Message });
            return;
        }

        // Keep the live config in step with what was just persisted, so the change
        // applies to the next turn without a /refresh.
        App.SwarmConfig = config;

        await WriteJson(context, 200, new { slot = target.Label, model = model.Trim(), previous });
    }

    // POST /api/agent   body: { name }
    // Non-interactive equivalent of /swap: rebinds single-agent mode to another
    // configured agent definition. In-memory only, exactly like the REPL command --
    // it does not rewrite Swarm.json's default agent.
    private static async Task HandleSetAgent(HttpContext context)
    {
        string name;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "Invalid JSON body" });
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            await WriteJson(context, 400, new { error = "'name' is required" });
            return;
        }

        var defs = Common.GetAgentDefinitions(PlatformContext.SwarmPath);
        var current = SingleAgentOrchestrator.GetCurrSingleAgentDef(fromCfg: true);
        var all = (current != null
                ? new[] { current }.Concat(defs.Where(a => !a.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase)))
                : defs)
            .DistinctBy(a => a.Name)
            .ToList();

        var matched = all.FirstOrDefault(a => a.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matched == null)
        {
            await WriteJson(context, 404, new { error = $"No such agent: {name}", agents = all.Select(a => a.Name).ToList() });
            return;
        }

        SingleAgentOrchestrator.AgentDef = matched;
        await WriteJson(context, 200, new { agent = matched.Name, description = matched.Description });
    }

    // GET /api/workflows
    // The graph plane: every workflow run with its full section/task shape, plus the
    // saved workflow definitions. This is the same snapshot the TUI viewer renders,
    // exposed read-only so the web app can draw the node graph without a slash command.
    private static async Task HandleWorkflows(HttpContext context)
    {
        var runs = State.WorkflowRunRegistry.Snapshot()
            .OrderByDescending(r => r.Started)
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                mode = r.Mode,
                state = r.State.ToString().ToLowerInvariant(),
                started = r.Started,
                finished = r.Finished,
                error = r.Error,
                recent = r.RecentEvents.ToArray(),
                sections = r.Manifest.Sections.Select(s => new
                {
                    name = s.Name,
                    tasks = s.Tasks.Select(t => new
                    {
                        id = t.Id,
                        agent = t.Agent,
                        label = t.Label,
                        status = t.Status,
                        detail = t.Detail,
                        secs = t.Secs,
                        tools = t.Tools,
                        tokens = t.Tokens,
                        model = t.Model,
                    }).ToList(),
                }).ToList(),
            })
            .ToList();

        var saved = new List<string>();
        try
        {
            if (Directory.Exists(PlatformContext.TeamsDirectory))
            {
                saved = Directory.EnumerateFiles(PlatformContext.TeamsDirectory, "*.workflow.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f)!.Replace(".workflow", ""))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        await WriteJson(context, 200, new
        {
            running = runs.Count(r => r.state == "running"),
            count = runs.Count,
            items = runs,
            saved,
        });
    }

    // POST /api/workflows/{id}/cancel
    private static async Task HandleWorkflowCancel(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(id))
        {
            await WriteJson(context, 400, new { error = "No run id" });
            return;
        }
        if (!State.WorkflowRunRegistry.Cancel(id))
        {
            await WriteJson(context, 404, new { error = $"No running workflow with id: {id}" });
            return;
        }
        await WriteJson(context, 200, new { id, state = "cancelled" });
    }

    // Trigger summary shared by the daemon endpoints. NextFire is computed only for
    // cron triggers; the other types are event-driven and have no schedule.
    private static object TriggerView(DaemonTrigger t)
    {
        DateTime? next = null;
        if (string.Equals(t.Type, "cron", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(t.Schedule))
        {
            try { next = CronExpression.Parse(t.Schedule!)?.GetNextOccurrence(DateTime.Now); }
            catch { /* a malformed schedule must not break the listing */ }
        }
        return new
        {
            id = t.Id,
            type = t.Type,
            mode = t.Mode,
            agent = t.Agent,
            goal = t.Goal,
            schedule = t.Schedule,
            path = t.Path,
            check = t.Check,
            command = t.Command,
            interval = t.EffectiveInterval,
            enabled = !DisabledTriggers.Contains(t.Id),
            nextFire = next,
        };
    }

    /// <summary>
    /// Trigger ids muted at runtime from the web UI. Kept in memory (not written back to
    /// Config.json) so a UI toggle is a session-scoped mute, matching how the daemon
    /// treats other runtime state. The daemon loop consults this before firing.
    /// </summary>
    public static readonly HashSet<string> DisabledTriggers = new(StringComparer.OrdinalIgnoreCase);

    // GET /api/daemon
    private static async Task HandleDaemon(HttpContext context)
    {
        var cfg = App.Config.Daemon;
        var triggers = (cfg?.Triggers ?? []).Select(TriggerView).ToList();
        await WriteJson(context, 200, new
        {
            enabled = cfg?.Enabled == true,
            count = triggers.Count,
            items = triggers,
        });
    }

    // POST /api/daemon   body: { enabled }
    private static async Task HandleDaemonToggle(HttpContext context)
    {
        bool enabled;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (!doc.RootElement.TryGetProperty("enabled", out var el))
            {
                await WriteJson(context, 400, new { error = "'enabled' is required" });
                return;
            }
            enabled = el.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "Invalid JSON body" });
            return;
        }

        App.Config.Daemon ??= new DaemonConfig();
        App.Config.Daemon.Enabled = enabled;
        await WriteJson(context, 200, new { enabled });
    }

    // POST /api/daemon/{id}/toggle   body: { enabled }
    private static async Task HandleTriggerToggle(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(id))
        {
            await WriteJson(context, 400, new { error = "No trigger id" });
            return;
        }

        var trigger = (App.Config.Daemon?.Triggers ?? []).FirstOrDefault(t =>
            string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (trigger is null)
        {
            await WriteJson(context, 404, new { error = $"No such trigger: {id}" });
            return;
        }

        bool enabled;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            enabled = !doc.RootElement.TryGetProperty("enabled", out var el) || el.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "Invalid JSON body" });
            return;
        }

        if (enabled) DisabledTriggers.Remove(trigger.Id);
        else DisabledTriggers.Add(trigger.Id);

        await WriteJson(context, 200, TriggerView(trigger));
    }

    /// <summary>
    /// Persist the current in-memory daemon config back to Config.json. Trigger
    /// create/delete are durable operations (unlike the session-scoped mute), so the
    /// file is the source of truth and must be rewritten. Reads the file fresh and
    /// replaces only the daemon block, so unrelated concurrent edits are preserved.
    /// </summary>
    private static async Task<string?> PersistDaemonAsync(CancellationToken ct)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(
                          await File.ReadAllTextAsync(PlatformContext.ConfigPath, ct), Setup.Setup.CfgSerialOpts)
                      ?? new AppConfig();
            cfg.Daemon = App.Config.Daemon;
            await File.WriteAllTextAsync(PlatformContext.ConfigPath,
                JsonSerializer.Serialize(cfg, Setup.Setup.CfgSerialOpts), ct);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    // POST /api/daemon/trigger
    // body: { id, type, goal?, schedule?, path?, check?, command?, mode?, agent?, interval? }
    // Creates a trigger and persists it. Validation mirrors what the daemon loop
    // actually requires per type, so a trigger created here can never be inert.
    private static async Task HandleTriggerCreate(HttpContext context)
    {
        JsonElement root;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "Invalid JSON body" });
            return;
        }

        string Str(string name) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()!.Trim() : "";

        var id = Str("id");
        var type = Str("type").ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(id))
        {
            await WriteJson(context, 400, new { error = "'id' is required" });
            return;
        }
        // Ids address the trigger in URLs and log lanes; keep them boring.
        if (!id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            await WriteJson(context, 400, new { error = "'id' may only contain letters, digits, '-' and '_'" });
            return;
        }

        string[] known = ["cron", "watch", "status", "webhook"];
        if (!known.Contains(type))
        {
            await WriteJson(context, 400, new { error = $"'type' must be one of: {string.Join(", ", known)}" });
            return;
        }

        App.Config.Daemon ??= new DaemonConfig();
        if (App.Config.Daemon.Triggers.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteJson(context, 409, new { error = $"A trigger named '{id}' already exists" });
            return;
        }

        var trigger = new DaemonTrigger
        {
            Id = id,
            Type = type,
            Goal = Str("goal") is { Length: > 0 } g ? g : null,
            Schedule = Str("schedule") is { Length: > 0 } s ? s : null,
            Path = Str("path") is { Length: > 0 } p ? p : null,
            Check = Str("check") is { Length: > 0 } c ? c : null,
            Command = Str("command") is { Length: > 0 } cmd ? cmd : null,
            Agent = Str("agent") is { Length: > 0 } a ? a : null,
            Mode = Str("mode") is { Length: > 0 } m ? m : "agent",
        };
        if (root.TryGetProperty("interval", out var iv) && iv.TryGetUInt32(out var ivv) && ivv > 0)
            trigger.Interval = ivv;

        // Per-type requirements. Without these the daemon would register the trigger
        // and then never be able to act on it.
        switch (type)
        {
            case "cron":
                if (string.IsNullOrWhiteSpace(trigger.Schedule))
                {
                    await WriteJson(context, 400, new { error = "cron triggers need a 'schedule'" });
                    return;
                }
                if (CronExpression.Parse(trigger.Schedule!) is null)
                {
                    await WriteJson(context, 400, new { error = $"Unparseable cron schedule: {trigger.Schedule}" });
                    return;
                }
                break;
            case "watch":
                if (string.IsNullOrWhiteSpace(trigger.Path))
                {
                    await WriteJson(context, 400, new { error = "watch triggers need a 'path' glob" });
                    return;
                }
                break;
            case "status":
                if (string.IsNullOrWhiteSpace(trigger.Check))
                {
                    await WriteJson(context, 400, new { error = "status triggers need a 'check' (http://, process:, or tcp:)" });
                    return;
                }
                break;
        }

        if (type is "cron" or "watch" && string.IsNullOrWhiteSpace(trigger.Goal))
        {
            await WriteJson(context, 400, new { error = $"{type} triggers need a 'goal'" });
            return;
        }

        App.Config.Daemon.Triggers.Add(trigger);

        if (await PersistDaemonAsync(context.RequestAborted) is { } err)
        {
            App.Config.Daemon.Triggers.Remove(trigger);   // keep memory and disk in step
            await WriteJson(context, 500, new { error = "Could not write Config.json: " + err });
            return;
        }

        await WriteJson(context, 201, TriggerView(trigger));
    }

    // DELETE /api/daemon/{id}
    private static async Task HandleTriggerDelete(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        var trigger = (App.Config.Daemon?.Triggers ?? []).FirstOrDefault(t =>
            string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (trigger is null)
        {
            await WriteJson(context, 404, new { error = $"No such trigger: {id}" });
            return;
        }

        var index = App.Config.Daemon!.Triggers.IndexOf(trigger);
        App.Config.Daemon.Triggers.Remove(trigger);

        if (await PersistDaemonAsync(context.RequestAborted) is { } err)
        {
            App.Config.Daemon.Triggers.Insert(index, trigger);
            await WriteJson(context, 500, new { error = "Could not write Config.json: " + err });
            return;
        }

        DisabledTriggers.Remove(trigger.Id);
        await WriteJson(context, 200, new { id = trigger.Id, deleted = true });
    }

    // POST /api/workflows   body: { name, goal, mode }
    // Launches a workflow run without the interactive /workflow picker. Dynamic runs
    // author + spawn a driver script; static runs are handed to the agent loop as a
    // goal, which is what the REPL path does once its prompts are answered.
    private static async Task HandleWorkflowStart(HttpContext context)
    {
        string name, goal, mode;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            name = doc.RootElement.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
            goal = doc.RootElement.TryGetProperty("goal", out var g) ? (g.GetString() ?? "").Trim() : "";
            mode = doc.RootElement.TryGetProperty("mode", out var m) ? (m.GetString() ?? "").Trim().ToLowerInvariant() : "dynamic";
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "Invalid JSON body" });
            return;
        }

        if (string.IsNullOrWhiteSpace(goal))
        {
            await WriteJson(context, 400, new { error = "'goal' is required" });
            return;
        }
        if (mode is not ("dynamic" or "static"))
        {
            await WriteJson(context, 400, new { error = "'mode' must be 'dynamic' or 'static'" });
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) name = "workflow";

        if (mode == "static")
        {
            // Static workflows run inside the agent loop; the web app drives that the
            // same way it sends any other goal.
            await WriteJson(context, 202, new
            {
                mode,
                name,
                dispatch = "goal",
                message = "Static workflows run in-session; send the goal on the socket.",
            });
            return;
        }

        // Same authoring client the REPL path uses, so a run started from the web
        // app is identical to one started with /workflow dynamic.
        var (client, opts) = CliCmdUtils.ResolveWorkflowAuthorClient();
        if (client is null)
        {
            await WriteJson(context, 503, new
            {
                error = "No model available to author the driver script (configure an Orchestrator/singleAgent model).",
            });
            return;
        }

        var result = await State.DynamicWorkflow.GenerateAndLaunchAsync(
            name, goal, client, opts, context.RequestAborted);

        await WriteJson(context, 202, new { mode, name, message = result });
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
