using System.Collections.Concurrent;
using System.Text.Json;

namespace MuxSwarm.Engine.Acp;

/// <summary>
/// ACP (Agent Client Protocol, Zed) transport + lifecycle server. Drives the EXISTING
/// interactive single-agent REPL headlessly:
///
/// <list type="bullet">
/// <item>Installs an <see cref="AcpInputReader"/> as <c>MuxConsole.InputOverride</c> so the
///   orchestrator's per-turn <c>ReadInput()</c> is fed by ACP prompts instead of a keyboard
///   (the same trick <c>ServeMode</c> uses for WebSocket input).</item>
/// <item>Captures the orchestrator's NDJSON event stream via <c>MuxConsole.AcpSink</c> and
///   translates it into ACP <c>session/update</c> notifications. stdout carries ONLY ACP
///   JSON-RPC; NDJSON never reaches it.</item>
/// <item>Uses the moment the orchestrator re-enters <c>ReadInput()</c> (the
///   <see cref="AcpInputReader.ReadEntered"/> tick) as the precise turn-completion boundary at
///   which the in-flight <c>session/prompt</c> request is answered with a <c>stopReason</c>.</item>
/// </list>
///
/// MVP scope (g12.38): initialize, session/new, session/prompt (text), session/cancel,
/// session/close; agent_message_chunk, agent_thought_chunk, tool_call/tool_call_update,
/// usage_update; stopReason end_turn|cancelled. A single active session per process.
/// </summary>
public sealed class AcpServer
{
    private readonly Func<AcpInputReader, AcpResume?, Task> _runSession;
    private readonly string _version;
    private readonly object _outLock = new();

    // Model selector (ACP session config option). _modelsProvider returns (current, available);
    // _setModel applies a chosen model id for the next/current session. Both optional (null-safe).
    private readonly Func<(string Current, IReadOnlyList<string> Available)>? _modelsProvider;
    private readonly Action<string>? _setModel;

    private AcpInputReader? _reader;
    private Task? _sessionTask;
    private string? _sessionId;
    private string? _sessionCwd;     // cwd from session/new, persisted at the first prompt
    private bool _titlePending;      // new session whose first prompt has not arrived yet

    // Turn-boundary state. _pendingPromptId is the JSON-RPC id of the in-flight session/prompt;
    // its response is deferred until the orchestrator re-enters ReadInput (turn complete).
    private object? _pendingPromptId;
    private volatile bool _cancelRequested;
    private bool _sawFirstReadTick;
    private int _messageCounter;
    private string _currentMessageId = "msg_0";

    // Tool-call correlation. Single-agent events carry the provider callId on both tool_call and
    // tool_result; events without one (swarm paths) pair FIFO. Every announced call stays in
    // _openTools until a result or the turn boundary closes it, so no card is left spinning.
    private int _toolCounter;
    private readonly object _toolLock = new();
    private readonly Dictionary<string, OpenTool> _openTools = new(StringComparer.Ordinal);
    private readonly Queue<string> _fifoToolIds = new();

    /// <summary>An announced, not yet finished tool call. OldText/Snapshot hold the edited file's
    /// content captured before the tool ran (Snapshot=false when it could not be read).</summary>
    private sealed record OpenTool(string AcpId, string Tool, string? Args, string? Path, string? OldText, bool Snapshot);

    // Files larger than this are not snapshotted for diff cards.
    private const long MaxDiffBytes = 1_000_000;

    // Client capabilities (from initialize) gating agent->client callbacks (fs/*, terminal/*).
    private AcpClientCaps _clientCaps;

    // Outbound request correlation (agent->client requests like fs/* and request_permission).
    private int _outboundId = 1000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingOutbound = new();

    public AcpServer(string version, Func<AcpInputReader, AcpResume?, Task> runSession,
        Func<(string Current, IReadOnlyList<string> Available)>? modelsProvider = null,
        Action<string>? setModel = null)
    {
        _version = version;
        _runSession = runSession;
        _modelsProvider = modelsProvider;
        _setModel = setModel;
    }

    /// <summary>Build the current model-selector configOptions array (empty when no provider).</summary>
    private object[] BuildConfigOptions()
    {
        if (_modelsProvider is null) return System.Array.Empty<object>();
        try
        {
            var (current, available) = _modelsProvider();
            return new[] { AcpProtocol.ModelConfigOption(current, available) };
        }
        catch { return System.Array.Empty<object>(); }
    }

    /// <summary>Build the Zed/Copilot Models API state (NewSessionResponse.models), or null.</summary>
    private object? BuildModelState()
    {
        if (_modelsProvider is null) return null;
        try
        {
            var (current, available) = _modelsProvider();
            return AcpProtocol.ModelState(current, available);
        }
        catch { return null; }
    }

