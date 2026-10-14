using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Lets trusted extension processes gate tool calls. Before a matching tool runs, Mux sends a
/// <c>tool/before</c> request; the extension answers <c>allow</c> (optionally with rewritten
/// arguments) or <c>deny</c> (with a reason the model sees). After it runs, a <c>tool/after</c>
/// request may replace the result (e.g. to redact secrets). Installed into the function-invocation
/// pipeline only when extensions are enabled; with none running it is a direct pass-through.
/// The ActivityTimeout watchdog is not involved: every wait is capped at <see cref="MaxTimeout"/>.
/// </summary>
internal static class ToolInterceptor
{
    /// <summary>Upper bound on any interceptor wait, whatever the manifest says.</summary>
    internal static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>FunctionInvoker</c> for <see cref="FunctionInvokingChatClient"/>: runs matching interceptors
    /// in extension-name order around <c>context.Function.InvokeAsync</c> (what the SDK does by default).
    /// </summary>
    internal static async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken ct)
    {
        var tool = context.Function.Name;
        var chain = ExtensionHost.Running
            .Where(p => p.IsRunning)
            .SelectMany(p => p.Intercepts.Where(i => i.Matches(tool)).Select(i => (Process: p, Rule: i)))
            .ToList();
        if (chain.Count == 0) return await context.Function.InvokeAsync(context.Arguments, ct);

        foreach (var (process, rule) in chain.Where(c => c.Rule.Before))
        {
            var reply = await process.RequestAsync("tool/before",
                new { tool, arguments = context.Arguments }, rule.Timeout, ct);
            var decision = Decide(reply, rule, out var reason, out var rewritten);
            if (decision == Decision.Deny)
            {
                // tool_call already fired (when the model streamed it); this says it never ran.
                State.HookWorker.Emit("tool_denied", summary: tool, data: [("tool", tool), ("by", process.Name), ("reason", reason)]);
                return $"[BLOCKED by extension {process.Name}] {reason}";
            }
            if (rewritten is not null)
                context.Arguments = new AIFunctionArguments(rewritten) { Services = context.Arguments.Services, Context = context.Arguments.Context };
        }

        object? result = await context.Function.InvokeAsync(context.Arguments, ct);

        foreach (var (process, rule) in chain.Where(c => c.Rule.After))
        {
            var reply = await process.RequestAsync("tool/after",
                new { tool, arguments = context.Arguments, result = ResultText(result) }, rule.Timeout, ct);
            if (reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("result", out var r)
                && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("result", out var replaced)
                && replaced.ValueKind == JsonValueKind.String)
                result = replaced.GetString();
        }
        return result;
    }

    /// <summary>
    /// Intercept patterns that match none of <paramref name="toolNames"/>, as display warnings
    /// (e.g. a guard written for <c>Shell_*</c> when the shell tools are <c>execute_command_async</c>).
    /// Diagnostic only: patterns are still applied, since tools can appear later (MCP reconnect).
    /// </summary>
    internal static IEnumerable<string> UnmatchedPatterns(IEnumerable<ExtensionRegistry.Extension> extensions, IReadOnlyCollection<string> toolNames) =>
        extensions.SelectMany(e => e.Intercepts
            .Where(rule => !toolNames.Any(rule.Matches))
            .Select(rule => $"{e.Name}: intercept '{rule.Tool}' matches no loaded tool (shell tools are execute_command_async / repl_shell_exec)."));

    internal enum Decision { Allow, Deny }

    /// <summary>
    /// Read a <c>tool/before</c> reply. No answer (timeout, exit, malformed) applies the rule's
    /// <c>onTimeout</c> (allow by default). A JSON-RPC <c>error</c> counts as no answer.
    /// </summary>
    internal static Decision Decide(JsonElement reply, InterceptRule rule, out string reason, out Dictionary<string, object?>? arguments)
    {
        reason = "";
        arguments = null;
        if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object)
        {
            reason = "interceptor did not answer in time";
            return rule.DenyOnTimeout ? Decision.Deny : Decision.Allow;
        }
        var decision = r.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : "allow";
        if (string.Equals(decision, "deny", StringComparison.OrdinalIgnoreCase))
        {
            reason = r.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() ?? "" : "denied";
            return Decision.Deny;
        }
        if (r.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
            arguments = args.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return Decision.Allow;
    }

    private static string? ResultText(object? result) => result switch
    {
        null => null,
        string s => s,
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText(),
        _ => JsonSerializer.Serialize(result),
    };
}

/// <summary>
/// An <c>intercepts</c> manifest entry: which tools to gate (exact name, or a prefix ending in
/// <c>*</c>; <c>*</c> = all), whether to run before and/or after, the wait budget, and what to
/// do when the extension does not answer in time.
/// </summary>
internal sealed record InterceptRule(string Tool, bool Before, bool After, TimeSpan Timeout, bool DenyOnTimeout)
{
    /// <summary>True when <paramref name="tool"/> is covered by this rule (case-insensitive).</summary>
    internal bool Matches(string tool) =>
        Tool.EndsWith('*')
            ? tool.StartsWith(Tool[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(tool, Tool, StringComparison.OrdinalIgnoreCase);
}
