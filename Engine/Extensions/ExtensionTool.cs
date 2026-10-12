using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// A tool served by a running extension process, exposed to the model as
/// <c>{extension}_{name}</c>. Invoking it sends a <c>tool/call</c> JSON-RPC request and returns
/// the response's <c>result.content</c>. Failures (error reply, timeout, process gone) come back as
/// an error string for the model; nothing is thrown into the orchestrator.
/// </summary>
internal sealed class ExtensionTool : AIFunction
{
    private readonly ExtensionProcess _process;
    private readonly ExtensionToolSpec _spec;

    internal ExtensionTool(ExtensionProcess process, ExtensionToolSpec spec)
    {
        _process = process;
        _spec = spec;
        Name = $"{process.Name}_{spec.Name}";
    }

    /// <inheritdoc/>
    public override string Name { get; }

    /// <inheritdoc/>
    public override string Description => _spec.Description;

    /// <inheritdoc/>
    public override JsonElement JsonSchema => _spec.Parameters;

    /// <summary>
    /// True when the manifest grants this tool to <paramref name="agentName"/> directly
    /// (<c>agents: ["*"]</c> or the agent's name), bypassing the Swarm.json tool filter.
    /// </summary>
    internal bool GrantedTo(string? agentName) =>
        _spec.Agents is { Count: > 0 } agents
        && agents.Any(a => a == "*" || (agentName is not null && string.Equals(a, agentName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>True when <paramref name="tool"/> is an extension tool granted to <paramref name="agentName"/>.</summary>
    internal static bool IsGranted(AITool tool, string? agentName) => tool is ExtensionTool t && t.GrantedTo(agentName);

    /// <summary>Build the tools of every running extension process (empty when none run).</summary>
    internal static IEnumerable<AITool> ForRunning()
    {
        var loaded = ExtensionRegistry.Loaded.Extensions;
        foreach (var process in ExtensionHost.Running)
        {
            var ext = loaded.FirstOrDefault(e => e.Name == process.Name);
            foreach (var spec in ext?.Tools ?? [])
                yield return new ExtensionTool(process, spec);
        }
    }

    /// <inheritdoc/>
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var args = arguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        JsonElement reply;
        try
        {
            reply = await _process.RequestAsync("tool/call", new { name = _spec.Name, arguments = args },
                TimeSpan.FromSeconds(Timeout), cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
        if (reply.ValueKind != JsonValueKind.Object)
            return Fail(_process.IsRunning ? $"no answer within {Timeout}s" : "the extension process is not running");
        if (reply.TryGetProperty("error", out var error))
            return Fail(error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.ToString() : error.ToString());
        if (reply.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("content", out var content))
            return content.ValueKind == JsonValueKind.String ? content.GetString() : content.GetRawText();
        return Fail("the response has no result.content");
    }

    private int Timeout => _spec.TimeoutSeconds ?? ExtensionRegistry.DefaultToolTimeoutSeconds;

    private string Fail(string message) => $"[extension {_process.Name}] tool failed: {message}";
}