    /// <summary>
    /// Run the ACP server: install the event sink, then read JSON-RPC lines from stdin until
    /// EOF. Blocks for the lifetime of the connection. stdout is reserved for ACP messages.
    /// </summary>
    public async Task RunAsync()
    {
        MuxConsole.AcpActive = true;
        MuxConsole.AcpSink = OnMuxEvent;

        // Read AND dispatch on a DEDICATED thread, never the thread pool. During startup MCP
        // init spawns many subprocess connections concurrently, which can saturate the pool
        // for ~15s; a pool-scheduled read (e.g. `Task.Run(Console.In.ReadLine)`) would be
        // starved and the ACP handshake would stall even though the runtime is ready at ~1s.
        // A dedicated reader thread (mirroring StdinCancelMonitor) delivers the initialize
        // request immediately. Dispatch is synchronous; session turns spin up their own tasks.
        var done = new TaskCompletionSource();
        var readerThread = new Thread(() =>
        {
            try
            {
                var stdin = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
                while (true)
                {
                    string? line = stdin.ReadLine();
                    if (line is null) break;       // stdin closed -> client disconnected
                    if (line.Length == 0) continue;
                    try { Dispatch(line); }
                    catch (Exception ex) { Log($"dispatch error: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log($"reader error: {ex.Message}"); }
            finally { done.TrySetResult(); }
        }) { IsBackground = true, Name = "AcpStdinReader" };
        readerThread.Start();

        try
        {
            await done.Task;   // until stdin closes
        }
        finally
        {
            _reader?.CloseSession();
            MuxConsole.AcpSink = null;
            try { if (_sessionTask is not null) await _sessionTask; } catch { /* session teardown best-effort */ }
        }
    }

    // ----- inbound dispatch --------------------------------------------------------------

    private void Dispatch(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }   // not valid JSON; ignore per transport contract

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            string? method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
            bool hasId = root.TryGetProperty("id", out var idEl);
            object? id = hasId ? ExtractId(idEl) : null;

            if (method is null)
            {
                // A response to one of OUR outbound requests (fs/*, request_permission, etc.).
                // Complete the awaiting task with the result (or a JSON null on error).
                if (hasId && id is long rid && _pendingOutbound.TryRemove(rid, out var tcs))
                {
                    if (root.TryGetProperty("result", out var resEl)) tcs.TrySetResult(resEl.Clone());
                    else tcs.TrySetResult(default);
                }
                return;
            }

            JsonElement prms = root.TryGetProperty("params", out var p) ? p : default;

            switch (method)
            {
                case "initialize":
                    _clientCaps = AcpProtocol.ParseClientCaps(prms);
                    Respond(id, AcpProtocol.InitializeResult(_version, loadSession: true));
                    break;

                case "authenticate":
                    Respond(id, new Dictionary<string, object?>());
                    break;

                case "session/new":
                    HandleSessionNew(id, prms);
                    break;

                case "session/load":
                    HandleSessionLoad(id, prms);
                    break;

                case "session/list":
                    Respond(id, ListSessions(PlatformContext.SessionsDirectory,
                        StrProp(prms, "cwd"), StrProp(prms, "cursor"), Environment.CurrentDirectory));
                    break;

                case "session/prompt":
                    HandlePrompt(id, prms);
                    break;

                case "session/cancel":
                    _cancelRequested = true;
                    StdinCancelMonitor.Instance?.FireCancel();
                    break;            // notification: no response

                case "session/close":
                    _reader?.CloseSession();
                    Respond(id, new Dictionary<string, object?>());
                    break;

                case "session/resume":
                    // Like load but WITHOUT replaying history: restore context + return ready.
                    HandleSessionResume(id, prms);
                    break;

                case "session/set_mode":
                    HandleSetMode(id, prms);
                    break;

                case "session/set_config_option":
                    HandleSetConfigOption(id, prms);
                    break;

                case "session/set_model":
                    HandleSetModel(id, prms);
                    break;

                case "logout":
                    Respond(id, new Dictionary<string, object?>());
                    break;

                default:
                    if (hasId)
                        WriteMessage(AcpProtocol.Error(id, -32601, $"Method not found: {method}"));
                    break;
            }
        }
    }

    private void HandleSessionNew(object? id, JsonElement prms)
    {
        // The session id IS the Sessions folder name the orchestrator persists to, so
        // session/list, session/load and session/resume all round-trip with no id mapping.
        string sid = NewSessionId();
        string dir = Path.Combine(PlatformContext.SessionsDirectory, sid);
        _sessionCwd = StrProp(prms, "cwd");
        StartSession(sid, new AcpResume(null, dir));
        _titlePending = true;
        var result = new Dictionary<string, object?> { ["sessionId"] = sid };
        var cfg = BuildConfigOptions();
        if (cfg.Length > 0) result["configOptions"] = cfg;   // configOptions-API clients (e.g. Copilot CLI)
        var models = BuildModelState();
        if (models is not null) result["models"] = models;   // Models-API clients (e.g. Zed agent-panel model picker)
        Respond(id, result);
        AdvertiseCommands(sid);   // after the response: clients attach commands to a known session
    }

    private void HandleSessionLoad(object? id, JsonElement prms)
    {
        string? sid = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("sessionId", out var s)
            && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrEmpty(sid))
        {
            if (id is not null) WriteMessage(AcpProtocol.Error(id, -32602, "session/load requires a sessionId"));
            return;
        }

        // Resolve the persisted session by id (folder name). HandleSessionResume's console
        // output routes through the (currently null-mapped) ACP sink, never to stdout.
        var resumeData = CliCmdUtils.HandleSessionResume(sid);
        if (resumeData is null)
        {
            if (id is not null) WriteMessage(AcpProtocol.Error(id, -32001, $"No resumable session: {sid}"));
            return;
        }

        // Start the session FIRST so _sessionId is set, then replay the persisted transcript as
        // session/update notifications (user_message_chunk / agent_message_chunk) per the spec,
        // then respond with result:null. The started loop resumes the same context so the next
        // session/prompt continues the conversation.
        StartSession(sid, new AcpResume(resumeData.Value.data, resumeData.Value.sessionDir));
        ReplayHistory(sid, resumeData.Value.data);
        var lres = new Dictionary<string, object?>();
        var lcfg = BuildConfigOptions();
        if (lcfg.Length > 0) lres["configOptions"] = lcfg;
        var lmodels = BuildModelState();
        if (lmodels is not null) lres["models"] = lmodels;
        Respond(id, lres);
        AdvertiseCommands(sid);   // after the response: clients attach commands to a known session
    }

    private void HandleSessionResume(object? id, JsonElement prms)
    {
        string? sid = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("sessionId", out var s)
            && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrEmpty(sid))
        {
            if (id is not null) WriteMessage(AcpProtocol.Error(id, -32602, "session/resume requires a sessionId"));
            return;
        }
        var resumeData = CliCmdUtils.HandleSessionResume(sid);
        if (resumeData is null)
        {
            if (id is not null) WriteMessage(AcpProtocol.Error(id, -32001, $"No resumable session: {sid}"));
            return;
        }
        // Resume = restore context, NO history replay (unlike session/load). Start the loop
        // with the resumed session and return an empty result when ready.
        StartSession(sid, new AcpResume(resumeData.Value.data, resumeData.Value.sessionDir));
        var rres = new Dictionary<string, object?>();
        var rcfg = BuildConfigOptions();
        if (rcfg.Length > 0) rres["configOptions"] = rcfg;
        var rmodels = BuildModelState();
        if (rmodels is not null) rres["models"] = rmodels;
        Respond(id, rres);
        AdvertiseCommands(sid);   // after the response: clients attach commands to a known session
    }

