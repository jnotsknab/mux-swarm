using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Provider-aware reasoning escalation for /ultra mode. Applied to a freshly built
/// <see cref="ChatOptions"/> only while ultra is active; because escalation is gated on
/// the App.UltraMode flag at build time and ChatOptions are reconstructed per run, toggling
/// ultra off naturally restores prior reasoning with no persistent mutation of config.
/// </summary>
public static class UltraReasoning
{
    /// <summary>
    /// Escalates reasoning on <paramref name="opts"/> to the maximum available: a provider-native
    /// numeric thinking budget (honored by budget-capable providers e.g. Anthropic thinking.budget_tokens)
    /// AND the top effort tier <see cref="ReasoningEffort.ExtraHigh"/>. ExtraHigh serializes to the wire
    /// value "xhigh", which some endpoints (CLIProxy/Claude path) reject; that no longer gates the mode,
    /// because <see cref="ReasoningEffortFallbackClient"/> (wired inside CreateChatClient) transparently
    /// retries at High on rejection. Endpoints that accept ExtraHigh get it.
    /// </summary>
    public static void Apply(ChatOptions opts)
    {
        var budget = App.Config.Ultra.ThinkingBudget;

        // Numeric budget path — forwarded as an arbitrary provider param, exactly like
        // ModelOpts.AdditionalParams. Providers that honor a numeric thinking budget
        // (e.g. Anthropic thinking.budget_tokens) pick it up; others ignore it.
        if (budget > 0)
        {
            opts.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            opts.AdditionalProperties["thinking"] = new Dictionary<string, object>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budget
            };
        }

        // Effort tier — request the top tier (ExtraHigh). ReasoningEffortFallbackClient degrades
        // to High per-model if the endpoint rejects the "xhigh" wire value, so this never fails a run.
        opts.Reasoning = new ReasoningOptions
        {
            Effort = ReasoningEffort.ExtraHigh,
            Output = opts.Reasoning?.Output
        };
    }
}
