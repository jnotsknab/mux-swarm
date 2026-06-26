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
    private readonly Func<AcpInputReader, Task> _runSession;
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

    public AcpServer(string version, Func<AcpInputReader, Task> runSession)
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

        try
        {
            while (true)
            {
                string? line = await Task.Run(Console.In.ReadLine);
                if (line is null) break;          // stdin closed -> client disconnected
                if (line.Length == 0) continue;
                try { Dispatch(line); }
                catch (Exception ex) { Log($"dispatch error: {ex.Message}"); }
            }
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

            if (method is null) return;       // a response to one of our outbound calls; none expected in MVP

            JsonElement prms = root.TryGetProperty("params", out var p) ? p : default;

            switch (method)
            {
                case "initialize":
                    Respond(id, AcpProtocol.InitializeResult(_version));
                    break;

                case "authenticate":
                    Respond(id, new Dictionary<string, object?>());
                    break;

                case "session/new":
                    HandleSessionNew(id);
                    break;

                case "session/load":
                    // loadSession capability is not advertised in MVP; reply empty so a
                    // permissive client does not hang.
                    Respond(id, (object?)null);
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

                default:
                    if (hasId)
                        WriteMessage(AcpProtocol.Error(id, -32601, $"Method not found: {method}"));
                    break;
            }
        }
    }

    private void HandleSessionNew(object? id)
    {
        // MVP supports a single active session per process. Tear down any prior reader.
        _reader?.CloseSession();

        var reader = new AcpInputReader();
        _reader = reader;
        _sessionId = "sess_" + Guid.NewGuid().ToString("N")[..12];
        _sawFirstReadTick = false;
        _pendingPromptId = null;
        _cancelRequested = false;

        reader.ReadEntered += OnTurnBoundary;
        MuxConsole.InputOverride = reader;

        // Start the interactive single-agent loop. It immediately enters ReadInput (the first
        // boundary tick, which is just "ready") and blocks until the first prompt is pushed.
        _sessionTask = Task.Run(async () =>
        {
            try { await _runSession(reader); }
            catch (Exception ex) { Log($"session ended: {ex.Message}"); }
            finally { MuxConsole.InputOverride = Console.In; }
        });

        Respond(id, new Dictionary<string, object?> { ["sessionId"] = _sessionId });
    }

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
}