    private void HandleSetConfigOption(object? id, JsonElement prms)
    {
        // The client picked a config value (e.g. the model selector). Apply known ids, then
        // respond with the COMPLETE config state per spec (the whole configOptions array).
        string? configId = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("configId", out var c)
            && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        string? value = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (string.Equals(configId, "model", StringComparison.Ordinal) && !string.IsNullOrEmpty(value))
            _setModel?.Invoke(value);
        Respond(id, new Dictionary<string, object?> { ["configOptions"] = BuildConfigOptions() });
    }

    /// <summary>
    /// Zed/Copilot Models API: the client picked a model via the agent-panel model picker
    /// (session/set_model {sessionId, modelId}). Apply it as a CLI-style override and ack with
    /// an empty result per the (UNSTABLE) SetSessionModelResponse contract.
    /// </summary>
    private void HandleSetModel(object? id, JsonElement prms)
    {
        string? modelId = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("modelId", out var mEl)
            && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() : null;
        if (!string.IsNullOrEmpty(modelId))
            _setModel?.Invoke(modelId);
        Respond(id, new Dictionary<string, object?>());
    }

    private void HandleSetMode(object? id, JsonElement prms)
    {
        // Mux does not expose distinct per-session ACP operating modes; accept the request,
        // echo the requested mode back via current_mode_update, and ack. This keeps clients
        // that drive a mode selector happy without changing engine behavior.
        string? mode = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("modeId", out var mEl)
            && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() : null;
        if (_sessionId is not null && mode is not null)
            WriteMessage(AcpProtocol.SessionUpdate(_sessionId, AcpProtocol.CurrentModeUpdate(mode)));
        Respond(id, new Dictionary<string, object?>());
    }

