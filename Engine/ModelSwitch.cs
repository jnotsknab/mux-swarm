using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>
/// <c>/setmodel</c> inside a live session (/agent, /stateless, /swarm, /pswarm, ultra, giga; not teams).
/// Saves the choice to Swarm.json like the menu command, then the caller rebuilds its lead on the new
/// model with the session carried over unchanged, and changed specialists are rebuilt here. Forms:
/// <c>/setmodel</c> (picker), <c>/setmodel &lt;model&gt;</c> (the agent you are talking to),
/// <c>/setmodel &lt;agent&gt; &lt;model&gt;</c> (any slot; the name is matched like <c>/agent</c>).
/// </summary>
internal static class ModelSwitch
{
    /// <summary>One saved slot change. <paramref name="Effort"/> is set only when the effort changed;
    /// <paramref name="IsLead"/> = the slot the caller's lead runs on.</summary>
    internal sealed record Change(string Id, string Label, string Model, string? Effort, bool IsLead);

    /// <summary>True for <c>/setmodel</c> with or without arguments.</summary>
    internal static bool IsCommand(string? line)
    {
        var l = (line ?? "").Trim();
        return l.Equals("/setmodel", StringComparison.OrdinalIgnoreCase) || l.StartsWith("/setmodel ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Run the command and return what changed. <paramref name="leadName"/> is the single-agent lead (its
    /// slot = its <c>agents</c> entry when that has a model, else <c>singleAgent</c>); null = the
    /// orchestrator. <paramref name="agentModels"/> (the caller's name-to-model map) is refreshed in place.
    /// </summary>
    internal static async Task<IReadOnlyList<Change>> RunAsync(string line, Dictionary<string, string>? agentModels,
        string? leadName, string? swarmPath = null)
    {
        swarmPath ??= PlatformContext.SwarmPath;
        var before = ModelSelectionStore.Load(swarmPath);
        string leadId = LeadSlotId(before, leadName);
        var args = line.Trim()["/setmodel".Length..].Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        try
        {
            if (args.Length == 0) CliCmdUtils.HandleModelSwap(inSession: true);
            else if (args.Length == 1 && before.Slots.Any(s => s.Label.Equals(args[0], StringComparison.OrdinalIgnoreCase)))
            {
                MuxConsole.WriteWarning($"Usage: /setmodel {args[0]} <model>  (or /setmodel <model> for the agent you are talking to)");
                return [];
            }
            else
            {
                var slot = args.Length == 1
                    ? before.Slots.FirstOrDefault(s => s.Id == leadId)
                    : AgentMatch.Best(args[0], before.Slots.Select(s => s.Label)) is { } label ? before.Slots.First(s => s.Label == label) : null;
                if (slot is null)
                {
                    MuxConsole.WriteWarning($"No agent matches '{args[0]}'. Slots: {string.Join(", ", before.Slots.Select(s => s.Label))}");
                    return [];
                }
                App.SwarmConfig = ModelSelectionStore.Save(before, slot.Id, args[^1], slot.Effort);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            MuxConsole.WriteWarning("Model not changed: " + ex.Message);
            return [];
        }
        var after = ModelSelectionStore.Load(swarmPath);
        var changes = after.Slots
            .Where(a => before.Slots.FirstOrDefault(b => b.Id == a.Id) is not { } b || b.Model != a.Model || b.Effort != a.Effort)
            .Where(a => !string.IsNullOrEmpty(a.Model))
            .Select(a => new Change(a.Id, a.Label, a.Model!,
                before.Slots.FirstOrDefault(b => b.Id == a.Id)?.Effort == a.Effort ? null : a.Effort, a.Id == leadId))
            .ToList();
        if (changes.Count > 0 && args.Length == 0)
            App.SwarmConfig = JsonSerializer.Deserialize<SwarmConfig>(File.ReadAllText(swarmPath)) ?? App.SwarmConfig;
        if (agentModels is not null)
            foreach (var c in changes)
            {
                string? key = c.Id switch { "orchestrator" => "Orchestrator", "compactionAgent" => "Compaction", "singleAgent" => null, _ => c.Label };
                if (key is not null) agentModels[key] = c.Model;
            }
        return changes;
    }

    /// <summary>The configured model of lead <paramref name="leadName"/> (null = the orchestrator), resolved
    /// the way a session resolves it: the agent's own entry first, then <c>singleAgent</c>.</summary>
    internal static string? LeadModel(SwarmConfig? config, string? leadName) =>
        leadName is null ? config?.Orchestrator?.Model
        : config?.Agents?.FirstOrDefault(a => a.Name?.Equals(leadName, StringComparison.OrdinalIgnoreCase) == true)?.Model is { Length: > 0 } m
            ? m : config?.SingleAgent?.Model;

    /// <summary>Slot id the lead runs on (mirrors how a session resolves its model).</summary>
    internal static string LeadSlotId(ModelSelectionStore.Snapshot s, string? leadName) =>
        leadName is null ? "orchestrator"
        : s.Slots.FirstOrDefault(x => x.Id.StartsWith("agents/", StringComparison.Ordinal)
              && x.Label.Equals(leadName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(x.Model))?.Id
          ?? "singleAgent";

    /// <summary>Apply a saved effort to live options (null = leave the live effort as is). Returns the label set.</summary>
    internal static string? ApplyEffort(ChatOptions options, string? effort)
    {
        if (effort is null || !ReasoningEffortControl.TryParse(effort, out var selection)) return null;
        ReasoningEffortControl.Apply(options, selection!);
        return selection!.Label;
    }

    /// <summary>
    /// The same agent (name, instructions, tools, options) on <paramref name="client"/>. An agent copies its
    /// options when built, so a caller that edits live options (e.g. /effort) passes them as
    /// <paramref name="chatOptions"/>; otherwise the agent's own are reused. <paramref name="effort"/> is applied when set.
    /// </summary>
    internal static ChatClientAgent Rebuild(AIAgent agent, IChatClient client, ChatOptions? chatOptions = null, string? effort = null)
    {
        var options = (agent.GetService(typeof(ChatClientAgentOptions)) as ChatClientAgentOptions)?.Clone()
            ?? new ChatClientAgentOptions { Name = agent.Name };
        if (chatOptions is not null) options.ChatOptions = chatOptions;
        if (options.ChatOptions is { } co) ApplyEffort(co, effort);
        return client.AsAIAgent(options);
    }

    /// <summary>Move <paramref name="session"/> from one agent to another unchanged (every message, tool call and result).</summary>
    internal static async Task<AgentSession> MoveSessionAsync(AIAgent from, AgentSession session, AIAgent to) =>
        await to.DeserializeSessionAsync(await from.SerializeSessionAsync(session));

    /// <summary>
    /// Rebuild the specialists whose slot changed on the new model, keeping their instructions, tools and
    /// options. Their history is per delegation, so each gets a fresh session. Background jobs already
    /// running keep the agent they started with.
    /// </summary>
    internal static async Task RebuildSpecialistsAsync(
        IDictionary<string, (AIAgent Agent, AgentSession Session, Common.AgentDefinition Def)> specialists,
        IReadOnlyList<Change> changes, Func<string, IChatClient>? clientFactory = null)
    {
        clientFactory ??= m => App.CreateChatClient(m);
        foreach (var c in changes.Where(c => c.Id.StartsWith("agents/", StringComparison.Ordinal)))
        {
            if (!specialists.TryGetValue(c.Label, out var s)) continue;
            var agent = Rebuild(s.Agent, clientFactory(c.Model), effort: c.Effort);
            specialists[c.Label] = (agent, await agent.CreateSessionAsync(), s.Def);
        }
    }

    /// <summary>One line per change. <paramref name="leadNote"/> (e.g. "same session, history kept") marks the
    /// lead as switched live; null = nothing is live (the menu), every change applies from the next run.</summary>
    internal static void Report(IReadOnlyList<Change> changes, string? leadNote)
    {
        if (changes.Count == 0) { MuxConsole.WriteMuted("Model unchanged."); return; }
        foreach (var c in changes)
        {
            string effort = c.Effort is { } e ? $" \u00b7 effort {e}" : "";
            MuxConsole.WriteSuccess(c.IsLead && leadNote is not null
                ? $"{c.Label} now on {c.Model}{effort} - {leadNote}."
                : $"{c.Label} set to {c.Model}{effort} (saved; used from its next {(leadNote is null ? "run" : "task")}).");
        }
    }
}
