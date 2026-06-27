using System.Collections.Concurrent;
using System.Text.Json;

namespace MuxSwarm.Utils.Acp;

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

    private AcpInputReader? _reader;
    private Task? _sessionTask;
    private string? _sessionId;

    // Turn-boundary state. _pendingPromptId is the JSON-RPC id of the in-flight session/prompt;
    // its response is deferred until the orchestrator re-enters ReadInput (turn complete).
    private object? _pendingPromptId;
    private volatile bool _cancelRequested;
    private bool _sawFirstReadTick;
    private int _messageCounter;
    private string _currentMessageId = "msg_0";

    // Tool-call correlation: Mux tool_call/tool_result carry no id, so synthesize one and pair
    // results to calls FIFO (single-agent tools run sequentially).
    private int _toolCounter;
    private readonly ConcurrentQueue<string> _pendingToolIds = new();
    private string? _lastToolArgs;

    // Client capabilities (from initialize) gating agent->client callbacks (fs/*, terminal/*).
    private AcpClientCaps _clientCaps;

    // Outbound request correlation (agent->client requests like fs/* and request_permission).
    private int _outboundId = 1000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingOutbound = new();

    public AcpServer(string version, Func<AcpInputReader, AcpResume?, Task> runSession)
    {
        _version = version;
        _runSession = runSession;
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
                    HandleSessionNew(id);
                    break;

                case "session/load":
                    HandleSessionLoad(id, prms);
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

    private void HandleSessionNew(object? id)
    {
        string sid = "sess_" + Guid.NewGuid().ToString("N")[..12];
        StartSession(sid, resume: null);
        Respond(id, new Dictionary<string, object?> { ["sessionId"] = sid });
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
        var resumeData = MuxSwarm.Utils.CliCmdUtils.HandleSessionResume(sid);
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
        Respond(id, (object?)null);
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
        var resumeData = MuxSwarm.Utils.CliCmdUtils.HandleSessionResume(sid);
        if (resumeData is null)
        {
            if (id is not null) WriteMessage(AcpProtocol.Error(id, -32001, $"No resumable session: {sid}"));
            return;
        }
        // Resume = restore context, NO history replay (unlike session/load). Start the loop
        // with the resumed session and return an empty result when ready.
        StartSession(sid, new AcpResume(resumeData.Value.data, resumeData.Value.sessionDir));
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
        _sawFirstReadTick = false;
        _pendingPromptId = null;
        _cancelRequested = false;
        _messageCounter = 0;
        _toolCounter = 0;
        while (_pendingToolIds.TryDequeue(out _)) { }   // drop any stale tool ids
        _lastToolArgs = null;

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

        AdvertiseCommands(sid);
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
    /// Replay a persisted session's messages to the client as session/update notifications
    /// (user_message_chunk for user turns, agent_message_chunk for assistant turns), per the
    /// session/load contract. Each message gets its own messageId.
    /// </summary>
    private void ReplayHistory(string sid, JsonElement data)
    {
        int n = 0;
        foreach (var msg in MuxSwarm.Utils.Common.ExtractMessagesFromSession(data))
        {
            string txt = msg.Text ?? string.Empty;
            if (txt.Length == 0) continue;
            bool isUser = msg.Role == Microsoft.Extensions.AI.ChatRole.User;
            string mid = (isUser ? "msg_user_" : "msg_agent_") + (++n);
            object update = isUser
                ? UserMessageChunk(txt, mid)
                : AcpProtocol.AgentMessageChunk(txt, mid);
            WriteMessage(AcpProtocol.SessionUpdate(sid, update));
        }
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
            {
                // Explicit tool_call frame (swarm/parallel paths). Emit a pending->in_progress
                // ACP tool_call; the matching tool_result completes it.
                string tool = Str(payload, "tool");
                string args = Str(payload, "args");
                string toolId = "call_" + (++_toolCounter);
                _pendingToolIds.Enqueue(toolId);
                _lastToolArgs = args;
                EmitToolCall(sid, toolId, tool, args);
                break;
            }
            case "tool_result":
            {
                // The single-agent path emits ONLY a tool_result frame (no preceding tool_call),
                // carrying {agent, tool, result}. Swarm paths use {agent, summary} after a
                // tool_call. Synthesize whatever the client has not seen yet so every tool shows
                // a pending->completed lifecycle.
                string tool = Str(payload, "tool");
                string body = payload.TryGetValue("result", out var rv) && rv is string rs && rs.Length > 0
                    ? rs : Str(payload, "summary");
                string toolId;
                if (_pendingToolIds.TryDequeue(out var tid))
                {
                    toolId = tid;   // completes a tool_call already announced
                }
                else
                {
                    // No preceding tool_call (single-agent): announce one first.
                    toolId = "call_" + (++_toolCounter);
                    EmitToolCall(sid, toolId, string.IsNullOrEmpty(tool) ? "tool" : tool, _lastToolArgs);
                }
                var content = BuildToolContent(tool, _lastToolArgs, body);
                WriteMessage(AcpProtocol.SessionUpdate(sid,
                    AcpProtocol.ToolCallUpdateRich(toolId, "completed", content)));
                _lastToolArgs = null;
                break;
            }
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

    private void EmitToolCall(string sid, string toolId, string tool, string? args)
    {
        var raw = string.IsNullOrEmpty(args) ? null : new Dictionary<string, object?> { ["args"] = args };
        var locations = AcpProtocol.Locations(args);
        WriteMessage(AcpProtocol.SessionUpdate(sid,
            AcpProtocol.ToolCallWithLocations(toolId, tool, AcpProtocol.ToolKind(tool), "in_progress", raw, locations)));
    }

    /// <summary>
    /// Build the completed tool_call_update content. Edit-tool results that contain a git-style
    /// unified diff are surfaced as an ACP diff block (path from the tool args); everything else
    /// becomes a text block. Empty bodies yield no content.
    /// </summary>
    private object[]? BuildToolContent(string tool, string? args, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        string kind = AcpProtocol.ToolKind(tool);
        string? path = AcpProtocol.ExtractAbsolutePath(args);
        if (kind == "edit" && path is not null && LooksLikeDiff(body))
        {
            // Surface the unified diff as both a diff block (rich UI) and the raw text.
            return new[] { AcpProtocol.DiffContent(path, null, body) };
        }
        string text = body.Length > 4000 ? body[..4000] + "\n... (truncated)" : body;
        return new[] { AcpProtocol.TextContent(text) };
    }

    private static bool LooksLikeDiff(string s) =>
        s.Contains("@@") || s.Contains("--- ") || s.Contains("+++ ") ||
        (s.Contains("\n+") && s.Contains("\n-"));

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

    /// <summary>Resume payload for session/load: the persisted session JSON + its directory.</summary>
    public readonly record struct AcpResume(System.Text.Json.JsonElement Data, string Dir);
}