    /// <summary>
    /// Send an agent->client REQUEST and await its response. Used for fs/* and
    /// session/request_permission callbacks. Returns the raw result element (default/Undefined
    /// on error or when the client never answers within the timeout).
    /// </summary>
    private async Task<JsonElement> SendRequestAsync(string method, object @params, int timeoutMs = 30000)
    {
        long rid = System.Threading.Interlocked.Increment(ref _outboundId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingOutbound[rid] = tcs;
        WriteMessage(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = rid,
            ["method"] = method,
            ["params"] = @params
        });
        using var cts = new CancellationTokenSource(timeoutMs);
        await using (cts.Token.Register(() => { if (_pendingOutbound.TryRemove(rid, out var t)) t.TrySetResult(default); }))
        {
            return await tcs.Task;
        }
    }

    private void StartSession(string sid, AcpResume? resume)
    {
        // One active session per process. A new session/new (e.g. the client's "/new") must
        // FULLY tear down the prior session BEFORE installing the new one, because the two
        // single-agent loops share global statics (MuxConsole.InputOverride, the cancel
        // monitor, SingleAgentOrchestrator.AgentDef + session tokens, ServeMode.ActiveMode).
        // If they overlap, the dying session's teardown finally resets InputOverride back to
        // Console.In AFTER the new reader was installed, so the new orchestrator reads raw
        // stdin instead of its ACP reader and the first prompt is never delivered (the
        // "sits on thinking forever" bug).
        var prevReader = _reader;
        var prevTask = _sessionTask;
        if (prevReader is not null)
        {
            prevReader.ReadEntered -= OnTurnBoundary;   // stop stale turn-boundary ticks
            StdinCancelMonitor.Instance?.FireCancel();  // abort any in-flight turn so it can quit
            prevReader.CloseSession();                  // queue /qc so the loop exits
        }
        if (prevTask is not null)
        {
            // Block the dedicated reader thread briefly until the old loop unwinds. Nothing
            // else needs dispatching during a session swap; the cap guards against a wedged turn.
            try { prevTask.Wait(5000); } catch { /* best-effort teardown */ }
        }

        var reader = new AcpInputReader();
        _reader = reader;
        _sessionId = sid;
        _titlePending = false;
        _sawFirstReadTick = false;
        _pendingPromptId = null;
        _cancelRequested = false;
        _messageCounter = 0;
        lock (_toolLock) { _toolCounter = 0; _openTools.Clear(); _fifoToolIds.Clear(); }

        reader.ReadEntered += OnTurnBoundary;
        MuxConsole.InputOverride = reader;

        // Start the interactive single-agent loop. It immediately enters ReadInput (the first
        // boundary tick, which is just "ready") and blocks until the first prompt is pushed.
        _sessionTask = Task.Run(async () =>
        {
            try { await _runSession(reader, resume); }
            catch (Exception ex) { Log($"session ended: {ex.Message}"); }
            // Only relinquish stdin if THIS reader is still the installed override; a newer
            // session may have already taken over (guards the InputOverride clobber race).
            finally { if (ReferenceEquals(MuxConsole.InputOverride, reader)) MuxConsole.InputOverride = Console.In; }
        });

    }

    /// <summary>
    /// Advertise Mux's in-session slash commands to the client so it can surface them in its
    /// own command palette. A curated subset of the most useful in-session controls.
    /// </summary>
    private void AdvertiseCommands(string sid)
    {
        var cmds = new (string, string)[]
        {
            ("compact", "Compress conversation context to free tokens"),
            ("wipe", "Clear the session context and start fresh"),
            ("tokens", "Show current context token usage"),
            ("sub", "Toggle single-agent delegation to sub-agents"),
            ("psub", "Toggle parallel sub-agent delegation"),
            ("ultra", "Toggle maximum-reasoning ultra mode"),
            ("giga", "Toggle dynamic team/workflow orchestration"),
            ("qc", "End the current session")
        };
        WriteMessage(AcpProtocol.SessionUpdate(sid, AcpProtocol.AvailableCommandsUpdate(cmds)));
    }

