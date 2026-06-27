using System.Text;
using System.Text.Json;

namespace MuxSwarm.Utils.Acp;

/// <summary>
/// Pure protocol layer for the Agent Client Protocol (ACP, Zed Industries) adapter.
/// JSON-RPC 2.0 over newline-delimited stdio: every message is a single compact JSON
/// line, UTF-8, with no embedded newlines and no Content-Length framing.
///
/// This type holds ONLY pure, side-effect-free helpers (envelope build/parse + the
/// Mux-event -> ACP session/update translation) so the wire contract is unit-testable
/// without a live model or any console I/O. All transport / lifecycle lives in
/// <see cref="AcpServer"/>.
/// </summary>
public static class AcpProtocol
{
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Canonical serializer options for the ACP wire: camelCase property names (ACP keys are
    /// camelCase) and null-property omission. Discriminator string VALUES (e.g.
    /// "agent_message_chunk", "end_turn") are snake_case and are emitted as explicit string
    /// literals, so the naming policy never touches them.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serialize any payload to a single compact JSON line (no trailing newline).</summary>
    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, Json);

    // ----- Outbound envelope builders ---------------------------------------------------

    /// <summary>A successful JSON-RPC response to a request id.</summary>
    public static object Response(object id, object result) =>
        new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    /// <summary>A JSON-RPC error response to a request id.</summary>
    public static object Error(object? id, int code, string message) =>
        new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message }
        };

    /// <summary>A one-way JSON-RPC notification (no id, never answered).</summary>
    public static object Notification(string method, object @params) =>
        new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params };

    /// <summary>
    /// Wrap an ACP update object in a session/update notification for the given session.
    /// </summary>
    public static object SessionUpdate(string sessionId, object update) =>
        Notification("session/update", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["update"] = update
        });

    // ----- initialize result ------------------------------------------------------------

    /// <summary>
    /// Build the InitializeResponse. MVP advertises only the baseline: protocolVersion 1, no
    /// optional agent capabilities (text + resource_link prompts are baseline and always
    /// supported), and agentInfo identifying the mux-swarm engine.
    /// </summary>
    public static object InitializeResult(string version, bool loadSession = false) =>
        new Dictionary<string, object?>
        {
            ["protocolVersion"] = ProtocolVersion,
            ["agentCapabilities"] = new Dictionary<string, object?>
            {
                ["loadSession"] = loadSession,
                ["promptCapabilities"] = new Dictionary<string, object?>
                {
                    // Baseline text + resource_link are always supported; embeddedContext lets
                    // clients inline text resources (e.g. open-buffer contents) into a prompt.
                    ["image"] = false,
                    ["audio"] = false,
                    ["embeddedContext"] = true
                },
                // Session lifecycle: resume (no-replay restore) + close. Each '{}' = supported.
                ["sessionCapabilities"] = new Dictionary<string, object?>
                {
                    ["resume"] = new Dictionary<string, object?>(),
                    ["close"] = new Dictionary<string, object?>()
                },
                // Auth: logout supported (auth itself is a no-op; no methods required).
                ["auth"] = new Dictionary<string, object?>
                {
                    ["logout"] = new Dictionary<string, object?>()
                }
            },
            ["agentInfo"] = new Dictionary<string, object?>
            {
                ["name"] = "mux-swarm",
                ["version"] = version
            },
            ["authMethods"] = Array.Empty<object>()
        };

    // ----- prompt content extraction ----------------------------------------------------

    /// <summary>
    /// Flatten an ACP prompt ContentBlock[] into a single user-message string. Text blocks
    /// contribute their text; embedded text resources contribute their text with a small
    /// header; resource_link blocks contribute a "[resource: uri]" reference. Unknown block
    /// types are skipped. Returns an empty string when nothing usable is present.
    /// </summary>
    public static string ExtractPromptText(JsonElement prompt)
    {
        if (prompt.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var block in prompt.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!block.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) continue;
            string type = t.GetString() ?? "";
            switch (type)
            {
                case "text":
                    if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                        Append(sb, txt.GetString());
                    break;
                case "resource":
                    if (block.TryGetProperty("resource", out var res) && res.ValueKind == JsonValueKind.Object)
                    {
                        string? uri = res.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                        if (res.TryGetProperty("text", out var rtxt) && rtxt.ValueKind == JsonValueKind.String)
                        {
                            if (uri != null) Append(sb, $"[resource: {uri}]");
                            Append(sb, rtxt.GetString());
                        }
                        else if (uri != null)
                        {
                            Append(sb, $"[resource: {uri}]");
                        }
                    }
                    break;
                case "resource_link":
                    if (block.TryGetProperty("uri", out var lu) && lu.ValueKind == JsonValueKind.String)
                        Append(sb, $"[resource: {lu.GetString()}]");
                    break;
            }
        }
        return sb.ToString().Trim();

        static void Append(StringBuilder sb, string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(s);
        }
    }

    // ----- update (session/update) factories --------------------------------------------

    public static object AgentMessageChunk(string text, string? messageId = null) =>
        Chunk("agent_message_chunk", text, messageId);

    public static object AgentThoughtChunk(string text, string? messageId = null) =>
        Chunk("agent_thought_chunk", text, messageId);

    private static object Chunk(string variant, string text, string? messageId)
    {
        // WhenWritingNull does NOT apply to Dictionary entries (only object properties), so a
        // null messageId must be omitted explicitly to keep it off the wire.
        var d = new Dictionary<string, object?>
        {
            ["sessionUpdate"] = variant,
            ["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = text }
        };
        if (messageId is not null) d["messageId"] = messageId;
        return d;
    }

    public static object ToolCall(string toolCallId, string title, string kind = "other", string status = "pending", object? rawInput = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["sessionUpdate"] = "tool_call",
            ["toolCallId"] = toolCallId,
            ["title"] = title,
            ["kind"] = kind,
            ["status"] = status
        };
        if (rawInput is not null) d["rawInput"] = rawInput;
        return d;
    }

    public static object ToolCallUpdate(string toolCallId, string status, string? text = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["sessionUpdate"] = "tool_call_update",
            ["toolCallId"] = toolCallId,
            ["status"] = status
        };
        if (text is not null)
            d["content"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "content",
                    ["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = text }
                }
            };
        return d;
    }

    /// <summary>tool_call with optional rawInput + follow-along locations.</summary>
    public static object ToolCallWithLocations(string toolCallId, string title, string kind, string status, object? rawInput, object[]? locations)
    {
        var d = new Dictionary<string, object?>
        {
            ["sessionUpdate"] = "tool_call",
            ["toolCallId"] = toolCallId,
            ["title"] = title,
            ["kind"] = kind,
            ["status"] = status
        };
        if (rawInput is not null) d["rawInput"] = rawInput;
        if (locations is not null) d["locations"] = locations;
        return d;
    }

    /// <summary>tool_call_update carrying an already-built ToolCallContent[] (text/diff).</summary>
    public static object ToolCallUpdateRich(string toolCallId, string status, object[]? content)
    {
        var d = new Dictionary<string, object?>
        {
            ["sessionUpdate"] = "tool_call_update",
            ["toolCallId"] = toolCallId,
            ["status"] = status
        };
        if (content is { Length: > 0 }) d["content"] = content;
        return d;
    }

    /// <summary>A regular ToolCallContent wrapping a text ContentBlock.</summary>
    public static object TextContent(string text) =>
        new Dictionary<string, object?>
        {
            ["type"] = "content",
            ["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = text }
        };

    /// <summary>A diff ToolCallContent. oldText may be null (new file); newText required.</summary>
    public static object DiffContent(string path, string? oldText, string newText)
    {
        var d = new Dictionary<string, object?>
        {
            ["type"] = "diff",
            ["path"] = path,
            ["newText"] = newText
        };
        d["oldText"] = oldText;   // explicit null is meaningful (new file) per spec
        return d;
    }

    /// <summary>
    /// Map a Mux tool name to the closest ACP ToolKind
    /// (read|edit|delete|move|search|execute|think|fetch|other).
    /// </summary>
    public static string ToolKind(string tool)
    {
        string t = (tool ?? string.Empty).ToLowerInvariant();
        if (t.Contains("read") || t.Contains("get_file") || t.Contains("list") || t.Contains("cat")) return "read";
        if (t.Contains("write") || t.Contains("edit") || t.Contains("save") || t.Contains("patch") || t.Contains("apply")) return "edit";
        if (t.Contains("delete") || t.Contains("remove") || t.Contains("rm")) return "delete";
        if (t.Contains("move") || t.Contains("rename")) return "move";
        if (t.Contains("search") || t.Contains("grep") || t.Contains("find")) return "search";
        if (t.Contains("exec") || t.Contains("command") || t.Contains("shell") || t.Contains("run") || t.Contains("python") || t.Contains("bash")) return "execute";
        if (t.Contains("fetch") || t.Contains("http") || t.Contains("download") || t.Contains("url")) return "fetch";
        if (t.Contains("think") || t.Contains("reason")) return "think";
        return "other";
    }

    /// <summary>
    /// A plan update (full snapshot). Each entry: content + priority (high|medium|low) +
    /// status (pending|in_progress|completed). The client REPLACES the whole plan each time.
    /// </summary>
    public static object Plan(IEnumerable<(string Content, string Priority, string Status)> entries) =>
        new Dictionary<string, object?>
        {
            ["sessionUpdate"] = "plan",
            ["entries"] = entries.Select(e => new Dictionary<string, object?>
            {
                ["content"] = e.Content,
                ["priority"] = e.Priority,
                ["status"] = e.Status
            }).ToArray()
        };

    /// <summary>
    /// A ToolCallLocation { path (absolute), line? } for follow-along UI, or null when no
    /// absolute path can be extracted from the tool arguments.
    /// </summary>
    public static object[]? Locations(string? toolArgs)
    {
        string? path = ExtractAbsolutePath(toolArgs);
        if (path is null) return null;
        return new object[] { new Dictionary<string, object?> { ["path"] = path } };
    }

    /// <summary>
    /// Best-effort absolute-path extraction from a tool's JSON argument blob ("path"/"file"/
    /// "filename" keys, or a bare absolute-looking token). Returns null if none is found. ACP
    /// requires all paths to be absolute, so relative matches are rejected.
    /// </summary>
    public static string? ExtractAbsolutePath(string? toolArgs)
    {
        if (string.IsNullOrWhiteSpace(toolArgs)) return null;
        try
        {
            using var doc = JsonDocument.Parse(toolArgs);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "path", "file", "filename", "filePath", "file_path" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        string s = v.GetString() ?? "";
                        if (IsAbsolute(s)) return s;
                    }
                }
            }
        }
        catch (JsonException) { /* args not JSON; fall through */ }
        return null;
    }

    private static bool IsAbsolute(string s) =>
        !string.IsNullOrEmpty(s) &&
        (s.StartsWith('/') || (s.Length > 2 && char.IsLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/')) || s.StartsWith("\\\\"));

    /// <summary>
    /// available_commands_update: advertise the slash commands the client can offer the user.
    /// Each command: name (no leading slash), description, optional input hint.
    /// </summary>
    public static object AvailableCommandsUpdate(IEnumerable<(string Name, string Description)> commands) =>
        new Dictionary<string, object?>
        {
            ["sessionUpdate"] = "available_commands_update",
            ["availableCommands"] = commands.Select(c => new Dictionary<string, object?>
            {
                ["name"] = c.Name,
                ["description"] = c.Description
            }).ToArray()
        };

    /// <summary>current_mode_update: report the agent's active session mode id to the client.</summary>
    public static object CurrentModeUpdate(string modeId) =>
        new Dictionary<string, object?>
        {
            ["sessionUpdate"] = "current_mode_update",
            ["currentModeId"] = modeId
        };

    /// <summary>
    /// session/request_permission params (Agent->Client). Asks the client to approve a tool
    /// call, offering allow/reject options. The client replies with a selected optionId or a
    /// cancelled outcome.
    /// </summary>
    public static object RequestPermissionParams(string sessionId, string toolCallId, string title) =>
        new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["toolCall"] = new Dictionary<string, object?>
            {
                ["toolCallId"] = toolCallId,
                ["title"] = title
            },
            ["options"] = new object[]
            {
                new Dictionary<string, object?> { ["optionId"] = "allow-once",   ["name"] = "Allow once",      ["kind"] = "allow_once" },
                new Dictionary<string, object?> { ["optionId"] = "allow-always", ["name"] = "Allow always",    ["kind"] = "allow_always" },
                new Dictionary<string, object?> { ["optionId"] = "reject-once",  ["name"] = "Reject",          ["kind"] = "reject_once" }
            }
        };

    /// <summary>fs/read_text_file params (Agent->Client).</summary>
    public static object FsReadParams(string sessionId, string path, int? line = null, int? limit = null)
    {
        var d = new Dictionary<string, object?> { ["sessionId"] = sessionId, ["path"] = path };
        if (line is not null) d["line"] = line;
        if (limit is not null) d["limit"] = limit;
        return d;
    }

    /// <summary>fs/write_text_file params (Agent->Client).</summary>
    public static object FsWriteParams(string sessionId, string path, string content) =>
        new Dictionary<string, object?> { ["sessionId"] = sessionId, ["path"] = path, ["content"] = content };

    /// <summary>Parse client capabilities advertised in initialize into a simple struct.</summary>
    public static AcpClientCaps ParseClientCaps(JsonElement prms)
    {
        bool fsRead = false, fsWrite = false, terminal = false;
        if (prms.ValueKind == JsonValueKind.Object &&
            prms.TryGetProperty("clientCapabilities", out var cc) && cc.ValueKind == JsonValueKind.Object)
        {
            if (cc.TryGetProperty("fs", out var fs) && fs.ValueKind == JsonValueKind.Object)
            {
                fsRead = fs.TryGetProperty("readTextFile", out var r) && r.ValueKind == JsonValueKind.True;
                fsWrite = fs.TryGetProperty("writeTextFile", out var w) && w.ValueKind == JsonValueKind.True;
            }
            terminal = cc.TryGetProperty("terminal", out var t) && t.ValueKind == JsonValueKind.True;
        }
        return new AcpClientCaps(fsRead, fsWrite, terminal);
    }

    /// <summary>Valid ACP stop reasons.</summary>
    public static class StopReason
    {
        public const string EndTurn = "end_turn";
        public const string MaxTokens = "max_tokens";
        public const string MaxTurnRequests = "max_turn_requests";
        public const string Refusal = "refusal";
        public const string Cancelled = "cancelled";
    }
}

/// <summary>Client capabilities advertised in initialize (gates agent->client callbacks).</summary>
public readonly record struct AcpClientCaps(bool FsRead, bool FsWrite, bool Terminal);
