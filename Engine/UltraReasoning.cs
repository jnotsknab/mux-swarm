using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>
/// Provider-aware reasoning escalation for /ultra mode. Applied to a freshly built
/// <see cref="ChatOptions"/> only while ultra is active; because escalation is gated on
/// the App.UltraMode flag at build time and ChatOptions are reconstructed per run, toggling
/// ultra off naturally restores prior reasoning with no persistent mutation of config.
/// </summary>
public static class UltraReasoning
{
    /// <summary>
    /// Escalates legacy reasoning on <paramref name="opts"/> to ExtraHigh plus a provider-native
    /// numeric thinking budget (honored by budget-capable providers e.g. Anthropic thinking.budget_tokens)
    /// and the portable enum tier <see cref="ReasoningEffort.ExtraHigh"/>. ExtraHigh serializes to the wire
    /// value "xhigh", which some endpoints (CLIProxy/Claude path) reject; that no longer gates the mode,
    /// because <see cref="ReasoningEffortFallbackClient"/> (wired inside CreateChatClient) transparently
    /// retries at High on rejection. Explicit max/custom efforts are preserved, not downgraded to ExtraHigh.
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

        // Keep the existing ExtraHigh escalation and rejection fallback for portable tiers.
        // An explicit max/custom choice is authoritative and must not be replaced.
        if (ReasoningEffortControl.GetExplicit(opts) is null)
        {
            opts.Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.ExtraHigh,
                Output = opts.Reasoning?.Output
            };
        }
    }
}