    /// <summary>
    /// Replay a persisted session to the client, per the session/load contract: user text as
    /// user_message_chunk, reasoning as agent_thought_chunk, assistant text as agent_message_chunk,
    /// and each tool call as a tool_call + finished tool_call_update (completed, or failed for
    /// error results / calls that never got one), in conversation order.
    /// </summary>
    private void ReplayHistory(string sid, JsonElement data)
    {
        foreach (var update in ReplayUpdates(data))
            WriteMessage(AcpProtocol.SessionUpdate(sid, update));
    }

    /// <summary>The session/update payloads that replay a serialized agent session (pure; testable).</summary>
    internal static List<object> ReplayUpdates(JsonElement data)
    {
        var updates = new List<object>();
        if (!Common.TryGetSessionMessages(data, out var messages)) return updates;
        var open = new List<string>();
        int n = 0;
        foreach (var msg in messages.EnumerateArray())
        {
            string role = msg.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
            if (!msg.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array) continue;
            string mid = "msg_replay_" + (++n);
            foreach (var c in contents.EnumerateArray())
            {
                string type = c.TryGetProperty("$type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() ?? "" : "";
                switch (type)
                {
                    case "text" when role is "user" or "assistant":
                    {
                        string text = StripInsight(c.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "");
                        if (text.Length == 0) break;
                        updates.Add(role == "user" ? UserMessageChunk(text, mid) : AcpProtocol.AgentMessageChunk(text, mid));
                        break;
                    }
                    case "reasoning" when role == "assistant":
                    {
                        string text = c.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                        if (text.Length > 0) updates.Add(AcpProtocol.AgentThoughtChunk(text, mid));
                        break;
                    }
                    case "functionCall":
                    {
                        string callId = c.TryGetProperty("callId", out var ci) ? ci.GetString() ?? "" : "";
                        string name = c.TryGetProperty("name", out var nm) ? nm.GetString() ?? "tool" : "tool";
                        string? args = c.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.Object ? ar.GetRawText() : null;
                        if (callId.Length == 0) break;
                        updates.Add(AcpProtocol.ToolCallWithLocations(callId, AcpProtocol.ToolTitle(name, args), AcpProtocol.ToolKind(name),
                            "in_progress", args is null ? null : ar.Clone(), AcpProtocol.Locations(args)));
                        open.Add(callId);
                        break;
                    }
                    case "functionResult":
                    {
                        string callId = c.TryGetProperty("callId", out var ci) ? ci.GetString() ?? "" : "";
                        if (!open.Remove(callId)) break;   // result for a call that was never announced
                        string body = StripInsight(c.TryGetProperty("result", out var rv) ? ResultText(rv) : "");
                        bool failed = ContextPruner.IsErrorText(body);
                        if (body.Length > 4000) body = body[..4000] + "\n... (truncated)";
                        updates.Add(AcpProtocol.ToolCallUpdateRich(callId, failed ? "failed" : "completed",
                            body.Trim().Length == 0 ? null : new[] { AcpProtocol.TextContent(body) }));
                        break;
                    }
                }
            }
        }
        foreach (var callId in open)   // interrupted before a result was saved
            updates.Add(AcpProtocol.ToolCallUpdateRich(callId, "failed", null));
        return updates;
    }

    /// <summary>Text of a serialized tool result: a string, the text parts of a content list, or raw JSON.</summary>
    private static string ResultText(JsonElement result) => result.ValueKind switch
    {
        JsonValueKind.String => result.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n", result.EnumerateArray()
            .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            .Select(p => p.GetProperty("text").GetString())),
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => result.GetRawText()
    };

    /// <summary>Drop the runtime's "[mux context: ...]" gauge line (/insight max), which is saved
    /// with the message but was never typed or seen by the user.</summary>
    private static string StripInsight(string text)
    {
        int i = text.IndexOf("[mux context:", StringComparison.Ordinal);
        return (i >= 0 ? text[..i] : text).TrimEnd();
    }

