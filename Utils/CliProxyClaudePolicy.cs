using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MuxSwarm.Utils;

/// <summary>
/// Request-only pipeline policy that reproduces the two transforms the external cliproxy-filter shim used
/// to perform, now in-process so they apply whenever Mux points DIRECTLY at the CLIProxyAPI sidecar (no
/// external :8318 proxy). Both are gated on the model id (Claude/Opus); non-matching models pass through
/// untouched. Only the outbound request body is rewritten - the streamed SSE response is never touched, so
/// there is no re-chunking risk.
///
/// Transform A: strip sampling params the Claude/OAuth backend rejects (temperature, top_p, ...).
/// Transform B: fold system/developer messages into the first user message (the OpenAI-compat -> Claude
///   OAuth bridge drops system/developer roles, so Mux's injected context would otherwise never reach the
///   model -> an agent with no system context).
/// </summary>
internal sealed class CliProxyClaudePolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        TryRewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        TryRewrite(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void TryRewrite(PipelineMessage message)
    {
        try
        {
            var content = message.Request?.Content;
            if (content is null) return;

            using var ms = new MemoryStream();
            content.WriteTo(ms);
            if (ms.Length == 0) return;
            string original = Encoding.UTF8.GetString(ms.ToArray());

            string? rewritten = Transform(original);
            if (rewritten is not null && !ReferenceEquals(rewritten, original))
                message.Request!.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
        }
        catch
        {
            // Never break a request because the optional rewrite failed; forward the body as-is.
        }
    }

    // ── pure transform (unit-tested) ─────────────────────────────────────────

    private static readonly string[] BlockedSamplingParams =
    {
        "temperature", "top_p", "top_k", "frequency_penalty",
        "presence_penalty", "repetition_penalty", "min_p", "typical_p",
    };

    internal static bool IsOpus(string model) => model.Contains("opus", StringComparison.OrdinalIgnoreCase);
    internal static bool IsClaude(string model) => model.TrimStart().StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies transforms A and B to an OpenAI chat-completions request JSON body. Returns the rewritten
    /// JSON, or the SAME reference (unchanged) if the model does not match or nothing needed changing.
    /// Invalid JSON is returned unchanged.
    /// </summary>
    internal static string Transform(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return json; }
        if (root is not JsonObject obj) return json;

        string model = obj["model"]?.GetValue<string>() ?? "";
        if (model.Length == 0) return json;

        bool claude = IsClaude(model);
        bool opus = IsOpus(model);
        if (!claude && !opus) return json;

        bool changed = false;

        // Transform A: strip blocked sampling params (claude OR opus).
        foreach (var key in BlockedSamplingParams)
            if (obj.Remove(key)) changed = true;

        // Transform B: merge system/developer -> first user message (claude only, chat-completions shape).
        if (claude && obj["messages"] is JsonArray messages && messages.Count > 0)
            changed |= MergeSystemIntoFirstUser(messages);

        return changed ? obj.ToJsonString() : json;
    }

    private static bool MergeSystemIntoFirstUser(JsonArray messages)
    {
        var contextBlocks = new List<string>();
        var keepIndices = new List<int>();      // indices to keep (non system/developer)
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i] as JsonObject;
            string role = m?["role"]?.GetValue<string>() ?? "";
            if (role is "system" or "developer")
            {
                string text = ExtractText(m!["content"]);
                if (text.Length > 0)
                    contextBlocks.Add($"[{role.ToUpperInvariant()}]\n{text}");
            }
            else
            {
                keepIndices.Add(i);
            }
        }

        if (contextBlocks.Count == 0) return false; // nothing to merge

        string ctx = string.Join("\n\n", contextBlocks);

        // Rebuild the array without system/developer messages.
        var kept = new List<JsonNode?>();
        foreach (int i in keepIndices)
        {
            var node = messages[i];
            messages[i] = null;     // detach so we can re-parent into the new array
            kept.Add(node);
        }

        // Find the first user message among the kept set and prefix the trusted context.
        int firstUser = kept.FindIndex(n => (n as JsonObject)?["role"]?.GetValue<string>() == "user");
        if (firstUser >= 0)
        {
            var um = (JsonObject)kept[firstUser]!;
            string userText = ExtractText(um["content"]);
            um["content"] = $"For this conversation, relevant trusted application context: {ctx}\n\nUser request: {userText}";
        }
        else
        {
            // No user message: synthesize one at the front.
            kept.Insert(0, new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"For this conversation, relevant trusted application context: {ctx}\n\nPlease acknowledge and continue.",
            });
        }

        messages.Clear();
        foreach (var n in kept) messages.Add(n);
        return true;
    }

    /// <summary>
    /// Extracts plain text from an OpenAI message content node: a plain string, or the multi-part list form
    /// [{"type":"text","text":"..."}, ...] (pulls "text", falling back to "content", per part, joined by \n).
    /// </summary>
    internal static string ExtractText(JsonNode? content)
    {
        if (content is null) return "";
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is JsonArray arr)
        {
            var parts = new List<string>();
            foreach (var item in arr)
            {
                if (item is JsonObject io)
                {
                    string? t = io["text"]?.GetValue<string>() ?? io["content"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(t)) parts.Add(t);
                }
                else if (item is JsonValue iv && iv.TryGetValue<string>(out var its))
                {
                    parts.Add(its);
                }
            }
            return string.Join("\n", parts);
        }
        return content.ToString();
    }
}