    private static object UserMessageChunk(string text, string messageId) =>
        new Dictionary<string, object?>
        {
            ["sessionUpdate"] = "user_message_chunk",
            ["messageId"] = messageId,
            ["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = text }
        };

    private void HandlePrompt(object? id, JsonElement prms)
    {
        if (_reader is null || _sessionId is null)
        {
            if (id is not null) WriteMessage(AcpProtocol.Error(id, -32002, "No active session. Call session/new first."));
            return;
        }

        string text = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("prompt", out var promptEl)
            ? AcpProtocol.ExtractPromptText(promptEl)
            : string.Empty;

        // Defer the response: it is sent when the orchestrator finishes this turn and re-enters
        // ReadInput (OnTurnBoundary). Begin a fresh assistant message id for this turn.
        _pendingPromptId = id;
        _cancelRequested = false;
        _currentMessageId = "msg_" + (++_messageCounter);

        // First prompt of a new session: record its working directory beside the session file
        // (for session/list) and give the client a title for its thread list.
        if (_titlePending && !string.IsNullOrWhiteSpace(text))
        {
            _titlePending = false;
            WriteCwdSidecar(Path.Combine(PlatformContext.SessionsDirectory, _sessionId), _sessionCwd);
            WriteMessage(AcpProtocol.SessionUpdate(_sessionId, new Dictionary<string, object?>
            {
                ["sessionUpdate"] = "session_info_update",
                ["title"] = Title(text),
                ["updatedAt"] = DateTimeOffset.UtcNow.ToString("o")
            }));
        }

        // A blank/whitespace prompt would be treated as quit by the orchestrator; substitute a
        // no-op nudge so the turn still produces a clean end_turn.
        _reader.Push(string.IsNullOrWhiteSpace(text) ? "(no input)" : text);
    }

    /// <summary>
    /// Fires whenever the orchestrator enters ReadInput. The first tick after session start is
    /// just "ready"; every later tick means the previously in-flight prompt's turn has fully
    /// completed, so we answer that session/prompt with a stopReason.
    /// </summary>
    private void OnTurnBoundary()
    {
        if (!_sawFirstReadTick)
        {
            _sawFirstReadTick = true;
            return;
        }

        var id = _pendingPromptId;
        if (id is null) return;
        _pendingPromptId = null;

        CloseOpenTools();
        EmitUsage();

        string stop = _cancelRequested ? AcpProtocol.StopReason.Cancelled : AcpProtocol.StopReason.EndTurn;
        _cancelRequested = false;
        Respond(id, new Dictionary<string, object?> { ["stopReason"] = stop });
    }

    // ----- outbound: Mux event -> ACP session/update ------------------------------------

    private void OnMuxEvent(string type, IReadOnlyDictionary<string, object?> payload)
    {
        // No active session yet -> route diagnostics to stderr (stdout must stay pure ACP).
        string? sid = _sessionId;
        if (sid is null) { Log($"{type}"); return; }

        switch (type)
        {
            case "stream":
            {
                string text = Str(payload, "text");
                if (text.Length == 0) return;
                bool reasoning = payload.TryGetValue("reasoning", out var r) && r is true;
                object update = reasoning
                    ? AcpProtocol.AgentThoughtChunk(text, _currentMessageId)
                    : AcpProtocol.AgentMessageChunk(text, _currentMessageId);
                WriteMessage(AcpProtocol.SessionUpdate(sid, update));
                break;
            }
            case "tool_call":
                OnToolCall(sid, payload);
                break;
            case "tool_result":
                OnToolResult(sid, payload);
                break;
            case "step":
            {
                // A WriteStep frame is a single plan item becoming the in-progress focus. ACP
                // plans are full snapshots, so emit a one-entry plan marking the current step.
                string title = Str(payload, "title");
                if (title.Length > 0)
                    WriteMessage(AcpProtocol.SessionUpdate(sid,
                        AcpProtocol.Plan(new[] { (title, "medium", "in_progress") })));
                break;
            }
            // Diagnostics / unmapped frames -> stderr so the client may surface logs.
            case "error":
            case "warning":
                Log($"{type}: {Str(payload, "message")}");
                break;
            default:
                break;   // thinking/step/rule/banner/etc. are not part of the ACP turn stream
        }
    }

    /// <summary>
    /// A tool was invoked: announce it right away (before it runs) with a readable title,
    /// rawInput and follow-along locations. Edit/write tools snapshot the target file first so
    /// the result can carry a real old/new diff.
    /// </summary>
    private void OnToolCall(string sid, IReadOnlyDictionary<string, object?> payload)
    {
        string tool = Str(payload, "tool");
        string args = Str(payload, "args");
        string callId = Str(payload, "callId");
        string kind = AcpProtocol.ToolKind(tool);
        string? path = AcpProtocol.ExtractAbsolutePath(args);
        string? oldText = null;
        bool snapshot = false;
        if (kind == "edit" && path is not null)
            snapshot = TrySnapshot(path, out oldText);

        string acpId;
        lock (_toolLock)
        {
            acpId = callId.Length > 0 && !_openTools.ContainsKey(callId) ? callId : "call_" + (++_toolCounter);
            _openTools[acpId] = new OpenTool(acpId, tool, args, path, oldText, snapshot);
            if (callId.Length == 0) _fifoToolIds.Enqueue(acpId);
        }
        EmitToolCall(sid, acpId, tool, args, "in_progress");
    }

    /// <summary>
    /// A tool finished: complete its card (failed when the result is an error). A result with no
    /// announced call (older event shapes) announces one first, so the client never sees an
    /// update for an unknown id.
    /// </summary>
    private void OnToolResult(string sid, IReadOnlyDictionary<string, object?> payload)
    {
        string tool = Str(payload, "tool");
        string callId = Str(payload, "callId");
        string body = payload.TryGetValue("result", out var rv) && rv is string rs && rs.Length > 0
            ? rs : Str(payload, "summary");
        bool isError = payload.TryGetValue("isError", out var ev) && ev is true;

        OpenTool? open = null;
        bool announced = true;
        lock (_toolLock)
        {
            if (callId.Length > 0 && _openTools.Remove(callId, out var byId)) open = byId;
            else
            {
                while (open is null && _fifoToolIds.TryDequeue(out var fid))
                    if (_openTools.Remove(fid, out var byFifo)) open = byFifo;
            }
            if (open is null)
            {
                announced = false;
                string acpId = callId.Length > 0 ? callId : "call_" + (++_toolCounter);
                open = new OpenTool(acpId, string.IsNullOrEmpty(tool) ? "tool" : tool, null, null, null, false);
            }
        }
        if (!announced)
            EmitToolCall(sid, open.AcpId, open.Tool, null, "in_progress");

        var content = BuildToolContent(open, body, isError);
        WriteMessage(AcpProtocol.SessionUpdate(sid,
            AcpProtocol.ToolCallUpdateRich(open.AcpId, isError ? "failed" : "completed", content)));
    }

    /// <summary>Finish every still-open call as failed (turn ended or was cancelled before its
    /// result arrived). Must run before the session/prompt response.</summary>
    private void CloseOpenTools()
    {
        string? sid = _sessionId;
        List<OpenTool> open;
        lock (_toolLock)
        {
            open = _openTools.Values.ToList();
            _openTools.Clear();
            _fifoToolIds.Clear();
        }
        if (sid is null) return;
        foreach (var t in open)
            WriteMessage(AcpProtocol.SessionUpdate(sid, AcpProtocol.ToolCallUpdateRich(t.AcpId, "failed", null)));
    }

    private void EmitToolCall(string sid, string toolId, string tool, string? args, string status)
    {
        object? raw = null;
        if (!string.IsNullOrWhiteSpace(args))
        {
            try
            {
                using var doc = JsonDocument.Parse(args);
                raw = doc.RootElement.Clone();
            }
            catch (JsonException) { raw = new Dictionary<string, object?> { ["args"] = args }; }
        }
        WriteMessage(AcpProtocol.SessionUpdate(sid,
            AcpProtocol.ToolCallWithLocations(toolId, AcpProtocol.ToolTitle(tool, args), AcpProtocol.ToolKind(tool),
                status, raw, AcpProtocol.Locations(args))));
    }

    /// <summary>Read a file's current text for a diff card. Missing file = new file (true, null).</summary>
    private static bool TrySnapshot(string path, out string? text)
    {
        text = null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return true;
            if (fi.Length > MaxDiffBytes) return false;
            text = File.ReadAllText(path);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Build the finished tool_call_update content. A successful edit/write with a snapshot
    /// becomes an ACP diff block {path, oldText, newText} read from disk; everything else is a
    /// text block (truncated at 4000 chars). Empty bodies yield no content.
    /// </summary>
    private static object[]? BuildToolContent(OpenTool t, string body, bool isError)
    {
        if (!isError && t.Snapshot && t.Path is not null && TrySnapshot(t.Path, out var newText) && newText is not null
            && !string.Equals(newText, t.OldText, StringComparison.Ordinal))
            return new[] { AcpProtocol.DiffContent(t.Path, t.OldText, newText) };
        if (string.IsNullOrWhiteSpace(body)) return null;
        string text = body.Length > 4000 ? body[..4000] + "\n... (truncated)" : body;
        return new[] { AcpProtocol.TextContent(text) };
    }

    private void EmitUsage()
    {
        string? sid = _sessionId;
        if (sid is null) return;
        try
        {
            uint used = SingleAgentOrchestrator.SessionTokens;
            int size = SingleAgentOrchestrator.AutoCompactThreshold;
            if (used == 0) return;
            WriteMessage(AcpProtocol.SessionUpdate(sid, new Dictionary<string, object?>
            {
                ["sessionUpdate"] = "usage_update",
                ["used"] = (int)used,
                ["size"] = size > 0 ? size : 200_000
            }));
        }
        catch { /* telemetry best-effort */ }
    }

    // ----- low-level write ---------------------------------------------------------------

    private void Respond(object? id, object? result) => WriteMessage(AcpProtocol.Response(id ?? 0, result!));

    private void WriteMessage(object message)
    {
        string line = AcpProtocol.Serialize(message);
        lock (_outLock)
        {
            Console.Out.Write(line);
            Console.Out.Write('\n');
            Console.Out.Flush();
        }
    }

    private static void Log(string msg)
    {
        try { Console.Error.WriteLine("[acp] " + msg); } catch { /* ignore */ }
    }

    private static string Str(IReadOnlyDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is string s ? s : string.Empty;

    private static object? ExtractId(JsonElement idEl) => idEl.ValueKind switch
    {
        JsonValueKind.String => idEl.GetString(),
        JsonValueKind.Number => idEl.TryGetInt64(out var l) ? l : idEl.GetDouble(),
        _ => null
    };

    /// <summary>Session payload for the loop: the persisted session JSON (null for a new
    /// session) + the Sessions directory it persists to.</summary>
    public readonly record struct AcpResume(System.Text.Json.JsonElement? Data, string Dir);

    // ----- session/list ------------------------------------------------------------------

    /// <summary>Sidecar beside a session's *_session.json holding the client cwd (not *.json, so
    /// session readers and the lead-session detector ignore it).</summary>
    internal const string CwdFileName = "acp.muxcwd";
    private const int ListPageSize = 50;

    /// <summary>A new session id: the Sessions folder-name timestamp, suffixed if that folder exists.</summary>
    private string NewSessionId()
    {
        string baseId = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string sid = baseId;
        for (int n = 2; Directory.Exists(Path.Combine(PlatformContext.SessionsDirectory, sid)) || sid == _sessionId; n++)
            sid = $"{baseId}-{n}";
        return sid;
    }

    private static void WriteCwdSidecar(string sessionDir, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) return;
        try
        {
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, CwdFileName), cwd);
        }
        catch (Exception ex) { Log($"cwd sidecar: {ex.Message}"); }
    }

    /// <summary>One-line thread title from a first prompt (whitespace collapsed, at most 80 chars).</summary>
    internal static string Title(string text)
    {
        string t = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return t.Length > 80 ? t[..77] + "..." : t;
    }

    /// <summary>
    /// session/list result: lead-agent sessions with a saved file, newest first, paged by an
    /// opaque offset cursor. Each row: sessionId (folder name), absolute cwd, title (tag, else
    /// first user message), updatedAt (RFC 3339, session file write time). Sessions without a
    /// recorded cwd (started outside ACP) report <paramref name="cwdFilter"/> or
    /// <paramref name="fallbackCwd"/>, so they show in every project; recorded ones are filtered.
    /// </summary>
    internal static object ListSessions(string sessionsDir, string? cwdFilter, string? cursor, string fallbackCwd)
    {
        var rows = new List<(string Id, string Cwd, string? Title, DateTime Updated)>();
        if (Directory.Exists(sessionsDir))
        {
            foreach (var d in Directory.GetDirectories(sessionsDir))
            {
                var files = Directory.GetFiles(d, "*.json");
                var file = files.FirstOrDefault(f => f.EndsWith("_session.json", StringComparison.OrdinalIgnoreCase));
                if (files.Length > 2 || file is null) continue;   // swarm sessions / not saved yet
                string? recorded = null;
                try { recorded = File.ReadAllText(Path.Combine(d, CwdFileName)).Trim(); } catch { /* none */ }
                if (!string.IsNullOrEmpty(recorded) && cwdFilter is not null && !SamePath(recorded, cwdFilter)) continue;
                string cwd = !string.IsNullOrEmpty(recorded) ? recorded : cwdFilter ?? fallbackCwd;
                string? title = SessionTags.TagLabel(d);
                if (string.IsNullOrEmpty(title))
                {
                    string first = Common.GetFirstUserMessage(file, int.MaxValue);
                    title = first == "No preview" ? null : Title(first);
                }
                rows.Add((Path.GetFileName(d), cwd, title, File.GetLastWriteTimeUtc(file)));
            }
        }
        rows.Sort((a, b) => b.Updated.CompareTo(a.Updated));
        int start = int.TryParse(cursor, out var c) && c > 0 ? c : 0;
        var page = rows.Skip(start).Take(ListPageSize).Select(r => new Dictionary<string, object?>
        {
            ["sessionId"] = r.Id,
            ["cwd"] = r.Cwd,
            ["title"] = r.Title,
            ["updatedAt"] = new DateTimeOffset(r.Updated, TimeSpan.Zero).ToString("o")
        }).ToArray();
        var result = new Dictionary<string, object?> { ["sessions"] = page };
        if (start + ListPageSize < rows.Count) result["nextCursor"] = (start + ListPageSize).ToString();
        return result;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? StrProp(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
